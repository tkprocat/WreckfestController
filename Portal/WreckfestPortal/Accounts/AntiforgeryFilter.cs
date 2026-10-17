using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WreckfestPortal.Accounts;

/// <summary>
/// CSRF protection: every unsafe request to the portal's own API must carry the antiforgery
/// token in <c>X-XSRF-TOKEN</c>. The validation itself is the framework's.
/// </summary>
public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    /// <summary>
    /// The title of the 400 for a missing or stale token. The web app matches it to fetch a
    /// fresh token and retry (web/src/api/client.ts, ANTIFORGERY_FAILURE).
    /// </summary>
    public const string FailureTitle = "Missing or invalid antiforgery token.";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method)
            || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method)
            || HttpMethods.IsTrace(request.Method))
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = FailureTitle,
                Detail = $"Fetch GET /api/auth/antiforgery and send its {PortalAccounts.XsrfCookieName} cookie back in the {PortalAccounts.XsrfHeaderName} header.",
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }
}
