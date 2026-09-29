using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;

namespace WreckfestController.Controllers;

/// <summary>
/// The settings a person edits over the web: today only voting. One section at a time,
/// with its version as the ETag; a PUT changes only the fields it sends and needs
/// If-Match, so the web app and the WPF window cannot silently undo each other.
/// </summary>
/// <remarks>
/// Startup settings (API, database path) and launch settings (programs, paths) are
/// desktop-only and have no route (docs/API.md, "What the web can reach"). The partial
/// update and its checks are <see cref="SettingsApi"/>'s; the routes here are typed so the
/// web app gets types for them.
/// </remarks>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/settings")]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
public class SettingsController : ControllerBase
{
    private const string Vote = "vote";

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(ISettingsStore store, ILogger<SettingsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Every section, each with its version.</summary>
    [HttpGet]
    public SettingsResponse List() => new(VoteSettingsResponse.From(_store.GetEntry<VoteSettings>()));

    /// <summary>The voting settings, with their version as the ETag.</summary>
    [HttpGet(Vote)]
    public ActionResult<VoteSettingsResponse> GetVote()
    {
        var entry = _store.GetEntry<VoteSettings>();
        this.SetETag(entry.Version);
        return VoteSettingsResponse.From(entry);
    }

    /// <summary>
    /// Changes the voting fields in the body; the rest keep their values. Needs If-Match
    /// (428 without); a stale one gets 409 with the settings as they are now.
    /// </summary>
    [HttpPut(Vote)]
    [ProducesResponseType<VoteSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<VoteSettingsResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> PutVote([FromBody] JsonElement body)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var result = await SettingsApi.Find(Vote)!.PutAsync(this, _store, body, expected);
        if (result is OkObjectResult)
        {
            _logger.LogInformation("{Caller} saved the voting settings", this.Caller());
        }

        return result;
    }
}

/// <summary>Every settings section the web may edit.</summary>
public sealed record SettingsResponse(VoteSettingsResponse Vote);

/// <summary>
/// The voting settings as the API shows them: <see cref="VoteSettings"/> without the legacy
/// Enabled flag (it mirrors Mode). The PUT answers with SettingsApi's body for the same
/// fields; a test keeps the two in step.
/// </summary>
public sealed record VoteSettingsResponse(
    string Mode,
    int DirectCooldownSeconds,
    int VoteTimeoutSeconds,
    int MaxLapsAllowed,
    int MessageDelayMs,
    bool SuppressCommandsDuringRace,
    int Version)
{
    public static VoteSettingsResponse From(SettingsEntry<VoteSettings> entry) => new(
        entry.Value.Mode,
        entry.Value.DirectCooldownSeconds,
        entry.Value.VoteTimeoutSeconds,
        entry.Value.MaxLapsAllowed,
        entry.Value.MessageDelayMs,
        entry.Value.SuppressCommandsDuringRace,
        entry.Version);
}
