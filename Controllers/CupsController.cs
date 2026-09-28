using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Data.Cups;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;

namespace WreckfestController.Controllers;

/// <summary>
/// Cups. Edits and deletes need If-Match with the version the caller read,
/// so two admins, or an admin and the WPF window, cannot silently undo each other.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/cups")]
public class CupsController : ControllerBase
{
    private readonly CupStore _store;
    private readonly CupActivator _activator;
    private readonly TimeProvider _time;
    private readonly ILogger<CupsController> _logger;

    public CupsController(
        CupStore store,
        CupActivator activator,
        TimeProvider time,
        ILogger<CupsController> logger)
    {
        _store = store;
        _activator = activator;
        _time = time;
        _logger = logger;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Every cup, past ones included: upcoming soonest first, then finished ones.</summary>
    [HttpGet]
    public async Task<CupListResponse> List() => ToList(await _store.ListAsync());

    [HttpGet("current")]
    public async Task<ActionResult<CupResponse>> Current()
    {
        var active = (await _store.ListAsync()).FirstOrDefault(e => e.IsActive);
        return active is null ? NoContent() : CupResponse.From(active);
    }

    /// <summary>Cups whose next occurrence is past the lead-in window, soonest first.</summary>
    [HttpGet("upcoming")]
    public async Task<CupListResponse> Upcoming()
    {
        var dueBy = UtcNow + CupActivator.LeadIn;
        return ToList((await _store.ListAsync()).Where(e => e.NextOccurrence > dueBy));
    }

    /// <summary>Cups the scheduler will start at its next check.</summary>
    [HttpGet("due")]
    public async Task<CupListResponse> Due()
    {
        var dueBy = UtcNow + CupActivator.LeadIn;
        return ToList((await _store.ListAsync()).Where(e => e.NextOccurrence <= dueBy));
    }

    [HttpGet("summary")]
    public async Task<CupSummaryResponse> Summary()
    {
        var cups = await _store.ListAsync();
        var dueBy = UtcNow + CupActivator.LeadIn;
        return new CupSummaryResponse(
            cups.Count,
            cups.Count(e => e.IsActive),
            cups.Count(e => e.NextOccurrence > dueBy),
            cups.Count(e => e.NextOccurrence <= dueBy),
            cups.Count == 0 ? null : cups.Max(e => e.UpdatedAt));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CupResponse>> Get(int id)
    {
        var cup = await _store.GetAsync(id);
        if (cup is null)
        {
            return NotFound();
        }

        this.SetETag(cup.Version);
        return CupResponse.From(cup);
    }

    [HttpPost]
    public async Task<ActionResult<CupResponse>> Create(CupRequest request)
    {
        if (CupRules.Validate(request, out var definition) is { } error)
        {
            return this.Invalid(error.Field, error.Message);
        }

        // A signed-in user; API-key callers have no user id.
        var createdById = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var (status, cup) = await _store.CreateAsync(definition!, createdById);
        if (status == CupWriteStatus.UnknownCollection)
        {
            return UnknownCollection();
        }

        _logger.LogInformation("{Caller} created cup {Name} (ID {Id})", this.Caller(), cup!.Name, cup.Id);
        this.SetETag(cup.Version);
        return CreatedAtAction(nameof(Get), new { id = cup.Id }, CupResponse.From(cup));
    }

    /// <summary>Replaces everything an admin sets. Needs If-Match; a stale one gets 409 with the current cup.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<CupResponse>> Update(int id, CupRequest request)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        if (CupRules.Validate(request, out var definition) is { } error)
        {
            return this.Invalid(error.Field, error.Message);
        }

        var (status, cup) = await _store.UpdateAsync(id, definition!, expected);
        switch (status)
        {
            case CupWriteStatus.NotFound:
                return NotFound();
            case CupWriteStatus.UnknownCollection:
                return UnknownCollection();
            case CupWriteStatus.Conflict:
                return this.VersionConflict(CupResponse.From(cup!), cup!.Version);
        }

        _logger.LogInformation("{Caller} saved cup {Name} (ID {Id})", this.Caller(), cup!.Name, id);
        this.SetETag(cup.Version);
        return CupResponse.From(cup);
    }

    /// <summary>Needs If-Match, like PUT: deleting a cup someone just changed would lose their change.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var (status, current) = await _store.DeleteAsync(id, expected);
        switch (status)
        {
            case CupWriteStatus.NotFound:
                return NotFound();
            case CupWriteStatus.Conflict:
                return this.VersionConflict(CupResponse.From(current!), current!.Version);
        }

        _logger.LogInformation("{Caller} deleted cup {Id}", this.Caller(), id);
        return NoContent();
    }

    /// <summary>
    /// Applies the cup's settings and starts a smart restart; the cup becomes active
    /// when the restart succeeds. 202, because that can take minutes.
    /// </summary>
    [HttpPost("{id:int}/activate")]
    public async Task<IActionResult> Activate(int id)
    {
        ActivationResult result;
        try
        {
            result = await _activator.ActivateAsync(id);
        }
        catch (Exception ex) when (ConfigWriteFailure.From(ex) is { } failure)
        {
            _logger.LogWarning(ex, "Could not activate cup {Id}: {Reason}", id, failure.Reason);
            return this.Refused(failure.Message, ("reason", failure.Reason));
        }

        switch (result)
        {
            case ActivationResult.NotFound:
                return NotFound();
            case ActivationResult.AlreadyActive:
                return this.Refused("The cup is already active.");
            case ActivationResult.Busy:
                return this.Refused("A server restart is already in progress. Try again when it has finished.");
        }

        _logger.LogInformation("{Caller} activated cup {Id}", this.Caller(), id);
        return Accepted(new
        {
            message = "Activation started. Players are warned, and the server restarts with the cup's settings.",
            cupId = id,
        });
    }

    private ActionResult UnknownCollection() =>
        this.Invalid("collectionId", "There is no collection with that id.");

    private static CupListResponse ToList(IEnumerable<Cup> cups)
    {
        var list = cups.Select(CupResponse.From).ToList();
        return new CupListResponse(list.Count, list);
    }
}
