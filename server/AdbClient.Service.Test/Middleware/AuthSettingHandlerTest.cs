using System.Security.Claims;
using AdbClient.Data.Enums;
using AdbClient.Service.Middleware;
using AdbClient.Service.Services;
using Microsoft.AspNetCore.Authorization;

namespace AdbClient.Service.Test.Middleware;

[Collection(SettingsIsolationCollection.Name)]
public class AuthSettingHandlerTest
{
    [Theory]
    [InlineData(AuthenticationType.None, false, true)]
    [InlineData(AuthenticationType.UserNamePassword, false, false)]
    [InlineData(AuthenticationType.UserNamePassword, true, true)]
    public async Task HandleAsync_UsesTheConfiguredAuthenticationMode(
        AuthenticationType authenticationType,
        bool isAuthenticated,
        bool expectedSuccess)
    {
        var originalAuthenticationType = Settings.Get.General.AuthenticationType;

        try
        {
            Settings.Get.General.AuthenticationType = authenticationType;
            var requirement = new AuthSettingRequirement();
            var identity = isAuthenticated
                ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "test")
                : new ClaimsIdentity();
            var context = new AuthorizationHandlerContext(
                [requirement],
                new ClaimsPrincipal(identity),
                null);

            await new AuthSettingHandler().HandleAsync(context);

            Assert.Equal(expectedSuccess, context.HasSucceeded);
        }
        finally
        {
            Settings.Get.General.AuthenticationType = originalAuthenticationType;
        }
    }
}
