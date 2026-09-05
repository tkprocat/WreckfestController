using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ControllerLogBufferTests
{
    [Fact]
    public void BurstBeforeUiRefresh_RetainsOnlyNewest500Entries()
    {
        using var buffer = new ControllerLogBuffer();
        for (var i = 0; i < 10000; i++)
            buffer.Add($"entry {i}");

        var snapshot = Assert.IsType<string[]>(buffer.TakeSnapshot());
        Assert.Equal(500, snapshot.Length);
        Assert.Equal("entry 9500", snapshot[0]);
        Assert.Equal("entry 9999", snapshot[^1]);
        Assert.Null(buffer.TakeSnapshot());
    }

    [Fact]
    public void Overflow_RemovesWholeMultilineEntry()
    {
        using var buffer = new ControllerLogBuffer(2);
        buffer.Add("old entry\nException: old details");
        buffer.Add("retained entry\nException: retained details");
        buffer.Add("new entry");

        Assert.Equal(
            new[] { "retained entry\nException: retained details", "new entry" },
            buffer.TakeSnapshot());
    }

    [Fact]
    public void Clear_DiscardsEntriesThatHaveNotBeenDisplayed()
    {
        using var buffer = new ControllerLogBuffer();
        buffer.Add("pending");
        buffer.Clear();
        Assert.Empty(Assert.IsType<string[]>(buffer.TakeSnapshot()));
        buffer.Add("after clear");
        Assert.Equal(new[] { "after clear" }, buffer.TakeSnapshot());
    }

    [Fact]
    public void ConcurrentProducers_KeepBufferBounded()
    {
        using var buffer = new ControllerLogBuffer();
        Parallel.For(0, 10000, i => buffer.Add($"entry {i}"));
        var snapshot = Assert.IsType<string[]>(buffer.TakeSnapshot());
        Assert.Equal(500, snapshot.Length);
        Assert.Equal(500, snapshot.Distinct().Count());
    }

    [Fact]
    public void Dispose_IgnoresLateLogCalls()
    {
        var buffer = new ControllerLogBuffer();
        buffer.Add("before close");
        buffer.Dispose();
        buffer.Add("after close");
        Assert.Null(buffer.TakeSnapshot());
    }
}
