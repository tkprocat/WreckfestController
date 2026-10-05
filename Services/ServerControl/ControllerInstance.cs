using System.Security.Cryptography;
using System.Text;

namespace WreckfestController.Services.ServerControl;

/// <summary>
/// This controller's identity among several on one PC. Every server it starts carries
/// <see cref="Argument"/> on its command line, so the server is identifiable in the
/// process list and the controller can tell its own server from another controller's,
/// even when both run from the same install folder (issue #201).
/// </summary>
/// <remarks>
/// <para>
/// The id is derived from the database path rather than stored: the first 8 hex digits of
/// SHA-256 over the full, lower-cased path. It is the same on every start, so the controller
/// finds its server again after it restarts; and two controllers cannot share a database,
/// so it is unique among them.
/// </para>
/// <para>
/// Moving the database changes the id, and a server started under the old one then looks
/// foreign until it is restarted. That is deliberate: a moved database is a different
/// instance.
/// </para>
/// <para>
/// <c>/restart</c> keeps the command line exactly (<see cref="RestartProcessIdentity"/>
/// requires it), so the marker survives restarts. Confirmed live on build 1.308438, which
/// also accepts the unknown argument.
/// </para>
/// </remarks>
public sealed class ControllerInstance
{
    public const string MarkerName = "-wfc_controller";

    public ControllerInstance(string databasePath)
    {
        Id = IdFor(databasePath);
    }

    /// <summary>8 lower-case hex digits, such as <c>3f9a1c07</c>.</summary>
    public string Id { get; }

    /// <summary>The argument added to the server's command line: <c>-wfc_controller=3f9a1c07</c>.</summary>
    public string Argument => $"{MarkerName}={Id}";

    public static string IdFor(string databasePath)
    {
        var normalized = Path.GetFullPath(databasePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>
    /// <paramref name="arguments"/> with this controller's marker appended. The arguments
    /// themselves are left exactly as configured: rewriting them would mean re-quoting, and a
    /// quoted path can contain anything. A marker already in them does no harm, because the
    /// last marker on a command line is the one that counts
    /// (<see cref="WindowsCommandLine.ControllerMarker"/>).
    /// </summary>
    /// <returns>False when the arguments end inside an unclosed quote, which would swallow
    /// the marker into the last argument.</returns>
    public bool TryAddTo(string? arguments, out string withMarker)
    {
        var trimmed = (arguments ?? string.Empty).Trim();
        withMarker = trimmed.Length == 0 ? Argument : $"{trimmed} {Argument}";

        // Split as the server will: the marker must come out as its own last argument.
        var parsed = WindowsCommandLine.Split("x " + withMarker);
        return parsed.Count > 1 && parsed[^1] == Argument;
    }
}
