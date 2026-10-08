using WreckfestController.Services.Desktop;
using Xunit;

namespace WreckfestController.Tests.Services.Desktop;

public class DialogServiceTests
{
    [Fact]
    public async Task ShowsAtOnceWhenNoDialogIsOpen()
    {
        var shown = 0;

        var result = await DialogService.ShowWhenFreeAsync(
            () => false,
            () => { shown++; return Task.FromResult<object?>("ok"); },
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, shown);
        Assert.Equal("ok", result);
    }

    // The crash: a second dialog shown while one was open threw "DialogHost is already open".
    [Fact]
    public async Task WaitsForTheOpenDialogToCloseBeforeShowing()
    {
        var open = true;
        var shown = false;

        var pending = DialogService.ShowWhenFreeAsync(
            () => Volatile.Read(ref open),
            () => { shown = true; return Task.FromResult<object?>(true); },
            TimeSpan.FromMilliseconds(5));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(shown);
        Assert.False(pending.IsCompleted);

        Volatile.Write(ref open, false);
        Assert.Equal(true, await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(shown);
    }
}
