using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WreckfestController.Services;

/// <summary>
/// CSRF protection for browser traffic. Every unsafe request that does not carry
/// <c>X-Api-Key</c> must carry the antiforgery token in <c>X-XSRF-TOKEN</c>.
/// </summary>
/// <remarks>
/// The authorization middleware has already turned away unauthenticated requests to
/// protected endpoints, so what reaches this filter without a key is either
/// cookie-authenticated or an anonymous endpoint such as login. API-key requests are
/// exempt: a browser cannot attach a custom header cross-site without a CORS
/// preflight, which this API never grants, and scripts have no antiforgery cookie.
/// The built-in <c>[AutoValidateAntiforgeryToken]</c> cannot be used because it would
/// reject them. The validation itself is the framework's.
/// </remarks>
public sealed class CookieAntiforgeryFilter : IAsyncAuthorizationFilter
{
    private readonly IAntiforgery _antiforgery;

    public CookieAntiforgeryFilter(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method)
            || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method)
            || HttpMethods.IsTrace(request.Method)
            || request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName))
        {
            return;
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Missing or invalid antiforgery token.",
                Detail = $"Fetch GET /api/auth/antiforgery and send its {ApiAuthentication.XsrfCookieName} cookie back in the {ApiAuthentication.XsrfHeaderName} header.",
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }
}
