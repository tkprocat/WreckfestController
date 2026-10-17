using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WreckfestController.Tests.Api;

internal static class ProblemAssert
{
    /// <summary>Asserts a 400 whose <c>errors</c> name <paramref name="field"/>.</summary>
    public static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = problem.GetProperty("errors");
        // Ordinal: "Email" and "email" are different keys to a JavaScript form.
        Assert.True(
            errors.EnumerateObject().Any(e => e.Name == field),
            $"expected an error for '{field}', got {errors}");
    }
}
