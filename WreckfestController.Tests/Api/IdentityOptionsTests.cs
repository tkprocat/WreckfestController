using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>The account rules, checked through the API host's real UserManager.</summary>
public class IdentityOptionsTests
{
    [Fact]
    public async Task Password_NeedsTenCharacters_ButNoCharacterClasses()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var tooShort = await users.CreateAsync(NewUser("short"), "abcdefghi");
        Assert.False(tooShort.Succeeded);

        var plain = await users.CreateAsync(NewUser("plain"), "abcdefghij");
        Assert.True(plain.Succeeded, Describe(plain));
    }

    [Fact]
    public async Task Email_MustBeUnique()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var first = NewUser("first");
        var second = NewUser("second");
        second.Email = first.Email;

        Assert.True((await users.CreateAsync(first, ApiTestHost.Password)).Succeeded);
        Assert.False((await users.CreateAsync(second, ApiTestHost.Password)).Succeeded);
    }

    [Fact]
    public async Task FiveFailures_LockTheAccountForFifteenMinutes()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var scope = host.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        var user = (await signIn.UserManager.FindByNameAsync("admin"))!;

        for (var attempt = 1; attempt < ApiAuthentication.MaxFailedSignIns; attempt++)
        {
            var failed = await signIn.CheckPasswordSignInAsync(user, "wrong password", lockoutOnFailure: true);
            Assert.False(failed.IsLockedOut, $"locked out after only {attempt} failures");
        }

        var last = await signIn.CheckPasswordSignInAsync(user, "wrong password", lockoutOnFailure: true);
        Assert.True(last.IsLockedOut);

        // Locked out means locked out: the right password does not get in either.
        var correct = await signIn.CheckPasswordSignInAsync(user, ApiTestHost.Password, lockoutOnFailure: true);
        Assert.False(correct.Succeeded);

        var end = await signIn.UserManager.GetLockoutEndDateAsync(user);
        Assert.NotNull(end);
        Assert.InRange(
            end.Value - DateTimeOffset.UtcNow,
            ApiAuthentication.LockoutDuration - TimeSpan.FromMinutes(1),
            ApiAuthentication.LockoutDuration);
    }

    private static AppUser NewUser(string name) => new() { UserName = name, Email = $"{name}@example.com" };

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
