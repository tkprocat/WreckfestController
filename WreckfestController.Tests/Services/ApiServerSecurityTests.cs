using Microsoft.AspNetCore.Http;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class ApiServerSecurityTests
{
    [Fact]
    public async Task ApiRequest_WithMissingKey_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, "test-key");
        var context = CreateApiRequest();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ApiRequest_WithWrongKey_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, "test-key");
        var context = CreateApiRequest();
        context.Request.Headers["X-Api-Key"] = "wrong-key";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ApiRequest_WithCorrectKey_CallsNextMiddleware()
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, "test-key");
        var context = CreateApiRequest();
        context.Request.Headers["X-Api-Key"] = "test-key";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ApiRequest_WithNoConfiguredKey_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, string.Empty);
        var context = CreateApiRequest();
        context.Request.Headers["X-Api-Key"] = "any-key";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData(false, "http://127.0.0.1:5100;https://127.0.0.1:5101")]
    [InlineData(true, "http://0.0.0.0:5100;https://0.0.0.0:5101")]
    public void GetListenUrls_UsesExpectedBindAddress(bool allowRemote, string expectedUrls)
    {
        Assert.Equal(expectedUrls, ApiServer.GetListenUrls(allowRemote));
    }

    // Several controller instances can manage separate servers on one Windows host,
    // so the ports must not be fixed to the defaults.
    [Theory]
    [InlineData(false, 6200, 6201, "http://127.0.0.1:6200;https://127.0.0.1:6201")]
    [InlineData(true, 8080, 8443, "http://0.0.0.0:8080;https://0.0.0.0:8443")]
    public void GetListenUrls_UsesConfiguredPorts(
        bool allowRemote,
        int httpPort,
        int httpsPort,
        string expectedUrls)
    {
        Assert.Equal(expectedUrls, ApiServer.GetListenUrls(allowRemote, httpPort, httpsPort));
    }

    private static DefaultHttpContext CreateApiRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/server/status";
        return context;
    }
}
