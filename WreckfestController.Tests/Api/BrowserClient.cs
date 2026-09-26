using System.Net;
using System.Net.Http.Json;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Behaves like the SPA's fetch wrapper: keeps cookies between requests and copies the
/// <c>XSRF-TOKEN</c> cookie into the <c>X-XSRF-TOKEN</c> header on unsafe methods.
/// </summary>
public sealed class BrowserClient : IDisposable
{
    private readonly Handler _handler;

    public BrowserClient(HttpMessageHandler server, Uri baseAddress)
    {
        _handler = new Handler { InnerHandler = server };
        Http = new HttpClient(_handler) { BaseAddress = baseAddress };
    }

    public HttpClient Http { get; }

    public CookieContainer Cookies => _handler.Cookies;

    /// <summary>Set false to act like a page that never fetched or sent the token.</summary>
    public bool SendXsrfHeader
    {
        get => _handler.SendXsrfHeader;
        set => _handler.SendXsrfHeader = value;
    }

    public async Task FetchAntiforgeryAsync()
    {
        using var response = await Http.GetAsync("/api/auth/antiforgery", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>The SPA's login sequence: token, sign in, fresh token for the new identity.</summary>
    public async Task<HttpResponseMessage> LoginAsync(string login, string password, bool remember = false)
    {
        await FetchAntiforgeryAsync();
        var response = await Http.PostAsJsonAsync(
            "/api/auth/login",
            new { login, password, remember },
            TestContext.Current.CancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await FetchAntiforgeryAsync();
        }

        return response;
    }

    public async Task SignInAsync(string login, string password = ApiTestHost.Password)
    {
        using var response = await LoginAsync(login, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose() => Http.Dispose();

    private sealed class Handler : DelegatingHandler
    {
        public CookieContainer Cookies { get; } = new();

        public bool SendXsrfHeader { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var cookieHeader = Cookies.GetCookieHeader(uri);
            if (cookieHeader.Length > 0)
            {
                request.Headers.Add("Cookie", cookieHeader);
            }

            var method = request.Method;
            if (SendXsrfHeader && method != HttpMethod.Get && method != HttpMethod.Head
                && Cookies.GetCookies(uri)[ApiAuthentication.XsrfCookieName] is { } xsrf)
            {
                request.Headers.Add(ApiAuthentication.XsrfHeaderName, xsrf.Value);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    Cookies.SetCookies(uri, setCookie);
                }
            }

            return response;
        }
    }
}
