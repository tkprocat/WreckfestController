using System.Globalization;
using System.Runtime.CompilerServices;
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
/// folder cannot be written, the file goes to <c>%TEMP%\WreckfestController\crashes</c>
/// instead. Nothing here throws, and no write may hold up the process for longer than
/// <see cref="WriteTimeout"/>: it runs while the process is already going down.
/// Stack overflows and native faults end the process without reaching it.
/// </remarks>
public sealed class CrashLog
{
    /// <summary>How many crash files each folder keeps; older ones are removed at startup.</summary>
    public const int KeepCount = 20;

    private const string FilePrefix = "crash-";

    private readonly string _directory;
    private readonly string _fallbackDirectory;
    private readonly TimeProvider _time;

    // Every exception written, so one reported by both the dispatcher and the app domain
    // is written once, however many others arrive in between. Weak, so nothing is kept alive.
    private readonly ConditionalWeakTable<Exception, object> _written = new();

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
    public static CrashLog Default { get; } = new(
        DefaultDirectory,
        Path.Combine(Path.GetTempPath(), "WreckfestController", "crashes"),
        TimeProvider.System);

    /// <summary>
    /// How long one folder may take to write a file before the next is tried, so a folder
    /// on a share that stopped answering cannot keep a crashing process alive.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

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
    /// nothing was written: it was written before, or no folder could take it.
    /// </summary>
    public string? Write(Exception? exception, string source)
    {
        try
        {
            if (exception != null && _written.TryGetValue(exception, out _))
            {
                return null;
            }

            var now = _time.GetLocalNow();
            var pid = Environment.ProcessId;
            var name = string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{now:yyyy-MM-dd_HH-mm-ss}-pid{pid}");
            var bytes = Encoding.UTF8.GetBytes(Format(exception, source, now, pid, DatabasePath));

            foreach (var directory in new[] { _directory, _fallbackDirectory })
            {
                if (WriteWithin(directory, name, bytes) is { } path)
                {
                    // Only now: a report that failed everywhere may be tried again by
                    // the second handler.
                    if (exception != null)
                    {
                        _written.AddOrUpdate(exception, path);
                    }

                    return path;
                }
            }
        }
        catch
        {
            // Already going down; there is nowhere left to report this.
        }

        return null;
    }

    /// <summary>
    /// Removes all but the newest <see cref="KeepCount"/> crash files from each folder.
    /// Only <c>crash-*.txt</c> files are touched. Never throws.
    /// </summary>
    public void Prune()
    {
        Prune(_directory);
        Prune(_fallbackDirectory);
    }

    private static void Prune(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var old = new DirectoryInfo(directory)
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

    /// <summary>
    /// The write runs on a thread of its own, waited for up to <see cref="WriteTimeout"/>:
    /// a hung file system call cannot be cancelled, only left behind.
    /// </summary>
    private string? WriteWithin(string directory, string name, byte[] bytes)
    {
        string? written = null;
        var thread = new Thread(() => written = TryWrite(directory, name, bytes))
        {
            IsBackground = true,
            Name = "Crash log",
        };
        thread.Start();
        return thread.Join(WriteTimeout) ? written : null;
    }

    private static string? TryWrite(string directory, string name, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // CreateNew, so two crashes in the same second never overwrite each other.
            for (var n = 1; n < 100; n++)
            {
                var path = Path.Combine(directory, (n == 1 ? name : name + "-" + n) + ".txt");
                try
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    file.Write(bytes);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Taken; try the next name.
                }
            }
        }
        catch
        {
            // This folder cannot take it; the caller tries the next.
        }

        return null;
    }

    /// <summary>
    /// The report. Each part is guarded, so one that fails, such as an exception whose
    /// <c>ToString</c> throws, costs only that part.
    /// </summary>
    internal static string Format(Exception? exception, string source, DateTimeOffset when, int processId, string? databasePath)
    {
        var text = new StringBuilder();
        Line(text, () => $"Wreckfest Controller {AppInfo.Version} crashed.");
        text.AppendLine();
        Line(text, () => $"Time:      {when:yyyy-MM-dd HH:mm:ss zzz}");
        Line(text, () => $"Source:    {source}");
        Line(text, () => $"Process:   {processId} ({Environment.ProcessPath})");
        Line(text, () => $"Database:  {databasePath ?? "(not resolved yet)"}");
        Line(text, () => $"Runtime:   {Environment.Version} on {Environment.OSVersion}");
        text.AppendLine();
        // ToString includes the inner exceptions and their stack traces.
        Line(text, () => $"{exception?.ToString() ?? "(no exception object)"}", () =>
            $"{exception!.GetType().FullName}: (its description could not be read){Environment.NewLine}{exception.StackTrace}");
        return text.ToString();
    }

    private static void Line(StringBuilder text, Func<FormattableString> part, Func<FormattableString>? fallback = null)
    {
        try
        {
            text.AppendLine(FormattableString.Invariant(part()));
        }
        catch
        {
            try
            {
                text.AppendLine(fallback is null ? "(could not be read)" : FormattableString.Invariant(fallback()));
            }
            catch
            {
                text.AppendLine("(could not be read)");
            }
        }
    }
}
