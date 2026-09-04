using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AdbClient.Data.Enums;
using AdbClient.Service.Middleware;
using AdbClient.Service.Models.QBittorrent;
using AdbClient.Service.Services;
using AdbClient.Web;
using AdbClient.Web.Controllers;
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

namespace AdbClient.Service.Test.Controllers;

[Collection(SettingsIsolationCollection.Name)]
public class QBittorrentContractTest
{
    [Fact]
    public async Task LoginAndVersion_MatchLogposeRequests()
    {
        var compatibility = new RecordingCompatibility();
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var login = await client.PostAsync("api/v2/auth/login", Form(
            ("username", "logpose"),
            ("password", "secret")));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("text/plain", login.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Ok.", await login.Content.ReadAsStringAsync());

        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("SID=", cookie, StringComparison.Ordinal);

        using var versionRequest = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version");
        versionRequest.Headers.Add("Cookie", cookie.Split(';')[0]);
        using var version = await client.SendAsync(versionRequest);

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal("text/plain", version.Content.Headers.ContentType?.MediaType);
        Assert.Equal(QBittorrentController.CompatibleVersion, await version.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidationEndpoints_MatchArrClientRequests()
    {
        var compatibility = new RecordingCompatibility
        {
            Preferences = new()
            {
                SavePath = "/media/downloads",
                QueueingEnabled = true
            },
            Categories = new Dictionary<string, QBittorrentCategory>
            {
                ["radarr"] = new()
                {
                    Name = "radarr",
                    SavePath = "/media/downloads/radarr"
                }
            }
        };
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var login = await client.PostAsync("api/v2/auth/login", Form(
            ("username", "arr"),
            ("password", "secret")));
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);

        using var webApiVersion = await client.GetAsync("api/v2/app/webapiVersion");
        Assert.Equal(HttpStatusCode.OK, webApiVersion.StatusCode);
        Assert.True(Version.TryParse(await webApiVersion.Content.ReadAsStringAsync(), out _));
        Assert.Equal(
            QBittorrentController.CompatibleWebApiVersion,
            await webApiVersion.Content.ReadAsStringAsync());

        using var preferences = await client.GetAsync("api/v2/app/preferences");
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        using var preferencesJson = JsonDocument.Parse(await preferences.Content.ReadAsStringAsync());
        Assert.Equal("/media/downloads", preferencesJson.RootElement.GetProperty("save_path").GetString());
        Assert.True(preferencesJson.RootElement.GetProperty("dht").GetBoolean());
        Assert.False(preferencesJson.RootElement.GetProperty("max_ratio_enabled").GetBoolean());
        Assert.True(preferencesJson.RootElement.GetProperty("queueing_enabled").GetBoolean());

        using var categories = await client.GetAsync("api/v2/torrents/categories");
        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
        using var categoriesJson = JsonDocument.Parse(await categories.Content.ReadAsStringAsync());
        Assert.Equal(
            "/media/downloads/radarr",
            categoriesJson.RootElement.GetProperty("radarr").GetProperty("savePath").GetString());

        using var torrents = await client.GetAsync("api/v2/torrents/info?category=radarr");
        Assert.Equal(HttpStatusCode.OK, torrents.StatusCode);
        Assert.Equal("radarr", compatibility.RequestedCategory);
    }

    [Fact]
    public async Task WebApiVersion_IsAvailableBeforeArrAuthenticationProbe()
    {
        var originalAuthenticationType = Settings.Get.General.AuthenticationType;

        try
        {
            Settings.Get.General.AuthenticationType = AuthenticationType.UserNamePassword;
            await using var app = await StartApplication(new RecordingCompatibility());
            using var client = CreateClient(app);

            using var response = await client.GetAsync("api/v2/app/webapiVersion");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                QBittorrentController.CompatibleWebApiVersion,
                await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Settings.Get.General.AuthenticationType = originalAuthenticationType;
        }
    }

    [Fact]
    public async Task ExpiredSession_ReturnsForbiddenAndCanReauthenticate()
    {
        var originalAuthenticationType = Settings.Get.General.AuthenticationType;

        try
        {
            Settings.Get.General.AuthenticationType = AuthenticationType.UserNamePassword;
            var compatibility = new RecordingCompatibility();
            await using var app = await StartApplication(compatibility);
            var httpContextAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();
            compatibility.LoginHandler = async (userName, password) =>
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, userName)],
                    CookieAuthenticationDefaults.AuthenticationScheme);
                await httpContextAccessor.HttpContext!.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));
                return password == "secret";
            };
            using var client = CreateClient(app);

            using var staleRequest = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/preferences");
            staleRequest.Headers.Add("Cookie", "SID=expired");
            using var staleResponse = await client.SendAsync(staleRequest);

            Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);

            using var login = await client.PostAsync("api/v2/auth/login", Form(
                ("username", "arr"),
                ("password", "secret")));
            var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/preferences");
            retryRequest.Headers.Add("Cookie", cookie);
            using var retryResponse = await client.SendAsync(retryRequest);

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        }
        finally
        {
            Settings.Get.General.AuthenticationType = originalAuthenticationType;
        }
    }

    [Fact]
    public async Task AddTorrentFile_MatchesArrMultipartRequest()
    {
        var torrentBytes = new byte[] { 1, 2, 3, 4 };
        var compatibility = new RecordingCompatibility();
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(torrentBytes), "torrents", "release.torrent");
        content.Add(new StringContent("radarr"), "category");
        content.Add(new StringContent("false"), "paused");
        content.Add(new StringContent("1.5"), "ratioLimit");

        using var response = await client.PostAsync("api/v2/torrents/add", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ok.", await response.Content.ReadAsStringAsync());
        Assert.NotNull(compatibility.UploadedTorrent);
        Assert.Equal(torrentBytes, compatibility.UploadedTorrent.Value.Bytes);
        Assert.Equal("radarr", compatibility.UploadedTorrent.Value.Category);
    }

    [Fact]
    public async Task AddInvalidTorrentFile_ReturnsQbittorrentStatusCode()
    {
        var compatibility = new RecordingCompatibility
        {
            AddFileException = new InvalidDataException("Invalid torrent file.")
        };
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3, 4]), "torrents", "invalid.torrent");

        using var response = await client.PostAsync("api/v2/torrents/add", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Null(compatibility.UploadedTorrent);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("sequentialDownload")]
    [InlineData("firstLastPiecePrio")]
    public async Task AddUnsupportedOperationalMode_IsRejectedBeforeQueueing(string option)
    {
        var compatibility = new RecordingCompatibility();
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var response = await client.PostAsync("api/v2/torrents/add", Form(
            ("urls", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
            ("category", "radarr"),
            (option, "true")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(compatibility.AddedTorrent);
    }

    [Fact]
    public async Task AddExistingTorrentInDifferentCategory_ReturnsConflict()
    {
        var compatibility = new RecordingCompatibility
        {
            AddUrlException = new InvalidOperationException("Torrent already exists under a different category.")
        };
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var response = await client.PostAsync("api/v2/torrents/add", Form(
            ("urls", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
            ("category", "radarr")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(compatibility.AddedTorrent);
    }

    [Fact]
    public async Task MonitoringEndpoints_MatchArrRequests()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var compatibility = new RecordingCompatibility
        {
            Torrents =
            [
                new()
                {
                    Hash = hash,
                    Name = "Series.Release",
                    Category = "sonarr",
                    Progress = 1,
                    State = "pausedUP",
                    ContentPath = "/media/downloads/sonarr/Series.Release",
                    SavePath = "/media/downloads/sonarr",
                    RatioLimit = 0
                }
            ],
            Properties = new()
            {
                Hash = hash,
                SavePath = "/media/downloads/sonarr",
                SeedingTime = 0
            },
            Files =
            [
                new() { Name = "Series/Season 01/episode.mkv" }
            ]
        };
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var info = await client.GetAsync("api/v2/torrents/info?category=sonarr");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        using var infoJson = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        var torrent = Assert.Single(infoJson.RootElement.EnumerateArray());
        Assert.Equal("pausedUP", torrent.GetProperty("state").GetString());
        Assert.Equal(0, torrent.GetProperty("ratio_limit").GetDouble());

        using var properties = await client.GetAsync($"api/v2/torrents/properties?hash={hash}");
        Assert.Equal(HttpStatusCode.OK, properties.StatusCode);
        using var propertiesJson = JsonDocument.Parse(await properties.Content.ReadAsStringAsync());
        Assert.Equal("/media/downloads/sonarr", propertiesJson.RootElement.GetProperty("save_path").GetString());

        using var files = await client.GetAsync($"api/v2/torrents/files?hash={hash}");
        Assert.Equal(HttpStatusCode.OK, files.StatusCode);
        using var filesJson = JsonDocument.Parse(await files.Content.ReadAsStringAsync());
        Assert.Equal("Series/Season 01/episode.mkv", filesJson.RootElement[0].GetProperty("name").GetString());

        using var setCategory = await client.PostAsync("api/v2/torrents/setCategory", Form(
            ("hashes", hash),
            ("category", "sonarr-imported")));
        Assert.Equal(HttpStatusCode.OK, setCategory.StatusCode);
        Assert.Equal((hash, "sonarr-imported"), compatibility.UpdatedCategory);

        using var topPriority = await client.PostAsync("api/v2/torrents/topPrio", Form(("hashes", hash)));
        Assert.Equal(HttpStatusCode.OK, topPriority.StatusCode);
        Assert.Equal(hash, compatibility.PrioritizedHashes);

        using var shareLimits = await client.PostAsync("api/v2/torrents/setShareLimits", Form(
            ("hashes", hash),
            ("ratioLimit", "1.5")));
        Assert.Equal(HttpStatusCode.OK, shareLimits.StatusCode);
    }

    [Fact]
    public async Task TorrentLifecycle_MatchesLogposeRequestsAndResponseFields()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=One%20Pace";

        var compatibility = new RecordingCompatibility
        {
            Torrents =
            [
                new()
                {
                    Hash = hash,
                    Name = "One Pace Episode 01",
                    Category = "logpose",
                    Progress = 0.75,
                    State = "downloading",
                    ContentPath = "/downloads/logpose/One Pace Episode 01/episode.mkv",
                    SavePath = "/downloads/logpose",
                    Size = 1_000,
                    DownloadSpeed = 250,
                    Eta = 1
                }
            ]
        };

        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var login = await client.PostAsync("api/v2/auth/login", Form(
            ("username", "logpose"),
            ("password", "secret")));
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);

        using var createCategory = await client.PostAsync("api/v2/torrents/createCategory", Form(
            ("category", "logpose"),
            ("savePath", string.Empty)));
        Assert.Equal(HttpStatusCode.OK, createCategory.StatusCode);
        Assert.Equal("logpose", compatibility.CreatedCategory);

        using var add = await client.PostAsync("api/v2/torrents/add", Form(
            ("urls", magnet),
            ("category", "logpose")));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal("Ok.", await add.Content.ReadAsStringAsync());
        Assert.Equal((magnet, "logpose"), compatibility.AddedTorrent);

        using var info = await client.GetAsync("api/v2/torrents/info?category=logpose");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.Equal("logpose", compatibility.RequestedCategory);

        using var document = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        var torrent = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(hash, torrent.GetProperty("hash").GetString());
        Assert.Equal("One Pace Episode 01", torrent.GetProperty("name").GetString());
        Assert.Equal("logpose", torrent.GetProperty("category").GetString());
        Assert.Equal(0.75, torrent.GetProperty("progress").GetDouble());
        Assert.Equal("downloading", torrent.GetProperty("state").GetString());
        Assert.Equal("/downloads/logpose/One Pace Episode 01/episode.mkv", torrent.GetProperty("content_path").GetString());
        Assert.Equal("/downloads/logpose", torrent.GetProperty("save_path").GetString());
        Assert.Equal(1_000, torrent.GetProperty("size").GetInt64());
        Assert.Equal(250, torrent.GetProperty("dlspeed").GetInt64());
        Assert.Equal(1, torrent.GetProperty("eta").GetInt64());

        using var delete = await client.PostAsync("api/v2/torrents/delete", Form(
            ("hashes", hash),
            ("deleteFiles", "false")));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal((hash, false), compatibility.DeletedTorrent);
    }

    [Theory]
    [InlineData("https://nyaa.si/?q=a031cac6baf81b804c4d034dfaef0e5e4a671145")]
    [InlineData("https://nyaa.si/?q=A031CAC6BAF81B804C4D034DFAEF0E5E4A671145")]
    public async Task AddNyaaInfoHashSearchUrl_MatchesLogposeRequest(string searchUrl)
    {
        var compatibility = new RecordingCompatibility();
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var add = await client.PostAsync("api/v2/torrents/add", Form(
            ("urls", searchUrl),
            ("category", "logpose")));

        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal("Ok.", await add.Content.ReadAsStringAsync());
        Assert.Equal((searchUrl, "logpose"), compatibility.AddedTorrent);
    }

    [Fact]
    public async Task DeleteRetry_RemainsIdempotentForLogpose()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var compatibility = new RecordingCompatibility();
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var delete = await client.PostAsync("api/v2/torrents/delete", Form(
                ("hashes", hash),
                ("deleteFiles", "false")));
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        Assert.Equal(2, compatibility.DeleteCount);
        Assert.Equal((hash, false), compatibility.DeletedTorrent);
    }

    [Fact]
    public async Task DeleteAll_IsRejectedWithoutTouchingTorrentData()
    {
        var compatibility = new RecordingCompatibility();
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var response = await client.PostAsync("api/v2/torrents/delete", Form(
            ("hashes", "all"),
            ("deleteFiles", "true")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(compatibility.DeletedTorrent);
    }

    [Fact]
    public async Task DeleteUnsafeLocalPath_ReturnsConflictWithoutMutation()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var compatibility = new RecordingCompatibility
        {
            DeleteException = new InvalidDataException("Refusing to delete an unsafe torrent download path.")
        };
        await using var app = await StartApplication(compatibility);
        using var client = CreateClient(app);

        using var response = await client.PostAsync("api/v2/torrents/delete", Form(
            ("hashes", hash),
            ("deleteFiles", "true")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(compatibility.DeletedTorrent);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
    {
        return new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));
    }

    private static async Task<WebApplication> StartApplication(IQBittorrentCompatibility compatibility)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(QBittorrentController).Assembly);
        builder.Services.AddSingleton(compatibility);
        builder.Services.AddHttpContextAccessor();
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
        app.MapControllers();
        await app.StartAsync();

        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);

        return new(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new(address)
        };
    }

    private sealed class RecordingCompatibility : IQBittorrentCompatibility
    {
        public IReadOnlyList<QBittorrentTorrentInfo> Torrents { get; init; } = [];
        public QBittorrentPreferences Preferences { get; init; } = new() { SavePath = "/downloads" };
        public IReadOnlyDictionary<string, QBittorrentCategory> Categories { get; init; } =
            new Dictionary<string, QBittorrentCategory>();
        public QBittorrentTorrentProperties? Properties { get; init; }
        public IReadOnlyList<QBittorrentTorrentFile>? Files { get; init; }
        public Exception? AddUrlException { get; init; }
        public Exception? AddFileException { get; init; }
        public Exception? DeleteException { get; init; }
        public string? CreatedCategory { get; private set; }
        public (string Urls, string? Category)? AddedTorrent { get; private set; }
        public (byte[] Bytes, string? Category)? UploadedTorrent { get; private set; }
        public string? RequestedCategory { get; private set; }
        public (string Hashes, string? Category)? UpdatedCategory { get; private set; }
        public string? PrioritizedHashes { get; private set; }
        public (string Hashes, bool DeleteFiles)? DeletedTorrent { get; private set; }
        public int DeleteCount { get; private set; }
        public Func<string, string, Task<bool>>? LoginHandler { get; set; }

        public Task<bool> Login(string userName, string password)
        {
            return LoginHandler?.Invoke(userName, password) ?? Task.FromResult(true);
        }

        public QBittorrentPreferences GetPreferences()
        {
            return Preferences;
        }

        public Task<IReadOnlyDictionary<string, QBittorrentCategory>> GetCategories()
        {
            return Task.FromResult(Categories);
        }

        public Task CreateCategory(string category)
        {
            CreatedCategory = category;
            return Task.CompletedTask;
        }

        public Task Add(string urls, string? category, CancellationToken cancellationToken = default)
        {
            if (AddUrlException != null)
            {
                return Task.FromException(AddUrlException);
            }

            AddedTorrent = (urls, category);
            return Task.CompletedTask;
        }

        public Task Add(byte[] torrentBytes, string? category)
        {
            if (AddFileException != null)
            {
                return Task.FromException(AddFileException);
            }

            UploadedTorrent = (torrentBytes, category);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QBittorrentTorrentInfo>> GetTorrents(string? category)
        {
            RequestedCategory = category;
            return Task.FromResult(Torrents);
        }

        public Task<QBittorrentTorrentProperties?> GetProperties(string hash)
        {
            return Task.FromResult(Properties);
        }

        public Task<IReadOnlyList<QBittorrentTorrentFile>?> GetFiles(string hash)
        {
            return Task.FromResult(Files);
        }

        public Task SetCategory(string hashes, string? category)
        {
            UpdatedCategory = (hashes, category);
            return Task.CompletedTask;
        }

        public Task SetTopPriority(string hashes)
        {
            PrioritizedHashes = hashes;
            return Task.CompletedTask;
        }

        public Task Delete(string hashes, bool deleteFiles)
        {
            if (DeleteException != null)
            {
                return Task.FromException(DeleteException);
            }

            DeletedTorrent = (hashes, deleteFiles);
            DeleteCount++;
            return Task.CompletedTask;
        }
    }
}
