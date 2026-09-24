using System.Net;
using System.Net.Http.Json;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Requests through the full API pipeline, so the key check is proven where it is
/// actually wired in rather than on the middleware in isolation.
/// </summary>
public class ApiKeyEndToEndTests
{
    [Fact]
    public async Task ServerStatus_WithoutKey_ReturnsUnauthorized()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            "/api/server/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServerStatus_WithWrongKey_ReturnsUnauthorized()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

        using var response = await client.GetAsync(
            "/api/server/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServerStatus_WithKey_ReturnsStatus()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.GetAsync(
            "/api/server/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<ServerStatus>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.False(status.IsRunning);
    }
}
