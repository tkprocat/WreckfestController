namespace WreckfestController.Services.Config;

/// <summary>
/// server_config.cfg has no <c># Event Loop</c> heading, so there is nowhere to write the
/// track rotation. The file itself exists and is where the settings say: this is about its
/// contents, not its location (<see cref="ConfigWriteFailure.MissingEventLoop"/>).
/// </summary>
public sealed class EventLoopHeadingMissingException()
    : InvalidOperationException(
        "server_config.cfg has no '# Event Loop' heading, so the track rotation cannot be written. " +
        "Add the heading, then try again.");
