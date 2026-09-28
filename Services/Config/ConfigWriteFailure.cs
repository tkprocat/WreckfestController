namespace WreckfestController.Services.Config;

/// <summary>
/// Why server_config.cfg could not be written, in terms an admin can act on.
/// <see cref="Reason"/> is stable, for a client that shows its own help.
/// </summary>
public sealed record ConfigWriteFailure(string Reason, string Message)
{
    public const string AccessDenied = "accessDenied";
    public const string FileInUse = "fileInUse";
    public const string NotFound = "notFound";
    public const string NotConfigured = "notConfigured";
    public const string IoError = "ioError";

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Describes a failure from <see cref="ConfigService"/>'s file access, or returns
    /// null for an exception that is not about the file and should propagate.
    /// </summary>
    public static ConfigWriteFailure? From(Exception exception) => exception switch
    {
        // Windows reports both a missing ACL permission and the read-only attribute this way.
        UnauthorizedAccessException => new(
            AccessDenied,
            $"Windows denied write access to the server config. {exception.Message} "
            + "Give the account running WreckfestController write permission on the file, "
            + "and check that the file is not marked read-only."),

        FileNotFoundException or DirectoryNotFoundException => new(
            NotFound,
            $"The server config was not found. {exception.Message} "
            + "Check the working directory and server_config= in the server settings."),

        IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation => new(
            FileInUse,
            "Another program has the server config open, such as an editor. Close it and try again."),

        IOException => new(IoError, $"Could not write the server config. {exception.Message}"),

        // Thrown by ConfigService when the settings do not say where the config is.
        InvalidOperationException => new(
            NotConfigured,
            $"The server config's location is not set up. {exception.Message}"),

        _ => null,
    };
}
