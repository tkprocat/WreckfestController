namespace WreckfestController.Services;

internal sealed record RestartProcessIdentity(
    int ProcessId, int ParentProcessId, string ExecutablePath, string CommandLine, DateTime CreatedUtc)
{
    public bool IsComplete => ProcessId > 0 && ParentProcessId > 0
        && !string.IsNullOrWhiteSpace(ExecutablePath) && !string.IsNullOrWhiteSpace(CommandLine)
        && CreatedUtc != default;

    public static (RestartProcessIdentity? Process, string Error) SelectReplacement(
        RestartProcessIdentity original, IReadOnlyCollection<int> previousPids,
        IReadOnlyCollection<RestartProcessIdentity?> candidates, DateTime requestedUtc, DateTime originalExitedUtc)
    {
        if (!original.IsComplete)
            return (null, "Original server identity is unavailable.");
        if (candidates.Any(candidate => candidate == null || !candidate.IsComplete))
            return (null, "A replacement candidate's identity could not be verified.");

        // Parent PID alone is insufficient because Windows reuses PIDs. Require
        // creation during the original process's lifetime, after our request, as
        // well as the same executable and complete launch arguments/configuration.
        var matches = candidates.OfType<RestartProcessIdentity>().Where(candidate =>
            !previousPids.Contains(candidate.ProcessId)
            && candidate.ProcessId != original.ProcessId
            && candidate.ParentProcessId == original.ProcessId
            && candidate.CreatedUtc >= requestedUtc
            && candidate.CreatedUtc >= original.CreatedUtc
            && candidate.CreatedUtc <= originalExitedUtc
            && string.Equals(candidate.ExecutablePath, original.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.CommandLine.Trim(), original.CommandLine.Trim(), StringComparison.Ordinal))
            .ToList();

        return matches.Count switch
        {
            1 => (matches[0], string.Empty),
            0 => (null, "No replacement process could be correlated with the original server."),
            _ => (null, "Multiple matching replacement processes were found; refusing ambiguous attachment.")
        };
    }
}
