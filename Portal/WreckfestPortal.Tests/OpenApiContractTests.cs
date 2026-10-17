using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WreckfestPortal.Tests;

/// <summary>
/// The portal's API contract the web app is typed against: web/src/api/portal-openapi.json
/// must match what the portal serves, so a changed endpoint cannot drift from the Vue app.
/// </summary>
public sealed class OpenApiContractTests
{
    /// <summary>Set to 1 to rewrite the committed document from the running portal.</summary>
    private const string UpdateVariable = "WFC_UPDATE_OPENAPI";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCommittedContract_MatchesTheApi()
    {
        await using var host = await AccountTestHost.StartAsync();
        await host.CreateAdminAsync();
        using var admin = await host.SignInAsync("admin");
        var served = Normalize(await admin.Http.GetStringAsync("/openapi/v1.json", Ct));

        var path = Path.Combine(RepositoryRoot(), "web", "src", "api", "portal-openapi.json");
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, served, Ct);
            return;
        }

        var committed = File.Exists(path) ? Normalize(await File.ReadAllTextAsync(path, Ct)) : "(missing)";
        Assert.True(
            committed == served,
            $"web/src/api/portal-openapi.json is out of date with the portal. Run `{UpdateVariable}=1 dotnet test --filter-class " +
            $"{typeof(OpenApiContractTests).FullName}`, then `npm run gen:api` in web/, and commit both.");
    }

    [Fact]
    public async Task TheDocument_NeedsASignedInCaller()
    {
        await using var host = await AccountTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Indented, with a trailing newline, and without the test server's address.</summary>
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
