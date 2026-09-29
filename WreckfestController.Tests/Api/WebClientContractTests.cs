using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Api;

/// <summary>
/// What the web app matches in the API's answers, beyond the OpenAPI document. If one side
/// changes, the web app quietly stops recognising the answer, so the text is pinned here.
/// </summary>
public class WebClientContractTests
{
    // client.ts retries once with a fresh token when a 400 carries this title; a mismatch
    // would leave a stale token failing every change, including signing back in.
    [Fact]
    public void TheWebApp_RecognisesTheAntiforgeryFailureTitle()
    {
        var client = File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "api", "client.ts"));

        Assert.Contains($"export const ANTIFORGERY_FAILURE = '{CookieAntiforgeryFilter.FailureTitle}'", client);
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
