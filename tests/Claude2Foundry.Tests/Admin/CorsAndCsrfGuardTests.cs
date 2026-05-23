using Claude2Foundry.Admin;
using Microsoft.AspNetCore.Http;

namespace Claude2Foundry.Tests.Admin;

public class CorsAndCsrfGuardTests
{
    private static CorsAndCsrfGuard BuildMiddleware(RequestDelegate next) => new(next);

    [Fact]
    public async Task Post_WithoutHeader_Returns400()
    {
        bool nextCalled = false;
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Post_WithWrongHeaderValue_Returns400()
    {
        bool nextCalled = false;
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Headers["X-C2F-Admin"] = "true";
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Post_WithCorrectHeader_CallsNext()
    {
        bool nextCalled = false;
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Headers["X-C2F-Admin"] = "1";
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Get_WithoutHeader_CallsNext()
    {
        bool nextCalled = false;
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Options_UnknownOrigin_Returns403()
    {
        var middleware = BuildMiddleware(_ => Task.CompletedTask);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Options;
        ctx.Request.Headers.Origin = "https://evil.example.com";
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
    }
}
