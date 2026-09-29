using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WreckfestController.Tests.Controllers;

/// <summary>
/// What a controller needs to answer outside a web host: a request, and a factory for the
/// problem bodies <c>Problem(...)</c> and <c>ValidationProblem(...)</c> build.
/// </summary>
internal static class ControllerTesting
{
    public static T Hosted<T>(this T controller)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.ProblemDetailsFactory = new PlainProblemDetailsFactory();
        return controller;
    }

    /// <summary>A 409 refusal, and its reason.</summary>
    public static string RefusalOf<T>(ActionResult<T> result)
    {
        var refused = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
        return Assert.IsType<ProblemDetails>(refused.Value).Title!;
    }

    /// <summary>A 400 naming <paramref name="field"/>.</summary>
    public static void AssertFieldError<T>(ActionResult<T> result, string field)
    {
        var invalid = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(invalid.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status ?? invalid.StatusCode);
        Assert.Contains(field, problem.Errors.Keys);
    }

    private sealed class PlainProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null) =>
            new() { Status = statusCode ?? 500, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext,
            ModelStateDictionary modelStateDictionary,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode ?? 400, Title = title, Type = type, Detail = detail, Instance = instance };
    }
}
