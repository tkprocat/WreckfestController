using Microsoft.AspNetCore.Http;
using WreckfestController.Data;

namespace WreckfestController.Services.Hosting;

/// <summary>
/// Recovery mode for the API. While the database is unavailable, every API and hub request
/// fails closed with 503 and <c>degraded: true</c>. The web app comes from the portal and
/// signing in is the portal's, so the pages still load and can say what is wrong.
/// </summary>
public sealed class DatabaseUnavailableMiddleware
{
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

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            degraded = true,
            error = "The controller's database is unavailable. See the controller window for details.",
        });
    }
}
