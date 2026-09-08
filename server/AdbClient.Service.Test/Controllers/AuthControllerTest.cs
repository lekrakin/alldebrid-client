using AdbClient.Data.Data;
using AdbClient.Service.Services;
using AdbClient.Web.Controllers;
using AdbClient.Web.Models.Requests;
using AdbClient.Web.Models.Responses;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AdbClient.Service.Test.Controllers;

[Collection(SettingsIsolationCollection.Name)]
public class AuthControllerTest
{
    [Theory]
    [InlineData("", false)]
    [InlineData("existing-provider-key", true)]
    public async Task Create_ReportsWhetherProviderIsAlreadyConfigured(
        string apiKey,
        bool expectedProviderConfigured)
    {
        var originalApiKey = Settings.Get.Provider.ApiKey;

        try
        {
            Settings.Get.Provider.ApiKey = apiKey;

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var dataContext = CreateDataContext(connection);
            await dataContext.Database.EnsureCreatedAsync();

            var userManager = CreateUserManager();
            userManager.Setup(manager => manager.CreateAsync(
                           It.Is<IdentityUser>(user => user.UserName == "new-user"),
                           "new-password"))
                       .ReturnsAsync(IdentityResult.Success);
            var signInManager = CreateSignInManager(userManager.Object);
            signInManager.Setup(manager => manager.PasswordSignInAsync(
                              "new-user",
                              "new-password",
                              true,
                              false))
                         .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);
            var authentication = new Authentication(
                signInManager.Object,
                userManager.Object,
                new UserData(dataContext));
            var controller = new AuthController(authentication, null!);

            var result = await controller.Create(new AuthControllerLoginRequest
            {
                UserName = "new-user",
                Password = "new-password"
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<AuthControllerCreateResponse>(ok.Value);
            Assert.Equal(expectedProviderConfigured, response.ProviderConfigured);
            userManager.VerifyAll();
            signInManager.VerifyAll();
        }
        finally
        {
            Settings.Get.Provider.ApiKey = originalApiKey;
        }
    }

    [Fact]
    public async Task Create_RejectsAdditionalUsersBeforeReturningProviderState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();
        dataContext.Users.Add(new IdentityUser("current-user"));
        await dataContext.SaveChangesAsync();

        var userManager = CreateUserManager();
        var authentication = new Authentication(null!, userManager.Object, new UserData(dataContext));
        var controller = new AuthController(authentication, null!);

        var result = await controller.Create(new AuthControllerLoginRequest
        {
            UserName = "second-user",
            Password = "new-password"
        });

        var unauthorized = Assert.IsType<StatusCodeResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        userManager.Verify(
            manager => manager.CreateAsync(It.IsAny<IdentityUser>(), It.IsAny<string>()),
            Times.Never());
    }

