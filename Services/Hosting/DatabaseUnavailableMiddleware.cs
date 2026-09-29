using Microsoft.AspNetCore.Http;
using WreckfestController.Data;

namespace WreckfestController.Services.Hosting;

/// <summary>
/// Recovery mode for the API. While the database is unavailable, the only API answer is
/// the anonymous <c>/api/auth/state</c>, reporting <c>degraded: true</c> so the web UI
/// can say why sign-in is unavailable. Every other API and hub request fails closed with
/// 503. The web app's own pages still load: the files are served before this runs,
/// and a page route gets index.html from here, before authentication, which checks a
/// signed-in browser's cookie against the database.
/// </summary>
public sealed class DatabaseUnavailableMiddleware
{
    public const string AuthStatePath = "/api/auth/state";

    private readonly RequestDelegate _next;
    private readonly DatabaseState _state;
    private readonly WebApp _web;

    public DatabaseUnavailableMiddleware(RequestDelegate next, DatabaseState state, WebApp web)
    {
        _next = next;
        _state = state;
        _web = web;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_state.IsReady)
        {
            await _next(context);
            return;
        }

        if (WebApp.IsAppPath(context.Request.Path) && await _web.TryServeIndexAsync(context))
        {
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals(AuthStatePath, StringComparison.OrdinalIgnoreCase))
        {
            await context.Response.WriteAsJsonAsync(new
            {
                authenticated = false,
                user = (object?)null,
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
