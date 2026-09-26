using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WreckfestController.Data;

namespace WreckfestController.Controllers;

/// <summary>A web UI account as the API returns it. Never carries the password hash or stamps.</summary>
public sealed record UserResponse(
    string Id,
    string UserName,
    string? Email,
    string? DisplayName,
    string? TimeZone,
    bool IsLockedOut,
    DateTimeOffset? LockoutEnd)
{
    public static UserResponse From(AppUser user, TimeProvider time) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.Email,
        user.DisplayName,
        user.TimeZone,
        user.LockoutEnd is { } end && end > time.GetUtcNow(),
        user.LockoutEnd);
}

public sealed record AuthStateResponse(
    bool Authenticated,
    UserResponse? User,
    bool SetupRequired,
    bool Degraded);

/// <summary>Profile fields a user may change about themselves, or an admin about anyone.</summary>
public abstract record ProfileRequest
{
    [Required, EmailAddress]
    public string Email { get; init; } = string.Empty;

    [StringLength(AppUser.DisplayNameMaxLength)]
    public string? DisplayName { get; init; }

    /// <summary>An IANA id such as "Europe/Copenhagen", or null for the browser's own zone.</summary>
    [StringLength(AppUser.TimeZoneMaxLength)]
    public string? TimeZone { get; init; }
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

    /// <summary>True for a null zone or an IANA id; Windows ids are rejected so the browser can use it.</summary>
    public static bool IsValidTimeZone(string? timeZone) =>
        timeZone is null || TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _);

    /// <summary>
    /// Runs the password validators without saving, so an admin reset can be refused
    /// before the old password is replaced.
    /// </summary>
    public static async Task<IdentityResult> ValidatePasswordAsync(
        UserManager<AppUser> users,
        AppUser user,
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
