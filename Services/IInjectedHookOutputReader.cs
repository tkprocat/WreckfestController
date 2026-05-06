namespace WreckfestController.Services;

public interface IInjectedHookOutputReader : IServerOutputReader
{
    event Action<string>? HookOutputReceived;

    Task<(bool Success, string Message)> InjectAsync(int processId);
}
