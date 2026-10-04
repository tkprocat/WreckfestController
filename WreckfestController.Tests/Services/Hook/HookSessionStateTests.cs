using Xunit;
using WreckfestController.Services.Hook;

namespace WreckfestController.Tests.Services.Hook;

public class HookSessionStateTests
{
    [Fact]
    public void Parses_TheHooksAnswer()
    {
        Assert.True(HookSessionState.TryParse("OK session state=2 timer=-100000 counter=3 ended=0", out var session));

        Assert.Equal(new HookSessionState(2, -100000, 3, false), session);
        Assert.Equal(ServerSessionPhase.Racing, session!.Phase);
    }

    [Fact]
    public void Parses_TheResultsScreen()
    {
        Assert.True(HookSessionState.TryParse("OK session state=3 timer=18500 counter=3 ended=1", out var session));

        Assert.Equal(ServerSessionPhase.Results, session!.Phase);
        Assert.Equal(18500, session.TimerMs);
        Assert.True(session.Ended);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(1234)]
    public void AnUnknownState_HasNoPhase(int state)
    {
        Assert.True(HookSessionState.TryParse($"OK session state={state} timer=0 counter=0 ended=0", out var session));

        Assert.Null(session!.Phase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ERR session SERVER object unreadable")]
    [InlineData("ERR session module layout not validated")]
    [InlineData("OK dispatched command=__hook_session")]
    [InlineData("OK session state=2 timer=0 counter=1")]
    [InlineData("OK session state=two timer=0 counter=1 ended=0")]
    [InlineData("OK session state=2 timer= counter=1 ended=0")]
    [InlineData("OK session state=0 timer=0 counter=1 ended=0 state=2")]
    public void RejectsAnythingElse(string? line)
    {
        Assert.False(HookSessionState.TryParse(line, out var session));
        Assert.Null(session);
    }
}
