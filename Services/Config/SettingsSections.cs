using Microsoft.Extensions.Configuration;
using WreckfestController.Models;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Voting;

namespace WreckfestController.Services.Config;

/// <summary>
/// The settings sections <see cref="SettingsStore"/> keeps: each one's name, its first-run
/// value and how a value is brought into range before it is stored.
/// </summary>
/// <remarks>
/// First-run values come from the <b>shipped</b> appsettings.json only, never from
/// user-settings.json: 2.0 starts clean, and a 1.x file is not converted.
/// </remarks>
public static class SettingsSections
{
    public const string WreckfestServer = "WreckfestServer";
    public const string SteamCmd = "SteamCmd";
    public const string Vote = "Vote";
    public const string PublicSite = "PublicSite";
    public const string Assistant = "Assistant";

    public const string DefaultServerArguments = "-s server_config=server_config.cfg";
    public const string DefaultWreckfestAppId = "361580";

    private static readonly Dictionary<Type, Definition> Definitions = new()
    {
        [typeof(WreckfestServerSettings)] = new(WreckfestServer, DefaultServer, s => Normalize((WreckfestServerSettings)s)),
        [typeof(SteamCmdSettings)] = new(SteamCmd, DefaultSteamCmd, s => Normalize((SteamCmdSettings)s)),
        [typeof(VoteSettings)] = new(Vote, DefaultVote, s => Normalize((VoteSettings)s)),
        [typeof(PublicSiteSettings)] = new(PublicSite, DefaultPublicSite, s => Normalize((PublicSiteSettings)s)),
        [typeof(AssistantSettings)] = new(Assistant, DefaultAssistant, s => Normalize((AssistantSettings)s)),
    };

    /// <summary>Every section type the store keeps.</summary>
    public static IReadOnlyCollection<Type> Types => Definitions.Keys;

    public static string NameOf(Type type) => Get(type).Name;

    /// <summary>The first-run value, from the shipped defaults.</summary>
    public static object Default(Type type, IConfiguration shipped) => Get(type).Normalize(Get(type).Default(shipped));

    /// <summary>Brings <paramref name="value"/> into range. Returns the same instance.</summary>
    public static object Normalize(Type type, object value) => Get(type).Normalize(value);

    private static Definition Get(Type type) =>
        Definitions.TryGetValue(type, out var definition)
            ? definition
            : throw new ArgumentException($"{type.Name} is not a settings section.", nameof(type));

    private static WreckfestServerSettings DefaultServer(IConfiguration shipped) => new()
    {
        ServerPath = shipped["WreckfestServer:ServerPath"] ?? string.Empty,
        ServerArguments = shipped["WreckfestServer:ServerArguments"] ?? DefaultServerArguments,
        WorkingDirectory = shipped["WreckfestServer:WorkingDirectory"] ?? string.Empty,
        LogFilePath = shipped["WreckfestServer:LogFilePath"] ?? string.Empty,
    };

    private static SteamCmdSettings DefaultSteamCmd(IConfiguration shipped) => new()
    {
        SteamCmdPath = shipped["SteamCmd:SteamCmdPath"] ?? string.Empty,
        WreckfestAppId = shipped["SteamCmd:WreckfestAppId"] ?? DefaultWreckfestAppId,
    };

    private static VoteSettings DefaultVote(IConfiguration shipped)
    {
        var vote = new VoteSettings();
        var section = shipped.GetSection(Vote);
        vote.Mode = VoteModes.Normalize(section["Mode"], section.GetValue<bool?>("Enabled"));
        vote.DirectCooldownSeconds = section.GetValue(nameof(VoteSettings.DirectCooldownSeconds), vote.DirectCooldownSeconds);
        vote.VoteTimeoutSeconds = section.GetValue(nameof(VoteSettings.VoteTimeoutSeconds), vote.VoteTimeoutSeconds);
        vote.MaxLapsAllowed = section.GetValue(nameof(VoteSettings.MaxLapsAllowed), vote.MaxLapsAllowed);
        vote.MessageDelayMs = section.GetValue(nameof(VoteSettings.MessageDelayMs), vote.MessageDelayMs);
        vote.SuppressCommandsDuringRace = section.GetValue(nameof(VoteSettings.SuppressCommandsDuringRace), vote.SuppressCommandsDuringRace);
        return vote;
    }

