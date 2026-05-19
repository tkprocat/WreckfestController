using Microsoft.Extensions.Logging;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ConsoleWriterTests
{
    [Fact]
    public async Task SendCommandAsync_SendsCommandWithoutEmbeddedNewLine()
    {
        var writer = new CapturingConsoleWriter();

        var result = await writer.SendCommandAsync("/message hello", 1234);

        Assert.True(result.Success);
        Assert.Equal("/message hello", writer.SentCommand);
    }

    private sealed class CapturingConsoleWriter : ConsoleWriter
    {
        public string? SentCommand { get; private set; }

        public CapturingConsoleWriter()
            : base(Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsoleWriter>.Instance)
        {
        }

        public override IntPtr FindConsoleWindow(string? windowTitle = null)
        {
            return 1234;
        }

        public override bool SendCommand(IntPtr windowHandle, string command)
        {
            SentCommand = command;
            return true;
        }
    }
}
