using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace AdbClient.Service.Middleware;

public sealed partial class BaseHrefMiddleware(RequestDelegate next, string basePath)
{
    [GeneratedRegex(@"<base href=""/""")]
    private static partial Regex BaseHrefRegex();

    private readonly string _basePath = HtmlEncoder.Default.Encode($"/{basePath.Trim('/')}/");

    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        await using var responseBody = new HtmlResponseStream(context, originalBody);

        try
        {
            context.Response.Body = responseBody;

            await next(context);

            context.Response.Body = originalBody;
            await WriteRewrittenHtmlAsync(context, responseBody, originalBody);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private async Task WriteRewrittenHtmlAsync(
        HttpContext context,
        HtmlResponseStream responseBody,
        Stream destination)
    {
        if (responseBody.BufferedContent is not { } bufferedContent)
        {
            return;
        }

        var html = Encoding.UTF8.GetString(bufferedContent.ToArray());
        var rewrittenHtml = BaseHrefRegex().Replace(
            html,
            _ => $@"<base href=""{_basePath}""",
            count: 1);

        context.Response.ContentLength = null;
        await destination.WriteAsync(Encoding.UTF8.GetBytes(rewrittenHtml), context.RequestAborted);
    }

    private sealed class HtmlResponseStream(HttpContext context, Stream destination) : Stream
    {
        private MemoryStream? _bufferedContent;
        private Stream? _target;

        public MemoryStream? BufferedContent => _bufferedContent;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            Target.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Target.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Target.Write(buffer, offset, count);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return Target.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return Target.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bufferedContent?.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_bufferedContent is not null)
            {
                await _bufferedContent.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }

        private Stream Target => _target ??= ShouldRewriteHtml()
            ? _bufferedContent = new MemoryStream()
            : destination;

        private bool ShouldRewriteHtml()
        {
            if (!HttpMethods.IsGet(context.Request.Method) || context.Response.StatusCode != StatusCodes.Status200OK)
            {
                return false;
            }

            var mediaType = context.Response.ContentType?.Split(';', 2)[0].Trim();

            return string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
