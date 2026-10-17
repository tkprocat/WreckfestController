namespace WreckfestPortal.Tests;

public sealed class PortalOptionsTests
{
    private static PortalServer Server(string id = "tvtp2", string apiUrl = "http://127.0.0.1:5102") =>
        new() { Id = id, Name = "TVTP2", ApiUrl = apiUrl };

    [Fact]
    public void UsableSettings_HaveNoProblems()
    {
        var options = new PortalOptions
        {
            Servers = [Server(), Server("vanilla_2-b", "https://10.0.0.5:5100/")],
            TrustedProxies = ["192.168.1.1", "10.0.0.0/24", " fd00::1 "],
        };

        Assert.Empty(options.Problems());
    }

    [Fact]
    public void TheIdIsTheSitesPath()
    {
        Assert.Equal("/tvtp2/", Server().Path);
    }

    [Theory]
    [InlineData("tv tp2", "not usable as a path")]
    [InlineData("tvtp2/admin", "not usable as a path")]
    [InlineData("..", "not usable as a path")]
    [InlineData("v2.0", "not usable as a path")]
    [InlineData("api", "the portal uses itself")]
    [InlineData("Assets", "the portal uses itself")]
    public void AnIdThatCannotBeAPath_IsAProblem(string id, string problem)
    {
        var options = new PortalOptions { Servers = [Server(id)] };

        Assert.Contains(options.Problems(), p => p.Contains("Portal:Servers:0:Id", StringComparison.Ordinal) && p.Contains(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("127.0.0.1:5102")]
    [InlineData("ftp://127.0.0.1/")]
    public void ABadAddress_IsAProblem(string apiUrl)
    {
        var options = new PortalOptions { Servers = [Server(apiUrl: apiUrl)] };

        Assert.Contains(options.Problems(), p => p.Contains("Portal:Servers:0:ApiUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("proxy.lan")]
    [InlineData("10.0.0.0/33")]
    [InlineData("")]
    public void AnUnusableProxy_IsAProblem(string proxy)
    {
        var options = new PortalOptions { Servers = [Server()], TrustedProxies = [proxy] };

        Assert.Contains(options.Problems(), p => p.Contains("Portal:TrustedProxies:0", StringComparison.Ordinal));
    }

    [Fact]
    public void NoServers_ADuplicateId_AndBadTimings_AreProblems()
    {
        Assert.Contains(new PortalOptions().Problems(), p => p.Contains("lists no servers", StringComparison.Ordinal));
        Assert.Contains(new PortalOptions { Servers = [Server(), Server("TVTP2")] }.Problems(), p => p.Contains("used twice", StringComparison.Ordinal));
        Assert.Contains(new PortalOptions { PollSeconds = 1, Servers = [Server()] }.Problems(), p => p.Contains("PollSeconds", StringComparison.Ordinal));
        // PeriodicTimer refuses an interval this long: a settings error, not a crash.
        Assert.Contains(new PortalOptions { PollSeconds = 4_294_968, Servers = [Server()] }.Problems(), p => p.Contains("PollSeconds", StringComparison.Ordinal));
        Assert.Contains(new PortalOptions { TimeoutSeconds = 0, Servers = [Server()] }.Problems(), p => p.Contains("TimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains(new PortalOptions { Title = " ", Servers = [Server()] }.Problems(), p => p.Contains("Title", StringComparison.Ordinal));
    }
}
