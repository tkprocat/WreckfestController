using WreckfestController.Models;

namespace WreckfestController.Services;

public class ConfiguredServerInputWriter : IServerInputWriter, IPlayerSnapshotReader
{
    private readonly IConfiguration _configuration;
    private readonly ConsoleWriter _consoleWriter;
    private readonly InjectedHookInputWriter _injectedHookInputWriter;

    public ConfiguredServerInputWriter(
        IConfiguration configuration,
        ConsoleWriter consoleWriter,
        InjectedHookInputWriter injectedHookInputWriter)
    {
        _configuration = configuration;
        _consoleWriter = consoleWriter;
        _injectedHookInputWriter = injectedHookInputWriter;
    }

    public Task<(bool Success, string Message)> SendCommandAsync(string command, int processId)
    {
        return GetConfiguredInputMode() == ServerInputModes.InjectedHook
            ? _injectedHookInputWriter.SendCommandAsync(command, processId)
            : _consoleWriter.SendCommandAsync(command, processId);
    }

    public Task<(bool Success, string Message, IReadOnlyList<Player> Players)> ReadPlayerSnapshotAsync(int processId)
    {
        return GetConfiguredInputMode() == ServerInputModes.InjectedHook
            ? _injectedHookInputWriter.ReadPlayerSnapshotAsync(processId)
            : Task.FromResult<(bool Success, string Message, IReadOnlyList<Player> Players)>(
                (false, "Injected hook input mode is not enabled", Array.Empty<Player>()));
    }

    private string GetConfiguredInputMode()
    {
        return _configuration["WreckfestServer:InputMode"]?.Trim() switch
        {
            ServerInputModes.InjectedHook => ServerInputModes.InjectedHook,
            ServerInputModes.ConsoleWriter => ServerInputModes.ConsoleWriter,
            _ => ServerInputModes.ConsoleWriter
        };
    }
}
