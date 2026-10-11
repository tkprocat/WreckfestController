namespace WreckfestController.Services.ServerControl;

/// <summary>
/// Once the controller has started, reattaches to its own server if one is still running
/// from before (<see cref="ServerManager.ReattachOwnServerAsync"/>, #201), so a restart of
/// the controller does not leave the server without attachment or hook. Runs in the
/// background: startup does not wait for the inject.
/// </summary>
public sealed class ServerReattachService : IHostedService
{
    private readonly ServerManager _serverManager;
    private readonly StartupReattach _startup;
    private readonly ILogger<ServerReattachService> _logger;

    public ServerReattachService(ServerManager serverManager, StartupReattach startup, ILogger<ServerReattachService> logger)
    {
        _serverManager = serverManager;
        _startup = startup;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (reattached, _) = await _serverManager.ReattachOwnServerAsync();
                if (reattached)
                {
                    await ReadRosterAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reattaching to this controller's server failed");
            }
            finally
            {
                _startup.Settle();
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The players already on the server, before anything decides whether to warn them
    /// (#232). The hook's pipe can take a moment to answer after the inject, hence a few
    /// tries; without an answer, event polling fills the roster in later.
    /// </summary>
    private async Task ReadRosterAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await _serverManager.TryRefreshPlayersFromHookAsync())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        _logger.LogWarning("Reattached, but the roster could not be read from the hook yet");
    }
}
