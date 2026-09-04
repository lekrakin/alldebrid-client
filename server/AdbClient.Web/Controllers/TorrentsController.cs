using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MonoTorrent;
using AdbClient.Data.Helpers;
using AdbClient.Data.Models.TorrentClient;
using AdbClient.Service.Helpers;
using AdbClient.Service.Services;
using AdbClient.Web.Models.Requests;
using Torrent = AdbClient.Data.Models.Data.Torrent;

namespace AdbClient.Web.Controllers;

[Authorize(Policy = "AuthSetting")]
[Route("Api/Torrents")]
public class TorrentsController(ILogger<TorrentsController> logger, Torrents torrents, TorrentRunner torrentRunner) : Controller
{
    [HttpGet]
    [Route("")]
    public async Task<ActionResult<IList<Torrent>>> GetAll()
    {
        var results = await torrents.Get();

        // Prevent infinite recursion when serializing
        foreach (var file in results.SelectMany(torrent => torrent.Downloads))
        {
            file.Torrent = null;
        }

        return Ok(results);
    }

    [HttpGet]
    [Route("Get/{torrentId:guid}")]
    public async Task<ActionResult<Torrent>> GetById(Guid torrentId)
    {
        var torrent = await torrents.GetById(torrentId);

        if (torrent?.Downloads != null)
        {
            foreach (var file in torrent.Downloads)
            {
                file.Torrent = null;
            }
        }

        return Ok(torrent);
    }

