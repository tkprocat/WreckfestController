namespace WreckfestPortal.Tests;

public sealed class PortalOptionsTests
{
    private static PortalServer Server(string id = "tvtp2", string link = "/tvtp2/", string apiUrl = "http://127.0.0.1:5102/tvtp2") =>
        new() { Id = id, Name = "TVTP2", Link = link, ApiUrl = apiUrl };

    [Fact]
    public void UsableSettings_HaveNoProblems()
    {
        var options = new PortalOptions { Servers = [Server(), Server("vanilla", "https://vanilla.example.com/", "http://10.0.0.5:5100")] };

        Assert.Empty(options.Problems());
    }

    [Theory]
    [InlineData("tvtp2/", "http://127.0.0.1:5102", "Link")]
    [InlineData("//evil.example.com/", "http://127.0.0.1:5102", "Link")]
    [InlineData("javascript:alert(1)", "http://127.0.0.1:5102", "Link")]
    [InlineData("/tvtp2/", "127.0.0.1:5102", "ApiUrl")]
    [InlineData("/tvtp2/", "ftp://127.0.0.1/", "ApiUrl")]
    public void ABadAddress_IsAProblem(string link, string apiUrl, string key)
    {
        var options = new PortalOptions { Servers = [Server(link: link, apiUrl: apiUrl)] };

        Assert.Contains(options.Problems(), p => p.Contains($"Portal:Servers:0:{key}", StringComparison.Ordinal));
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
