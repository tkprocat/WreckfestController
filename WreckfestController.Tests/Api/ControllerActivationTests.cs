using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Services.Hosting;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The API host copies the WPF app's singletons into its own container by hand, so a
/// service a controller needs can be missing there even though the app has it. MVC
/// only notices when a request reaches that controller, and the controller tests
/// construct controllers directly, so build every one from the API host's services.
/// </summary>
public class ControllerActivationTests
{
    public static TheoryData<Type> Controllers()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(ApiServer).Assembly.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
                     .OrderBy(t => t.Name))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Controllers))]
    public async Task EveryController_CanBeBuiltFromTheApiHostsServices(Type controller)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var scope = host.Services.CreateScope();

        var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller);

        Assert.IsAssignableFrom(controller, instance);
    }

    [Fact]
    public async Task AnEventsEndpoint_Answers()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/events/current", TestContext.Current.CancellationToken);

        Assert.True((int)response.StatusCode < 500, $"GET /api/events/current returned {(int)response.StatusCode}");
    }
}
