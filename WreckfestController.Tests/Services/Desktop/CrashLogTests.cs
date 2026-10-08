using System.Globalization;
using WreckfestController.Services.Desktop;
using WreckfestController.Tests.Services.Cups;

namespace WreckfestController.Tests.Services.Desktop;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfc-crashlog-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();

    private string Crashes => Path.Combine(_root, "crashes");
    private string Fallback => Path.Combine(_root, "temp");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CrashLog Log(string? directory = null) => new(directory ?? Crashes, Fallback, _clock);

    private static Exception Thrown()
    {
        try
        {
            try
            {
                throw new FormatException("inner cause");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("DialogHost is already open.", inner);
            }
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Writes_one_file_named_for_the_time_and_process_with_the_whole_exception()
    {
        var log = Log();
        log.DatabasePath = @"D:\Servers\Second\controller.db";

        var path = log.Write(Thrown(), "Unhandled exception on the UI thread");

        var expectedName = string.Create(
            CultureInfo.InvariantCulture,
            $"crash-{_clock.GetLocalNow():yyyy-MM-dd_HH-mm-ss}-pid{Environment.ProcessId}.txt");
        Assert.Equal(Path.Combine(Crashes, expectedName), path);
        var text = File.ReadAllText(path!);
        Assert.Contains("Unhandled exception on the UI thread", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\Servers\Second\controller.db", text, StringComparison.Ordinal);
        Assert.Contains("DialogHost is already open.", text, StringComparison.Ordinal);
        Assert.Contains("inner cause", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), text, StringComparison.Ordinal);
        Assert.Contains(AppInfo.Version, text, StringComparison.Ordinal);
    }

    // The dispatcher reports it, then the app domain does as the process goes down.
    [Fact]
    public void The_same_exception_reported_twice_is_written_once()
    {
        var log = Log();
        var ex = Thrown();

        Assert.NotNull(log.Write(ex, "Unhandled exception on the UI thread"));
        Assert.Null(log.Write(ex, "Unhandled exception"));

        Assert.Single(Directory.GetFiles(Crashes));
    }

    [Fact]
    public void Two_crashes_in_the_same_second_get_a_file_each()
    {
        var log = Log();

        var first = log.Write(Thrown(), "one");
        var second = log.Write(Thrown(), "two");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(Crashes).Length);
    }

    [Fact]
    public void Falls_back_to_temp_when_the_crash_folder_cannot_be_made()
    {
        // A file where the folder should be.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Crashes, "in the way");

        var path = Log().Write(Thrown(), "Unhandled exception");

        Assert.Equal(Fallback, Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Gives_up_quietly_when_nowhere_can_be_written()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Crashes, "in the way");
        File.WriteAllText(Fallback, "in the way too");

        Assert.Null(Log().Write(Thrown(), "Unhandled exception"));
        Assert.Null(Log().Write(null, "No exception object"));
    }

    [Fact]
    public void Prune_keeps_the_newest_crash_files_and_nothing_else_is_touched()
    {
        Directory.CreateDirectory(Crashes);
        var start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < CrashLog.KeepCount + 5; i++)
        {
            var file = Path.Combine(Crashes, $"crash-{i:D2}.txt");
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, start.AddMinutes(i));
        }

        var other = Path.Combine(Crashes, "notes.txt");
        File.WriteAllText(other, "mine");

        Log().Prune();

        var left = Directory.GetFiles(Crashes, "crash-*.txt").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(CrashLog.KeepCount, left.Count);
        Assert.Equal("crash-05.txt", left[0]);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Prune_without_a_crash_folder_does_nothing()
    {
        Log().Prune();

        Assert.False(Directory.Exists(Crashes));
    }
}
