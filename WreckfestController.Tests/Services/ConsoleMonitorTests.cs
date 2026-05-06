using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ConsoleMonitorTests
{
    [Fact]
    public void ProcessOutput_FullConsoleRowsWithoutNewlines_EmitsEachRowSeparately()
    {
        // Arrange
        var monitor = new ConsoleMonitor(Mock.Of<ILogger<ConsoleMonitor>>());
        var outputs = new List<string>();
        monitor.SubscribeToOutput(outputs.Add);

        // Act
        InvokeProcessOutput(monitor, "16:53:14 - Alice: !yes".PadRight(80), 80);
        InvokeProcessOutput(monitor, "16:53:15 - Bob: !no".PadRight(80), 80);

        // Assert
        Assert.Equal(
            [
                "16:53:14 - Alice: !yes",
                "16:53:15 - Bob: !no"
            ],
            outputs);
    }

    [Fact]
    public void ProcessOutput_ShortTimestampedChatLineWithoutNewline_EmitsImmediately()
    {
        // Arrange
        var monitor = new ConsoleMonitor(Mock.Of<ILogger<ConsoleMonitor>>());
        var outputs = new List<string>();
        monitor.SubscribeToOutput(outputs.Add);

        // Act
        InvokeProcessOutput(monitor, "* 12:38:08 Shachor: !vote bonebreaker_valley_main_circuit 6", 120);

        // Assert
        Assert.Equal(["* 12:38:08 Shachor: !vote bonebreaker_valley_main_circuit 6"], outputs);
    }

    private static void InvokeProcessOutput(ConsoleMonitor monitor, string rawOutput, short consoleWidth)
    {
        var method = typeof(ConsoleMonitor).GetMethod(
            "ProcessOutput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(monitor, [rawOutput, consoleWidth]);
    }
}
