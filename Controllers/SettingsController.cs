using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;

namespace WreckfestController.Controllers;

/// <summary>
/// The settings a person edits: the server paths, SteamCMD and voting. One section at a
/// time, with its version as the ETag; a PUT changes only the fields it sends and needs
/// If-Match, so the web app and the WPF window cannot silently undo each other.
/// </summary>
/// <remarks>The startup settings (API, database path) are file-only and have no route.</remarks>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(ISettingsStore store, ILogger<SettingsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Every section, each with its version.</summary>
    [HttpGet]
    public Dictionary<string, Dictionary<string, object?>> List() =>
        SettingsApi.All.ToDictionary(s => s.Name, s => s.Read(_store, out _));

    [HttpGet("{section}")]
    public IActionResult Get(string section)
    {
        if (SettingsApi.Find(section) is not { } api)
        {
            return NotFound();
        }

        var body = api.Read(_store, out var version);
        this.SetETag(version);
        return Ok(body);
    }

    /// <summary>Changes the fields in the body. Needs If-Match; a stale one gets 409 with the section as it is.</summary>
    [HttpPut("{section}")]
    public async Task<IActionResult> Put(string section, [FromBody] JsonElement body)
    {
        if (SettingsApi.Find(section) is not { } api)
        {
            return NotFound();
        }

        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var result = await api.PutAsync(this, _store, body, expected);
        if (result is OkObjectResult)
        {
            _logger.LogInformation("{Caller} saved the {Section} settings", this.Caller(), api.Name);
        }

        return result;
    }
}
