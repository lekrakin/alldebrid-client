using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Services;
using AdbClient.Web.Controllers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdbClient.Service.Test.Controllers;

[Collection(SettingsIsolationCollection.Name)]
public class SettingsContractTest
{
    [Fact]
    public async Task GetAndSave_UseReleasedKeysWithIndependentDisplayGroups()
    {
        var original = Settings.Get;
        var originalLogLevel = Settings.LoggingLevelSwitch.MinimumLevel;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<DataContext>(options => options.UseSqlite(connection));
        builder.Services.AddScoped<SettingData>();
        builder.Services.AddScoped<Settings>();
        builder.Services.AddControllers().AddApplicationPart(typeof(SettingsController).Assembly).AddControllersAsServices();
        builder.Services.AddScoped(provider => new SettingsController(provider.GetRequiredService<Settings>(), null!));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization(options => options.AddPolicy("AuthSetting", policy => policy.RequireAssertion(_ => true)));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        try
        {
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DataContext>().Database.EnsureCreatedAsync();
                var settings = scope.ServiceProvider.GetRequiredService<Settings>();
                await settings.Seed();
                await settings.ResetCache();
            }

            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var fields = (await client.GetFromJsonAsync<SettingProperty[]>("api/settings"))!
                .Where(setting => setting.Type != "Object").ToDictionary(setting => setting.Key);

            Assert.Equal(40, fields.Count);
            Assert.Equal("Downloads", fields["DownloadClient:MaxSpeed"].ParentKey);
            Assert.Equal("Storage", fields["Paths:DownloadPath"].ParentKey);
            Assert.Equal("Integrations", fields["General:Categories"].ParentKey);
            Assert.Equal("Downloads:Defaults", fields["DownloadClient:Default:FinishedAction"].ParentKey);

            fields["DownloadClient:MaxSpeed"].Value = 17;
            fields["Paths:DownloadPath"].Value = Path.GetFullPath("/settings-contract/downloads");
            fields["Paths:MappedPath"].Value = "/media/downloads";
            fields["General:Categories"].Value = "radarr,logpose";
            fields["DownloadClient:Default:FinishedAction"].Value = 0;
            using var response = await client.PutAsJsonAsync("api/settings", fields.Values);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Reload from the database, not the singleton used by the successful save.
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<Settings>();
                await settings.Seed();
                await settings.ResetCache();
                var stored = await scope.ServiceProvider.GetRequiredService<DataContext>().Settings.AsNoTracking()
                    .ToDictionaryAsync(setting => setting.SettingId, setting => setting.Value);
                Assert.Equal("17", stored["DownloadClient:MaxSpeed"]);
                Assert.Equal(Path.GetFullPath("/settings-contract/downloads"), stored["Paths:DownloadPath"]);
                Assert.Equal("/media/downloads", stored["Paths:MappedPath"]);
                Assert.Equal("radarr,logpose", stored["General:Categories"]);
                Assert.Equal("0", stored["DownloadClient:Default:FinishedAction"]);
                Assert.Equal(fields.Keys.Order(), stored.Keys.Order());
            }

            var reloaded = (await client.GetFromJsonAsync<SettingProperty[]>("api/settings"))!
                .Where(setting => setting.Type != "Object").ToDictionary(setting => setting.Key);
            Assert.Equal(17, Assert.IsType<JsonElement>(reloaded["DownloadClient:MaxSpeed"].Value).GetInt32());
            Assert.Equal("radarr,logpose", Assert.IsType<JsonElement>(reloaded["General:Categories"].Value).GetString());
            Assert.Equal("Integrations", reloaded["General:Categories"].ParentKey);
        }
        finally
        {
            await app.StopAsync();
            Settings.Get.General = original.General;
            Settings.Get.Downloads = original.Downloads;
            Settings.Get.Storage = original.Storage;
            Settings.Get.Provider = original.Provider;
            Settings.Get.Integrations = original.Integrations;
            Settings.Get.WatchFolder = original.WatchFolder;
            Settings.LoggingLevelSwitch.MinimumLevel = originalLogLevel;
        }
    }
}
