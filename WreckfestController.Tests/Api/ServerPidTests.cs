using System.Net;

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

    // A real process that is not the configured server - here, this test run itself -
    // must not be attachable: Force stop would then kill it.
    [Theory]
    [InlineData("attach")]
    [InlineData("inject")]
    public async Task AProcessThatIsNotTheServer_IsRefused(string action)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsync($"/api/server/{action}/{Environment.ProcessId}", content: null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // The list a remote admin picks from: only the configured server, and no paths.
    [Fact]
    public async Task Processes_ListsOnlyTheConfiguredServer()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/server/processes", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    // "Log file not found at C:\..." named the path; the web never sees paths.
    [Fact]
    public async Task ALogFileFailure_DoesNotNameThePath()
    {
        var directory = ApiTestHost.NewDataDirectory();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "server_config.cfg"), "server_name=Test\nlog=missing.log\n", Ct);
        try
        {
            await using var host = await ApiTestHost.StartAsync(
                new Dictionary<string, string?> { ["WreckfestServer:WorkingDirectory"] = directory },
                dataDirectory: directory);
            using var client = host.CreateAuthenticatedClient();

            using var response = await client.GetAsync("/api/server/logfile", Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("could not be read", body, StringComparison.Ordinal);
            Assert.DoesNotContain("missing.log", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFileName(directory), body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ApiTestHost.DeleteDataDirectory(directory);
        }
    }
}
