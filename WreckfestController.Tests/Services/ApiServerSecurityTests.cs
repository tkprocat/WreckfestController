using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class ApiServerSecurityTests
{
    [Fact]
    public void ApiKey_MatchesTheConfiguredKey()
    {
        Assert.True(ApiKeyAuthenticationHandler.Matches("test-key", "test-key"));
    }

    [Theory]
    [InlineData("test-key", "wrong-key")]
    [InlineData("test-key", "")]
    [InlineData("test-key", "test-key ")]
    public void ApiKey_RejectsAnyOtherValue(string configured, string provided)
    {
        Assert.False(ApiKeyAuthenticationHandler.Matches(configured, provided));
    }

    // The key is optional now, so a blank one must mean "no key accepted", not "any".
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("", "anything")]
    public void ApiKey_BlankConfiguredKey_MatchesNothing(string? configured, string provided)
    {
        Assert.False(ApiKeyAuthenticationHandler.Matches(configured, provided));
    }

    [Theory]
    [InlineData(false, "http://127.0.0.1:5100;https://127.0.0.1:5101")]
    [InlineData(true, "http://0.0.0.0:5100;https://0.0.0.0:5101")]
    public void GetListenUrls_UsesExpectedBindAddress(bool allowRemote, string expectedUrls)
    {
        Assert.Equal(expectedUrls, ApiServer.GetListenUrls(allowRemote));
    }

    // Several controller instances can manage separate servers on one Windows host,
    // so the ports must not be fixed to the defaults.
    [Theory]
    [InlineData(false, 6200, 6201, "http://127.0.0.1:6200;https://127.0.0.1:6201")]
    [InlineData(true, 8080, 8443, "http://0.0.0.0:8080;https://0.0.0.0:8443")]
    public void GetListenUrls_UsesConfiguredPorts(
        bool allowRemote,
        int httpPort,
        int httpsPort,
        string expectedUrls)
    {
        Assert.Equal(expectedUrls, ApiServer.GetListenUrls(allowRemote, httpPort, httpsPort));
    }
}
