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
public class SettingsController(Settings settings, Torrents torrents) : Controller
{
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
            await settings.Update(settings1);
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
            var profile = await torrents.GetProfile();
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
    public async Task<ActionResult> TestPath([FromBody] SettingsControllerTestPathRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (string.IsNullOrEmpty(request.Path))
        {
            return BadRequest("Invalid path");
        }

        var path = request.Path.TrimEnd('/').TrimEnd('\\');

        if (!Directory.Exists(path))
        {
            throw new Exception($"Path {path} does not exist");
        }

        var testFile = $"{path}/test.txt";

        await System.IO.File.WriteAllTextAsync(testFile, "AllDebrid Client test file; you can remove this file.");

        await FileHelper.Delete(testFile);

        return Ok();
    }

    [HttpGet]
    [Route("TestDownloadSpeed")]
    public async Task<ActionResult> TestDownloadSpeed(CancellationToken cancellationToken)
    {
        var downloadPath = Settings.Get.Paths.DownloadPath;

        var testFilePath = Path.Combine(downloadPath, "speed-test.bin");

        await FileHelper.Delete(testFilePath);

        try
        {
            var download = new Download
            {
                Link = "https://speed.cloudflare.com/__down?bytes=52428800",
                FileName = "speed-test.bin",
                Torrent = new()
                {
                    DownloadClient = AdbClient.Data.Enums.DownloadClient.Internal,
                    RdName = "speed-test.bin"
                }
            };

            var downloadClient = new DownloadClient(download, download.Torrent, downloadPath);
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                _ = downloadClient.Cancel();
            });

            await downloadClient.Start();

            return Ok(downloadClient.Speed);
        }
        finally
        {
            await FileHelper.Delete(testFilePath);
        }
    }

    [HttpGet]
    [Route("TestWriteSpeed")]
    public async Task<ActionResult> TestWriteSpeed()
    {
        var downloadPath = Settings.Get.Paths.DownloadPath;

        var testFilePath = Path.Combine(downloadPath, "test.tmp");

        await FileHelper.Delete(testFilePath);

        const int testFileSize = 64 * 1024 * 1024;

        var watch = new Stopwatch();

        watch.Start();

        var rnd = new Random();

        await using var fileStream = new FileStream(testFilePath, FileMode.Create, FileAccess.Write, FileShare.Write);

        var buffer = new byte[64 * 1024];

        while (fileStream.Length < testFileSize)
        {
            rnd.NextBytes(buffer);

            await fileStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
        }

        watch.Stop();

        var writeSpeed = fileStream.Length / watch.Elapsed.TotalSeconds;

        fileStream.Close();

        await FileHelper.Delete(testFilePath);

        return Ok(writeSpeed);
    }

}
