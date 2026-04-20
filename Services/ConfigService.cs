using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class ConfigService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigService> _logger;

    public ConfigService(IConfiguration configuration, ILogger<ConfigService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetConfigFilePath()
    {
        var serverArgs = _configuration["WreckfestServer:ServerArguments"] ?? "";
        var workingDir = _configuration["WreckfestServer:WorkingDirectory"];

        if (string.IsNullOrEmpty(workingDir))
        {
            throw new InvalidOperationException("WorkingDirectory not configured");
        }

        // Extract server_config file path from arguments
        var match = Regex.Match(serverArgs, @"server_config=([^\s]+)");
        if (!match.Success)
        {
            throw new InvalidOperationException("server_config not found in ServerArguments");
        }

        var configFileName = match.Groups[1].Value;
        var configFilePath = Path.IsPathRooted(configFileName)
            ? configFileName
            : Path.Combine(workingDir, configFileName);

        if (!File.Exists(configFilePath))
        {
            throw new FileNotFoundException($"Config file not found: {configFilePath}");
        }

        return configFilePath;
    }

    public virtual ServerConfig ReadBasicConfig()
    {
        var configPath = GetConfigFilePath();
        var lines = File.ReadAllLines(configPath);
        var config = new ServerConfig();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("el_"))
                continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();
            config.ApplyConfigValue(key, value);
        }

        return config;
    }

    public virtual List<EventLoopTrack> ReadEventLoopTracks()
    {
        var configPath = GetConfigFilePath();
        var lines = File.ReadAllLines(configPath);
        var tracks = new List<EventLoopTrack>();
        EventLoopTrack? currentTrack = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("##") || string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Remove leading # to parse commented event loop entries
            var uncommented = trimmed.TrimStart('#');
            var parts = uncommented.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            if (key == "el_add")
            {
                if (currentTrack != null)
                {
                    tracks.Add(currentTrack);
                }
                currentTrack = new EventLoopTrack { Track = value };
            }
            else if (currentTrack != null)
            {
                switch (key)
                {
                    case "el_gamemode": currentTrack.Gamemode = value; break;
                    case "el_laps": int.TryParse(value, out var laps); currentTrack.Laps = laps; break;
                    case "el_bots": int.TryParse(value, out var bots); currentTrack.Bots = bots; break;
                    case "el_num_teams": int.TryParse(value, out var numTeams); currentTrack.NumTeams = numTeams; break;
                    case "el_car_reset_disabled": int.TryParse(value, out var carResetDisabled); currentTrack.CarResetDisabled = carResetDisabled; break;
                    case "el_wrong_way_limiter_disabled": int.TryParse(value, out var wrongWayLimiterDisabled); currentTrack.WrongWayLimiterDisabled = wrongWayLimiterDisabled; break;
                    case "el_car_class_restriction": currentTrack.CarClassRestriction = value; break;
                    case "el_car_restriction": currentTrack.CarRestriction = value; break;
                    case "el_weather": currentTrack.Weather = value; break;
                }
            }
        }

        if (currentTrack != null)
        {
            tracks.Add(currentTrack);
        }

        return tracks;
    }

    public virtual void WriteBasicConfig(ServerConfig config)
    {
        var configPath = GetConfigFilePath();
        var lines = File.ReadAllLines(configPath);
        var newLines = new List<string>();
        bool inEventLoop = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Mark when we reach event loop section
            if (trimmed.StartsWith("# Event Loop"))
            {
                inEventLoop = true;
            }

            // Skip processing if we're in the event loop section
            if (inEventLoop)
            {
                newLines.Add(line);
                continue;
            }

            // If it's a comment or empty line, keep as-is
            if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed))
            {
                newLines.Add(line);
                continue;
            }

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2)
            {
                newLines.Add(line);
                continue;
            }

            var key = parts[0].Trim();
            var newValue = key switch
            {
                "server_name" => config.ServerName,
                "welcome_message" => config.WelcomeMessage,
                "password" => config.Password,
                "max_players" => config.MaxPlayers.ToString(),
                "lan" => config.Lan.ToString(),
                "steam_port" => config.SteamPort.ToString(),
                "game_port" => config.GamePort.ToString(),
                "query_port" => config.QueryPort.ToString(),
                "exclude_from_quickplay" => config.ExcludeFromQuickplay.ToString(),
                "clear_users" => config.ClearUsers.ToString(),
                "owner_disabled" => config.OwnerDisabled.ToString(),
                "admin_control" => config.AdminControl.ToString(),
                "lobby_countdown" => config.LobbyCountdown.ToString(),
                "ready_players_required" => config.ReadyPlayersRequired.ToString(),
                "admin_steam_ids" => config.AdminSteamIds,
                "op_steam_ids" => config.OpSteamIds,
                "session_mode" => config.SessionMode,
                "grid_order" => config.GridOrder,
                "enable_track_vote" => config.EnableTrackVote.ToString(),
                "disable_idle_kick" => config.DisableIdleKick.ToString(),
                "track" => config.Track,
                "gamemode" => config.Gamemode,
                "bots" => config.Bots.ToString(),
                "ai_difficulty" => config.AiDifficulty,
                "num_teams" => config.NumTeams.ToString(),
                "laps" => config.Laps.ToString(),
                "time_limit" => config.TimeLimit.ToString(),
                "elimination_interval" => config.EliminationInterval.ToString(),
                "vehicle_damage" => config.VehicleDamage,
                "car_class_restriction" => config.CarClassRestriction,
                "car_restriction" => config.CarRestriction,
                "special_vehicles_disabled" => config.SpecialVehiclesDisabled.ToString(),
                "car_reset_disabled" => config.CarResetDisabled.ToString(),
                "car_reset_delay" => config.CarResetDelay.ToString(),
                "wrong_way_limiter_disabled" => config.WrongWayLimiterDisabled.ToString(),
                "weather" => config.Weather,
                "frequency" => config.Frequency,
                "mods" => config.Mods,
                "log" => config.Log,
                _ => null
            };

            if (newValue != null)
            {
                newLines.Add($"{key}={newValue}");
            }
            else
            {
                newLines.Add(line);
            }
        }

        File.WriteAllLines(configPath, newLines);
        _logger.LogInformation("Basic config updated successfully");
    }

    public virtual void WriteEventLoopTracks(String collectionName, List<EventLoopTrack> tracks)
    {
        var configPath = GetConfigFilePath();
        var lines = File.ReadAllLines(configPath);
        var newLines = new List<string>();
        bool inEventLoop = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Mark when we reach event loop section
            if (trimmed.StartsWith("# Event Loop"))
            {
                inEventLoop = true;
            }

            // Copy lines until event loop section
            if (!inEventLoop)
            {
                newLines.Add(line);
                continue;
            }

            // When we reach event loop, write header comments until we hit "## Add" or el_
            if (inEventLoop)
            {
                // Write event loop header comment section
                while (i < lines.Length &&
                       lines[i].Trim().StartsWith("#") &&
                       !lines[i].Trim().StartsWith("## Add") &&
                       !lines[i].Trim().TrimStart('#').StartsWith("el_"))
                {
                    newLines.Add(lines[i]);
                    i++;
                }

                newLines.Add("");
                newLines.Add($"#CollectionName { collectionName }");

                // Write all event loop tracks
                for (int j = 0; j < tracks.Count; j++)
                {
                    var track = tracks[j];
                    newLines.Add("");
                    newLines.Add($"## Add event {j + 1} to Loop");
                    newLines.Add($"el_add={track.Track}");
                    if (track.Gamemode != null) newLines.Add($"el_gamemode={track.Gamemode}");
                    if (track.Laps.HasValue) newLines.Add($"el_laps={track.Laps}");
                    if (track.Bots.HasValue) newLines.Add($"el_bots={track.Bots}");
                    if (track.NumTeams.HasValue) newLines.Add($"el_num_teams={track.NumTeams}");
                    if (track.CarResetDisabled.HasValue) newLines.Add($"el_car_reset_disabled={track.CarResetDisabled}");
                    if (track.WrongWayLimiterDisabled.HasValue) newLines.Add($"el_wrong_way_limiter_disabled={track.WrongWayLimiterDisabled}");
                    if (track.CarClassRestriction != null) newLines.Add($"el_car_class_restriction={track.CarClassRestriction}");
                    if (track.CarRestriction != null) newLines.Add($"el_car_restriction={track.CarRestriction}");
                    if (track.Weather != null) newLines.Add($"el_weather={track.Weather}");
                }

                // Skip old event loop lines in the original file, then copy any content that follows
                for (; i < lines.Length; i++)
                {
                    var t = lines[i].Trim();
                    if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith("#") && !t.StartsWith("el_"))
                    {
                        for (int k = i; k < lines.Length; k++)
                            newLines.Add(lines[k]);
                        break;
                    }
                }

                break; // Stop processing, we've written the new event loop
            }
        }

        File.WriteAllLines(configPath, newLines);
        _logger.LogInformation("Event loop tracks updated successfully");
    }
    public virtual string GetCurrentCollectionName()
    {
        string collectionName = String.Empty;
        var configPath = GetConfigFilePath();
        var lines = File.ReadAllLines(configPath);
        var collectionLine = lines.FirstOrDefault(l => l.Trim().StartsWith("#CollectionName"));
        if (collectionLine != null)
        {
            collectionName = collectionLine.Trim().Substring("#CollectionName".Length).Trim();
        }
        return collectionName;
    }
}
