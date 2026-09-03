namespace WreckfestController.Services;

public interface IInjectedHookOutputReader : IServerOutputReader
{
    event Action<string>? HookOutputReceived;

    bool IsHookConnected { get; }

    Task<(bool Success, string Message)> InjectAsync(int processId);
}
