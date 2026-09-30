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
    // `{ex.Message}` or `{exception.Message}` inside an interpolated string.
    private static readonly Regex Interpolated = new(@"\{\s*(ex|e|exception|error)\.Message\s*\}", RegexOptions.CultureInvariant);

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
            .Where(x => Interpolated.IsMatch(x.line))
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
