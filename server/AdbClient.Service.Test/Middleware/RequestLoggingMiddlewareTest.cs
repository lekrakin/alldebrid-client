using System.Text;
using AdbClient.Service.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdbClient.Service.Test.Middleware;

public class RequestLoggingMiddlewareTest
{
    [Fact]
    public async Task Login_DoesNotLogCredentialsOrRequestData()
    {
        const string userName = "logpose-user";
        const string password = "login-password";
        const string apiKey = "query-api-key";
        var body = $"username={userName}&password={password}";

        var messages = await InvokeMiddleware(
            "/api/v2/auth/login",
            $"?apiKey={apiKey}",
            body,
            "application/x-www-form-urlencoded");

        var message = Assert.Single(messages);
        Assert.Contains("POST /api/v2/auth/login responded 204", message, StringComparison.Ordinal);
        Assert.DoesNotContain(userName, message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain(body, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddTorrent_DoesNotLogMagnetUrlOrToken()
    {
        const string magnetUrl = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
        const string token = "provider-token";
        var body = $"urls={Uri.EscapeDataString(magnetUrl)}&token={token}&category=radarr";

        var messages = await InvokeMiddleware(
            "/api/v2/torrents/add",
            null,
            body,
            "application/x-www-form-urlencoded");

        var message = Assert.Single(messages);
        Assert.Contains("POST /api/v2/torrents/add responded 204", message, StringComparison.Ordinal);
        Assert.DoesNotContain(magnetUrl, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(magnetUrl), message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, message, StringComparison.Ordinal);
        Assert.DoesNotContain(body, message, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<string>> InvokeMiddleware(
        string path,
        string? queryString,
        string body,
        string contentType)
    {
        var logger = new RecordingLogger<RequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var middleware = new RequestLoggingMiddleware(
            async requestContext =>
            {
                using var reader = new StreamReader(requestContext.Request.Body, Encoding.UTF8, leaveOpen: true);
                Assert.Equal(body, await reader.ReadToEndAsync());
                requestContext.Response.StatusCode = StatusCodes.Status204NoContent;
            },
            logger);

        await middleware.InvokeAsync(context);

        return logger.Messages;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel == LogLevel.Debug;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
