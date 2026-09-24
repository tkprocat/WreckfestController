using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// A database that cannot be migrated must not take the controller down with it: the
/// host still builds, the services the WPF window needs still resolve, the scheduler
/// waits, and the API fails closed.
/// </summary>
public sealed class RecoveryModeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-recovery-tests", Guid.NewGuid().ToString("N"));

    private string DatabaseFile => Path.Combine(_directory, "controller.db");

    [Fact]
    public async Task MigrationFailure_KeepsHostAndServicesUsable()
    {
        WriteCorruptDatabase();

        using var host = Program
            .CreateHostBuilder([$"--Database:Path={DatabaseFile}"])
            .Build();

        Assert.False(host.Services.GetRequiredService<DatabaseBootstrapper>().Run());

        var state = host.Services.GetRequiredService<DatabaseState>();
        Assert.False(state.IsReady);
        Assert.NotNull(state.Error);

        // What MainWindow is built from, short of the window itself (which needs a WPF
        // Application and an STA thread).
        host.Services.GetRequiredService<ServerManager>();
        host.Services.GetRequiredService<PlayerTracker>();
        host.Services.GetRequiredService<SettingsService>();
        host.Services.GetRequiredService<EventStorageService>();
        host.Services.GetRequiredService<SmartRestartService>();
        host.Services.GetRequiredService<IApiServer>();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var gate = host.Services.GetServices<IHostedService>()
                .OfType<DatabaseGatedHostedService<EventSchedulerService>>()
                .Single();
            Assert.False(gate.IsStarted);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Api_InRecoveryMode_AnswersOnlyAuthState()
    {
        var state = new DatabaseState(DatabaseFile);
        state.MarkFailed("file is not a database", backupPath: null);
        await using var host = await ApiTestHost.StartAsync(databaseState: state);
        using var client = host.CreateAuthenticatedClient();
        var ct = TestContext.Current.CancellationToken;

        using var status = await client.GetAsync("/api/server/status", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status.StatusCode);

        using var write = await client.PostAsync("/api/server/start", content: null, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, write.StatusCode);

        // Anonymous, so the web UI can explain why sign-in is unavailable.
        using var anonymous = host.CreateClient();
        using var authState = await anonymous.GetAsync("/api/auth/state", ct);
        Assert.Equal(HttpStatusCode.OK, authState.StatusCode);
        var body = await authState.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.GetProperty("degraded").GetBoolean());
        Assert.False(body.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Api_AfterRecovery_ServesRequestsAgain()
    {
        var state = new DatabaseState(DatabaseFile);
        state.MarkFailed("file is not a database", backupPath: null);
        await using var host = await ApiTestHost.StartAsync(databaseState: state);
        using var client = host.CreateAuthenticatedClient();
        var ct = TestContext.Current.CancellationToken;

        state.MarkReady(backupPath: null);

        using var response = await client.GetAsync("/api/server/status", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private void WriteCorruptDatabase()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(DatabaseFile, Encoding.ASCII.GetBytes(new string('x', 4096)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
