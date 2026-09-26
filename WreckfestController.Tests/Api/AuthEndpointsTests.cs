using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data;

namespace WreckfestController.Tests.Api;

/// <summary>/api/auth/*: sign-in, sign-out and the caller's own profile.</summary>
public class AuthEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task State_ReportsSetupRequired_UntilAnAccountExists()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        var before = await GetStateAsync(browser);
        Assert.True(before.GetProperty("setupRequired").GetBoolean());
        Assert.False(before.GetProperty("authenticated").GetBoolean());
        Assert.False(before.GetProperty("degraded").GetBoolean());

        await host.CreateUserAsync();

        var after = await GetStateAsync(browser);
        Assert.False(after.GetProperty("setupRequired").GetBoolean());
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("admin@example.com")]
    public async Task Login_ByUsernameOrEmail_SignsIn(string login)
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();

        await browser.SignInAsync(login);

        var state = await GetStateAsync(browser);
        Assert.True(state.GetProperty("authenticated").GetBoolean());
        Assert.Equal("admin", state.GetProperty("user").GetProperty("userName").GetString());
        using var status = await browser.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    // Same answer for an unknown account and a wrong password, so login does not
    // reveal which usernames exist.
    [Theory]
    [InlineData("admin", "wrong password")]
    [InlineData("nobody", ApiTestHost.Password)]
    public async Task Login_WithBadCredentials_Returns401(string login, string password)
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.LoginAsync(login, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Invalid username, email or password.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Login_AfterFiveFailures_IsLockedEvenWithTheRightPassword()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await browser.LoginAsync("admin", "wrong password");
        }

        using var response = await browser.LoginAsync("admin", ApiTestHost.Password);
        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var logout = await browser.Http.PostAsync("/api/auth/logout", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var state = await GetStateAsync(browser);
        Assert.False(state.GetProperty("authenticated").GetBoolean());
        using var status = await browser.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Profile_CanBeReadAndUpdated()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var update = await browser.Http.PutAsJsonAsync("/api/auth/me", new
        {
            email = "admin@example.com",
            displayName = "Race Control",
            timeZone = "Europe/Copenhagen",
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var me = await browser.Http.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.Equal("Race Control", me.GetProperty("displayName").GetString());
        Assert.Equal("Europe/Copenhagen", me.GetProperty("timeZone").GetString());
    }

    [Theory]
    [InlineData("W. Europe Standard Time")] // a Windows id: the browser cannot use it
    [InlineData("Mars/Olympus_Mons")]
    public async Task Profile_RejectsAnythingButAnIanaTimeZone(string timeZone)
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var update = await browser.Http.PutAsJsonAsync(
            "/api/auth/me", new { email = "admin@example.com", timeZone }, Ct);

        await AssertFieldErrorAsync(update, "timeZone");
    }

    [Fact]
    public async Task Profile_RejectsAnEmailAnotherAccountUses()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        await host.CreateUserAsync("other");
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var update = await browser.Http.PutAsJsonAsync(
            "/api/auth/me", new { email = "other@example.com" }, Ct);

        await AssertFieldErrorAsync(update, "email");
    }

    [Fact]
    public async Task Profile_IsForbiddenToApiKeyCallers()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_KeepsThisSession_AndEndsTheOthers()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var here = host.CreateBrowser();
        using var elsewhere = host.CreateBrowser();
        await here.SignInAsync("admin");
        await elsewhere.SignInAsync("admin");
        const string newPassword = "a brand new passphrase";

        using var change = await here.Http.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = ApiTestHost.Password,
            newPassword,
        }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var stillHere = await here.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);
        using var goneElsewhere = await elsewhere.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, goneElsewhere.StatusCode);

        using var fresh = host.CreateBrowser();
        using var oldPassword = await fresh.LoginAsync("admin", ApiTestHost.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        await fresh.SignInAsync("admin", newPassword);
    }

    [Fact]
    public async Task ChangePassword_ReportsWhichFieldIsWrong()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var wrongCurrent = await browser.Http.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = "not my password",
            newPassword = "a brand new passphrase",
        }, Ct);
        await AssertFieldErrorAsync(wrongCurrent, "currentPassword");

        using var tooShort = await browser.Http.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = ApiTestHost.Password,
            newPassword = "short",
        }, Ct);
        await AssertFieldErrorAsync(tooShort, "newPassword");
    }

    // An admin lock must end the session the locked user already has, not just
    // refuse their next sign-in.
    [Fact]
    public async Task AdminLock_EndsAnExistingSession()
    {
        await using var host = await ApiTestHost.StartAsync();
        var user = await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var fresh = (await users.FindByIdAsync(user.Id))!;
            await users.SetLockoutEndDateAsync(fresh, DateTimeOffset.MaxValue);
            await users.UpdateSecurityStampAsync(fresh);
        }

        using var status = await browser.Http.GetAsync("/api/server/status", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    internal static async Task<JsonElement> GetStateAsync(BrowserClient browser) =>
        await browser.Http.GetFromJsonAsync<JsonElement>("/api/auth/state", Ct);

    /// <summary>Asserts a 400 whose <c>errors</c> name <paramref name="field"/>.</summary>
    internal static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var errors = problem.GetProperty("errors");
        Assert.True(
            errors.TryGetProperty(field, out _),
            $"expected an error for '{field}', got {errors}");
    }
}
