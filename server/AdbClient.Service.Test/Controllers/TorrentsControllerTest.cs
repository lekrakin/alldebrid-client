using System.IO.Abstractions.TestingHelpers;
using System.Text;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Services;
using AdbClient.Service.Wrappers;
using AdbClient.Web.Controllers;
using AdbClient.Web.Models.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace AdbClient.Service.Test.Controllers;

public class TorrentsControllerTest
{
    [Fact]
    public async Task Delete_NoEffects_ReturnsBadRequestWithoutDeleting()
    {
        var torrentData = new Mock<ITorrentData>(MockBehavior.Strict);
        var controller = CreateController(torrentData.Object);

        var result = await controller.Delete(Guid.NewGuid(), new TorrentControllerDeleteRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Select at least one delete action.", badRequest.Value);
        torrentData.Verify(data => data.GetById(It.IsAny<Guid>()), Times.Never);
        torrentData.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Delete_WithAnEffect_InvokesDeletion(bool deleteData, bool deleteRdTorrent, bool deleteLocalFiles)
    {
        var torrentId = Guid.NewGuid();
        var torrentData = new Mock<ITorrentData>(MockBehavior.Strict);
        torrentData.Setup(data => data.GetById(torrentId)).ReturnsAsync((Torrent?)null);
        var controller = CreateController(torrentData.Object);

        var result = await controller.Delete(torrentId, new TorrentControllerDeleteRequest
        {
            DeleteData = deleteData,
            DeleteRdTorrent = deleteRdTorrent,
            DeleteLocalFiles = deleteLocalFiles
        });

        Assert.IsType<OkResult>(result);
        torrentData.Verify(data => data.GetById(torrentId), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upload_InvalidDownloadFilter_ReturnsBadRequest(bool magnet)
    {
        var service = new Torrents(
            Mock.Of<ILogger<Torrents>>(),
            Mock.Of<ITorrentData>(),
            Mock.Of<IDownloads>(),
            Mock.Of<IProcessFactory>(),
            new MockFileSystem(),
            Mock.Of<IEnricher>(),
            null!);
        var controller = new TorrentsController(
            Mock.Of<ILogger<TorrentsController>>(),
            service,
            null!);
        var torrent = new Torrent { IncludeRegex = "[" };

        ActionResult result;

        if (magnet)
        {
            result = await controller.UploadMagnet(new TorrentControllerUploadMagnetRequest
            {
                MagnetLink = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                Torrent = torrent
            });
        }
        else
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not parsed"));
            var file = new FormFile(stream, 0, stream.Length, "file", "test.torrent");
            result = await controller.UploadFile(file, new TorrentControllerUploadFileRequest
            {
                Torrent = torrent
            });
        }

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid include regular expression", Assert.IsType<string>(badRequest.Value));
    }

    private static TorrentsController CreateController(ITorrentData torrentData)
    {
        var service = new Torrents(
            Mock.Of<ILogger<Torrents>>(),
            torrentData,
            Mock.Of<IDownloads>(),
            Mock.Of<IProcessFactory>(),
            new MockFileSystem(),
            Mock.Of<IEnricher>(),
            null!);

        return new TorrentsController(
            Mock.Of<ILogger<TorrentsController>>(),
            service,
            null!);
    }
}
