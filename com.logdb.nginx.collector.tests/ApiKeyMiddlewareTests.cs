using com.logdb.nginx.collector.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace com.logdb.nginx.collector.tests;

public class ApiKeyMiddlewareTests
{
    private static ApiKeyMiddleware Build(string? configuredKey, RequestDelegate next)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ApiKey"] = configuredKey })
            .Build();
        return new ApiKeyMiddleware(next, config);
    }

    private static DefaultHttpContext Context(string path, string? apiKey = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (apiKey is not null) ctx.Request.Headers[ApiKeyMiddleware.HeaderName] = apiKey;
        return ctx;
    }

    [Fact]
    public async Task NoKeyConfigured_AllowsApiRequestWithoutHeader()
    {
        var called = false;
        var mw = Build(configuredKey: "", next: _ => { called = true; return Task.CompletedTask; });
        var ctx = Context("/api/status");

        await mw.InvokeAsync(ctx);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task KeyConfigured_MissingHeader_Returns401()
    {
        var called = false;
        var mw = Build("secret", _ => { called = true; return Task.CompletedTask; });
        var ctx = Context("/api/status");

        await mw.InvokeAsync(ctx);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task KeyConfigured_WrongHeader_Returns401()
    {
        var mw = Build("secret", _ => Task.CompletedTask);
        var ctx = Context("/api/status", apiKey: "nope");

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task KeyConfigured_CorrectHeader_Passes()
    {
        var called = false;
        var mw = Build("secret", _ => { called = true; return Task.CompletedTask; });
        var ctx = Context("/api/status", apiKey: "secret");

        await mw.InvokeAsync(ctx);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    public async Task HealthEndpoints_StayOpen_EvenWithKeyConfigured(string path)
    {
        var called = false;
        var mw = Build("secret", _ => { called = true; return Task.CompletedTask; });
        var ctx = Context(path); // no API key header

        await mw.InvokeAsync(ctx);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }
}
