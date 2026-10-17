using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WreckfestPortal.Accounts;

/// <summary>A web account as the API returns it. Never carries the password hash or stamps.</summary>
/// <param name="IsAdmin">A global admin: every server, and the accounts.</param>
/// <param name="Servers">The ids of the servers a user who is not an admin may manage.</param>
public sealed record UserResponse(
    string Id,
    string UserName,
    string? Email,
    string? DisplayName,
    string? TimeZone,
    bool IsAdmin,
    IReadOnlyList<string> Servers,
    bool IsLockedOut,
    DateTimeOffset? LockoutEnd)
{
    public static UserResponse From(PortalUser user, TimeProvider time) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.Email,
        user.DisplayName,
        user.TimeZone,
        user.IsAdmin,
        user.IsAdmin ? [] : user.Servers.Select(g => g.ServerId).Order(StringComparer.OrdinalIgnoreCase).ToList(),
        user.LockoutEnd is { } end && end > time.GetUtcNow(),
        user.LockoutEnd);
}

/// <param name="SetupRequired">No account exists yet: one is made with <c>WreckfestPortal.exe create-admin</c>.</param>
public sealed record AuthStateResponse(bool Authenticated, UserResponse? User, bool SetupRequired);

/// <summary>Profile fields a user may change about themselves, or an admin about anyone.</summary>
public abstract record ProfileRequest
{
    [Required, EmailAddress]
    public string Email { get; init; } = string.Empty;

    [StringLength(PortalUser.DisplayNameMaxLength)]
    public string? DisplayName { get; init; }

    /// <summary>An IANA id such as "Europe/Copenhagen", or null for the browser's own zone.</summary>
    [StringLength(PortalUser.TimeZoneMaxLength)]
    public string? TimeZone { get; init; }
}

/// <summary>What an admin sets about an account on top of its profile: its role and servers.</summary>
public abstract record AccountRequest : ProfileRequest
{
    [Required]
    public string UserName { get; init; } = string.Empty;

    /// <summary>A global admin: every server, and the accounts.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>For a user who is not an admin: the ids of the servers they may manage. Ignored for an admin.</summary>
    public IReadOnlyList<string> Servers { get; init; } = [];
}

internal static class AccountValidation
{
    /// <summary>
    /// Copies Identity's errors into model state under the request field they are about,
    /// so the response is the same <c>{ errors: { field: [...] } }</c> shape as any
    /// other validation failure.
    /// </summary>
    public static void AddIdentityErrors(
        this ModelStateDictionary modelState,
        IdentityResult result,
        string passwordField = "password")
    {
        foreach (var error in result.Errors)
        {
            var field = error.Code switch
            {
                nameof(IdentityErrorDescriber.PasswordMismatch) => "currentPassword",
                _ when error.Code.StartsWith("Password", StringComparison.Ordinal) => passwordField,
                nameof(IdentityErrorDescriber.DuplicateUserName)
                    or nameof(IdentityErrorDescriber.InvalidUserName) => "userName",
                nameof(IdentityErrorDescriber.DuplicateEmail)
                    or nameof(IdentityErrorDescriber.InvalidEmail) => "email",
                _ => string.Empty,
            };
            modelState.AddModelError(field, error.Description);
        }
    }

    /// <summary>
    /// The response for a failed Identity operation: 409 when the account changed
    /// underneath the request (its concurrency stamp moved), otherwise a validation
    /// problem naming the fields.
    /// </summary>
    public static ActionResult IdentityFailure(
        this ControllerBase controller,
        IdentityResult result,
        string passwordField = "password")
    {
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            return controller.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This account was changed by someone else at the same time. Reload it and try again.");
        }

        controller.ModelState.AddIdentityErrors(result, passwordField);
        return controller.ValidationProblem(controller.ModelState);
    }

    /// <summary>True for a null zone or an IANA id; Windows ids are rejected so the browser can use it.</summary>
    public static bool IsValidTimeZone(string? timeZone) =>
        timeZone is null || TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _);

    /// <summary>
    /// Runs the password validators without saving, so an admin reset can be refused
    /// before the old password is replaced.
    /// </summary>
    public static async Task<IdentityResult> ValidatePasswordAsync(
        UserManager<PortalUser> users,
        PortalUser user,
        string password)
    {
        var errors = new List<IdentityError>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password);
            errors.AddRange(result.Errors);
        }

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }
}
