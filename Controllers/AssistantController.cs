using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WreckfestController.Models;
using WreckfestController.Services.Assistant;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;

namespace WreckfestController.Controllers;

/// <summary>
/// The public site's AI assistant: visitors ask about the server in plain language, and
/// the controller asks the configured model, which looks the answers up with
/// <see cref="AssistantTools"/>.
/// </summary>
/// <remarks>
/// The API key never leaves the controller. Questions are capped per client by
/// <see cref="RateLimits.AssistantPolicy"/> and per day by
/// <see cref="AssistantSettings.DailyRequestLimit"/>, since each one is paid for upstream.
/// Anonymous callers get no detail of a failure, as for <see cref="PublicController"/>.
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimits.PublicPolicy)]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
[Route("api/public/assistant")]
public class AssistantController : ControllerBase
{
    /// <summary>The most messages a conversation may carry, the new question included.</summary>
    public const int MaxMessages = 20;

    /// <summary>The longest message, in characters.</summary>
    public const int MaxMessageLength = 2000;

    private readonly ISettingsStore _settings;
    private readonly AssistantChat _chat;
    private readonly AssistantUsage _usage;
    private readonly IOptions<JsonOptions> _json;
    private readonly ILogger<AssistantController> _logger;

    public AssistantController(
        ISettingsStore settings,
        AssistantChat chat,
        AssistantUsage usage,
        IOptions<JsonOptions> json,
        ILogger<AssistantController> logger)
    {
        _settings = settings;
        _chat = chat;
        _usage = usage;
        _json = json;
        _logger = logger;
    }

    /// <summary>Whether the assistant is turned on and has a key: the web app shows its page only then.</summary>
    [HttpGet]
    public PublicAssistantStatus Status() => new(IsAvailable(_settings.Get<AssistantSettings>()));

    /// <summary>
    /// The assistant's answer to the last message of <c>messages</c>, the conversation so far
    /// (at most <see cref="MaxMessages"/>, each <c>user</c> or <c>assistant</c>, ending with
    /// the visitor's question). 404 while the assistant is off; 429 once today's questions
    /// are used up; 503 when the model cannot answer.
    /// </summary>
    [HttpPost("chat")]
    [EnableRateLimiting(RateLimits.AssistantPolicy)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<AssistantChatReply>> Chat([FromBody] AssistantChatRequest request, CancellationToken cancellationToken)
    {
        var settings = _settings.Get<AssistantSettings>();
        if (!IsAvailable(settings))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "The AI assistant is turned off.");
        }

        if (Validate(request.Messages) is { } invalid)
        {
            return this.Invalid("messages", invalid);
        }

        if (!_usage.TryTake(settings.DailyRequestLimit))
        {
            _logger.LogWarning("The AI assistant reached its limit of {Limit} questions for today", settings.DailyRequestLimit);
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "The AI assistant has answered all the questions it can for today. Try again tomorrow.");
        }

        var tools = new AssistantTools(Build<PublicController>(), Build<PublicTracksController>(), _json.Value.JsonSerializerOptions);
        var conversation = request.Messages.Select(m => new AssistantMessage(m.Role, m.Content.Trim())).ToList();
        try
        {
            var reply = await _chat.AskAsync(settings, conversation, AssistantTools.Definitions, tools.RunAsync, cancellationToken);
            return new AssistantChatReply(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AssistantUnavailableException for the model; anything else is ours, and the
            // visitor gets the same fixed answer either way.
            _logger.LogWarning(ex, "The AI assistant could not answer");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The AI assistant cannot answer right now.");
        }
    }

    private static bool IsAvailable(AssistantSettings settings) => settings.Enabled && !string.IsNullOrEmpty(settings.ApiKey);

    private static string? Validate(IReadOnlyList<AssistantChatMessage?>? messages)
    {
        if (messages is null || messages.Count is 0 or > MaxMessages)
        {
            return $"messages must hold between 1 and {MaxMessages} messages.";
        }

        foreach (var message in messages)
        {
            if (message is null)
            {
                return "Each message must be an object with a role and content.";
            }

            if (message.Role is not ("user" or "assistant"))
            {
                return "Each message's role must be user or assistant.";
            }

            if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > MaxMessageLength)
            {
                return $"Each message must have between 1 and {MaxMessageLength} characters.";
            }
        }

        return messages[^1]!.Role == "user" ? null : "The last message must be the visitor's question.";
    }

    /// <summary>A public controller for the tools, answering as part of this request.</summary>
    private T Build<T>()
        where T : ControllerBase
    {
        var controller = ActivatorUtilities.CreateInstance<T>(HttpContext.RequestServices);
        controller.ControllerContext = ControllerContext;
        return controller;
    }
}

/// <summary>GET /api/public/assistant.</summary>
public sealed record PublicAssistantStatus(bool Enabled);

/// <summary>POST /api/public/assistant/chat: the conversation so far, ending with the new question.</summary>
public sealed record AssistantChatRequest(IReadOnlyList<AssistantChatMessage> Messages);

/// <summary>One message: <c>role</c> is <c>user</c> or <c>assistant</c>.</summary>
public sealed record AssistantChatMessage(string Role, string Content);

/// <summary>The assistant's answer, as plain text.</summary>
public sealed record AssistantChatReply(string Reply);
