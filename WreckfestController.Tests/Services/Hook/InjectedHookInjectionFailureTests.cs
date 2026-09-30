using System.IO;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services.Hook;

namespace WreckfestController.Tests.Services.Hook;

/// <summary>
/// A failed injection answers the inject endpoint. Windows' own error text, and the DLL's
/// path, go to the log; the answer is plain (#153).
/// </summary>
public sealed class InjectedHookInjectionFailureTests
{
    [Fact]
    public async Task AFailedInjection_AnswersPlainly_AndLogsTheDetail()
    {
        // The reader looks for the DLL next to the tests; a placeholder is enough to reach
        // the injection itself, which then fails on a process that does not exist.
        var dll = Path.Combine(AppContext.BaseDirectory, "WreckfestConsoleHook.dll");
        var placed = !File.Exists(dll);
        if (placed)
        {
            File.WriteAllBytes(dll, []);
        }

        var logger = new Mock<ILogger<InjectedHookOutputReader>>();
        var reader = new InjectedHookOutputReader(logger.Object);
        try
        {
            var result = await reader.InjectAsync(int.MaxValue - 7);

            Assert.False(result.Success);
            Assert.Equal("The console hook could not be injected. The desktop app's log has the details.", result.Message);
            Assert.DoesNotContain(AppContext.BaseDirectory, result.Message, StringComparison.OrdinalIgnoreCase);
            logger.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("injection into process")),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            await reader.StopAsync();
            if (placed)
            {
                File.Delete(dll);
            }
        }
    }
}
