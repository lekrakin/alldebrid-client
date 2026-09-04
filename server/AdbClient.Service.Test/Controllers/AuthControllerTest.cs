using AdbClient.Data.Data;
using AdbClient.Service.Services;
using AdbClient.Web.Controllers;
using AdbClient.Web.Models.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AdbClient.Service.Test.Controllers;

public class AuthControllerTest
{
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
}
