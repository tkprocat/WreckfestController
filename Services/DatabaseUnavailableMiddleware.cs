using Microsoft.AspNetCore.Http;
using WreckfestController.Data;

namespace WreckfestController.Services;

/// <summary>
/// Recovery mode for the API. While the database is unavailable, the only answer is
/// the anonymous <c>/api/auth/state</c>, reporting <c>degraded: true</c> so the web UI
/// can say why sign-in is unavailable. Every other request fails closed with 503.
/// </summary>
public sealed class DatabaseUnavailableMiddleware
{
    public const string AuthStatePath = "/api/auth/state";

    private readonly RequestDelegate _next;
    private readonly DatabaseState _state;

    public DatabaseUnavailableMiddleware(RequestDelegate next, DatabaseState state)
    {
        _next = next;
        _state = state;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_state.IsReady)
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals(AuthStatePath, StringComparison.OrdinalIgnoreCase))
        {
            await context.Response.WriteAsJsonAsync(new
            {
                authenticated = false,
                setupRequired = false,
                degraded = true,
            });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            degraded = true,
            error = "The controller's database is unavailable. See the controller window for details.",
        });
    }
}
