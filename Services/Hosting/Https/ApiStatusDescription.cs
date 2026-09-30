namespace WreckfestController.Services.Hosting.Https;

/// <summary>The web API's state in a few lines, for the desktop app. No key material, password or path.</summary>
public static class ApiStatusDescription
{
    public static string Describe(bool running, string baseUrl, string? startError, HttpsStatus? https, DateTimeOffset now)
    {
        if (startError is not null)
        {
            return $"The web API did not start: {startError}";
        }

        if (!running)
        {
            return "The web API is off (Api:Enabled is false in the startup settings), or still starting.";
        }

        var lines = new List<string> { $"Listening on {baseUrl}." };
        if (https is null || !https.Enabled)
        {
            lines.Add("HTTPS is off: remote browsers use plain HTTP unless a reverse proxy adds HTTPS.");
            return string.Join("\n", lines);
        }

        if (https.Subject is not null)
        {
            var days = https.NotAfter is { } until ? (int)Math.Floor((until - now).TotalDays) : 0;
            lines.Add($"HTTPS certificate: {string.Join(", ", https.DnsNames.DefaultIfEmpty(https.Subject))}, from {https.Issuer}.");
            lines.Add(https.ExpiresSoon
                ? $"It expires on {https.NotAfter:yyyy-MM-dd} ({days} days): renew it."
                : $"Valid until {https.NotAfter:yyyy-MM-dd}.");
        }

        if (https.Error is not null)
        {
            lines.Add($"Last reload failed: {https.Error}");
        }

        return string.Join("\n", lines);
    }
}
