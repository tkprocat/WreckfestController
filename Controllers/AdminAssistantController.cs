using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WreckfestController.Models;
using WreckfestController.Services.Assistant;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;
using WreckfestController.Services.Cups;

namespace WreckfestController.Controllers;

/// <summary>
/// The admin panel's AI assistant: it looks things up like the public one, reads the
/// rotation, collections, cups and server settings, and proposes changes to them. It
/// writes nothing: each proposal comes back for the admin to apply through the admin API.
/// </summary>
/// <remarks>
/// It uses the public assistant's key, model and address, and needs only the key: an admin
/// may want it without a public AI page. Admins are not held to the daily question limit,
/// which caps what anonymous visitors can spend.
/// </remarks>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/assistant")]
public class AdminAssistantController : ControllerBase
{
    private readonly ISettingsStore _settings;
    private readonly AssistantChat _chat;
    private readonly ConfigService _config;
    private readonly CupStore _cups;
    private readonly IOptions<JsonOptions> _json;
    private readonly ILogger<AdminAssistantController> _logger;

    public AdminAssistantController(
        ISettingsStore settings,
        AssistantChat chat,
        ConfigService config,
        CupStore cups,
        IOptions<JsonOptions> json,
        ILogger<AdminAssistantController> logger)
    {
        _settings = settings;
        _chat = chat;
        _config = config;
        _cups = cups;
        _json = json;
        _logger = logger;
    }

    /// <summary>Whether an API key is set: the admin panel shows the assistant only then.</summary>
    [HttpGet]
    public AdminAssistantStatus Status() => new(!string.IsNullOrEmpty(_settings.Get<AssistantSettings>().ApiKey));

    /// <summary>
    /// The assistant's answer to the last message of <c>messages</c>, as for the public
    /// assistant, and the changes it proposes. Nothing is changed: the admin applies a
    /// proposal by sending its <c>body</c> to the endpoint its <c>kind</c> names, with
    /// <c>version</c> as If-Match. 404 without a key; 503 when the model cannot answer.
    /// </summary>
    [HttpPost("chat")]
    [RequestSizeLimit(AssistantController.MaxRequestBytes)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<AdminAssistantReply>> Chat([FromBody] AssistantChatRequest request, CancellationToken cancellationToken)
    {
        var settings = _settings.Get<AssistantSettings>();
        if (string.IsNullOrEmpty(settings.ApiKey))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "The AI assistant has no API key. Set one under Settings.");
        }

        if (AssistantController.Validate(request.Messages) is { } invalid)
        {
            return this.Invalid("messages", invalid);
        }

        var json = _json.Value.JsonSerializerOptions;
        var tools = new AdminAssistantTools(
            new AssistantTools(Build<PublicController>(), Build<PublicTracksController>(), json),
            _config,
            Build<CollectionsController>(),
            _cups,
            json);
        var conversation = request.Messages.Select(m => new AssistantMessage(m.Role, m.Content.Trim())).ToList();
        try
        {
            var reply = await _chat.AskAsync(
                settings, conversation, AdminAssistantTools.Definitions, tools.RunAsync, cancellationToken, AdminAssistantTools.SystemPrompt);
            return new AdminAssistantReply(AssistantController.Fit(reply), tools.Proposals);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reason is in the log, as for the public assistant: it can name the API's address.
            _logger.LogWarning(ex, "The admin AI assistant could not answer");
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The AI assistant cannot answer right now. The desktop app's log has the details.");
        }
        finally
        {
            // CollectionsController.Get sets one as it answers a lookup; this answer has none.
            Response.Headers.ETag = default;
        }
    }

    /// <summary>A controller for the tools, answering as part of this request.</summary>
    private T Build<T>()
        where T : ControllerBase
    {
        var controller = ActivatorUtilities.CreateInstance<T>(HttpContext.RequestServices);
        controller.ControllerContext = ControllerContext;
        return controller;
    }
}

/// <summary>GET /api/assistant.</summary>
public sealed record AdminAssistantStatus(bool Available);

/// <summary>The assistant's answer, and the changes it proposes, in order.</summary>
public sealed record AdminAssistantReply(string Reply, IReadOnlyList<AssistantProposal> Proposals);
