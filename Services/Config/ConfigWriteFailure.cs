namespace WreckfestController.Services.Config;

/// <summary>
/// Why server_config.cfg could not be written, in terms an admin can act on.
/// <see cref="Reason"/> is stable, for a client that shows its own help. The message never
/// carries the exception's own text: that can hold local paths, and the web never sees
/// those (#153). Callers log the exception.
/// </summary>
public sealed record ConfigWriteFailure(string Reason, string Message)
{
    public const string AccessDenied = "accessDenied";
    public const string FileInUse = "fileInUse";
    public const string NotFound = "notFound";
    public const string NotConfigured = "notConfigured";
    public const string MissingEventLoop = "missingEventLoop";
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
            "Windows denied write access to the server config. "
            + "Give the account running WreckfestController write permission on the file, "
            + "and check that the file is not marked read-only."),

        FileNotFoundException or DirectoryNotFoundException => new(
            NotFound,
            "The server config was not found. "
            + "Check the working directory and server_config= in the server settings."),

        IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation => new(
            FileInUse,
            "Another program has the server config open, such as an editor. Close it and try again."),

        IOException => new(IoError, "Could not write the server config. The desktop app's log has the details."),

        // Before the InvalidOperationException below, which it derives from: the file is
        // there, it just has nowhere to put the rotation.
        EventLoopHeadingMissingException => new(MissingEventLoop, exception.Message),

        // Thrown by ConfigService when the settings do not say where the config is.
        InvalidOperationException => new(
            NotConfigured,
            "The server config's location is not set up. Set the working directory in the server settings."),

        _ => null,
    };
}
