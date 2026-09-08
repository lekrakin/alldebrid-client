using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdbClient.Service.Middleware;

public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    private static readonly HashSet<string> LoggedEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v2/auth/login",
        "/api/v2/app/version",
        "/api/v2/app/webapiVersion",
        "/api/v2/app/preferences",
        "/api/v2/app/defaultSavePath",
        "/api/v2/torrents/categories",
        "/api/v2/torrents/createCategory",
        "/api/v2/torrents/add",
        "/api/v2/torrents/info",
        "/api/v2/torrents/properties",
        "/api/v2/torrents/files",
        "/api/v2/torrents/setCategory",
        "/api/v2/torrents/topPrio",
        "/api/v2/torrents/setShareLimits",
        "/api/v2/torrents/setForceStart",
        "/api/v2/torrents/delete",
        "/api/torrents",
        "/api/torrents/tick",
        "/api/torrents/uploadFile",
        "/api/torrents/uploadMagnet",
        "/api/torrents/checkFiles",
        "/api/torrents/checkFilesMagnet",
        "/api/torrents/update",
        "/api/torrents/verifyRegex"
    };

    private static readonly HashSet<string> LoggedTorrentIdActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "get",
        "delete",
        "retry",
        "retryDownload"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var loggedPath = logger.IsEnabled(LogLevel.Debug)
            ? GetLoggedPath(context.Request.Path)
            : null;

        if (loggedPath is null)
        {
            await next(context);

            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            logger.LogDebug(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds:0.###} ms",
                context.Request.Method,
                loggedPath,
                context.Response.StatusCode,
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static string? GetLoggedPath(PathString path)
    {
        var value = path.Value?.TrimEnd('/');

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (LoggedEndpoints.Contains(value))
        {
            return value;
        }

        if (!path.StartsWithSegments("/api/torrents", out var remaining))
        {
            return null;
        }

        var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments is [var action, var id]
               && LoggedTorrentIdActions.Contains(action)
               && Guid.TryParse(id, out _)
            ? $"/api/torrents/{action}/{{id}}"
            : null;
    }
}
