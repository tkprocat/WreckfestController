using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace WreckfestController.Services.Hosting.Https;

/// <summary>The status the API reports: the provider's, or "off" when HTTPS is not configured.</summary>
public sealed class HttpsStatusSource(CertificateProvider? provider)
{
    public HttpsStatus Status => provider?.Status ?? HttpsStatus.Off;
}

/// <summary>Kestrel's endpoints, from <see cref="ApiEndpoints"/> only.</summary>
public static class HttpsEndpoint
{
    /// <summary>
    /// Listens on the HTTP port and, with a provider, the HTTPS port. Kestrel's own
    /// configuration (Kestrel:Endpoints, URL settings, the development certificate) is set
    /// aside: <c>Api</c> alone decides where the API is reachable.
    /// </summary>
    public static void Configure(KestrelServerOptions options, ApiEndpoints endpoints, CertificateProvider? provider)
    {
        options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
        options.Listen(endpoints.Address, endpoints.HttpPort);
        if (provider is null)
        {
            return;
        }

        options.Listen(endpoints.Address, endpoints.HttpsPort, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            // Reads the provider's snapshot and nothing else: no store or file access, and
            // no chain building, during a handshake.
            OnConnection = _ =>
            {
                if (!provider.IsServable)
                {
                    throw new AuthenticationException("The HTTPS certificate has expired and no usable replacement has been found.");
                }

                return ValueTask.FromResult(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = provider.Current.Context,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
            },
        }));
    }
}
