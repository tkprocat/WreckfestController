namespace WreckfestController.Services;

/// <summary>
/// How the server responds to track-change commands. Mirrors the
/// <see cref="ServerOutputModes"/> const-string idiom rather than an enum, because
/// the value round-trips through JSON settings and unknown values must degrade
/// rather than throw.
/// </summary>
public static class VoteModes
{
    /// <summary>Track commands are disabled entirely.</summary>
    public const string Off = "Off";

    /// <summary>Track commands start a vote (the original behaviour).</summary>
    public const string Voting = "Voting";

    /// <summary>Track commands apply immediately, with no vote.</summary>
    public const string Direct = "Direct";

    /// <summary>
    /// Resolves a configured mode string, falling back to the legacy
    /// <c>Vote:Enabled</c> boolean so settings files written before the mode
    /// setting existed keep working without a migration pass.
    /// </summary>
    /// <param name="value">The configured Vote:Mode value. May be null, blank or unrecognised.</param>
    /// <param name="legacyEnabled">The configured Vote:Enabled value, if present.</param>
    public static string Normalize(string? value, bool? legacyEnabled = null)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            if (string.Equals(trimmed, Off, StringComparison.OrdinalIgnoreCase))
            {
                return Off;
            }

            if (string.Equals(trimmed, Voting, StringComparison.OrdinalIgnoreCase))
            {
                return Voting;
            }

            if (string.Equals(trimmed, Direct, StringComparison.OrdinalIgnoreCase))
            {
                return Direct;
            }
        }

        // Blank or unrecognised: fall back to the legacy flag, then to the historical
        // default of Vote:Enabled being true.
        if (legacyEnabled == false)
        {
            return Off;
        }

        return Voting;
    }
}
