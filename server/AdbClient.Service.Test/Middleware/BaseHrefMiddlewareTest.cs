using AdbClient.Service.Middleware;
using Microsoft.AspNetCore.Http;

namespace AdbClient.Service.Test.Middleware;

public class BaseHrefMiddlewareTest
{
    [Fact]
    public async Task HtmlResponse_RewritesBaseHref()
    {
        const string html = "<html><head><base href=\"/\"><link href=\"styles.css\"></head></html>";
        var context = CreateContext(HttpMethods.Get);
        var middleware = new BaseHrefMiddleware(
            async responseContext =>
            {
                responseContext.Response.ContentType = "text/html; charset=utf-8";
                responseContext.Response.ContentLength = html.Length;
                await responseContext.Response.WriteAsync(html);
            },
            "all-debrid");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Null(context.Response.ContentLength);
        Assert.Equal(
            "<html><head><base href=\"/all-debrid/\"><link href=\"styles.css\"></head></html>",
            await ReadResponse(context));
    }

    [Fact]
    public async Task ErrorResponse_IsPreservedWithoutRewriting()
    {
        const string errorHtml = "<html><head><base href=\"/\"></head><body>Not found</body></html>";
        var context = CreateContext(HttpMethods.Get);
        var destination = context.Response.Body;
        var responseWasWrittenDirectly = false;
        var middleware = new BaseHrefMiddleware(
            async responseContext =>
            {
                responseContext.Response.StatusCode = StatusCodes.Status404NotFound;
                responseContext.Response.ContentType = "text/html";
                await responseContext.Response.WriteAsync(errorHtml);
                responseWasWrittenDirectly = destination.Length == errorHtml.Length;
            },
            "all-debrid");

        await middleware.InvokeAsync(context);

        Assert.True(responseWasWrittenDirectly);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(errorHtml, await ReadResponse(context));
    }

    [Fact]
    public async Task NonHtmlResponse_PassesThroughWithoutBuffering()
    {
        const string json = "{\"status\":\"ok\"}";
        var context = CreateContext(HttpMethods.Get);
        var destination = context.Response.Body;
        var responseWasWrittenDirectly = false;
        var middleware = new BaseHrefMiddleware(
            async responseContext =>
            {
                responseContext.Response.ContentType = "application/json";
                await responseContext.Response.WriteAsync(json);
                responseWasWrittenDirectly = destination.Length == json.Length;
            },
            "all-debrid");

        await middleware.InvokeAsync(context);

        Assert.True(responseWasWrittenDirectly);
        Assert.Equal(json, await ReadResponse(context));
    }

    private static DefaultHttpContext CreateContext(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static async Task<string> ReadResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;

        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
