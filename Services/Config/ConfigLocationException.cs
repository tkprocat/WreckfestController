namespace WreckfestController.Services.Config;

/// <summary>
/// The server settings do not say where server_config.cfg is. <see cref="Message"/> names
/// the setting to fix and holds no path, so it can be shown to the web as it is (#153).
/// </summary>
public sealed class ConfigLocationException(string message) : InvalidOperationException(message)
{
    public static ConfigLocationException NoWorkingDirectory() =>
        new("The server's working directory is not set. Set it in the desktop app's server settings.");

    public static ConfigLocationException NoServerConfigArgument() =>
        new("The server arguments have no server_config=. Add server_config=server_config.cfg to them in the desktop app's server settings.");
}
