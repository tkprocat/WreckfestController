using System.Text.Json.Serialization;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;
using WreckfestController.Models;

namespace WreckfestController.Data.Cups;

/// <summary>
/// A cup: an event loop (the rotation) and the rules it runs under, applied through a
/// smart restart at a set time, once or on a repeat.
/// </summary>
/// <remarks>
/// <para>
/// Named after Wreckfest's own Cup Mode. In the game an <i>event</i> is one race of the
/// loop (<c>el_add</c> in server_config.cfg), and a cup is a run of events scored with cup
/// points. <see cref="SessionMode"/> picks the points system; <c>normal</c> turns cup
/// points off.
/// </para>
/// <para>
/// The row holds two kinds of state with different writers:
/// <list type="bullet">
/// <item>What an admin edits (name through tracks). Saved through the change tracker,
/// which bumps <see cref="Version"/>, so an edit from a stale copy gets a conflict.</item>
/// <item>What the scheduler keeps (<see cref="NextOccurrence"/> onwards). Written by
/// targeted updates that leave <see cref="Version"/> alone, so the scheduler marking an
/// occurrence done never invalidates an editor's If-Match, and an edit that leaves the
/// schedule alone never writes these columns back.</item>
/// </list>
/// </para>
/// </remarks>
public class Cup : IVersioned
{
    public const int NameMaxLength = 128;
    public const int DescriptionMaxLength = 2000;
    public const int TimeZoneMaxLength = 64;
    public const int ScoringMaxLength = 32;
    public const string DefaultTimeZone = "UTC";

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// UTC. The one occurrence of a one-off cup; the first of a recurring one when it
    /// is still ahead, and otherwise only an anchor.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>IANA (or Windows) zone the repeat's wall-clock time is in.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    /// <summary>Null for a one-off cup. Stored as JSON.</summary>
    public RepeatSchedule? Repeat { get; set; }

    /// <summary>Only the fields that are set are applied. Stored as JSON.</summary>
    public EventServerConfig? ServerConfig { get; set; }

    /// <summary>
    /// <c>session_mode</c>: a cup points system such as <c>30p-aggr</c>, <c>normal</c> for
    /// no cup points, or a qualifying session. Null keeps the server's own. One of
    /// <see cref="CupScoring.SessionModes"/>.
    /// </summary>
    public string? SessionMode { get; set; }

    /// <summary>
    /// <c>grid_order</c>, such as <c>cup_reverse</c> to start the leader at the back.
    /// Null keeps the server's own. One of <see cref="CupScoring.GridOrders"/>.
    /// </summary>
    public string? GridOrder { get; set; }

    /// <summary>
    /// The collection whose tracks deploy at activation, as they are then. Cleared when the
    /// collection is deleted, which first copies its tracks into <see cref="Tracks"/>.
    /// </summary>
    public int? CollectionId { get; set; }

    public TrackCollection? Collection { get; set; }

    /// <summary>
    /// The rotation to deploy when no collection is linked. For a linked cup, the
    /// collection's tracks as they were when the cup was saved. Stored as JSON.
    /// </summary>
    public List<EventLoopTrack> Tracks { get; set; } = new();

    /// <summary>The <c>#CollectionName</c> line for <see cref="Tracks"/>; a linked collection's own name wins.</summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>The web user who created it; null for API-key callers or a deleted user.</summary>
    public string? CreatedById { get; set; }

    public AppUser? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;

    // ---- Scheduler state: written only through CupStore's targeted updates. ----

    /// <summary>UTC. The occurrence the scheduler waits for; null once a one-off cup is over.</summary>
    public DateTime? NextOccurrence { get; set; }

    /// <summary>UTC. The occurrence most recently dealt with: activated, failed or missed.</summary>
    public DateTime? LastOccurrence { get; set; }

    /// <summary>What happened to <see cref="LastOccurrence"/>, so a miss or failure is visible.</summary>
    public OccurrenceOutcome? LastOutcome { get; set; }

    /// <summary>At most one cup is active: the one whose settings the server is running.</summary>
    public bool IsActive { get; set; }

    /// <summary>UTC. When the cup last became active.</summary>
    public DateTime? ActivatedAt { get; set; }
}

/// <summary>
/// One occurrence of a cup that has been dealt with, and how. The history is what
/// keeps an edit from running an occurrence twice: when the schedule changes, the next
/// occurrence is the first one under the new schedule that is not recorded here.
/// </summary>
public class CupOccurrenceRecord
{
    public int Id { get; set; }

    public int CupId { get; set; }

    public Cup Cup { get; set; } = null!;

    /// <summary>UTC. Unique per cup.</summary>
    public DateTime Occurrence { get; set; }

    public OccurrenceOutcome Outcome { get; set; }

    /// <summary>UTC. When it was recorded.</summary>
    public DateTime RecordedAt { get; set; }
}

/// <summary>
/// How an occurrence ended. Every occurrence gets one attempt; anything but
/// <see cref="Activated"/> is left for an admin to act on, by activating it by hand.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OccurrenceOutcome>))]
public enum OccurrenceOutcome
{
    /// <summary>The restart succeeded, or the cup was already active.</summary>
    Activated,

    /// <summary>The settings could not be written, or the restart failed.</summary>
    Failed,

    /// <summary>An admin cancelled the restart.</summary>
    Cancelled,

    /// <summary>
    /// Not started within <see cref="Services.CupSchedulerService.MissedGrace"/> of its
    /// time: the app was not running, or another restart ran too long.
    /// </summary>
    Missed,
}
