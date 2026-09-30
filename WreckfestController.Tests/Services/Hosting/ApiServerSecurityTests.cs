using Microsoft.Extensions.Configuration;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Hosting;

namespace WreckfestController.Tests.Services.Hosting;

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

    // Several controller instances can manage separate servers on one Windows host,
    // so the ports must not be fixed to the defaults.
    [Theory]
    [InlineData(false, "6200", "6201")]
    [InlineData(true, "8080", "8443")]
    public void Endpoints_UseTheConfiguredAddressAndPorts(bool allowRemote, string httpPort, string httpsPort)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:AllowRemote"] = allowRemote.ToString(),
                ["Api:HttpPort"] = httpPort,
                ["Api:HttpsPort"] = httpsPort,
            })
            .Build();

        var endpoints = WreckfestController.Services.Hosting.Https.ApiEndpoints.Resolve(configuration, AppContext.BaseDirectory);

        Assert.Equal(allowRemote ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback, endpoints.Address);
        Assert.Equal((int.Parse(httpPort), int.Parse(httpsPort)), (endpoints.HttpPort, endpoints.HttpsPort));
    }
}
