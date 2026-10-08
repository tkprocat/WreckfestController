using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WreckfestController.Services.Desktop;

/// <summary>
/// What the About tab shows. The version is the csproj's <c>Version</c>, which the build
/// stamps into the assembly; the build time is the file version the same build writes
/// (<c>yyyy.MMdd.HHmm</c>, UTC).
/// </summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/tkprocat/WreckfestController";
    public const string IssuesUrl = RepositoryUrl + "/issues";

    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    /// <summary>The full version, such as 2.0.0.</summary>
    public static string Version =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AppVersion")?.Value
        ?? Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>When this build was made, in UTC; null when the file version is not a build time.</summary>
    public static DateTime? BuiltUtc => ParseBuildTime(Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    internal static DateTime? ParseBuildTime(string? fileVersion) =>
        DateTime.TryParseExact(fileVersion, "yyyy.MMdd.HHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var built)
            ? built
            : null;
}
