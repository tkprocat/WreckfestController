using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WreckfestPortal.Accounts;

namespace WreckfestPortal.Tests;

/// <summary>The web accounts on the portal: signing in, global admins, and users granted servers.</summary>
public sealed class AccountTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NewUser(string userName, bool isAdmin = false, params string[] servers) => new
    {
        userName,
        email = userName + "@example.com",
        password = AccountTestHost.Password,
        isAdmin,
        servers,
    };

    private static async Task<JsonElement> CreateUserAsync(AccountTestHost.Browser admin, object user)
    {
        using var response = await admin.Http.PostAsJsonAsync("/api/users", user, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task WithNoAccounts_SetupIsRequired()
    {
        await using var host = await AccountTestHost.StartAsync();
        using var client = host.CreateClient();

        var state = await client.GetFromJsonAsync<JsonElement>("/api/auth/state", Ct);

        Assert.False(state.GetProperty("authenticated").GetBoolean());
        Assert.True(state.GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task TheCreatedAdmin_SignsIn_WithACookieForTheWholeSite()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var browser = await host.SignInAsync("admin");

        var me = await browser.Http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        var state = await browser.Http.GetFromJsonAsync<JsonElement>("/api/auth/state", Ct);

        Assert.Equal("admin", me.GetProperty("userName").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.False(state.GetProperty("setupRequired").GetBoolean());
        var cookie = Assert.Single(browser.Cookies.GetAllCookies(), c => c.Name == PortalAccounts.CookieName);
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.HttpOnly);
    }

    [Theory]
    [InlineData("admin", "wrong password!")]
    [InlineData("nobody", AccountTestHost.Password)]
    public async Task AWrongLogin_IsRefused(string login, string password)
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.LoginAsync(login, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheEleventhSignInInAMinute_IsRefused()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var browser = host.CreateBrowser();

        for (var attempt = 0; attempt < PortalRateLimits.LoginPermitsPerWindow; attempt++)
        {
            using var _ = await browser.LoginAsync("nobody", "x");
        }

        using var response = await browser.LoginAsync("admin", AccountTestHost.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task AChangeWithoutTheAntiforgeryToken_IsRefused()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var browser = await host.SignInAsync("admin");
        browser.SendXsrfHeader = false;

        using var response = await browser.Http.PostAsJsonAsync("/api/users", NewUser("someone"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AntiforgeryFilter.FailureTitle, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUser_GetsTheServersTheyWereGiven_InTheSettingsSpelling()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");

        var user = await CreateUserAsync(admin, NewUser("racer", false, "TVTP2"));
        var other = await CreateUserAsync(admin, NewUser("boss", true, "vanilla"));

        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(["tvtp2"], user.GetProperty("servers").EnumerateArray().Select(s => s.GetString()));
        // An admin has every server; a list would only go stale.
        Assert.Empty(other.GetProperty("servers").EnumerateArray());
    }

    [Fact]
    public async Task AServerThePortalDoesNotList_CannotBeGiven()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");

        using var response = await admin.Http.PostAsJsonAsync("/api/users", NewUser("racer", false, "nope"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nope", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserWhoIsNotAnAdmin_CannotManageAccounts_ButHasAProfile()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using (var admin = await host.SignInAsync("admin"))
        {
            await CreateUserAsync(admin, NewUser("racer", false, "vanilla"));
        }

        using var racer = await host.SignInAsync("racer");
        using var list = await racer.Http.GetAsync("/api/users", Ct);
        using var openApi = await racer.Http.GetAsync("/openapi/v1.json", Ct);
        var me = await racer.Http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, openApi.StatusCode);
        Assert.Equal(["vanilla"], me.GetProperty("servers").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Anonymous_GetsNothingButTheSignInPages()
    {
        await using var host = await AccountTestHost.StartAsync();
        using var client = host.CreateClient();

        using var users = await client.GetAsync("/api/users", Ct);
        using var me = await client.GetAsync("/api/auth/me", Ct);
        using var servers = await client.GetAsync("/api/servers", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, users.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.OK, servers.StatusCode);
    }

    // The sign-in is rebuilt from the account on every request.
    [Fact]
    public async Task ALostAdminRole_AppliesOnTheNextRequest()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");
        var boss = await CreateUserAsync(admin, NewUser("boss", true));
        using var bossBrowser = await host.SignInAsync("boss");
        using (var before = await bossBrowser.Http.GetAsync("/api/users", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var demote = await admin.Http.PutAsJsonAsync(
            "/api/users/" + boss.GetProperty("id").GetString(),
            new { userName = "boss", email = "boss@example.com", isAdmin = false, servers = new[] { "vanilla" } },
            Ct);
        using var after = await bossBrowser.Http.GetAsync("/api/users", Ct);

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task NobodyCanDeleteLockOrDemoteThemself()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");
        await CreateUserAsync(admin, NewUser("other", true));
        var me = await admin.Http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        var self = "/api/users/" + me.GetProperty("id").GetString();

        using var delete = await admin.Http.DeleteAsync(self, Ct);
        using var lockSelf = await admin.Http.PostAsync(self + "/lock", content: null, Ct);
        using var demote = await admin.Http.PutAsJsonAsync(self, new { userName = "admin", email = "admin@example.com", isAdmin = false }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lockSelf.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
    }

    // Two admins, each removing the other at the same moment. Both are signed in when they
    // start; SQLite's write lock makes one wait until the other commits, and by then it is
    // the last admin, so it is refused. Without the lock both would go ahead and leave none.
    [Theory]
    [InlineData("delete")]
    [InlineData("lock")]
    [InlineData("demote")]
    public async Task TwoAdminsRemovingEachOtherAtOnce_LeaveOneAdmin(string action)
    {
        var rendezvous = new TransactionRendezvous();
        await using var host = await AccountTestHost.StartAsync(services =>
            services.ConfigureDbContext<PortalDbContext>(options => options.AddInterceptors(rendezvous)));
        await host.CreateAdminAsync("first");
        await host.CreateAdminAsync("second");
        using var first = await host.SignInAsync("first");
        using var second = await host.SignInAsync("second");
        var firstId = (await first.Http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("id").GetString();
        var secondId = (await second.Http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("id").GetString();

        rendezvous.Arm();
        var responses = await Task.WhenAll(
            Remove(first, secondId!, "second"),
            Remove(second, firstId!, "first"));

        Assert.True(rendezvous.Met, "The two requests did not overlap.");
        var statuses = responses.Select(r => r.StatusCode).Order().ToList();
        var done = action == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK;
        Assert.Equal(new[] { done, HttpStatusCode.Conflict }.Order(), statuses);
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var admins = db.Users.Where(u => u.IsAdmin).AsEnumerable().Where(u => u.LockoutEnd != DateTimeOffset.MaxValue);
        Assert.Single(admins);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Task<HttpResponseMessage> Remove(AccountTestHost.Browser by, string id, string userName) => action switch
        {
            "delete" => by.Http.DeleteAsync("/api/users/" + id, Ct),
            "lock" => by.Http.PostAsync("/api/users/" + id + "/lock", content: null, Ct),
            _ => by.Http.PutAsJsonAsync("/api/users/" + id, new { userName, email = userName + "@example.com", isAdmin = false }, Ct),
        };
    }

    [Fact]
    public async Task ALockedAccount_IsSignedOutAtOnce()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");
        var racer = await CreateUserAsync(admin, NewUser("racer", false, "vanilla"));
        using var racerBrowser = await host.SignInAsync("racer");

        using var locked = await admin.Http.PostAsync("/api/users/" + racer.GetProperty("id").GetString() + "/lock", content: null, Ct);
        using var me = await racerBrowser.Http.GetAsync("/api/auth/me", Ct);
        using var again = await host.CreateBrowser().LoginAsync("racer", AccountTestHost.Password);

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Locked, again.StatusCode);
    }

    [Fact]
    public async Task CreateAdmin_RejectsAShortPassword()
    {
        await using var host = await AccountTestHost.StartAsync();
        using var output = new StringWriter();

        var exit = await CreateAdminCommand.CreateAsync(host.App.Services, "admin", "admin@example.com", "short", output);

        Assert.Equal(1, exit);
        Assert.Contains("10", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAdmin_AsksTwice_AndTakesAPasswordThatMatches()
    {
        var answers = new Queue<string>(["correct horse battery", "correct horse battery"]);
        var prompts = new List<string>();

        var password = CreateAdminCommand.ConfirmPassword(prompt => { prompts.Add(prompt); return answers.Dequeue(); }, TextWriter.Null);

        Assert.Equal("correct horse battery", password);
        Assert.Equal(["Password: ", "Confirm password: "], prompts);
    }

    // The password is never shown, so a typo in it would otherwise be found only at sign-in.
    [Fact]
    public void CreateAdmin_RefusesPasswordsThatDiffer()
    {
        var answers = new Queue<string>(["correct horse battery", "correct horse batery"]);
        using var output = new StringWriter();

        var password = CreateAdminCommand.ConfirmPassword(_ => answers.Dequeue(), output);

        Assert.Null(password);
        Assert.Contains("do not match", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public async Task CreateAdmin_WithNoPasswordPipedIn_SaysSo(string piped)
    {
        using var output = new StringWriter();

        var exit = await CreateAdminCommand.RunAsync(["create-admin", "admin", "admin@example.com"], new StringReader(piped), output);

        Assert.Equal(1, exit);
        Assert.Contains("No password given.", output.ToString(), StringComparison.Ordinal);
    }
}
