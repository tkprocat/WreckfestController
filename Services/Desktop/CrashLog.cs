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
/// <para>
/// The folder is fixed, not beside the database: <c>Database:Path</c> can point at a
/// folder that is missing or read-only, which is when a crash is likeliest. If the
/// folder cannot be written, the file goes to <c>%TEMP%\WreckfestController\crashes</c>
/// instead. Stack overflows and native faults end the process without reaching this.
/// </para>
/// <para>
/// It runs while the process is going down, so nothing here throws, and every step that
/// can hang - the exception's own <c>ToString</c>, each folder's write - is given up
/// after <see cref="WriteTimeout"/>. A step given up on cannot be cancelled, only left
/// behind: a folder that answers after its write was abandoned may end up holding a
/// second copy of a report the fallback also took. That is accepted; losing the report,
/// or hanging, is not.
/// </para>
/// </remarks>
public sealed class CrashLog
{
    /// <summary>How many crash files each folder keeps; older ones are removed at startup.</summary>
    public const int KeepCount = 20;

    private const string FilePrefix = "crash-";

    private readonly string _directory;
    private readonly string _fallbackDirectory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

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

    /// <summary>How long one step that can hang is waited for before it is given up on.</summary>
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
        // One report at a time, so the same exception from two threads is written once. A
        // report stuck behind a stalled one goes ahead after a while without that guarantee.
        var locked = false;
        try
        {
            Monitor.TryEnter(_gate, WriteTimeout * 3, ref locked);
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
                if (RunWithin(() => TryWrite(directory, name, bytes)) is { } path)
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
        finally
        {
            if (locked)
            {
                Monitor.Exit(_gate);
            }
        }

        return null;
    }

    /// <summary>The crash folder this log writes to first.</summary>
    public string Folder => _directory;

    /// <summary>
    /// The crash files in <see cref="Folder"/>, newest first; empty when there are none
    /// or the folder cannot be read. Never throws.
    /// </summary>
    public IReadOnlyList<FileInfo> Reports()
    {
        try
        {
            var folder = new DirectoryInfo(_directory);
            return folder.Exists
                ? folder.GetFiles(FilePrefix + "*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .ToList()
                : [];
        }
        catch
        {
            return [];
        }
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
    /// The report. The exception's own description runs under <see cref="WriteTimeout"/>;
    /// everything else is read from the runtime and cannot hang. Each part is guarded, so
    /// one that fails costs only that part.
    /// </summary>
    private string Format(Exception? exception, string source, DateTimeOffset when, int processId, string? databasePath)
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

        if (exception == null)
        {
            text.AppendLine("(no exception object)");
            return text.ToString();
        }

        // ToString includes the inner exceptions and their stack traces.
        var description = RunWithin(exception.ToString);
        Line(text, () => $"{description ?? exception.GetType().FullName + ": (its description could not be read in time)"}");
        if (description == null)
        {
            // StackTrace is virtual too, so it is timed as well.
            Line(text, () => $"{RunWithin(() => exception.StackTrace) ?? "(no stack trace)"}");
        }

        return text.ToString();
    }

    private static void Line(StringBuilder text, Func<FormattableString> part)
    {
        try
        {
            text.AppendLine(FormattableString.Invariant(part()));
        }
        catch
        {
            text.AppendLine("(could not be read)");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own and waits up to
    /// <see cref="WriteTimeout"/> for it. Null when it failed or took too long.
    /// </summary>
    private string? RunWithin(Func<string?> work)
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch
            {
                // Reported as no result.
            }
        })
        {
            IsBackground = true,
            Name = "Crash log",
        };
        thread.Start();
        return thread.Join(WriteTimeout) ? result : null;
    }

    private static string? TryWrite(string directory, string name, byte[] bytes)
    {
        string? created = null;
        try
        {
            Directory.CreateDirectory(directory);

            // CreateNew, so two crashes in the same second never overwrite each other. Only
            // a name that is taken moves on to the next; a failed write fails this folder.
            FileStream? file = null;
            for (var n = 1; n < 100 && file == null; n++)
            {
                var path = Path.Combine(directory, (n == 1 ? name : name + "-" + n) + ".txt");
                try
                {
                    file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    created = path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Taken; try the next name.
                }
            }

            if (file == null)
            {
                return null;
            }

            using (file)
            {
                file.Write(bytes);
            }

            return created;
        }
        catch
        {
            // This folder cannot take it; the caller tries the next. A partial file
            // would pass for a report.
            if (created != null)
            {
                try
                {
                    File.Delete(created);
                }
                catch
                {
                    // Pruning removes it in time.
                }
            }

            return null;
        }
    }
}
