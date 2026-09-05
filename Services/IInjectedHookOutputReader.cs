namespace WreckfestController.Services;

public interface IInjectedHookOutputReader : IServerOutputReader
{
    event Action<string>? HookOutputReceived;

    // Same lines as OutputReceived, tagged with the process they were actually read
    // from. A consumer cannot recover that afterwards: the reader's TargetProcessId
    // is cleared to 0 on stop and retargeted on the next inject, so by the time a
    // late callback is handled it no longer describes that callback's source.
    event Action<int, string>? OutputReceivedFrom;

    bool IsHookConnected { get; }

    Task<(bool Success, string Message)> InjectAsync(int processId);
}
