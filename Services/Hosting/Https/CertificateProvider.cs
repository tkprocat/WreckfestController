using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace WreckfestController.Services.Hosting.Https;

/// <summary>The certificate being served, ready for handshakes.</summary>
public sealed record CertificateSnapshot(LoadedCertificate Loaded, SslStreamCertificateContext Context, DateTimeOffset LoadedAt);

/// <summary>What the web admin, the log and the desktop app show. No key material, no password, no path.</summary>
public sealed record HttpsStatus(
    bool Enabled,
    string? Subject,
    IReadOnlyList<string> DnsNames,
    string? Issuer,
    DateTimeOffset? NotAfter,
    bool ExpiresSoon,
    DateTimeOffset? LastRefresh,
    string? Error)
{
    public static HttpsStatus Off { get; } = new(false, null, [], null, null, false, null, null);
}

/// <summary>
/// Loads the HTTPS certificate and keeps it current, outside the TLS handshake: handshakes
/// only read <see cref="Current"/>. Loads at start (where a failure stops the API), then
/// every five minutes (store renewals, missed file events, sleep and resume) and, for a
/// file, soon after it changes. A failed reload keeps the certificate being served, says
/// why, and retries sooner. A replaced certificate stays alive until shutdown, since a
/// handshake may still be using it.
/// </summary>
public sealed class CertificateProvider : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileSettle = TimeSpan.FromSeconds(2);

    private readonly ICertificateSource _source;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger _logger;
    private readonly object _refreshLock = new();
    private readonly List<LoadedCertificate> _retired = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private TimeSpan _retry = FirstRetry;
    private CertificateSnapshot? _current;
    private DateTimeOffset? _lastRefresh;
    private string? _error;
    private bool _disposed;

    public CertificateProvider(ICertificateSource source, Func<DateTimeOffset> now, ILogger logger)
    {
        _source = source;
        _now = now;
        _logger = logger;
        _timer = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The certificate to serve. Set by <see cref="Start"/>; never null after it returns.</summary>
    public CertificateSnapshot Current => _current ?? throw new InvalidOperationException("The certificate has not been loaded.");

    /// <summary>
    /// Loads the certificate, or throws <see cref="HttpsConfigurationException"/>: HTTPS that
    /// is configured but unusable stops the API rather than falling back to HTTP. Then
    /// starts the timer and the file watchers.
    /// </summary>
    public void Start()
    {
        lock (_refreshLock)
        {
            Publish(_source.Load());
        }

        _timer.Change(RefreshInterval, RefreshInterval);
        foreach (var file in _source.WatchedFiles)
        {
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };

            // A renewal writes, renames or replaces the file, often in several steps: wait
            // for it to settle, then load once.
            FileSystemEventHandler changed = (_, _) => Soon(FileSettle);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => Soon(FileSettle);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>Loads again. Serialised; the same certificate found again changes nothing.</summary>
    public void Refresh()
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var loaded = _source.Load();
                if (_current is not null
                    && loaded.Certificate.Thumbprint == _current.Loaded.Certificate.Thumbprint
                    && SameChain(loaded.Chain, _current.Loaded.Chain))
                {
                    // The same certificate again: this copy (and its key container) goes now.
                    Dispose(loaded);
                    _lastRefresh = _now();
                    ClearError();
                    return;
                }

                Publish(loaded);
                _logger.LogInformation("HTTPS certificate replaced: {Subject}, valid until {NotAfter:u}", loaded.Certificate.Subject, loaded.Certificate.NotAfter.ToUniversalTime());
            }
            catch (Exception ex)
            {
                // Runs on the timer: nothing may escape, whatever a certificate holds. Keep
                // serving what works, say why the new one does not, and look again sooner.
                // Only this code's own messages are shown; others can name paths.
                _error = ex is HttpsConfigurationException
                    ? ex.Message
                    : "The certificate could not be reloaded. The desktop app's log has the details.";
                _logger.LogWarning(ex, "HTTPS certificate reload failed; still serving the one loaded {LoadedAt:u}", _current?.LoadedAt);
                var retry = _retry;
                _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, RefreshInterval.Ticks));
                Soon(retry);
            }
        }
    }

    /// <summary>For the log, the web admin and the desktop app.</summary>
    public HttpsStatus Status
    {
        get
        {
            var snapshot = _current;
            if (snapshot is null)
            {
                return HttpsStatus.Off with { Enabled = true, Error = _error };
            }

            var certificate = snapshot.Loaded.Certificate;
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            return new HttpsStatus(
                true,
                certificate.Subject,
                CertificateRules.DnsNames(certificate),
                certificate.Issuer,
                notAfter,
                notAfter - _now() < ExpiryWarning,
                _lastRefresh,
                _error);
        }
    }

    /// <summary>
    /// Whether <see cref="Current"/> may be served now. Once it has expired (every reload
    /// having failed), handshakes are refused: never a fallback to plain HTTP.
    /// </summary>
    public bool IsServable => _current is { } c && _now() <= new DateTimeOffset(c.Loaded.Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);

    private void Publish(LoadedCertificate loaded)
    {
        // Built here, not during a handshake: the chain is resolved once per certificate.
        var context = SslStreamCertificateContext.Create(loaded.Certificate, loaded.Chain, offline: true);
        if (_current is not null)
        {
            _retired.Add(_current.Loaded);
        }

        _current = new CertificateSnapshot(loaded, context, _now());
        _lastRefresh = _current.LoadedAt;
        ClearError();
    }

    private void ClearError()
    {
        _error = null;
        _retry = FirstRetry;
    }

    private void Soon(TimeSpan delay)
    {
        if (!_disposed)
        {
            _timer.Change(delay, RefreshInterval);
        }
    }

    private static void Dispose(LoadedCertificate loaded)
    {
        loaded.Certificate.Dispose();
        foreach (var c in loaded.Chain)
        {
            c.Dispose();
        }
    }

    private static bool SameChain(X509Certificate2Collection a, X509Certificate2Collection b) =>
        a.Count == b.Count
        && a.Cast<X509Certificate2>().Select(c => c.Thumbprint).Order().SequenceEqual(b.Cast<X509Certificate2>().Select(c => c.Thumbprint).Order());

    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        foreach (var loaded in _retired.Append(_current?.Loaded).OfType<LoadedCertificate>())
        {
            loaded.Certificate.Dispose();
            foreach (var c in loaded.Chain)
            {
                c.Dispose();
            }
        }
    }
}
