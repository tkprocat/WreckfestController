using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data.Cups;
using WreckfestController.Hubs;
using WreckfestController.Services.Publishing;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The SignalR hub end to end: the real API host, the WPF app's publisher and a real
/// SignalR client. Everyone gets the public events; only a signed-in caller gets the
/// console log.
/// </summary>
public class ServerHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AnonymousClient_GetsTrackChanged_ButNotConsoleLog()
    {
        await using var host = await ApiTestHost.StartAsync();
        var publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();

        await using var anonymous = host.CreateHubConnection();
        await using var admin = host.CreateHubConnection(o => o.Headers["X-Api-Key"] = ApiTestHost.ApiKey);
        var anonymousLog = Record(anonymous);
        var adminLog = Record(admin);
        await anonymous.StartAsync(Ct);
        await admin.StartAsync(Ct);

        // A public message reaching both proves both have finished joining their groups.
        await PublishUntilAsync(() => publisher.TrackChangedAsync("speedway2_inner_oval"),
            () => anonymousLog.TrackIds.Contains("speedway2_inner_oval")
                  && adminLog.TrackIds.Contains("speedway2_inner_oval"));

        publisher.AddConsoleLog("12:00:00 Server started");
        await publisher.FlushConsoleLogAsync();
        await WaitUntilAsync(() => adminLog.ConsoleLines.Contains("12:00:00 Server started"));

        // Messages on one connection arrive in order, so once this one is in, a
        // console batch sent before it would already have arrived too.
        await publisher.TrackChangedAsync("fields14");
        await WaitUntilAsync(() => anonymousLog.TrackIds.Contains("fields14"));

        Assert.Empty(anonymousLog.ConsoleLines);
    }

    [Fact]
    public async Task OccurrenceOutcomes_GoToAdminsOnly()
    {
        await using var host = await ApiTestHost.StartAsync();
        var publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();

        await using var anonymous = host.CreateHubConnection();
        await using var admin = host.CreateHubConnection(o => o.Headers["X-Api-Key"] = ApiTestHost.ApiKey);
        var anonymousLog = Record(anonymous);
        var adminLog = Record(admin);
        await anonymous.StartAsync(Ct);
        await admin.StartAsync(Ct);
        await PublishUntilAsync(() => publisher.TrackChangedAsync("ready"),
            () => anonymousLog.TrackIds.Contains("ready") && adminLog.TrackIds.Contains("ready"));

        var occurrence = new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc);
        await publisher.CupOccurrenceEndedAsync(7, "Race night", occurrence, OccurrenceOutcome.Missed);
        await WaitUntilAsync(() => !adminLog.Outcomes.IsEmpty);

        await publisher.TrackChangedAsync("fields14");
        await WaitUntilAsync(() => anonymousLog.TrackIds.Contains("fields14"));

        var ended = Assert.Single(adminLog.Outcomes);
        Assert.Equal(7, ended.GetProperty("cupId").GetInt32());
        Assert.Equal("Missed", ended.GetProperty("outcome").GetString());
        Assert.Equal(occurrence, ended.GetProperty("occurrence").GetDateTime().ToUniversalTime());
        Assert.Empty(anonymousLog.Outcomes);
    }

    [Fact]
    public async Task CookieClient_GetsConsoleLog()
    {
        await using var host = await ApiTestHost.StartAsync();
        var publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();
        var cookie = await host.SignInAsync(await host.CreateUserAsync());

        await using var connection = host.CreateHubConnection(o => o.Headers["Cookie"] = cookie.Split(';')[0]);
        var log = Record(connection);
        await connection.StartAsync(Ct);
        await PublishUntilAsync(() => publisher.TrackChangedAsync("ready"), () => log.TrackIds.Contains("ready"));

        publisher.AddConsoleLog("a");
        publisher.AddConsoleLog("b");
        await publisher.FlushConsoleLogAsync();

        await WaitUntilAsync(() => log.ConsoleLines.Count == 2);
        Assert.Equal(["a", "b"], log.ConsoleLines);
        Assert.Single(log.ConsoleBatches);
    }

    // #227: the hub lets a connection into the admin group once, when it opens. Locking
    // the account ends its HTTP sessions, and must end that stream too.
    [Fact]
    public async Task LockingAnAccount_EndsItsLiveConsole()
    {
        await using var host = await ApiTestHost.StartAsync();
        var publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();
        await host.CreateUserAsync();
        var other = await host.CreateUserAsync("other");
        var cookie = (await host.SignInAsync(other)).Split(';')[0];

        await using var connection = host.CreateHubConnection(o => o.Headers["Cookie"] = cookie);
        var closed = ClosedSignal(connection);
        var log = Record(connection);
        await connection.StartAsync(Ct);
        await PublishUntilAsync(() => publisher.TrackChangedAsync("ready"), () => log.TrackIds.Contains("ready"));

        using var script = host.CreateAuthenticatedClient();
        using var locked = await script.PostAsync($"/api/users/{other.Id}/lock", null, Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, locked.StatusCode);
        await closed.WaitAsync(Timeout, Ct);

        // Back on the same cookie, now ended: public events only.
        await using var again = host.CreateHubConnection(o => o.Headers["Cookie"] = cookie);
        var againLog = Record(again);
        await again.StartAsync(Ct);
        await PublishUntilAsync(() => publisher.TrackChangedAsync("back"), () => againLog.TrackIds.Contains("back"));
        publisher.AddConsoleLog("after the lock");
        await publisher.FlushConsoleLogAsync();
        await PublishUntilAsync(() => publisher.TrackChangedAsync("fields14"), () => againLog.TrackIds.Contains("fields14"));

        Assert.Empty(againLog.ConsoleLines);
    }

    // Dropped connections whose session is still valid come back as admins.
    [Fact]
    public async Task EndedHubSessions_OfAValidSignIn_ReconnectAsAdmin()
    {
        await using var host = await ApiTestHost.StartAsync();
        var publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();
        var user = await host.CreateUserAsync();
        var cookie = (await host.SignInAsync(user)).Split(';')[0];

        await using var connection = host.CreateHubConnection(o => o.Headers["Cookie"] = cookie);
        var closed = ClosedSignal(connection);
        await connection.StartAsync(Ct);
        var admins = host.Services.GetRequiredService<AdminHubConnections>();
        var deadline = DateTime.UtcNow + Timeout;
        while (admins.EndSessionsOf(user.Id) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The connection never joined the admin list.");
            await Task.Delay(20, Ct);
        }
        await closed.WaitAsync(Timeout, Ct);

        await using var again = host.CreateHubConnection(o => o.Headers["Cookie"] = cookie);
        var log = Record(again);
        await again.StartAsync(Ct);
        await PublishUntilAsync(() => publisher.TrackChangedAsync("back"), () => log.TrackIds.Contains("back"));
        publisher.AddConsoleLog("still an admin");
        await publisher.FlushConsoleLogAsync();

        await WaitUntilAsync(() => log.ConsoleLines.Contains("still an admin"));
    }

    private static Task ClosedSignal(HubConnection connection)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    [Fact]
    public async Task ServerEvents_ArriveWithCamelCasePayloads()
    {
        await using var host = await ApiTestHost.StartAsync();
        var publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();

        await using var connection = host.CreateHubConnection();
        var restartPending = new TaskCompletionSource<ServerRestartPendingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<ServerRestartPendingMessage>(nameof(IServerHubClient.ServerRestartPending),
            m => restartPending.TrySetResult(m));
        await connection.StartAsync(Ct);

        var deadline = new DateTime(2026, 9, 26, 12, 5, 0, DateTimeKind.Utc);
        await PublishUntilAsync(
            () => publisher.ServerRestartPendingAsync(new Models.ServerRestartPendingEvent
            {
                MinutesRemaining = 3, CupName = "Friday night", CupId = 7, ScheduledRestartTime = deadline,
            }),
            () => restartPending.Task.IsCompleted);

        var message = await restartPending.Task;
        Assert.Equal(3, message.MinutesRemaining);
        Assert.Equal("Friday night", message.CupName);
        Assert.Equal(7, message.CupId);
        Assert.Equal(deadline, message.ScheduledRestartTime);
    }

    [Fact]
    public async Task StoppingTheApiHost_DetachesThePublisher()
    {
        HubServerEventPublisher publisher;
        await using (var host = await ApiTestHost.StartAsync())
        {
            publisher = host.MainServices.GetRequiredService<HubServerEventPublisher>();
            Assert.True(publisher.IsAttached);
        }

        Assert.False(publisher.IsAttached);
    }

    private sealed class Received
    {
        public ConcurrentQueue<string> TrackIdQueue { get; } = new();
        public ConcurrentQueue<IReadOnlyList<string>> Batches { get; } = new();
        public ConcurrentQueue<System.Text.Json.JsonElement> Outcomes { get; } = new();
        public IReadOnlyList<string> TrackIds => TrackIdQueue.ToList();
        public IReadOnlyList<IReadOnlyList<string>> ConsoleBatches => Batches.ToList();
        public IReadOnlyList<string> ConsoleLines => Batches.SelectMany(b => b).ToList();
    }

    private static Received Record(HubConnection connection)
    {
        var received = new Received();
        connection.On<TrackChangedMessage>(nameof(IServerHubClient.TrackChanged),
            m => received.TrackIdQueue.Enqueue(m.TrackId));
        connection.On<ConsoleLogMessage>(nameof(IServerHubClient.ConsoleLog),
            m => received.Batches.Enqueue(m.Logs));

        // Raw JSON, to see the wire format a browser gets.
        connection.On<System.Text.Json.JsonElement>(nameof(IServerHubClient.CupOccurrenceEnded),
            m => received.Outcomes.Enqueue(m));
        return received;
    }

    /// <summary>
    /// A client's StartAsync returns once the handshake is done, which can be before the
    /// hub has put it in its groups. So publish until the message is seen.
    /// </summary>
    private static async Task PublishUntilAsync(Func<Task> publish, Func<bool> seen)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!seen())
        {
            Assert.True(DateTime.UtcNow < deadline, "The hub message never arrived.");
            await publish();
            await Task.Delay(50, Ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The hub message never arrived.");
            await Task.Delay(20, Ct);
        }
    }
}
