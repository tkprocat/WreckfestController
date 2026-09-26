using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// Account management. Every account is an admin in v1, so any signed-in user (or an
/// API-key caller) may manage the others. Nobody can delete or lock themselves, and
/// the last account cannot be deleted.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly ControllerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<UsersController> _logger;

    /// <param name="db">The request's context: the same instance UserManager's store uses.</param>
    public UsersController(
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        ControllerDbContext db,
        TimeProvider time,
        ILogger<UsersController> logger)
    {
        _users = users;
        _signIn = signIn;
        _db = db;
        _time = time;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IEnumerable<UserResponse>> List()
    {
        var users = await _users.Users.OrderBy(u => u.UserName).ToListAsync();
        return users.Select(u => UserResponse.From(u, _time));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserResponse>> Get(string id)
    {
        var user = await _users.FindByIdAsync(id);
        return user is null ? NotFound() : UserResponse.From(user, _time);
    }

    /// <summary>Creates an account with a temporary password for its owner to change.</summary>
    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request)
    {
        if (!AccountValidation.IsValidTimeZone(request.TimeZone))
        {
            return InvalidTimeZone();
        }

        var user = new AppUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email,
            DisplayName = request.DisplayName,
            TimeZone = request.TimeZone,
        };
        var result = await _users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        _logger.LogInformation("{Caller} created web account {UserName}", Caller, user.UserName);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, UserResponse.From(user, _time));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UserResponse>> Update(string id, UpdateUserRequest request)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (!AccountValidation.IsValidTimeZone(request.TimeZone))
        {
            return InvalidTimeZone();
        }

        var loginChanged =
            !string.Equals(user.UserName, request.UserName.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase);
        user.UserName = request.UserName.Trim();
        user.Email = request.Email;
        user.DisplayName = request.DisplayName;
        user.TimeZone = request.TimeZone;

        // One save either way, validated as a whole. A new username or email is a new
        // login name, so it ends the account's sessions like a password change.
        var result = loginChanged
            ? await _users.UpdateSecurityStampAsync(user)
            : await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        if (loginChanged && IsCaller(user))
        {
            await _signIn.RefreshSignInAsync(user);
        }

        return UserResponse.From(user, _time);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        // Two admins deleting the other two accounts at once would each count two and
        // both go ahead, leaving nobody able to sign in. Microsoft.Data.Sqlite begins
        // with BEGIN IMMEDIATE, which takes the write lock before the count, so a
        // competing delete waits until this one commits and then counts again.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var user = await _users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (IsCaller(user))
        {
            return Refused("You cannot delete your own account.");
        }

        // Also guards API-key callers, who have no account of their own.
        if (await _users.Users.CountAsync() <= 1)
        {
            return Refused("The last account cannot be deleted. Create another one first.");
        }

        var result = await _users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        await transaction.CommitAsync();
        _logger.LogInformation("{Caller} deleted web account {UserName}", Caller, user.UserName);
        return NoContent();
    }

    /// <summary>Sets a new password without the old one, and ends the account's sessions.</summary>
    [HttpPost("{id}/password")]
    public async Task<IActionResult> ResetPassword(string id, ResetPasswordRequest request)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        // Validate first: the old password must survive a rejected new one.
        var validation = await AccountValidation.ValidatePasswordAsync(_users, user, request.NewPassword);
        if (!validation.Succeeded)
        {
            return this.IdentityFailure(validation, passwordField: "newPassword");
        }

        user.PasswordHash = _users.PasswordHasher.HashPassword(user, request.NewPassword);
        var result = await _users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result, passwordField: "newPassword");
        }

        if (IsCaller(user))
        {
            await _signIn.RefreshSignInAsync(user);
        }

        _logger.LogInformation("{Caller} reset the password of web account {UserName}", Caller, user.UserName);
        return NoContent();
    }

    /// <summary>Locks the account until unlocked, and ends its sessions.</summary>
    [HttpPost("{id}/lock")]
    public async Task<ActionResult<UserResponse>> Lock(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (IsCaller(user))
        {
            return Refused("You cannot lock your own account.");
        }

        // One save, so the lock and the new stamp that ends the account's sessions land
        // together or not at all.
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        var result = await _users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        _logger.LogInformation("{Caller} locked web account {UserName}", Caller, user.UserName);
        return UserResponse.From(user, _time);
    }

    /// <summary>Lifts an admin lock or a failed-sign-in lockout.</summary>
    [HttpPost("{id}/unlock")]
    public async Task<ActionResult<UserResponse>> Unlock(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        _logger.LogInformation("{Caller} unlocked web account {UserName}", Caller, user.UserName);
        return UserResponse.From(user, _time);
    }

    /// <summary>For the log: the signed-in user's name, or "ApiKey" for a script.</summary>
    private string Caller => User.Identity?.Name ?? "unknown";

    private bool IsCaller(AppUser user) => _users.GetUserId(User) == user.Id;

    private ObjectResult Refused(string title) =>
        Problem(statusCode: StatusCodes.Status409Conflict, title: title);

    private ActionResult InvalidTimeZone()
    {
        ModelState.AddModelError("timeZone", "Use an IANA time zone such as Europe/Copenhagen.");
        return ValidationProblem(ModelState);
    }
}

public sealed record CreateUserRequest : ProfileRequest
{
    [Required]
    public string UserName { get; init; } = string.Empty;

    /// <summary>A temporary password. Its owner changes it through /api/auth/me/password.</summary>
    [Required]
    public string Password { get; init; } = string.Empty;
}

public sealed record UpdateUserRequest : ProfileRequest
{
    [Required]
    public string UserName { get; init; } = string.Empty;
}

public sealed record ResetPasswordRequest
{
    [Required]
    public string NewPassword { get; init; } = string.Empty;
}
