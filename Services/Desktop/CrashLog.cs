using System.Globalization;
using System.Text;

namespace WreckfestController.Services.Desktop;

/// <summary>
/// Writes a file for every crash, one per crash, under
/// <c>%LocalAppData%\WreckfestController\crashes</c>. Nothing else records one: the
/// in-app log dies with the process, and Windows keeps only a terse event-log entry.
/// </summary>
/// <remarks>
/// The folder is fixed, not beside the database: <c>Database:Path</c> can point at a
/// folder that is missing or read-only, which is when a crash is likeliest. If the
/// folder cannot be written, the file goes to %TEMP% instead. Nothing here throws: it
/// runs while the process is already going down.
/// </remarks>
public sealed class CrashLog
{
    /// <summary>How many crash files are kept; older ones are removed at startup.</summary>
    public const int KeepCount = 20;

    private const string FilePrefix = "crash-";

    private readonly string _directory;
    private readonly string _fallbackDirectory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Exception? _lastWritten;

    public CrashLog(string directory, string fallbackDirectory, TimeProvider time)
    {
        _directory = directory;
        _fallbackDirectory = fallbackDirectory;
        _time = time;
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WreckfestController",
        "crashes");

    /// <summary>The one the app uses.</summary>
    public static CrashLog Default { get; } = new(DefaultDirectory, Path.GetTempPath(), TimeProvider.System);

    /// <summary>Shown in each file, so a crash can be told apart when several controllers run.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Reports every exception that will end the process.</summary>
    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating ? "Unhandled exception" : "Unhandled exception (not terminating)");
    }

    /// <summary>
    /// The WPF dispatcher's, recorded where the UI thread can still be named. The
    /// exception is left unhandled, so the app goes down as before.
    /// </summary>
    public void Install(System.Windows.Application app)
    {
        app.DispatcherUnhandledException += (_, e) => Write(e.Exception, "Unhandled exception on the UI thread");
    }

    /// <summary>
    /// Writes <paramref name="exception"/> to a new file and returns its path, or null when
    /// nothing could be written. The same exception reported twice (by the dispatcher, then
    /// the app domain) is written once.
    /// </summary>
    public string? Write(Exception? exception, string source)
    {
        try
        {
            lock (_gate)
            {
                if (exception != null && ReferenceEquals(exception, _lastWritten))
                {
                    return null;
                }

                _lastWritten = exception;
            }

            var now = _time.GetLocalNow();
            var pid = Environment.ProcessId;
            var name = string.Create(
                CultureInfo.InvariantCulture,
                $"{FilePrefix}{now:yyyy-MM-dd_HH-mm-ss}-pid{pid}.txt");
            var text = Format(exception, source, now, pid, DatabasePath);

            foreach (var directory in new[] { _directory, _fallbackDirectory })
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = UniquePath(directory, name);
                    File.WriteAllText(path, text, Encoding.UTF8);
                    return path;
                }
                catch
                {
                    // Try the next folder.
                }
            }
        }
        catch
        {
            // Already going down; there is nowhere left to report this.
        }

        return null;
    }

    /// <summary>Removes all but the newest <see cref="KeepCount"/> crash files. Never throws.</summary>
    public void Prune()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var old = new DirectoryInfo(_directory)
                .GetFiles(FilePrefix + "*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(KeepCount);
            foreach (var file in old)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // In use or gone; the next startup tries again.
                }
            }
        }
        catch
        {
            // Pruning is housekeeping; it must not stop the app starting.
        }
    }

    internal static string Format(Exception? exception, string source, DateTimeOffset when, int processId, string? databasePath)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Wreckfest Controller {AppInfo.Version} crashed.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Time:      {when:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Source:    {source}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Process:   {processId} ({Environment.ProcessPath})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Database:  {databasePath ?? "(not resolved yet)"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Runtime:   {Environment.Version} on {Environment.OSVersion}");
        text.AppendLine();
        // ToString includes the inner exceptions and their stack traces.
        text.AppendLine(exception?.ToString() ?? "(no exception object)");
        return text.ToString();
    }

    // Two crashes in the same second, such as the dispatcher's and a background thread's.
    private static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + n + ".txt");
        }

        return path;
    }
}