    /// <summary>
    ///     Used for debugging only. Force a tick.
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Route("Tick")]
    public async Task<ActionResult> Tick()
    {
        await torrentRunner.Tick();

        return Ok();
    }

    [HttpPost]
    [Route("UploadFile")]
    public async Task<ActionResult> UploadFile([FromForm] IFormFile? file,
                                               [ModelBinder(BinderType = typeof(JsonModelBinder))]
                                               TorrentControllerUploadFileRequest? formData)
    {
        if (file == null || file.Length <= 0)
        {
            return BadRequest("Invalid torrent file");
        }

        if (formData?.Torrent == null)
        {
            return BadRequest("Invalid Torrent");
        }

        logger.LogDebug($"Add file");

        await using var fileStream = file.OpenReadStream();

        await using var memoryStream = new MemoryStream();

        await fileStream.CopyToAsync(memoryStream);

        var bytes = memoryStream.ToArray();

        await torrents.AddFileToDebridQueue(bytes, formData.Torrent);

        return Ok();
    }

    [HttpPost]
    [Route("UploadMagnet")]
    public async Task<ActionResult> UploadMagnet([FromBody] TorrentControllerUploadMagnetRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (string.IsNullOrEmpty(request.MagnetLink))
        {
            return BadRequest("Invalid magnet link");
        }

        if (request.Torrent == null)
        {
            return BadRequest("Invalid Torrent");
        }

        logger.LogDebug($"Add magnet");

        await torrents.AddMagnetToDebridQueue(request.MagnetLink, request.Torrent);

        return Ok();
    }

    [HttpPost]
    [Route("CheckFiles")]
    public async Task<ActionResult> CheckFiles([FromForm] IFormFile? file)
    {
        if (file == null || file.Length <= 0)
        {
            return BadRequest("Invalid torrent file");
        }

        await using var fileStream = file.OpenReadStream();

        await using var memoryStream = new MemoryStream();

        await fileStream.CopyToAsync(memoryStream);

        var bytes = memoryStream.ToArray();

        var torrent = await MonoTorrent.Torrent.LoadAsync(bytes);

        var result = await torrents.GetAvailableFiles(torrent.InfoHashes.V1OrV2.ToHex());

        return Ok(result);
    }

    [HttpPost]
    [Route("CheckFilesMagnet")]
    public async Task<ActionResult> CheckFilesMagnet([FromBody] TorrentControllerCheckFilesRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (string.IsNullOrEmpty(request.MagnetLink))
        {
            return BadRequest("MagnetLink cannot be null or empty");
        }

        var magnet = MagnetLink.Parse(request.MagnetLink);

        var result = await torrents.GetAvailableFiles(magnet.InfoHashes.V1OrV2.ToHex());

        return Ok(result);
    }

    [HttpPost]
    [Route("Delete/{torrentId:guid}")]
    public async Task<ActionResult> Delete(Guid torrentId, [FromBody] TorrentControllerDeleteRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        logger.LogDebug("Delete {torrentId}", torrentId);

        await torrents.Delete(torrentId, request.DeleteData, request.DeleteRdTorrent, request.DeleteLocalFiles);

        return Ok();
    }

    [HttpPost]
    [Route("Retry/{torrentId:guid}")]
    public async Task<ActionResult> Retry(Guid torrentId)
    {
        logger.LogDebug("Retry {torrentId}", torrentId);

        await torrents.UpdateRetry(torrentId, DateTimeOffset.UtcNow, 0);
        await torrents.RetryTorrent(torrentId, 0);

        return Ok();
    }

    [HttpPost]
    [Route("RetryDownload/{downloadId:guid}")]
    public async Task<ActionResult> RetryDownload(Guid downloadId)
    {
        logger.LogDebug("Retry download {downloadId}", downloadId);

        await torrents.RetryDownload(downloadId);

        return Ok();
    }

    [HttpPut]
    [Route("Update")]
    public async Task<ActionResult> Update([FromBody] Torrent? torrent)
    {
        if (torrent == null)
        {
            return BadRequest();
        }

        await torrents.Update(torrent);

        return Ok();
    }

    [HttpPost]
    [Route("VerifyRegex")]
    public async Task<ActionResult> VerifyRegex([FromForm] IFormFile? file, [FromBody] TorrentControllerVerifyRegexRequest? request)
    {
        if (request == null)
        {
            return Ok();
        }

        var includeError = "";
        var excludeError = "";

        IList<TorrentClientAvailableFile> availableFiles;

        if (!string.IsNullOrWhiteSpace(request.MagnetLink))
        {
            var magnet = MagnetLink.Parse(request.MagnetLink);

            availableFiles = await torrents.GetAvailableFiles(magnet.InfoHashes.V1OrV2.ToHex());
        }
        else if (file != null)
        {
            await using var fileStream = file.OpenReadStream();

            await using var memoryStream = new MemoryStream();

            await fileStream.CopyToAsync(memoryStream);

            var bytes = memoryStream.ToArray();

            var torrent = await MonoTorrent.Torrent.LoadAsync(bytes);

            availableFiles = await torrents.GetAvailableFiles(torrent.InfoHashes.V1OrV2.ToHex());
        }
        else
        {
            return BadRequest();
        }

        var selectedFiles = new List<TorrentClientAvailableFile>();

        var includePattern = !string.IsNullOrWhiteSpace(request.IncludeRegex) ? request.IncludeRegex : null;
        var excludePattern = !string.IsNullOrWhiteSpace(request.ExcludeRegex) ? request.ExcludeRegex : null;
        var pattern = includePattern ?? excludePattern;

        if (pattern == null)
        {
            selectedFiles = [.. availableFiles];
        }
        else
        {
            try
            {
                var regex = BoundedRegex.Create(pattern);
                var includeMatches = includePattern != null;
                selectedFiles = availableFiles
                               .Where(file => regex.IsMatch(file.Filename) == includeMatches)
                               .ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                if (includePattern != null)
                {
                    includeError = BoundedRegex.TimeoutError;
                }
                else
                {
                    excludeError = BoundedRegex.TimeoutError;
                }
            }
            catch (ArgumentException ex)
            {
                var error = $"Invalid regular expression: {ex.Message}";

                if (includePattern != null)
                {
                    includeError = error;
                }
                else
                {
                    excludeError = error;
                }
            }
        }

        return Ok(new
        {
            includeError,
            excludeError,
            selectedFiles
        });
    }
}
