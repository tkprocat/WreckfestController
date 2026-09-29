namespace WreckfestController.Tests.Api;

/// <summary>
/// attach and inject take a process id in the path. A bad one is the caller's mistake: a
/// 400 naming pid, not a 404 from a route constraint or a 409 from the server manager.
/// </summary>
public class ServerPidTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("attach", "abc")]
    [InlineData("attach", "-1")]
    [InlineData("attach", "0")]
    [InlineData("inject", "abc")]
    [InlineData("inject", "-5")]
    public async Task ABadPid_IsA400NamingIt(string action, string pid)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsync($"/api/server/{action}/{pid}", content: null, Ct);

        await AuthEndpointsTests.AssertFieldErrorAsync(response, "pid");
    }
}
