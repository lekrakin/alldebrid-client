using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Net.Http.Json;
using AdbClient.Data.Data;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Middleware;
using AdbClient.Service.Models.QBittorrent;
using AdbClient.Service.Services;
using AdbClient.Service.Services.TorrentClients;
using AdbClient.Service.Wrappers;
using AdbClient.Web;
using AdbClient.Web.Controllers;
using AllDebridNET;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrent = AdbClient.Data.Models.Data.Torrent;

namespace AdbClient.Service.Test.Controllers;

[Collection(SettingsIsolationCollection.Name)]
public class QBittorrentLifecycleTest
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string MagnetUrl = $"magnet:?xt=urn:btih:{Hash}&dn=Movie.Release";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAndReadd_RetainsHistoryAndOnlyRequeuesMissingPayload(bool payloadMoved)
    {
        var original = (Settings.Get.General, Settings.Get.Downloads, Settings.Get.Storage,
            Settings.Get.Integrations, Settings.Get.Provider);

        try
        {
            var downloadRoot = OperatingSystem.IsWindows() ? @"C:\downloads" : "/downloads";
            Settings.Get.General = new() { AuthenticationType = AuthenticationType.None };
            Settings.Get.Downloads = new();
            Settings.Get.Downloads.Defaults.FinishedAction = TorrentFinishedAction.None;
            Settings.Get.Downloads.Defaults.HostDownloadAction = TorrentHostDownloadAction.DownloadAll;
            Settings.Get.Storage = new() { DownloadPath = downloadRoot };
            Settings.Get.Integrations = new() { ReportedDownloadPath = "/media/downloads", Categories = "radarr" };
            Settings.Get.Provider = new();
            await TorrentData.VoidCache();

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var context = new DataContext(new DbContextOptionsBuilder<DataContext>()
                .UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var torrentData = new TorrentData(context);
            var downloads = new Downloads(new DownloadData(context));
            var fileSystem = new MockFileSystem();
            var magnets = new Mock<IMagnetApi>(MockBehavior.Strict);
            magnets.Setup(api => api.StatusAsync("123", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Magnet
                {
                    Id = 123, Hash = Hash, Filename = "Movie.Release", Size = 400,
                    Downloaded = 400, StatusCode = 4, Status = "Ready"
                });
            var provider = new Mock<IAllDebridNETClient>(MockBehavior.Strict);
            provider.SetupGet(client => client.Magnet).Returns(magnets.Object);
            var providerFactory = new Mock<IAllDebridNetClientFactory>(MockBehavior.Strict);
            providerFactory.Setup(factory => factory.GetClient()).Returns(provider.Object);
            var enricher = new Mock<IEnricher>(MockBehavior.Strict);
            enricher.Setup(service => service.EnrichMagnetLink(MagnetUrl)).ReturnsAsync(MagnetUrl);
            var processes = new Mock<IProcessFactory>(MockBehavior.Strict);
            var httpClients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
            var torrents = new Torrents(NullLogger<Torrents>.Instance, torrentData, downloads,
                processes.Object, fileSystem, enricher.Object,
                new AllDebridTorrentClient(NullLogger<AllDebridTorrentClient>.Instance,
                    providerFactory.Object, Mock.Of<IDownloadableFileFilter>()));
            var compatibility = new QBittorrentCompatibility(NullLogger<QBittorrentCompatibility>.Instance,
                new Authentication(null!, null!, null!),
                new Settings(new SettingData(context, NullLogger<SettingData>.Instance)),
                torrents, httpClients.Object, fileSystem);
            await using var app = await StartApplication(compatibility, torrents, downloads);
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using (var login = await client.PostAsync("api/v2/auth/login", Form(("username", ""), ("password", ""))))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                Assert.Equal("Ok.", await login.Content.ReadAsStringAsync());
                Assert.Contains("SID=anonymous", Assert.Single(login.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);
            }

            Assert.Equal(QBittorrentController.CompatibleVersion, await client.GetStringAsync("api/v2/app/version"));
            var categories = await client.GetFromJsonAsync<Dictionary<string, QBittorrentCategory>>("api/v2/torrents/categories");
            Assert.Equal("/media/downloads/radarr", categories!["radarr"].SavePath);
            await Add(client);
            var queued = Assert.Single(await torrentData.Get());
            var torrentId = queued.TorrentId;
            Assert.Equal(TorrentFinishedAction.None, queued.FinishedAction);
            Assert.Equal(downloadRoot, queued.LocalDownloadPath);
            Assert.Equal("/media/downloads", queued.ClientReportedDownloadPath);
            Assert.Equal("radarr", queued.Category);
            Assert.Null(queued.Completed);

            // Stand in for provider/download completion; no worker or external network is started.
            var completed = DateTimeOffset.UtcNow;
            await torrentData.UpdateRdId(queued, "123");
            queued.RdName = "Movie.Release";
            queued.RdStatus = TorrentStatus.Finished;
            queued.RdProgress = 100;
            queued.RdSize = 400;
            await torrentData.UpdateRdData(queued);
            var download = await downloads.Add(torrentId, new DownloadInfo
            {
                FileName = "movie.mkv", RestrictedLink = "https://example.invalid/restricted"
            });
            await downloads.UpdateUnrestrictedLink(download.DownloadId, "https://example.invalid/movie.mkv");
            await downloads.UpdateDownloadFinished(download.DownloadId, completed);
            await downloads.UpdateCompleted(download.DownloadId, completed);
            await torrentData.UpdateComplete(torrentId, null, completed, false);
            var jobPath = fileSystem.Path.Combine(downloadRoot, "radarr", "Movie.Release");
            var payloadPath = fileSystem.Path.Combine(jobPath, "movie.mkv");
            var sidecarPath = fileSystem.Path.Combine(jobPath, "movie.nfo");
            fileSystem.AddFile(payloadPath, new MockFileData("media payload"));
            fileSystem.AddFile(sidecarPath, new MockFileData("sidecar"));

            var info = Assert.Single((await client.GetFromJsonAsync<QBittorrentTorrentInfo[]>("api/v2/torrents/info?category=radarr"))!);
            Assert.Equal(Hash, info.Hash);
            Assert.Equal("Movie.Release", info.Name);
            Assert.Equal("radarr", info.Category);
            Assert.Equal(1d, info.Progress);
            Assert.Equal("pausedUP", info.State);
            Assert.Equal("/media/downloads/radarr", info.SavePath);
            Assert.Equal("/media/downloads/radarr/Movie.Release/movie.mkv", info.ContentPath);

            var retainedPayloadPath = payloadPath;
            if (payloadMoved)
            {
                retainedPayloadPath = fileSystem.Path.Combine(downloadRoot, "library", "movie.mkv");
                fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(retainedPayloadPath)!);
                fileSystem.File.Move(payloadPath, retainedPayloadPath);
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var deleted = await client.PostAsync("api/v2/torrents/delete", Form(("hashes", Hash), ("deleteFiles", "false")));
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
                Assert.Empty((await client.GetFromJsonAsync<QBittorrentTorrentInfo[]>("api/v2/torrents/info?category=radarr"))!);
                var retained = Assert.Single((await client.GetFromJsonAsync<Torrent[]>("api/torrents"))!);
                Assert.Equal(torrentId, retained.TorrentId);
                Assert.Equal(Hash, retained.Hash);
                Assert.Equal("radarr", retained.Category);
                Assert.Equal("123", retained.RdId);
                Assert.Equal(completed, retained.Completed);
                Assert.True((await context.Torrents.AsNoTracking().SingleAsync()).QbittorrentHidden);
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Add(client);
                var readded = Assert.Single(await torrentData.Get());
                Assert.Equal(torrentId, readded.TorrentId);
                Assert.Equal("123", readded.RdId);
                Assert.Equal("radarr", readded.Category);
                Assert.Equal(downloadRoot, readded.LocalDownloadPath);
                Assert.False(readded.QbittorrentHidden);
                Assert.False((await context.Torrents.AsNoTracking().SingleAsync()).QbittorrentHidden);
                var readdedDownload = Assert.Single(await downloads.GetForTorrent(torrentId));
                Assert.Equal(download.DownloadId, readdedDownload.DownloadId);
                Assert.Equal(payloadMoved ? null : completed, readded.Completed);
                Assert.Equal(payloadMoved ? null : completed, readdedDownload.Completed);
                var readdedInfo = Assert.Single((await client.GetFromJsonAsync<QBittorrentTorrentInfo[]>("api/v2/torrents/info?category=radarr"))!);
                Assert.Equal(payloadMoved ? "queuedDL" : "pausedUP", readdedInfo.State);
                Assert.Equal(payloadMoved ? 0.5d : 1d, readdedInfo.Progress);
            }

            Assert.Equal("media payload", fileSystem.File.ReadAllText(retainedPayloadPath));
            Assert.Equal("sidecar", fileSystem.File.ReadAllText(sidecarPath));
            Assert.Equal(!payloadMoved, fileSystem.File.Exists(payloadPath));
            Assert.Equal(2, fileSystem.AllFiles.Count());
            Assert.Equal(1, await context.Torrents.CountAsync());
            Assert.Equal(1, await context.Downloads.CountAsync());
            magnets.Verify(api => api.StatusAsync("123", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
            magnets.VerifyNoOtherCalls();
            processes.VerifyNoOtherCalls();
            httpClients.VerifyNoOtherCalls();
        }
        finally
        {
            (Settings.Get.General, Settings.Get.Downloads, Settings.Get.Storage,
                Settings.Get.Integrations, Settings.Get.Provider) = original;
            await TorrentData.VoidCache();
        }
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    private static async Task Add(HttpClient client)
    {
        using var response = await client.PostAsync("api/v2/torrents/add", Form(("urls", MagnetUrl), ("category", "radarr")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ok.", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> StartApplication(
        IQBittorrentCompatibility compatibility, Torrents torrents, Downloads downloads)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(QBittorrentController).Assembly);
        builder.Services.AddSingleton(compatibility);
        builder.Services.AddSingleton(torrents);
        builder.Services.AddSingleton(new TorrentRunner(NullLogger<TorrentRunner>.Instance, torrents, downloads));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.Events.OnRedirectToLogin = AuthenticationRedirects.HandleLogin);
        builder.Services.AddSingleton<IAuthorizationHandler, AuthSettingHandler>();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("AuthSetting", policy => policy.Requirements.Add(new AuthSettingRequirement()));
        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}
