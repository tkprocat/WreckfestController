namespace WreckfestController.Data;

/// <summary>
/// Where the controller's database lives. Read once at startup from
/// <c>Database:Path</c>; changing it needs a restart.
/// </summary>
public static class DatabasePath
{
    public const string ConfigurationKey = "Database:Path";
    public const string DefaultFileName = "controller.db";

    /// <summary>
    /// The configured path with environment variables expanded, or
    /// <c>%LocalAppData%\WreckfestController\controller.db</c> when none is set.
    /// </summary>
    /// <remarks>
    /// The default is under %LocalAppData% rather than beside the exe because SQLite
    /// needs write access to the folder (for its -wal/-shm and -journal files), and the
    /// app folder is not writable when it is installed under Program Files. A Windows
    /// service running as LocalSystem gets a different %LocalAppData% from the desktop
    /// app, so a service install should set this to a %ProgramData% path.
    /// A relative path is taken relative to <paramref name="baseDirectory"/>, not the
    /// working directory, which differs between a desktop launch and a service.
    /// </remarks>
    public static string Resolve(IConfiguration configuration, string baseDirectory)
    {
        var configuredPath = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WreckfestController",
                DefaultFileName);
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        return Path.GetFullPath(expandedPath, baseDirectory);
    }
}
