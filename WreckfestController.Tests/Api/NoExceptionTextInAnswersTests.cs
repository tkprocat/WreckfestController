using System.IO;
using System.Text.RegularExpressions;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Exception text can hold local paths and framework internals, and the web never sees
/// local paths (#153). What the API answers, and what the services hand it, must not
/// interpolate an exception's Message; the exception goes to the log instead.
/// </summary>
public sealed class NoExceptionTextInAnswersTests
{
    // Any `.Message` interpolated into a string: `{ex.Message}`, `{new Win32Exception(code).Message}`.
    // A result's Message (`{stopResult.Message}`) is one of these answers, already fixed text.
    private static readonly Regex Interpolated = new(@"\{(?![^{}]*[Rr]esult\.Message)[^{}]*\.Message\s*\}", RegexOptions.CultureInvariant);

    // Reviewed exceptions, by file and the text of the line, with why each is safe.
    private static readonly (string File, string Line, string Why)[] Allowed =
    [
        ("SettingsResponses.cs", "title: ex.Message", "SettingsUnavailableException carries one fixed sentence of ours, no path."),
        ("NativeConsoleHookInjector.cs", "new Win32Exception(errorCode).Message", "Its error is logged by InjectedHookOutputReader, never answered."),
    ];

    // An exception's message handed over as it is: `Refused(ex.Message)`, `title: ex.Message`.
    private static readonly Regex Passed = new(@"(Refused|Problem|Invalid|Conflict)\([^;]*\b(ex|e|exception)\.Message", RegexOptions.CultureInvariant);

    // A path or folder interpolated into a result: `return (false, $"... {installDir}")`.
    private static readonly Regex PathInResult = new(
        @"(return \(false,|Refused\(|Problem\().*\$""[^""]*\{[^{}]*(Path|Dir|Directory|Folder|File)\b[^{}]*\}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Where answers are built. Services/Desktop is the WPF app's own log view, which is local.
    private static readonly string[] Folders = ["Controllers", "Services", "Hubs", "Api"];

    [Fact]
    public void NoAnswerInterpolatesAnExceptionsMessage()
    {
        var root = RepositoryRoot();
        var offenders = Folders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Desktop{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(x => !x.line.Contains("_logger.Log", StringComparison.Ordinal))
            .Where(x => Interpolated.IsMatch(x.line) || Passed.IsMatch(x.line) || PathInResult.IsMatch(x.line))
            .Where(x => !Allowed.Any(a => Path.GetFileName(x.file) == a.File && x.line.Contains(a.Line, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.number}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Exception text in an answer; log the exception and say something fixed instead:\n" + string.Join("\n", offenders));
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
