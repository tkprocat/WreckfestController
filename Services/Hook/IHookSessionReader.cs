namespace WreckfestController.Services.Hook;

/// <summary>
/// Reads the server's session state through the injected hook. SERVER lives on the
/// heap, out of reach of the module-relative <see cref="IHookMemoryReader"/>, so the
/// hook resolves it and answers <see cref="HookSessionState.Command"/>.
/// </summary>
public interface IHookSessionReader
{
    Task<(bool Success, string Message, HookSessionState? Session)> ReadSessionStateAsync(int processId);
}
