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
    private readonly ILogger<ServerReattachService> _logger;

    public ServerReattachService(ServerManager serverManager, ILogger<ServerReattachService> logger)
    {
        _serverManager = serverManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _serverManager.ReattachOwnServerAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reattaching to this controller's server failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
