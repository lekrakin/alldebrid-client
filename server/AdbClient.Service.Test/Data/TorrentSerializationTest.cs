using System.Buffers;
using System.Text.Json;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.EntityFrameworkCore;

namespace AdbClient.Service.Test.Data;

public class TorrentSerializationTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignalRUpdate_ReportsClientRemovalWithoutChangingCategory(bool hidden)
    {
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            Category = "radarr",
            QbittorrentHidden = hidden,
            RdSeeders = 7,
            LocalDownloadPath = "/private/downloads",
            ClientReportedDownloadPath = "/downloads"
        };
        var message = new InvocationMessage("update", [new[] { torrent }]);
        var buffer = new ArrayBufferWriter<byte>();
        new JsonHubProtocol().WriteMessage(message, buffer);

        // SignalR's JSON record separator is not part of the JSON document.
        Assert.Equal(0x1e, buffer.WrittenSpan[^1]);
        using var json = JsonDocument.Parse(buffer.WrittenMemory[..^1]);
        var row = json.RootElement.GetProperty("arguments")[0][0];

        Assert.Equal(hidden, row.GetProperty("externalClientRemoved").GetBoolean());
        Assert.Equal("radarr", row.GetProperty("category").GetString());
        Assert.False(row.TryGetProperty("qbittorrentHidden", out _));
        Assert.False(row.TryGetProperty("localDownloadPath", out _));
        Assert.False(row.TryGetProperty("clientReportedDownloadPath", out _));
        Assert.False(row.TryGetProperty("rdSeeders", out _));
        Assert.Equal(hidden, torrent.QbittorrentHidden);
        Assert.Equal("radarr", torrent.Category);
        Assert.Equal(7, torrent.RdSeeders);
    }

    [Fact]
    public void Deserialize_CannotSetClientRemovalOrInternalVisibility()
    {
        const string json = """
            { "category": "radarr", "externalClientRemoved": true, "qbittorrentHidden": true }
            """;
        var torrent = JsonSerializer.Deserialize<Torrent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(torrent);
        Assert.False(torrent.QbittorrentHidden);
        Assert.False(torrent.ExternalClientRemoved);
        Assert.Equal("radarr", torrent.Category);
        Assert.False(typeof(Torrent).GetProperty(nameof(Torrent.ExternalClientRemoved))!.CanWrite);
    }

    [Fact]
    public void ClientRemoval_IsDerivedWithoutAddingAPersistentColumn()
    {
        using var context = new DataContext(new DbContextOptionsBuilder<DataContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var entity = context.Model.FindEntityType(typeof(Torrent))!;

        Assert.Null(entity.FindProperty(nameof(Torrent.ExternalClientRemoved)));
        Assert.NotNull(entity.FindProperty(nameof(Torrent.QbittorrentHidden)));

        var torrent = new Torrent();
        Assert.False(torrent.ExternalClientRemoved);
        torrent.QbittorrentHidden = true;
        Assert.True(torrent.ExternalClientRemoved);
        torrent.QbittorrentHidden = false;
        Assert.False(torrent.ExternalClientRemoved);
    }
}
