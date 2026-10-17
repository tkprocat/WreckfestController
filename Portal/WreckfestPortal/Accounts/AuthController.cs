using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace WreckfestPortal.Accounts;

/// <summary>Signing in to the web site, and the signed-in user's own profile.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    UserManager<PortalUser> users,
    SignInManager<PortalUser> signIn,
    IAntiforgery antiforgery,
    TimeProvider time,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>Who is calling, and whether any account exists yet.</summary>
    [HttpGet("state")]
    [AllowAnonymous]
    public async Task<AuthStateResponse> State()
    {
        var user = await CurrentUserAsync();
        return new AuthStateResponse(
            Authenticated: user is not null,
            User: user is null ? null : UserResponse.From(user, time),
            SetupRequired: !await users.Users.AnyAsync());
    }

    /// <summary>
    /// Issues the antiforgery token as a JS-readable cookie. The token is tied to the
    /// user, so the web app fetches a new one after signing in and out.
    /// </summary>
    [HttpGet("antiforgery")]
    [AllowAnonymous]
    public IActionResult Antiforgery()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append(PortalAccounts.XsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps,
            Path = "/",
        });
        return NoContent();
    }

    /// <summary>
    /// Signs in by username, or by email when no username matches. Five failures lock
    /// the account for 15 minutes, and each client IP gets 10 attempts a minute.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(PortalRateLimits.LoginPolicy)]
    public async Task<ActionResult<UserResponse>> Login(LoginRequest request)
    {
        var user = await users.FindByNameAsync(request.Login)
                   ?? await users.FindByEmailAsync(request.Login);
        if (user is null)
        {
            return InvalidLogin();
        }

        var result = await signIn.PasswordSignInAsync(user, request.Password, request.Remember, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            logger.LogWarning("Sign-in refused for locked account {UserName}", user.UserName);
            return Problem(
                statusCode: StatusCodes.Status423Locked,
                title: "This account is locked. Try again later, or ask an admin to unlock it.");
        }

        if (!result.Succeeded)
        {
            return InvalidLogin();
        }

        logger.LogInformation("{UserName} signed in", user.UserName);
        return UserResponse.From(await WithServersAsync(user), time);
    }

    /// <summary>Anonymous so an expired session can still clear its cookie.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserResponse>> GetProfile()
    {
        var user = await CurrentUserAsync();
        return user is null ? Unauthorized() : UserResponse.From(user, time);
    }

    [HttpPut("me")]
    public async Task<ActionResult<UserResponse>> UpdateProfile(UpdateProfileRequest request)
    {
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        if (!AccountValidation.IsValidTimeZone(request.TimeZone))
        {
            ModelState.AddModelError("timeZone", "Use an IANA time zone such as Europe/Copenhagen.");
            return ValidationProblem(ModelState);
        }

        var emailChanged = !string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase);
        user.Email = request.Email;
        user.DisplayName = request.DisplayName;
        user.TimeZone = request.TimeZone;

        // A new email is a new login name, so it ends other sessions like a password change.
        var result = emailChanged ? await users.UpdateSecurityStampAsync(user) : await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        if (emailChanged)
        {
            await signIn.RefreshSignInAsync(user);
        }

        return UserResponse.From(user, time);
    }

    /// <summary>Changes the caller's password and signs out their other sessions.</summary>
    [HttpPost("me/password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result, passwordField: "newPassword");
        }

        // The new stamp would otherwise end this session too.
        await signIn.RefreshSignInAsync(user);
        logger.LogInformation("{UserName} changed their password", user.UserName);
        return NoContent();
    }

    private async Task<PortalUser?> CurrentUserAsync()
    {
        var id = users.GetUserId(User);
        return id is null ? null : await users.Users.Include(u => u.Servers).SingleOrDefaultAsync(u => u.Id == id);
    }

    private async Task<PortalUser> WithServersAsync(PortalUser user) =>
        await users.Users.Include(u => u.Servers).SingleAsync(u => u.Id == user.Id);

    private ObjectResult InvalidLogin() => Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid username, email or password.");
}

// Properties rather than a positional record: validation errors are keyed by the
// property's JSON name ("login"), but by the C# parameter name for constructor
// parameters ("Login"), which the web app's forms would not match.
public sealed record LoginRequest
{
    [Required]
    public string Login { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;

    public bool Remember { get; init; }
}

public sealed record UpdateProfileRequest : ProfileRequest;

public sealed record ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required]
    public string NewPassword { get; init; } = string.Empty;
}
