namespace WreckfestController.Services.Hook;

public interface IServerOutputReader
{
    event Action<string>? OutputReceived;

    string Mode { get; }

    bool IsMonitoring { get; }

    int TargetProcessId { get; }

    Task<bool> StartAsync(int processId);

    Task StopAsync();
}