    [Fact]
    public async Task Register_ConcurrentRequestsCreateOnlyOneAccount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();

        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.CreateAsync(
                       It.IsAny<IdentityUser>(),
                       It.IsAny<string>()))
                   .Returns<IdentityUser, string>(async (user, _) =>
                   {
                       dataContext.Users.Add(new IdentityUser(user.UserName!));
                       await dataContext.SaveChangesAsync();
                       return IdentityResult.Success;
                   });
        var userData = new UserData(dataContext);
        var first = new Authentication(null!, userManager.Object, userData);
        var second = new Authentication(null!, userManager.Object, userData);

        var results = await Task.WhenAll(
            first.Register("first-user", "first-password"),
            second.Register("second-user", "second-password"));

        Assert.Single(results, result => result.Succeeded);
        var failed = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal("AccountAlreadyExists", Assert.Single(failed.Errors).Code);
        Assert.Single(await dataContext.Users.AsNoTracking().ToListAsync());
        userManager.Verify(
            manager => manager.CreateAsync(It.IsAny<IdentityUser>(), It.IsAny<string>()),
            Times.Once());
    }

    [Fact]
    public async Task SetupProvider_DoesNotReplaceAnExistingApiKey()
    {
        const string existingApiKey = "existing-provider-key";
        var originalApiKey = Settings.Get.Provider.ApiKey;

        try
        {
            Settings.Get.Provider.ApiKey = existingApiKey;
            var controller = new AuthController(null!, null!);

            var result = await controller.SetupProvider(new AuthControllerSetupProviderRequest
            {
                Token = "replacement-provider-key"
            });

            var unauthorized = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
            Assert.Equal(existingApiKey, Settings.Get.Provider.ApiKey);
        }
        finally
        {
            Settings.Get.Provider.ApiKey = originalApiKey;
        }
    }

    [Fact]
    public void SetupProvider_RetainsTheAuthSettingPolicy()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.SetupProvider));
        var attribute = Assert.Single(
            method!.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());

        Assert.Equal("AuthSetting", attribute.Policy);
    }

    [Theory]
    [InlineData("new-user", null, true, false)]
    [InlineData(null, "new-password", false, true)]
    [InlineData("new-user", "new-password", true, true)]
    public async Task Update_AllowsEachCredentialToBeChangedIndependently(
        string? userName,
        string? password,
        bool updatesUserName,
        bool updatesPassword)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();
        dataContext.Users.Add(new IdentityUser("current-user"));
        await dataContext.SaveChangesAsync();

        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.UpdateAsync(It.IsAny<IdentityUser>()))
                   .ReturnsAsync(IdentityResult.Success);
        userManager.Setup(manager => manager.GeneratePasswordResetTokenAsync(It.IsAny<IdentityUser>()))
                   .ReturnsAsync("reset-token");
        userManager.Setup(manager => manager.ResetPasswordAsync(
                       It.IsAny<IdentityUser>(),
                       "reset-token",
                       It.IsAny<string>()))
                   .ReturnsAsync(IdentityResult.Success);
        var authentication = new Authentication(null!, userManager.Object, new UserData(dataContext));
        var controller = new AuthController(authentication, null!);

        var result = await controller.Update(new AuthControllerUpdateRequest
        {
            UserName = userName,
            Password = password
        });

        Assert.IsType<OkResult>(result);
        userManager.Verify(
            manager => manager.UpdateAsync(It.Is<IdentityUser>(user => user.UserName == userName)),
            updatesUserName ? Times.Once() : Times.Never());
        userManager.Verify(
            manager => manager.ResetPasswordAsync(
                It.IsAny<IdentityUser>(),
                "reset-token",
                It.IsAny<string>()),
            updatesPassword ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Update_CreatesTheFirstAccountWhenBothCredentialsAreProvided()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();

        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.CreateAsync(
                       It.Is<IdentityUser>(user => user.UserName == "first-user"),
                       "first-password"))
                   .ReturnsAsync(IdentityResult.Success);
        var authentication = new Authentication(null!, userManager.Object, new UserData(dataContext));
        var controller = new AuthController(authentication, null!);

        var result = await controller.Update(new AuthControllerUpdateRequest
        {
            UserName = "first-user",
            Password = "first-password"
        });

        Assert.IsType<OkResult>(result);
        userManager.VerifyAll();
        userManager.Verify(manager => manager.UpdateAsync(It.IsAny<IdentityUser>()), Times.Never());
        userManager.Verify(
            manager => manager.ResetPasswordAsync(
                It.IsAny<IdentityUser>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never());
    }

    [Theory]
    [InlineData("first-user", null)]
    [InlineData(null, "first-password")]
    [InlineData("first-user", " ")]
    [InlineData(" ", "first-password")]
    public async Task Update_RequiresBothCredentialsWhenCreatingTheFirstAccount(
        string? userName,
        string? password)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();

        var userManager = CreateUserManager();
        var authentication = new Authentication(null!, userManager.Object, new UserData(dataContext));
        var controller = new AuthController(authentication, null!);

        var result = await controller.Update(new AuthControllerUpdateRequest
        {
            UserName = userName,
            Password = password
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Enter both a username and password to create the first account.", badRequest.Value);
        userManager.Verify(
            manager => manager.CreateAsync(It.IsAny<IdentityUser>(), It.IsAny<string>()),
            Times.Never());
    }

    [Fact]
    public async Task Update_ReturnsIdentityValidationErrorsWhenCreatingTheFirstAccount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();

        var expectedError = new IdentityError { Description = "Password does not meet the configured requirements." };
        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.CreateAsync(
                       It.Is<IdentityUser>(user => user.UserName == "first-user"),
                       "invalid-password"))
                   .ReturnsAsync(IdentityResult.Failed(expectedError));
        var authentication = new Authentication(null!, userManager.Object, new UserData(dataContext));
        var controller = new AuthController(authentication, null!);

        var result = await controller.Update(new AuthControllerUpdateRequest
        {
            UserName = "first-user",
            Password = "invalid-password"
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(expectedError.Description, badRequest.Value);
        userManager.VerifyAll();
    }

    [Fact]
    public async Task Update_RejectsRequestWithoutAnyCredentialChange()
    {
        var controller = new AuthController(null!, null!);

        var result = await controller.Update(new AuthControllerUpdateRequest
        {
            UserName = " ",
            Password = null
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Enter a new username, password, or both.", badRequest.Value);
    }

    [Fact]
    public async Task Update_DoesNotResetPasswordWhenUsernameUpdateFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dataContext = CreateDataContext(connection);
        await dataContext.Database.EnsureCreatedAsync();
        dataContext.Users.Add(new IdentityUser("current-user"));
        await dataContext.SaveChangesAsync();

        var expectedError = new IdentityError { Description = "Username is unavailable." };
        var userManager = CreateUserManager();
        userManager.Setup(manager => manager.UpdateAsync(It.IsAny<IdentityUser>()))
                   .ReturnsAsync(IdentityResult.Failed(expectedError));
        var authentication = new Authentication(null!, userManager.Object, new UserData(dataContext));
        var controller = new AuthController(authentication, null!);

        var result = await controller.Update(new AuthControllerUpdateRequest
        {
            UserName = "duplicate-user",
            Password = "new-password"
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(expectedError.Description, badRequest.Value);
        userManager.Verify(
            manager => manager.ResetPasswordAsync(
                It.IsAny<IdentityUser>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never());
    }

    private static DataContext CreateDataContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        return new DataContext(options);
    }

    private static Mock<UserManager<IdentityUser>> CreateUserManager()
    {
        return new Mock<UserManager<IdentityUser>>(
            Mock.Of<IUserStore<IdentityUser>>(),
            Options.Create(new IdentityOptions()),
            Mock.Of<IPasswordHasher<IdentityUser>>(),
            Array.Empty<IUserValidator<IdentityUser>>(),
            Array.Empty<IPasswordValidator<IdentityUser>>(),
            Mock.Of<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Mock.Of<IServiceProvider>(),
            Mock.Of<ILogger<UserManager<IdentityUser>>>());
    }

    private static Mock<SignInManager<IdentityUser>> CreateSignInManager(UserManager<IdentityUser> userManager)
    {
        return new Mock<SignInManager<IdentityUser>>(
            userManager,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<IdentityUser>>(),
            Options.Create(new IdentityOptions()),
            Mock.Of<ILogger<SignInManager<IdentityUser>>>(),
            Mock.Of<IAuthenticationSchemeProvider>(),
            Mock.Of<IUserConfirmation<IdentityUser>>());
    }
}
