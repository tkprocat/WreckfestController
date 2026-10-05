using WreckfestController.Services.ServerControl;

namespace WreckfestController.Tests.Services.ServerControl;

/// <summary>
/// The controller's id (#201): stable for a database path, so the controller recognises its
/// server after it restarts; different for another path; and the marker it adds to the
/// server's arguments replaces any marker already there.
/// </summary>
public class ControllerInstanceTests
{
    [Fact]
    public void Id_IsEightLowerCaseHexDigits_AndTheSameEveryTime()
    {
        var path = @"C:\Data\WreckfestController\controller.db";

        var id = ControllerInstance.IdFor(path);

        Assert.Matches("^[0-9a-f]{8}$", id);
        Assert.Equal(id, new ControllerInstance(path).Id);
    }

    [Fact]
    public void Id_IgnoresTheCaseOfThePath()
    {
        Assert.Equal(
            ControllerInstance.IdFor(@"C:\Data\WreckfestController\controller.db"),
            ControllerInstance.IdFor(@"c:\data\wreckfestcontroller\CONTROLLER.DB"));
    }

    [Fact]
    public void Id_DiffersForAnotherDatabase()
    {
        Assert.NotEqual(
            ControllerInstance.IdFor(@"C:\Data\server1\controller.db"),
            ControllerInstance.IdFor(@"C:\Data\server2\controller.db"));
    }

    [Fact]
    public void Argument_IsTheMarkerWithTheId()
    {
        var instance = new ControllerInstance(@"C:\Data\controller.db");

        Assert.Equal($"-wfc_controller={instance.Id}", instance.Argument);
    }

    [Theory]
    [InlineData("-s server_config=server_config.cfg", "-s server_config=server_config.cfg {0}")]
    [InlineData("  -s server_config=a.cfg  ", "-s server_config=a.cfg {0}")]
    [InlineData("-s server_config=a.cfg -wfc_controller=deadbeef", "-s server_config=a.cfg -wfc_controller=deadbeef {0}")]
    [InlineData("-s \"-wfc_controller=deadbeef\"", "-s \"-wfc_controller=deadbeef\" {0}")]
    [InlineData("-s --save-dir=\"C:\\Servers -wfc_controller=copy\"", "-s --save-dir=\"C:\\Servers -wfc_controller=copy\" {0}")]
    [InlineData("", "{0}")]
    [InlineData(null, "{0}")]
    public void TryAddTo_AppendsTheMarker_LeavingTheArgumentsAsTheyAre(string? arguments, string expected)
    {
        var instance = new ControllerInstance(@"C:\Data\controller.db");

        Assert.True(instance.TryAddTo(arguments, out var withMarker));
        Assert.Equal(string.Format(expected, instance.Argument), withMarker);
    }

    [Theory]
    [InlineData("-s server_config=a.cfg -wfc_controller=deadbeef")]
    [InlineData("-s \"-wfc_controller=deadbeef\"")]
    [InlineData("-s --save-dir=\"C:\\Servers -wfc_controller=copy\"")]
    public void TryAddTo_WhatItAdds_IsReadBackAsThisController(string arguments)
    {
        var instance = new ControllerInstance(@"C:\Data\controller.db");
        Assert.True(instance.TryAddTo(arguments, out var withMarker));

        var commandLine = @"""C:\Servers\Wreckfest_x64.exe"" " + withMarker;

        Assert.Equal(instance.Id, WindowsCommandLine.ControllerMarker(commandLine));
        Assert.True(WindowsCommandLine.HasServerFlag(commandLine));
    }

    [Fact]
    public void TryAddTo_RefusesArgumentsEndingInsideAQuote()
    {
        var instance = new ControllerInstance(@"C:\Data\controller.db");

        Assert.False(instance.TryAddTo("-s --save-dir=\"C:\\Servers", out _));
    }
}
