using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// Browser sign-in and the signed-in user's own profile. There is no setup endpoint:
/// the first account is created in the desktop app.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly IAntiforgery _antiforgery;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IAntiforgery antiforgery,
        TimeProvider time,
        ILogger<AuthController> logger)
    {
        _users = users;
        _signIn = signIn;
        _antiforgery = antiforgery;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Who is calling, and whether any account exists yet. The recovery-mode version of
    /// this answer comes from <see cref="DatabaseUnavailableMiddleware"/>.
    /// </summary>
    [HttpGet("state")]
    [AllowAnonymous]
    public async Task<AuthStateResponse> State()
    {
        var user = await _users.GetUserAsync(User);
        return new AuthStateResponse(
            Authenticated: User.Identity?.IsAuthenticated == true,
            User: user is null ? null : UserResponse.From(user, _time),
            SetupRequired: !await _users.Users.AnyAsync(),
            Degraded: false);
    }

    /// <summary>
    /// Issues the antiforgery token as a JS-readable cookie. The token is tied to the
    /// user, so the SPA fetches a new one after login and logout.
    /// </summary>
    [HttpGet("antiforgery")]
    [AllowAnonymous]
    public IActionResult Antiforgery()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append(ApiAuthentication.XsrfCookieName, tokens.RequestToken!, new CookieOptions
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
    /// the account for 15 minutes.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<UserResponse>> Login(LoginRequest request)
    {
        var user = await _users.FindByNameAsync(request.Login)
                   ?? await _users.FindByEmailAsync(request.Login);
        if (user is null)
        {
            return InvalidLogin();
        }

        var result = await _signIn.PasswordSignInAsync(
            user, request.Password, request.Remember, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            _logger.LogWarning("Sign-in refused for locked account {UserName}", user.UserName);
            return Problem(
                statusCode: StatusCodes.Status423Locked,
                title: "This account is locked. Try again later, or ask another admin to unlock it.");
        }

        if (!result.Succeeded)
        {
            return InvalidLogin();
        }

        _logger.LogInformation("{UserName} signed in to the web UI", user.UserName);
        return UserResponse.From(user, _time);
    }

    /// <summary>Anonymous so an expired session can still clear its cookie.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserResponse>> GetProfile()
    {
        var user = await _users.GetUserAsync(User);
        return user is null ? NoProfile() : UserResponse.From(user, _time);
    }

    [HttpPut("me")]
    [Authorize]
    public async Task<ActionResult<UserResponse>> UpdateProfile(UpdateProfileRequest request)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null)
        {
            return NoProfile();
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
        var result = emailChanged
            ? await _users.UpdateSecurityStampAsync(user)
            : await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            ModelState.AddIdentityErrors(result);
            return ValidationProblem(ModelState);
        }

        if (emailChanged)
        {
            await _signIn.RefreshSignInAsync(user);
        }

        return UserResponse.From(user, _time);
    }

    /// <summary>Changes the caller's password and signs out their other sessions.</summary>
    [HttpPost("me/password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null)
        {
            return NoProfile();
        }

        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            ModelState.AddIdentityErrors(result, passwordField: "newPassword");
            return ValidationProblem(ModelState);
        }

        // The new stamp would otherwise end this session too.
        await _signIn.RefreshSignInAsync(user);
        _logger.LogInformation("{UserName} changed their password", user.UserName);
        return NoContent();
    }

    private ObjectResult InvalidLogin() => Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid username, email or password.");

    /// <summary>API-key callers are authenticated but are not a user.</summary>
    private ObjectResult NoProfile() => Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Only a signed-in user has a profile. API-key requests do not.");
}

public sealed record LoginRequest(
    [Required] string Login,
    [Required] string Password,
    bool Remember);

public sealed record UpdateProfileRequest : ProfileRequest;

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required] string NewPassword);
