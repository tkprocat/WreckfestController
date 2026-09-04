using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace WreckfestController.Services;

public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKeyBytes;

    public ApiKeyMiddleware(RequestDelegate next, string? apiKey)
    {
        _next = next;
        _expectedKeyBytes = Encoding.UTF8.GetBytes(apiKey ?? string.Empty);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var isAuthorized = _expectedKeyBytes.Length > 0
            && context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey)
            && CryptographicOperations.FixedTimeEquals(
                _expectedKeyBytes,
                Encoding.UTF8.GetBytes(providedKey.ToString()));

        if (!isAuthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }
}
