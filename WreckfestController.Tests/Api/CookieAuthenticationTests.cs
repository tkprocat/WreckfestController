using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Browser sign-in through the real Identity cookie, alongside the API key that
/// scripts use. The cookie is issued by SignInManager directly until the login
/// endpoint exists.
/// </summary>
public class CookieAuthenticationTests
{
    private const string StatusPath = "/api/server/status";

    [Fact]
    public async Task NoCredentials_Returns401_NotARedirect()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(StatusPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task SignedInCookie_IsAccepted()
    {
        await using var host = await ApiTestHost.StartAsync();
        var cookie = await host.SignInAsync(await host.CreateUserAsync());
        using var client = host.CreateCookieClient(cookie);

        using var response = await client.GetAsync(StatusPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_HasTheRequiredAttributes()
    {
        await using var host = await ApiTestHost.StartAsync();
        var cookie = await host.SignInAsync(await host.CreateUserAsync());

        var attributes = cookie.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=strict", attributes);
        // SameAsRequest: the test request is plain HTTP, as on loopback.
        Assert.DoesNotContain("secure", attributes);

        var expires = DateTimeOffset.Parse(
            cookie.Split(';').Select(a => a.Trim())
                .Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..]);
        var lifetime = expires - DateTimeOffset.UtcNow;
        Assert.InRange(lifetime, TimeSpan.FromDays(13.9), TimeSpan.FromDays(14.1));
    }

    [Fact]
    public async Task TamperedCookie_IsRejected()
    {
        await using var host = await ApiTestHost.StartAsync();
        var cookie = await host.SignInAsync(await host.CreateUserAsync());
        var value = cookie.Split(';')[0];
        var tampered = value[..^4] + (value.EndsWith("AAAA") ? "BBBB" : "AAAA");
        using var client = host.CreateCookieClient(tampered);

        using var response = await client.GetAsync(StatusPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Without persisted keys, every restart of the controller would sign everyone out.
    [Fact]
    public async Task Cookie_SurvivesARestart()
    {
        var directory = ApiTestHost.NewDataDirectory();
        try
        {
            string cookie;
            await using (var first = await ApiTestHost.StartAsync(dataDirectory: directory))
            {
                cookie = await first.SignInAsync(await first.CreateUserAsync());
            }

            await using var second = await ApiTestHost.StartAsync(dataDirectory: directory);
            using var client = second.CreateCookieClient(cookie);

            using var response = await client.GetAsync(StatusPath, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            ApiTestHost.DeleteDataDirectory(directory);
        }
    }

    [Fact]
    public async Task Keys_ArePersistedBesideTheDatabase_AndDpapiProtected()
    {
        var directory = ApiTestHost.NewDataDirectory();
        try
        {
            await using (var host = await ApiTestHost.StartAsync(dataDirectory: directory))
            {
                await host.SignInAsync(await host.CreateUserAsync());
            }

            var keysFolder = ApiAuthentication.KeysFolder(Path.Combine(directory, "controller.db"));
            var keyFile = Assert.Single(Directory.GetFiles(keysFolder, "key-*.xml"));
            var xml = await File.ReadAllTextAsync(keyFile, TestContext.Current.CancellationToken);
            Assert.Contains("DpapiXmlDecryptor", xml);
            // An unprotected key file carries the key in a plaintext <masterKey> element.
            Assert.DoesNotContain("<masterKey", xml);
        }
        finally
        {
            ApiTestHost.DeleteDataDirectory(directory);
        }
    }

    // A password change or an admin lock rotates the stamp; other sessions must end.
    [Fact]
    public async Task RotatedSecurityStamp_EndsExistingSessions()
    {
        await using var host = await ApiTestHost.StartAsync();
        var user = await host.CreateUserAsync();
        var cookie = await host.SignInAsync(user);
        using var client = host.CreateCookieClient(cookie);
        var ct = TestContext.Current.CancellationToken;

        using (var before = await client.GetAsync(StatusPath, ct))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(user.Id))!);
        }

        using var after = await client.GetAsync(StatusPath, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ApiKey_StillWorks()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.GetAsync(StatusPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The header decides the scheme: a wrong key is not rescued by a valid cookie.
    [Fact]
    public async Task WrongApiKey_IsRejected_EvenWithAValidCookie()
    {
        await using var host = await ApiTestHost.StartAsync();
        var cookie = await host.SignInAsync(await host.CreateUserAsync());
        using var client = host.CreateCookieClient(cookie);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

        using var response = await client.GetAsync(StatusPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The key is optional. Without one, cookies still work and no header value gets in.
    [Theory]
    [InlineData("")]
    [InlineData("anything")]
    public async Task BlankConfiguredKey_AcceptsNoKey_ButCookiesWork(string sentKey)
    {
        await using var host = await ApiTestHost.StartAsync(
            new Dictionary<string, string?> { ["Api:Key"] = "" });
        var ct = TestContext.Current.CancellationToken;

        using var keyClient = host.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", sentKey);
        using var keyResponse = await keyClient.GetAsync(StatusPath, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, keyResponse.StatusCode);

        var cookie = await host.SignInAsync(await host.CreateUserAsync());
        using var cookieClient = host.CreateCookieClient(cookie);
        using var cookieResponse = await cookieClient.GetAsync(StatusPath, ct);
        Assert.Equal(HttpStatusCode.OK, cookieResponse.StatusCode);
    }
}
