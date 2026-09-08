using System.Net;
using System.Security.Claims;
using AdbClient.Data.Enums;
using AdbClient.Service.Middleware;
using AdbClient.Service.Services;
using AdbClient.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdbClient.Service.Test.Services;

[Collection(SettingsIsolationCollection.Name)]
public class AdbHubContractTest
{
    [Fact]
    public async Task Negotiate_AllowsAnonymousAccess_WhenAuthenticationIsDisabled()
    {
        await WithAuthenticationType(AuthenticationType.None, async () =>
        {
            await using var app = await StartApplication();
            using var client = CreateClient(app);

            using var response = await Negotiate(client);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
    }

    [Fact]
    public async Task Negotiate_RejectsAnonymousAccess_WhenPasswordAuthenticationIsEnabled()
    {
        await WithAuthenticationType(AuthenticationType.UserNamePassword, async () =>
        {
            await using var app = await StartApplication();
            using var client = CreateClient(app);

            using var response = await Negotiate(client);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        });
    }

    [Fact]
    public async Task Negotiate_AllowsAuthenticatedAccess_WhenPasswordAuthenticationIsEnabled()
    {
        await WithAuthenticationType(AuthenticationType.UserNamePassword, async () =>
        {
            await using var app = await StartApplication();
            using var client = CreateClient(app);

            using var authentication = await client.GetAsync("authenticate");
            using var response = await Negotiate(client);

            Assert.Equal(HttpStatusCode.OK, authentication.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
    }

    private static async Task WithAuthenticationType(AuthenticationType authenticationType, Func<Task> test)
    {
        var originalAuthenticationType = Settings.Get.General.AuthenticationType;

        try
        {
            Settings.Get.General.AuthenticationType = authenticationType;
            await test();
        }
        finally
        {
            Settings.Get.General.AuthenticationType = originalAuthenticationType;
        }
    }

    private static async Task<WebApplication> StartApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
               .AddCookie(options =>
               {
                   options.Cookie.Name = "SID";
                   options.Events.OnRedirectToLogin = AuthenticationRedirects.HandleLogin;
               });
        builder.Services.AddSingleton<IAuthorizationHandler, AuthSettingHandler>();
        builder.Services.AddAuthorizationBuilder()
               .AddPolicy("AuthSetting", policy => policy.Requirements.Add(new AuthSettingRequirement()));

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/authenticate", Authenticate);
        app.MapHub<AdbHub>("/hub");
        await app.StartAsync();

        return app;
    }

    private static async Task Authenticate(HttpContext context)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }

    private static Task<HttpResponseMessage> Negotiate(HttpClient client)
    {
        return client.PostAsync("hub/negotiate?negotiateVersion=1", content: null);
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);

        return new(new HttpClientHandler())
        {
            BaseAddress = new(address)
        };
    }
}
