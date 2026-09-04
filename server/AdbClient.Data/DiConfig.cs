using AdbClient.Data.Data;
using AdbClient.Data.Models.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdbClient.Data;

public static class DiConfig
{
    public static void Config(IServiceCollection services, AppSettings appSettings)
    {
        var dbPath = appSettings.Database?.Path;

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new InvalidOperationException(
                "Application settings must be normalized before configuring the database.");
        }

        var databaseDirectory = Path.GetDirectoryName(dbPath)
                                ?? throw new InvalidOperationException("Database:Path must include a directory.");
        Directory.CreateDirectory(databaseDirectory);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        services.AddDbContext<DataContext>(options => options.UseSqlite(connectionString));

        services.AddScoped<DownloadData>();
        services.AddScoped<SettingData>();
        services.AddScoped<ITorrentData, TorrentData>();
        services.AddScoped<UserData>();
    }
}
