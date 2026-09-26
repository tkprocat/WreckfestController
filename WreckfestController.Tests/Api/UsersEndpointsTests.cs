using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WreckfestController.Tests.Api;

/// <summary>/api/users: account management by any signed-in admin or an API-key script.</summary>
public class UsersEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_CannotListUsers()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/users", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_ThenList_ShowsTheAccount_WithoutSecrets()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var create = await client.PostAsJsonAsync("/api/users", new
        {
            userName = "marshal",
            email = "marshal@example.com",
            password = ApiTestHost.Password,
            displayName = "Track Marshal",
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/users", Ct);
        var user = Assert.Single(list.EnumerateArray());
        Assert.Equal("marshal", user.GetProperty("userName").GetString());
        Assert.Equal("Track Marshal", user.GetProperty("displayName").GetString());
        var raw = list.GetRawText();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_ReportsWhichFieldIsWrong()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync("taken");
        using var client = host.CreateAuthenticatedClient();

        using var duplicate = await client.PostAsJsonAsync("/api/users", new
        {
            userName = "taken",
            email = "new@example.com",
            password = ApiTestHost.Password,
        }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(duplicate, "userName");

        using var shortPassword = await client.PostAsJsonAsync("/api/users", new
        {
            userName = "fresh",
            email = "fresh@example.com",
            password = "short",
        }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(shortPassword, "password");
    }

    [Fact]
    public async Task Rename_EndsTheRenamedUsersSessions_ButNotTheCallers()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        var other = await host.CreateUserAsync("other");
        using var admin = host.CreateBrowser();
        using var otherBrowser = host.CreateBrowser();
        await admin.SignInAsync("admin");
        await otherBrowser.SignInAsync("other");

        using var rename = await admin.Http.PutAsJsonAsync($"/api/users/{other.Id}", new
        {
            userName = "renamed",
            email = "other@example.com",
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        using var otherStatus = await otherBrowser.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherStatus.StatusCode);
        using var adminStatus = await admin.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.OK, adminStatus.StatusCode);
    }

    [Fact]
    public async Task RenamingYourself_KeepsYourSession()
    {
        await using var host = await ApiTestHost.StartAsync();
        var admin = await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var rename = await browser.Http.PutAsJsonAsync($"/api/users/{admin.Id}", new
        {
            userName = "chief",
            email = "admin@example.com",
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var state = await AuthEndpointsTests.GetStateAsync(browser);
        Assert.Equal("chief", state.GetProperty("user").GetProperty("userName").GetString());
    }

    // Two accounts, so the self check is tested apart from the last-account one.
    [Fact]
    public async Task Delete_RefusesYourself()
    {
        await using var host = await ApiTestHost.StartAsync();
        var admin = await host.CreateUserAsync();
        await host.CreateUserAsync("other");
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var self = await browser.Http.DeleteAsync($"/api/users/{admin.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
    }

    // A script has no account of its own, so only the count protects the last one.
    [Fact]
    public async Task Delete_RefusesTheLastAccount()
    {
        await using var host = await ApiTestHost.StartAsync();
        var admin = await host.CreateUserAsync();
        using var script = host.CreateAuthenticatedClient();

        using var last = await script.DeleteAsync($"/api/users/{admin.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesAnotherAccount()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        var other = await host.CreateUserAsync("other");
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var delete = await browser.Http.DeleteAsync($"/api/users/{other.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await browser.Http.GetAsync($"/api/users/{other.Id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_RejectedPassword_LeavesTheOldOneWorking()
    {
        await using var host = await ApiTestHost.StartAsync();
        var other = await host.CreateUserAsync("other");
        using var script = host.CreateAuthenticatedClient();

        using var reset = await script.PostAsJsonAsync(
            $"/api/users/{other.Id}/password", new { newPassword = "short" }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(reset, "newPassword");

        using var browser = host.CreateBrowser();
        await browser.SignInAsync("other");
    }

    [Fact]
    public async Task ResetPassword_EndsSessions_AndTheNewPasswordWorks()
    {
        await using var host = await ApiTestHost.StartAsync();
        var other = await host.CreateUserAsync("other");
        using var otherBrowser = host.CreateBrowser();
        await otherBrowser.SignInAsync("other");
        using var script = host.CreateAuthenticatedClient();
        const string newPassword = "set by an admin";

        using var reset = await script.PostAsJsonAsync(
            $"/api/users/{other.Id}/password", new { newPassword }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var status = await otherBrowser.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        using var fresh = host.CreateBrowser();
        await fresh.SignInAsync("other", newPassword);
    }

    [Fact]
    public async Task Lock_EndsSessionsAndBlocksSignIn_UntilUnlocked()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        var other = await host.CreateUserAsync("other");
        using var admin = host.CreateBrowser();
        using var otherBrowser = host.CreateBrowser();
        await admin.SignInAsync("admin");
        await otherBrowser.SignInAsync("other");

        using (var locked = await admin.Http.PostAsync($"/api/users/{other.Id}/lock", null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            var body = await locked.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.True(body.GetProperty("isLockedOut").GetBoolean());
        }

        using (var status = await otherBrowser.Http.GetAsync("/api/server/status", Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        }

        using (var refused = await otherBrowser.LoginAsync("other", ApiTestHost.Password))
        {
            Assert.Equal(HttpStatusCode.Locked, refused.StatusCode);
        }

        using (var unlocked = await admin.Http.PostAsync($"/api/users/{other.Id}/unlock", null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        }

        await otherBrowser.SignInAsync("other");
    }

    [Fact]
    public async Task Lock_RefusesYourself()
    {
        await using var host = await ApiTestHost.StartAsync();
        var admin = await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var response = await browser.Http.PostAsync($"/api/users/{admin.Id}/lock", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Someone edits the account between the lock reading it and saving it. The lock
    // must fail visibly, not answer isLockedOut: true over an unlocked row.
    [Fact]
    public async Task Lock_LosingARace_Returns409_AndLeavesTheAccountAsItWas()
    {
        var edit = new ConcurrentEditInterceptor();
        await using var host = await ApiTestHost.StartAsync(
            configureDatabase: options => options.AddInterceptors(edit));
        var other = await host.CreateUserAsync("other");
        using var script = host.CreateAuthenticatedClient();

        edit.ArmFor(other.Id);
        using var locked = await script.PostAsync($"/api/users/{other.Id}/lock", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        var stored = await script.GetFromJsonAsync<JsonElement>($"/api/users/{other.Id}", Ct);
        Assert.False(stored.GetProperty("isLockedOut").GetBoolean());
    }

    // Two admins delete the other two accounts at the same moment. Each sees two
    // accounts when it counts; unless the count and delete are serialized, both go
    // ahead and nobody can sign in any more.
    [Fact]
    public async Task ConcurrentDeletes_NeverRemoveTheLastAccount()
    {
        await using var host = await ApiTestHost.StartAsync(
            configureDatabase: options => options.AddInterceptors(new CountRendezvousInterceptor()));
        var first = await host.CreateUserAsync("first");
        var second = await host.CreateUserAsync("second");
        using var script = host.CreateAuthenticatedClient();

        var responses = await Task.WhenAll(
            script.DeleteAsync($"/api/users/{first.Id}", Ct),
            script.DeleteAsync($"/api/users/{second.Id}", Ct));

        var statuses = responses.Select(r => r.StatusCode).OrderBy(s => s).ToList();
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], statuses);
        var remaining = await script.GetFromJsonAsync<JsonElement>("/api/users", Ct);
        Assert.Single(remaining.EnumerateArray());
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    // MVC's own validation and Identity's errors must name fields the same way, the
    // way the request spells them, so the SPA can map both onto its form.
    [Fact]
    public async Task ValidationErrors_UseTheRequestsFieldNames()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var script = host.CreateAuthenticatedClient();

        using var badEmail = await script.PostAsJsonAsync("/api/users", new
        {
            userName = "fresh",
            email = "not an email",
            password = ApiTestHost.Password,
        }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(badEmail, "email");

        using var missingUserName = await script.PostAsJsonAsync("/api/users", new
        {
            email = "fresh@example.com",
            password = ApiTestHost.Password,
        }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(missingUserName, "userName");

        using var missingPassword = await script.PostAsJsonAsync(
            "/api/users/any/password", new { }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(missingPassword, "newPassword");

        // Login is anonymous, so it needs the antiforgery token first.
        using var browser = host.CreateBrowser();
        await browser.FetchAntiforgeryAsync();
        using var missingLogin = await browser.Http.PostAsJsonAsync(
            "/api/auth/login", new { password = ApiTestHost.Password }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(missingLogin, "login");
    }

    [Fact]
    public async Task UnknownId_Returns404()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/users/no-such-id/lock", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
