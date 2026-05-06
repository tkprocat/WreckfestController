using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ConfiguredServerInputWriterTests
{
    [Fact]
    public async Task SendCommandAsync_WhenInjectedHookInputConfigured_UsesInjectedHookWriter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WreckfestServer:InputMode"] = ServerInputModes.InjectedHook
            })
            .Build();

        var consoleWriter = new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>());
        var injectedWriter = new Mock<InjectedHookInputWriter>(Mock.Of<ILogger<InjectedHookInputWriter>>());
        injectedWriter
            .Setup(w => w.SendCommandAsync("status", 1234))
            .ReturnsAsync((true, "sent through hook"));

        var writer = new ConfiguredServerInputWriter(configuration, consoleWriter.Object, injectedWriter.Object);

        var result = await writer.SendCommandAsync("status", 1234);

        Assert.True(result.Success);
        Assert.Equal("sent through hook", result.Message);
        injectedWriter.Verify(w => w.SendCommandAsync("status", 1234), Times.Once);
        consoleWriter.Verify(w => w.SendCommandAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SendCommandAsync_WhenInputModeMissing_UsesConsoleWriter()
    {
        var configuration = new ConfigurationBuilder().Build();
        var consoleWriter = new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>());
        var injectedWriter = new Mock<InjectedHookInputWriter>(Mock.Of<ILogger<InjectedHookInputWriter>>());
        consoleWriter
            .Setup(w => w.SendCommandAsync("status", 1234))
            .ReturnsAsync((true, "sent through console"));

        var writer = new ConfiguredServerInputWriter(configuration, consoleWriter.Object, injectedWriter.Object);

        var result = await writer.SendCommandAsync("status", 1234);

        Assert.True(result.Success);
        Assert.Equal("sent through console", result.Message);
        consoleWriter.Verify(w => w.SendCommandAsync("status", 1234), Times.Once);
        injectedWriter.Verify(w => w.SendCommandAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
}
