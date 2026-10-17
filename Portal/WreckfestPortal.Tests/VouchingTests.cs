using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WreckfestPortal.Accounts;

namespace WreckfestPortal.Tests;

/// <summary>
/// The portal vouching for its users to the controllers: a token, for the server being
/// called, only for someone who may manage it, and never one the visitor brought.
/// </summary>
public sealed class VouchingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Controller _controller = new();

    private Task<AccountTestHost> StartAsync() =>
        AccountTestHost.StartAsync(services => services.AddSingleton(new HttpMessageInvoker(_controller)));

    private static async Task CreateUserAsync(AccountTestHost host, string userName, bool isAdmin, params string[] servers)
    {
        using var admin = await host.SignInAsync("admin");
        using var created = await admin.Http.PostAsJsonAsync("/api/users", new
        {
            userName,
            email = userName + "@example.com",
            password = AccountTestHost.Password,
            displayName = "The " + userName,
            isAdmin,
            servers,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task AUserGrantedTheServer_IsVouchedFor_ToThatServerAlone()
    {
        await using var host = await StartAsync();
        await host.CreateAdminAsync();
        await CreateUserAsync(host, "racer", false, "vanilla");
        using var racer = await host.SignInAsync("racer");

        using var response = await racer.Http.GetAsync("/vanilla/api/server/status", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = await ValidateAsync(Assert.Single(_controller.Requests).Bearer, "vanilla", AccountTestHost.VanillaSecret);
        Assert.Equal("racer", claims["name"]);
        Assert.Equal("The racer", claims[ControllerTokens.DisplayNameClaim]);
        // Never for another server: its secret does not sign it, nor is it addressed there.
        Assert.False((await new JsonWebTokenHandler().ValidateTokenAsync(
            _controller.Requests[0].Bearer, Parameters("tvtp2", AccountTestHost.Tvtp2Secret))).IsValid);
    }

    [Fact]
    public async Task AUserNotGrantedTheServer_ReachesItAsAnyoneWould()
    {
        await using var host = await StartAsync();
        await host.CreateAdminAsync();
        await CreateUserAsync(host, "racer", false, "vanilla");
        using var racer = await host.SignInAsync("racer");

        using var response = await racer.Http.GetAsync("/tvtp2/api/public/overview", Ct);

        Assert.Null(Assert.Single(_controller.Requests).Bearer);
    }

    [Theory]
    [InlineData("vanilla")]
    [InlineData("tvtp2")]
    public async Task AGlobalAdmin_IsVouchedFor_OnEveryServer(string server)
    {
        await using var host = await StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");

        using var response = await admin.Http.GetAsync($"/{server}/api/server/status", Ct);

        var secret = server == "vanilla" ? AccountTestHost.VanillaSecret : AccountTestHost.Tvtp2Secret;
        var claims = await ValidateAsync(Assert.Single(_controller.Requests).Bearer, server, secret);
        Assert.Equal("admin", claims["name"]);
    }

    // The controller believes the token alone, so one a visitor brought must never pass.
    [Fact]
    public async Task ATokenTheVisitorBrought_IsNotPassedOn()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer forged.token.here");

        using var response = await client.GetAsync("/vanilla/api/server/status", Ct);

        Assert.Null(Assert.Single(_controller.Requests).Bearer);
    }

    // The portal's cookie alone must not make a change: a page elsewhere cannot read the
    // antiforgery token, so it cannot send it.
    [Fact]
    public async Task AChangeInTheUsersName_NeedsTheAntiforgeryToken()
    {
        await using var host = await StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");

        admin.SendXsrfHeader = false;
        using var refused = await admin.Http.PostAsync("/vanilla/api/server/start", content: null, Ct);
        admin.SendXsrfHeader = true;
        using var allowed = await admin.Http.PostAsync("/vanilla/api/server/start", content: null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(AntiforgeryFilter.FailureTitle, await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("POST", forwarded.Method);
        Assert.NotNull(forwarded.Bearer);
    }

    // SignalR's client opens with a POST (negotiate), and long polling sends POSTs, without the
    // antiforgery header: a signed-in admin's live updates must still connect, as admin. The
    // hub has nothing a client can change.
    [Fact]
    public async Task TheHubsPosts_NeedNoAntiforgeryToken_AndAreVouchedFor()
    {
        await using var host = await StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");
        admin.SendXsrfHeader = false;

        using var negotiate = await admin.Http.PostAsync("/vanilla/hubs/server/negotiate?negotiateVersion=1", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("/hubs/server/negotiate", forwarded.Path);
        Assert.NotNull(forwarded.Bearer);
    }

    // Anonymous changes (the public AI assistant's questions) carry no one's authority.
    [Fact]
    public async Task AnAnonymousChange_IsForwarded_WithoutAToken()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/vanilla/api/public/assistant/chat", new { messages = Array.Empty<object>() }, Ct);

        Assert.Null(Assert.Single(_controller.Requests).Bearer);
    }

    [Fact]
    public async Task ALockedAccount_IsVouchedForNoLonger()
    {
        await using var host = await StartAsync();
        await host.CreateAdminAsync();
        await CreateUserAsync(host, "racer", false, "vanilla");
        using var racer = await host.SignInAsync("racer");
        using (var admin = await host.SignInAsync("admin"))
        {
            var list = await admin.Http.GetFromJsonAsync<JsonElement>("/api/users", Ct);
            var id = list.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "racer").GetProperty("id").GetString();
            using var locked = await admin.Http.PostAsync($"/api/users/{id}/lock", content: null, Ct);
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        }

        using var response = await racer.Http.GetAsync("/vanilla/api/server/status", Ct);

        Assert.Null(Assert.Single(_controller.Requests).Bearer);
    }

    private static TokenValidationParameters Parameters(string server, string secret) => new()
    {
        ValidIssuer = ControllerTokens.Issuer,
        ValidAudience = server,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
    };

    private static async Task<IDictionary<string, object>> ValidateAsync(string? token, string server, string secret)
    {
        Assert.NotNull(token);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, Parameters(server, secret));
        Assert.True(result.IsValid, result.Exception?.Message);
        var expires = ((JsonWebToken)result.SecurityToken).ValidTo;
        Assert.InRange(expires, DateTime.UtcNow, DateTime.UtcNow + ControllerTokens.Lifetime + TimeSpan.FromSeconds(5));
        return result.Claims;
    }

    private sealed record Received(string Method, string Path, string? Bearer);

    /// <summary>Stands in for the controllers: records what each request carried.</summary>
    private sealed class Controller : HttpMessageHandler
    {
        public List<Received> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Received(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
