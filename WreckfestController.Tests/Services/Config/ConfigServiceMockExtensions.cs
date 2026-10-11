using Moq;
using WreckfestController.Services.Config;

namespace WreckfestController.Tests.Services.Config;

internal static class ConfigServiceMockExtensions
{
    /// <summary>
    /// A mocked <see cref="ConfigService.WriteRestorably"/> runs the writes it is given, so
    /// they reach the mocked writers, and has nothing to restore.
    /// </summary>
    internal static Mock<ConfigService> RunsRestorableWrites(this Mock<ConfigService> config)
    {
        config.Setup(c => c.WriteRestorably(It.IsAny<Action>()))
            .Returns<Action>(write =>
            {
                write();
                return null;
            });
        return config;
    }
}
