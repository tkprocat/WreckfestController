using System.Text.Json;
using System.Text.Json.Nodes;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The API contract the web app is typed against: web/src/api/openapi.json must match what
/// the controller serves, so a changed endpoint cannot drift from the Vue app unnoticed.
/// </summary>
public class OpenApiContractTests
{
    /// <summary>Set to 1 to rewrite the committed document from the running API.</summary>
    private const string UpdateVariable = "WFC_UPDATE_OPENAPI";

    [Fact]
    public async Task TheCommittedContract_MatchesTheApi()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var served = Normalize(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        var path = Path.Combine(RepositoryRoot(), "web", "src", "api", "openapi.json");
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, served, TestContext.Current.CancellationToken);
            return;
        }

        var committed = File.Exists(path) ? Normalize(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)) : "(missing)";
        Assert.True(
            committed == served,
            $"web/src/api/openapi.json is out of date with the API. Run `{UpdateVariable}=1 dotnet test --filter-class " +
            $"{typeof(OpenApiContractTests).FullName}`, then `npm run gen:api` in web/, and commit both.");
    }

    [Fact]
    public async Task TheDocument_NeedsASignedInCaller()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Indented, with a trailing newline, and without <c>servers</c>: the test server's
    /// address is not part of the contract.
    /// </summary>
    private static string Normalize(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WreckfestController.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }
}
