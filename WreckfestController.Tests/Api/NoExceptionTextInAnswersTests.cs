using System.IO;
using System.Text.RegularExpressions;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Exception text and local paths never reach the web (#153): what the API answers, and what
/// the services hand it, says something fixed, and the detail goes to the log. The scan reads
/// whole statements (up to the next semicolon, across lines), so a call split over lines is
/// still one thing to check.
/// </summary>
public sealed class NoExceptionTextInAnswersTests
{
    // Any `.Message` interpolated into a string, except a result's (`{stopResult.Message}`),
    // which is one of these answers and already fixed text.
    private static readonly Regex Interpolated = new(@"\{(?![^{}]*[Rr]esult\.Message)[^{}]*\.Message\s*\}", RegexOptions.CultureInvariant);

    // An exception's message handed over as it is: `Refused(ex.Message)`, `Problem(title: ex.Message)`.
    private static readonly Regex Passed = new(@"\b(Refused|Problem|Invalid|Conflict)\(.*\b(ex|e|exception)\.Message\b", RegexOptions.CultureInvariant);

    // A path, folder or raw error interpolated into a failure: `(false, $"... {installDir}")`,
    // `Task.FromResult((false, $"... {error}"))`, `Refused($"... {path}")`.
    private static readonly Regex DetailInFailure = new(
        @"(\(false,|\bRefused\(|\bProblem\().*\$""[^""]*\{[^{}]*(Path|Dir|Directory|Folder|File|error)\b[^{}]*\}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Reviewed exceptions, by file and a piece of the statement, with why each is safe.
    private static readonly (string File, string Statement, string Why)[] Allowed =
    [
        ("SettingsResponses.cs", "title: ex.Message", "SettingsUnavailableException carries one fixed sentence of ours, no path."),
        ("NativeConsoleHookInjector.cs", "new Win32Exception(errorCode).Message", "Its error is logged by InjectedHookOutputReader, never answered."),
        ("ServerManager.cs", "{selection.Error}", "RestartProcessIdentity's errors are four fixed sentences of ours, no path."),
    ];

    // Where answers are built. Services/Desktop is the WPF app's own log view, which is local.
    private static readonly string[] Folders = ["Controllers", "Services", "Hubs", "Api"];

    [Fact]
    public void NoAnswerCarriesExceptionTextOrAPath()
    {
        var root = RepositoryRoot();
        var offenders = Folders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Desktop{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => Offenders(File.ReadAllText(file))
                .Where(o => !Allowed.Any(a => Path.GetFileName(file) == a.File && o.Statement.Contains(a.Statement, StringComparison.Ordinal)))
                .Select(o => $"{Path.GetRelativePath(root, file)}:{o.Line}: {o.Statement}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Exception text or a path in an answer; log it and say something fixed instead:\n" + string.Join("\n", offenders));
    }

    // The scan itself: each form it must catch, including the ones that slipped past a
    // line-by-line version.
    [Theory]
    [InlineData("return this.Refused($\"Failed to read basic config: {ex.Message}\");")]
    [InlineData("return controller.Problem(\n    statusCode: 503,\n    title: ex.Message);")]
    [InlineData("return this.Refused(ex.Message);")]
    [InlineData("return Task.FromResult((false, $\"Console hook injection failed: {error}\"));")]
    [InlineData("return Task.FromResult((false, $\"Failed: {dllPath}\"));")]
    [InlineData("return (false, $\"Wreckfest Working Directory not found: {installDir}. Please configure.\");")]
    [InlineData("return $\"{new Win32Exception(errorCode).Message} ({errorCode})\";")]
    public void TheScan_CatchesEachForm(string source) =>
        Assert.NotEmpty(Offenders(source));

    [Theory]
    [InlineData("return (false, $\"Failed to restart: {stopResult.Message}\");")]
    [InlineData("_logger.LogError(ex, \"Failed to read {Path}\", path);")]
    [InlineData("_logger.LogWarning(\"Injection failed: {Error}\", error);")]
    [InlineData("return (false, \"The server could not be started. The desktop app's log has the details.\");")]
    [InlineData("// return this.Refused(ex.Message);")]
    public void TheScan_LeavesSafeFormsAlone(string source) =>
        Assert.Empty(Offenders(source));

    private static IEnumerable<(int Line, string Statement)> Offenders(string source)
    {
        // Line comments out; strings keep their text (a comment inside a string is rare here).
        var code = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        var start = 0;
        foreach (var raw in code.Split(';'))
        {
            // The line the statement ends on: where its semicolon is.
            start += raw.Length + 1;
            var line = code[..Math.Min(start, code.Length)].Count(c => c == '\n') + 1;
            var statement = Regex.Replace(raw, @"\s+", " ").Trim();
            if (statement.Length == 0 || statement.Contains("_logger.Log", StringComparison.Ordinal))
            {
                continue;
            }

            if (Interpolated.IsMatch(statement) || Passed.IsMatch(statement) || DetailInFailure.IsMatch(statement))
            {
                yield return (line, statement.Length > 160 ? statement[..160] + "..." : statement);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WreckfestController.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }
}
