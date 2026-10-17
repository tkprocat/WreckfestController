using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace WreckfestPortal.Accounts;

/// <summary>
/// Account management, for global admins only. Nobody can delete, lock or demote
/// themselves, and the last global admin who can sign in cannot be deleted, locked or
/// demoted by anyone.
/// </summary>
[ApiController]
[Authorize(Policy = PortalAccounts.AdminPolicy)]
[Route("api/users")]
public class UsersController(
    UserManager<PortalUser> users,
    SignInManager<PortalUser> signIn,
    PortalDbContext db,
    IOptions<PortalOptions> options,
    TimeProvider time,
    ILogger<UsersController> logger) : ControllerBase
{
    /// <summary>Tags the query below, so a test can hold requests at exactly that point.</summary>
    internal const string AdminFloorTag = "admin floor";

    private IQueryable<PortalUser> Accounts => users.Users.Include(u => u.Servers);

    [HttpGet]
    public async Task<IEnumerable<UserResponse>> List()
    {
        var all = await Accounts.OrderBy(u => u.UserName).ToListAsync();
        return all.Select(u => UserResponse.From(u, time));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserResponse>> Get(string id)
    {
        var user = await FindAsync(id);
        return user is null ? NotFound() : UserResponse.From(user, time);
    }

    /// <summary>Creates an account with a temporary password for its owner to change.</summary>
    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var user = new PortalUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email,
            DisplayName = request.DisplayName,
            TimeZone = request.TimeZone,
            IsAdmin = request.IsAdmin,
        };
        SetServers(user, request);
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        logger.LogInformation("{Caller} created account {UserName}{Role}", Caller, user.UserName, user.IsAdmin ? " (admin)" : "");
        return CreatedAtAction(nameof(Get), new { id = user.Id }, UserResponse.From(user, time));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UserResponse>> Update(string id, UpdateUserRequest request)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        // As Delete: losing an admin is checked against the others inside the write lock.
        await using var transaction = await db.Database.BeginTransactionAsync();

        var user = await FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (user.IsAdmin && !request.IsAdmin)
        {
            if (IsCaller(user))
            {
                return Refused("You cannot remove your own admin role. Ask another admin.");
            }

            if (!await AnotherAdminCanSignInAsync(user.Id))
            {
                return Refused("The last admin who can sign in cannot lose the admin role. Make another account an admin first.");
            }
        }

        var loginChanged =
            !string.Equals(user.UserName, request.UserName.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase);
        user.UserName = request.UserName.Trim();
        user.Email = request.Email;
        user.DisplayName = request.DisplayName;
        user.TimeZone = request.TimeZone;
        user.IsAdmin = request.IsAdmin;
        SetServers(user, request);

        // One save either way, validated as a whole. A new username or email is a new login
        // name, so it ends the account's sessions like a password change. A new role or new
        // servers apply on the account's next request, whose sign-in is rebuilt every time.
        var result = loginChanged ? await users.UpdateSecurityStampAsync(user) : await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        await transaction.CommitAsync();
        if (loginChanged && IsCaller(user))
        {
            await signIn.RefreshSignInAsync(user);
        }

        return UserResponse.From(user, time);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        // Two admins deleting the other two admins at once would each count two and both
        // go ahead, leaving no admin. Microsoft.Data.Sqlite begins with BEGIN IMMEDIATE,
        // which takes the write lock before the count, so a competing change waits until
        // this one commits and then counts again.
        await using var transaction = await db.Database.BeginTransactionAsync();

        var user = await FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (IsCaller(user))
        {
            return Refused("You cannot delete your own account.");
        }

        if (user.IsAdmin && !await AnotherAdminCanSignInAsync(user.Id))
        {
            return Refused("The last admin who can sign in cannot be deleted. Unlock or make another admin first.");
        }

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        await transaction.CommitAsync();
        logger.LogInformation("{Caller} deleted account {UserName}", Caller, user.UserName);
        return NoContent();
    }

    /// <summary>Sets a new password without the old one, and ends the account's sessions.</summary>
    [HttpPost("{id}/password")]
    public async Task<IActionResult> ResetPassword(string id, ResetPasswordRequest request)
    {
        var user = await FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        // Validate first: the old password must survive a rejected new one.
        var validation = await AccountValidation.ValidatePasswordAsync(users, user, request.NewPassword);
        if (!validation.Succeeded)
        {
            return this.IdentityFailure(validation, passwordField: "newPassword");
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, request.NewPassword);
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result, passwordField: "newPassword");
        }

        if (IsCaller(user))
        {
            await signIn.RefreshSignInAsync(user);
        }

        logger.LogInformation("{Caller} reset the password of account {UserName}", Caller, user.UserName);
        return NoContent();
    }

    /// <summary>Locks the account until unlocked, and ends its sessions.</summary>
    [HttpPost("{id}/lock")]
    public async Task<ActionResult<UserResponse>> Lock(string id)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        var user = await FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (IsCaller(user))
        {
            return Refused("You cannot lock your own account.");
        }

        if (user.IsAdmin && !await AnotherAdminCanSignInAsync(user.Id))
        {
            return Refused("The last admin who can sign in cannot be locked. Unlock or make another admin first.");
        }

        // One save, so the lock and the new stamp that ends the account's sessions land
        // together or not at all.
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        await transaction.CommitAsync();
        logger.LogInformation("{Caller} locked account {UserName}", Caller, user.UserName);
        return UserResponse.From(user, time);
    }

    /// <summary>Lifts an admin lock or a failed-sign-in lockout.</summary>
    [HttpPost("{id}/unlock")]
    public async Task<ActionResult<UserResponse>> Unlock(string id)
    {
        var user = await FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return this.IdentityFailure(result);
        }

        logger.LogInformation("{Caller} unlocked account {UserName}", Caller, user.UserName);
        return UserResponse.From(user, time);
    }

    private Task<PortalUser?> FindAsync(string id) => Accounts.SingleOrDefaultAsync(u => u.Id == id);

    /// <summary>
    /// Whether a global admin other than <paramref name="exceptId"/> could still sign in. A
    /// failed-sign-in lockout ends by itself, so only an admin lock (until MaxValue) takes
    /// an account out. Read inside the caller's BEGIN IMMEDIATE transaction.
    /// </summary>
    private async Task<bool> AnotherAdminCanSignInAsync(string exceptId)
    {
        // Compared here rather than in SQL: SQLite cannot compare DateTimeOffset values.
        var lockEnds = await users.Users
            .TagWith(AdminFloorTag)
            .Where(u => u.Id != exceptId && u.IsAdmin)
            .Select(u => u.LockoutEnd)
            .ToListAsync();
        return lockEnds.Any(end => end != DateTimeOffset.MaxValue);
    }

    /// <summary>The time zone and the servers, which Identity does not check.</summary>
    private ActionResult? Validate(AccountRequest request)
    {
        if (!AccountValidation.IsValidTimeZone(request.TimeZone))
        {
            ModelState.AddModelError("timeZone", "Use an IANA time zone such as Europe/Copenhagen.");
        }

        if (!request.IsAdmin)
        {
            var known = options.Value.Servers.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var unknown in request.Servers.Where(id => !known.Contains(id)))
            {
                ModelState.AddModelError("servers", $"The portal lists no server \"{unknown}\".");
            }
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    /// <summary>The requested servers, in the settings' spelling; none for an admin, who has them all.</summary>
    private void SetServers(PortalUser user, AccountRequest request)
    {
        var wanted = request.IsAdmin
            ? []
            : options.Value.Servers
                .Where(s => request.Servers.Contains(s.Id, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Id)
                .ToList();
        user.Servers.RemoveAll(g => !wanted.Contains(g.ServerId, StringComparer.OrdinalIgnoreCase));
        foreach (var id in wanted.Where(id => !user.Servers.Any(g => string.Equals(g.ServerId, id, StringComparison.OrdinalIgnoreCase))))
        {
            user.Servers.Add(new ServerGrant { UserId = user.Id, ServerId = id });
        }
    }

    /// <summary>For the log: the signed-in admin's name.</summary>
    private string Caller => User.Identity?.Name ?? "unknown";

    private bool IsCaller(PortalUser user) => users.GetUserId(User) == user.Id;

    private ObjectResult Refused(string title) => Problem(statusCode: StatusCodes.Status409Conflict, title: title);
}

public sealed record CreateUserRequest : AccountRequest
{
    /// <summary>A temporary password. Its owner changes it through /api/auth/me/password.</summary>
    [Required]
    public string Password { get; init; } = string.Empty;
}

public sealed record UpdateUserRequest : AccountRequest;

public sealed record ResetPasswordRequest
{
    [Required]
    public string NewPassword { get; init; } = string.Empty;
}