    private static PublicSiteSettings DefaultPublicSite(IConfiguration shipped)
    {
        var site = new PublicSiteSettings();
        site.FeedItemCount = shipped.GetSection(PublicSite).GetValue(nameof(PublicSiteSettings.FeedItemCount), site.FeedItemCount);
        return site;
    }

    // No key from the shipped file: a key is entered by an admin, never shipped.
    private static AssistantSettings DefaultAssistant(IConfiguration shipped)
    {
        var assistant = new AssistantSettings();
        var section = shipped.GetSection(Assistant);
        assistant.BaseUrl = section[nameof(AssistantSettings.BaseUrl)] ?? assistant.BaseUrl;
        assistant.Model = section[nameof(AssistantSettings.Model)] ?? assistant.Model;
        assistant.DailyRequestLimit = section.GetValue(nameof(AssistantSettings.DailyRequestLimit), assistant.DailyRequestLimit);
        return assistant;
    }

    private static WreckfestServerSettings Normalize(WreckfestServerSettings server)
    {
        server.ServerPath = server.ServerPath?.Trim() ?? string.Empty;
        server.ServerArguments = string.IsNullOrWhiteSpace(server.ServerArguments) ? DefaultServerArguments : server.ServerArguments.Trim();
        server.WorkingDirectory = server.WorkingDirectory?.Trim() ?? string.Empty;
        server.LogFilePath = server.LogFilePath?.Trim() ?? string.Empty;

        // Server I/O is hook-only; the old console and log-file modes are gone.
        server.OutputMode = ServerOutputModes.InjectedHook;
        return server;
    }

    private static SteamCmdSettings Normalize(SteamCmdSettings steamCmd)
    {
        steamCmd.SteamCmdPath = steamCmd.SteamCmdPath?.Trim() ?? string.Empty;
        steamCmd.WreckfestAppId = string.IsNullOrWhiteSpace(steamCmd.WreckfestAppId) ? DefaultWreckfestAppId : steamCmd.WreckfestAppId.Trim();
        return steamCmd;
    }

    /// <summary>The same ranges VotingService has always clamped to.</summary>
    private static VoteSettings Normalize(VoteSettings vote)
    {
        vote.Mode = VoteModes.Normalize(vote.Mode, vote.Enabled);
        vote.Enabled = vote.Mode != VoteModes.Off;
        vote.DirectCooldownSeconds = Math.Clamp(vote.DirectCooldownSeconds, 0, 3600);
        vote.VoteTimeoutSeconds = Math.Clamp(vote.VoteTimeoutSeconds, 1, 3600);
        vote.MaxLapsAllowed = Math.Max(1, vote.MaxLapsAllowed);
        vote.MessageDelayMs = Math.Clamp(vote.MessageDelayMs, 0, 5000);
        return vote;
    }

    private static PublicSiteSettings Normalize(PublicSiteSettings site)
    {
        site.FeedItemCount = Math.Clamp(site.FeedItemCount, 1, PublicSiteSettings.MaxFeedItemCount);
        return site;
    }

    private static AssistantSettings Normalize(AssistantSettings assistant)
    {
        assistant.BaseUrl = string.IsNullOrWhiteSpace(assistant.BaseUrl)
            ? AssistantSettings.DefaultBaseUrl
            : assistant.BaseUrl.Trim().TrimEnd('/');
        assistant.Model = string.IsNullOrWhiteSpace(assistant.Model) ? AssistantSettings.DefaultModel : assistant.Model.Trim();
        assistant.ApiKey = assistant.ApiKey?.Trim() ?? string.Empty;
        assistant.DailyRequestLimit = Math.Clamp(assistant.DailyRequestLimit, 1, AssistantSettings.MaxDailyRequestLimit);
        return assistant;
    }

    private sealed record Definition(string Name, Func<IConfiguration, object> Default, Func<object, object> Normalize);
}
