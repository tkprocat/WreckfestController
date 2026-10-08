namespace WreckfestController.Services.ServerControl;

/// <summary>
/// One continuous attachment to a server process (#40). A new session begins on every
/// attach, detach and process replacement; reinjecting the hook or restarting output
/// monitoring keeps the current one.
///
/// Work that acts on the server carries the session it was accepted under, and
/// <see cref="ServerManager.SendCommandAsync"/> refuses a session that is no longer
/// current. So a command, chat reply or vote result can never reach a server other than
/// the one it was meant for, however long it waited: attach A, switch to B and back to
/// A, and work from the first A is still refused, because the second A is a new session.
/// </summary>
/// <param name="Id">Increases with every session; never reused.</param>
/// <param name="ProcessId">The process this session is attached to.</param>
/// <param name="Ended">
/// Cancelled once the session is replaced. It asks obsolete work to stop; it is not what
/// makes that work safe - a callback already running cannot be stopped by it, which is
/// why dispatch still checks the session itself.
/// </param>
public sealed record AttachmentSession(long Id, int ProcessId, CancellationToken Ended);
