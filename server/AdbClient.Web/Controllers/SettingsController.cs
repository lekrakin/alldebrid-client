using System.Diagnostics;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Helpers;
using AdbClient.Service.Services;
using AdbClient.Web.Models.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdbClient.Web.Controllers;

[Authorize(Policy = "AuthSetting")]
[Route("Api/Settings")]
public class SettingsController : Controller
{
    private const string DefaultDownloadSpeedTestUrl = "https://speed.cloudflare.com/__down?bytes=52428800";
    private const int DefaultWriteTestFileSize = 64 * 1024 * 1024;
    private const int TemporaryNameAttempts = 10;

    private readonly string _downloadSpeedTestUrl;
    private readonly int _writeTestFileSize;
    private readonly Settings _settings;
    private readonly Torrents _torrents;

    public SettingsController(Settings settings, Torrents torrents)
        : this(settings, torrents, DefaultDownloadSpeedTestUrl, DefaultWriteTestFileSize)
    {
    }

    protected SettingsController(
        Settings settings,
        Torrents torrents,
        string downloadSpeedTestUrl,
        int writeTestFileSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadSpeedTestUrl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(writeTestFileSize);

        _settings = settings;
        _torrents = torrents;
        _downloadSpeedTestUrl = downloadSpeedTestUrl;
        _writeTestFileSize = writeTestFileSize;
    }

    [HttpGet]
    [Route("")]
    public ActionResult Get()
    {
        var result = SettingData.GetAll();
        return Ok(result);
    }

    [HttpPut]
    [Route("")]
    public async Task<ActionResult> Update([FromBody] IList<SettingProperty>? settings1)
    {
        if (settings1 == null)
        {
            return BadRequest();
        }

        try
        {
            await _settings.Update(settings1);
            return Ok();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet]
    [Route("Profile")]
    public async Task<ActionResult<Profile>> Profile()
    {
        try
        {
            var profile = await _torrents.GetProfile();
            return Ok(profile);
        }
        catch (Exception ex) when (ex.Message.Contains("API Key not set"))
        {
            return Ok((Profile?)null);
        }
    }

    [HttpGet]
    [Route("Version")]
    public ActionResult<Version> Version()
    {
        return Ok(new
        {
            Version = ApplicationVersion.CurrentText ?? "unknown"
        });
    }

    [HttpPost]
    [Route("TestPath")]
    public async Task<ActionResult> TestPath(
        [FromBody] SettingsControllerTestPathRequest? request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (string.IsNullOrEmpty(request.Path))
        {
            return BadRequest("Invalid path");
        }

        var path = Path.TrimEndingDirectorySeparator(request.Path.Trim());

        if (!Directory.Exists(path))
        {
            throw new Exception($"Path {path} does not exist");
        }

        string? testFilePath = null;

        try
        {
            await using (var fileStream = CreateTemporaryFile(path, "path-test", out testFilePath))
            {
                await fileStream.WriteAsync(
                    "AllDebrid Client path test."u8.ToArray(),
                    cancellationToken);
                await fileStream.FlushAsync(cancellationToken);
            }

            return Ok();
        }
        finally
        {
            if (testFilePath != null)
            {
                await FileHelper.Delete(testFilePath);
            }
        }
    }

    [HttpGet]
    [Route("TestDownloadSpeed")]
    public async Task<ActionResult> TestDownloadSpeed(CancellationToken cancellationToken)
    {
        var downloadPath = Settings.Get.Storage.DownloadPath;

        if (string.IsNullOrWhiteSpace(downloadPath) || !Directory.Exists(downloadPath))
        {
            throw new DirectoryNotFoundException($"Download path {downloadPath} does not exist.");
        }

        var testDirectory = CreateTemporaryDirectory(downloadPath, "download-test");
        DownloadClient? downloadClient = null;

        try
        {
            var download = new Download
            {
                Link = _downloadSpeedTestUrl,
                FileName = "payload.bin",
                Torrent = new()
                {
                    DownloadClient = AdbClient.Data.Enums.DownloadClient.Internal,
                    RdName = "transfer"
                }
            };

            downloadClient = new DownloadClient(download, download.Torrent, testDirectory);
            await downloadClient.Start();

            var completion = await downloadClient.WaitForCompletionAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(completion.Error))
            {
                throw new IOException($"The download speed test failed: {completion.Error}");
            }

            return Ok(downloadClient.Speed);
        }
        finally
        {
            try
            {
                if (downloadClient != null)
                {
                    await downloadClient.Cancel();
                }
            }
            finally
            {
                await FileHelper.DeleteDirectory(testDirectory);
            }
        }
    }

    [HttpGet]
    [Route("TestWriteSpeed")]
    public async Task<ActionResult> TestWriteSpeed(CancellationToken cancellationToken)
    {
        var downloadPath = Settings.Get.Storage.DownloadPath;

        if (string.IsNullOrWhiteSpace(downloadPath) || !Directory.Exists(downloadPath))
        {
            throw new DirectoryNotFoundException($"Download path {downloadPath} does not exist.");
        }

        string? testFilePath = null;

        try
        {
            var watch = Stopwatch.StartNew();
            long bytesWritten;

            await using (var fileStream = CreateTemporaryFile(downloadPath, "write-test", out testFilePath))
            {
                var buffer = new byte[64 * 1024];

                while (fileStream.Length < _writeTestFileSize)
                {
                    Random.Shared.NextBytes(buffer);
                    var count = (int)Math.Min(buffer.Length, _writeTestFileSize - fileStream.Length);
                    await fileStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }

                await fileStream.FlushAsync(cancellationToken);
                bytesWritten = fileStream.Length;
            }

            watch.Stop();
            return Ok(bytesWritten / watch.Elapsed.TotalSeconds);
        }
        finally
        {
            if (testFilePath != null)
            {
                await FileHelper.Delete(testFilePath);
            }
        }
    }

    private static FileStream CreateTemporaryFile(string directory, string purpose, out string path)
    {
        path = string.Empty;

        for (var attempt = 0; attempt < TemporaryNameAttempts; attempt++)
        {
            path = Path.Combine(directory, $".adbclient-{purpose}-{Guid.NewGuid():N}.tmp");

            try
            {
                return new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (System.IO.File.Exists(path))
            {
            }
        }

        throw new IOException($"Unable to reserve a temporary file in {directory}.");
    }

    private static string CreateTemporaryDirectory(string directory, string purpose)
    {
        for (var attempt = 0; attempt < TemporaryNameAttempts; attempt++)
        {
            var path = Path.Combine(directory, $".adbclient-{purpose}-{Guid.NewGuid():N}");

            if (Directory.Exists(path) || System.IO.File.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (IOException) when (Directory.Exists(path) || System.IO.File.Exists(path))
            {
            }
        }

        throw new IOException($"Unable to reserve a temporary directory in {directory}.");
    }
}
