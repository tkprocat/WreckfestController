# WreckfestController - AI Development Guide

**Project Type:** ASP.NET Core 8.0 Web API + WPF Desktop Application
**Purpose:** REST API wrapper and desktop GUI for controlling Wreckfest Dedicated Server
**Last Updated:** January 2025

---

## Table of Contents

1. [Project Overview](#project-overview)
2. [Architecture](#architecture)
3. [Tech Stack](#tech-stack)
4. [Project Structure](#project-structure)
5. [Key Components](#key-components)
6. [API Endpoints](#api-endpoints)
7. [Real-time Features](#real-time-features)
8. [Configuration System](#configuration-system)
9. [Server Process Management](#server-process-management)
10. [Laravel Integration](#laravel-integration)
11. [Events System Implementation](#events-system-implementation)
12. [Development Workflows](#development-workflows)
13. [Testing](#testing)
14. [Troubleshooting](#troubleshooting)
15. [Cross-Project Documentation](#cross-project-documentation)

---

## Project Overview

**WreckfestController** is a dual-mode .NET 10.0 application that provides:

### Desktop GUI Mode (WPF)
- **Material Design UI** - Professional dark theme using MaterialDesignInXamlToolkit 5.3.0
- **Custom Dark Titlebar** - Frameless window with custom titlebar (no white Windows titlebar), wrench icon, min/max/close buttons
- **Process Manager Tab** - Scan and attach to running Wreckfest servers (view PID, config file, uptime, memory)
  - Process list with auto-refresh every 5 seconds
  - Refresh, Attach, Kill Process, Start New Server buttons
  - Color-coded buttons (Green=Start, Red=Kill, Light Blue=Attach)
- **Server Control Tab** - Complete server management interface
  - Server status panel (running/stopped, players, track, uptime)
  - Start/Stop/Restart buttons (Green/Red/Orange)
  - Real-time console output mirror (left panel)
  - Event log with timestamps (right panel)
  - Command input to send commands to server
- **Configuration Tab** - User-friendly settings management
  - Server settings (executable path, working directory, arguments)
  - Network settings (WreckfestWeb webhook URL, API ports)
  - Browse buttons for file/folder selection
  - Saves to user-settings.json (customizable location)
  - Reset to defaults functionality
- **Event Scheduler Tab** - View and manage scheduled events
  - Upcoming events list (auto-refreshes every 30 seconds)
  - Event details panel (name, start time, tracks, recurring pattern)
  - Manual event activation ("Activate Now" button)
  - Triggers smart restart process with countdown warnings
  - Read-only view (schedule management done via WreckfestWeb)
- **Modular Architecture** - Tabs implemented as separate UserControls for maintainability
  - `Views/ProcessManagerTab.xaml` - Process management tab
  - `Views/ServerControlTab.xaml` - Server control and monitoring tab
  - `Views/ConfigurationTab.xaml` - Settings configuration tab
  - `Views/EventSchedulerTab.xaml` - Event scheduler and activation tab
- **Window Title** - Dynamic title shows PID and config file when attached to a server

### REST API Mode (ASP.NET Core)
- REST API for server control (start/stop/restart/update)
- Server configuration management (server_config.cfg)
- Real-time player tracking via console output and log parsing
- Track rotation management (event loops)
- WebSocket streaming for console output, player events, and track changes
- Integration with Laravel WreckfestWeb admin panel
- SteamCmd integration for server updates

### How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│                    System Architecture                          │
└─────────────────────────────────────────────────────────────────┘

┌──────────────────┐         ┌──────────────────┐         ┌──────────────────┐
│  Laravel         │         │  C#              │         │  Wreckfest       │
│  WreckfestWeb    │◄───────►│  Controller      │◄───────►│  Dedicated Server│
│  (Admin Panel)   │  REST   │  (This Project)  │  Process│  (Game Server)   │
└──────────────────┘   API    └──────────────────┘  Control └──────────────────┘
         │                            │                           │
         │                            │                           │
    Admin sends              Manages server               Writes to
    config/track             process, monitors             log.txt,
    rotation updates         logs, tracks players          runs events
```

**Data Flow:**
1. **Laravel → C# Controller**: Admin creates/updates config or track rotation
2. **C# Controller → Wreckfest Server**: Updates server_config.cfg, restarts server
3. **Wreckfest Server → C# Controller**: Writes logs, C# monitors and parses
4. **C# Controller → Laravel**: Webhooks notify of player events, track changes
5. **C# Controller ↔ Clients**: WebSockets for real-time updates

---

## Architecture

### Integration Points

**C# WreckfestController** sits between Laravel WreckfestWeb and the Wreckfest Dedicated Server:

1. **Laravel WreckfestWeb (F:\Projects\Web\WreckfestWeb)**
   - Provides admin UI (Filament) for server management
   - Calls C# API to control server and update config
   - Receives webhooks from C# when events occur
   - See: `F:\Projects\Web\WreckfestWeb\CLAUDE_GUIDE.md`

2. **C# WreckfestController (This Project)**
   - Exposes REST API for server control
   - Manages Wreckfest server process (start/stop/restart)
   - Monitors server console output and logs for player tracking
   - Sends webhooks to Laravel
   - Implements the Events System

3. **Wreckfest Dedicated Server**
   - Game server process (Wreckfest_x64.exe)
   - Reads server_config.cfg for configuration
   - Writes log.txt with events (player join/leave, track changes)
   - Outputs player data to console

---

## Tech Stack

### Framework & Language
- **.NET 10.0** (`net10.0-windows`) - Target framework
- **ASP.NET Core 10.0** - Web API framework
- **C# 14** - Language version

### Key NuGet Packages
- **Swashbuckle.AspNetCore** (6.5.0) - Swagger/OpenAPI documentation
- **System.Management** (8.0.0) - Windows process management

### Development Tools
- **Visual Studio 2022** (recommended) - Full IDE with debugging
- **Visual Studio Code** - Lightweight alternative
- **xUnit** - Testing framework (WreckfestController.Tests)
- **Swagger UI** - API testing and documentation

### Configuration
- **appsettings.json** - Server paths, ports, webhook settings
- **launchSettings.json** - Development profiles (http/https)

---

## Project Structure

```
WreckfestController/
├── Controllers/              # API endpoints
│   ├── ServerController.cs        # Server control (start/stop/restart)
│   ├── ConfigController.cs        # Configuration management
│   └── EventsController.cs        # Events system endpoints
├── Services/                # Business logic
│   ├── ServerManager.cs           # Server process management
│   ├── ConfigService.cs           # server_config.cfg parsing/writing
│   ├── PlayerTracker.cs           # Log-based player tracking
│   ├── TrackChangeTracker.cs      # Track change detection
│   ├── LaravelWebhookService.cs   # Webhooks to Laravel
│   ├── ConsoleMonitor.cs          # Monitor server console output
│   ├── EventStorageService.cs     # SQLite event storage
│   ├── SmartRestartService.cs     # Automatic server restarts
│   └── ApiServer.cs               # ASP.NET Core server hosting
├── Models/                  # Data models
│   ├── ServerConfig.cs            # server_config.cfg model
│   ├── EventLoopTrack.cs          # Track rotation entry
│   ├── Player.cs                  # Player tracking model
│   ├── PlayerListResponse.cs      # API response model
│   ├── ServerProcessInfo.cs       # Process information for UI
│   └── UpdateEventLoopTracksRequest.cs  # Track update request
├── Views/                   # WPF UserControls (UI components)
│   ├── ProcessManagerTab.xaml/.cs # Process list and management
│   ├── ServerControlTab.xaml/.cs  # Server control and monitoring
│   └── ConfigurationTab.xaml/.cs  # Settings configuration
├── WebSockets/              # Real-time streaming
│   ├── ConsoleWebSocketHandler.cs     # Stream console output
│   ├── PlayerTrackerWebSocketHandler.cs  # Stream player events
│   └── TrackChangeWebSocketHandler.cs    # Stream track changes
├── MainWindow.xaml/.cs      # WPF main window with custom titlebar
├── App.xaml/.cs             # WPF application entry & Material Design theme
├── Program.cs               # Application entry point (dual-mode: WPF/API)
├── GlobalUsings.cs          # Global using directives
├── appsettings.json         # Configuration
├── appsettings.example.json # Configuration template
├── WreckfestController.Tests/  # Unit tests (51+ tests)
│   ├── Controllers/
│   └── Services/
├── README.md                # Main documentation
├── INSTALL.md               # Installation and deployment guide
├── MATERIALDESIGN.md        # Material Design UI guidelines
└── CLAUDE_GUIDE.md          # This file (AI development guide)
```

---

## Key Components

### WPF Desktop UI (Views/)

The WPF desktop application uses a modular architecture with UserControls for better maintainability:

#### **MainWindow.xaml/.cs**
The main application window with custom dark titlebar:
- **Custom Titlebar**: Frameless window (`WindowStyle="None"`) with custom-drawn titlebar
  - Material Design wrench icon (white)
  - Window title (shows PID and config when attached)
  - Min/Max/Close buttons with Material Design flat button style
  - Draggable titlebar (click to drag, double-click to maximize)
- **Tab Container**: Hosts Process Manager and Server Control tabs as UserControls
- **Timers**:
  - Status update timer (1 second) - updates server status panel
  - Process list refresh timer (5 seconds) - refreshes process list
- **Icon Generation**: Programmatically generates taskbar icon from Material Design PackIcon

#### **Views/ProcessManagerTab.xaml/.cs**
Process management and server discovery (displayed as "Process Manager" tab):
- **DataGrid**: Lists all running Wreckfest server processes
  - Columns: PID, Config File, Uptime, Memory (MB), Status
  - Blue selection highlight (#2C5282)
  - Auto-refresh every 5 seconds
- **Buttons**:
  - REFRESH - Manual refresh of process list
  - ATTACH (Light Blue) - Attach to selected process for monitoring
  - KILL PROCESS (Red) - Terminate selected process
  - START NEW SERVER (Green) - Launch new server instance
- **Code-behind**: Handles process enumeration, attach/kill operations

#### **Views/ServerControlTab.xaml/.cs**
Complete server control and monitoring interface:
- **Server Status Panel** (top):
  - Server Status: Running/Stopped (Green/Red)
  - Players: Current / Max (e.g., "12 / 24")
  - Current Track: Track name or "None"
  - Uptime: HH:MM:SS format
  - Control buttons: START (Green), STOP (Red), RESTART (Orange)
- **Console Output** (left panel):
  - Real-time mirror of server console
  - Monospace font (Consolas/Courier New)
  - Auto-scroll to bottom
  - Max 1000 lines (auto-trimmed)
- **Event Log** (right panel):
  - Timestamped events (HH:MM:SS)
  - Color-coded messages:
    - Green (#51CF66) - Player joined, server started
    - Red (#FF6B6B) - Player left, errors
    - Yellow (#FFD43B) - Player kicked, warnings
    - Light Blue (#74C0FC) - Track changes
  - Clear log button
  - Max 100 items (auto-trimmed)
- **Command Input** (bottom):
  - Text input for server commands
  - Send button (Green)
  - Enter key to submit
- **Code-behind**: Manages subscriptions to ServerManager events, updates UI in real-time

#### **App.xaml**
Application-wide resources and Material Design theme:
```xml
<materialDesign:BundledTheme BaseTheme="Dark"
                           PrimaryColor="Blue"
                           SecondaryColor="Cyan" />
```
- **Color Palette**:
  - `BackgroundDark` (#1a1a1a) - Main background
  - `BackgroundMedium` (#2d2d2d) - Panels
  - `TextPrimary` (#e0e0e0) - Main text
  - `TextSecondary` (#a0a0a0) - Labels
  - `BorderColor` (#444) - Borders
  - `ButtonGreen` (#238636) - Success actions
  - `ButtonRed` (#DC3545) - Destructive actions
  - `ButtonOrange` (#FD7E14) - Warning actions
  - `ButtonLightBlue` (#74C0FC) - Info actions

### Controllers

#### **ServerController.cs**
REST API endpoints for server control:

```csharp
[ApiController]
[Route("api/[controller]")]
public class ServerController : ControllerBase
{
    // GET /api/server/status - Server status (running, PID, uptime)
    // POST /api/server/start - Start server
    // POST /api/server/stop - Stop server
    // POST /api/server/restart - Restart server
    // POST /api/server/update - Update via SteamCmd
    // POST /api/server/command - Send console command
    // POST /api/server/attach/{pid} - Attach to existing process
    // GET /api/server/logfile?lines=100 - Get log file content
    // GET /api/server/players - Get player list
}
```

**Key Features:**
- PID-based server tracking (only tracks servers started via API)
- Log file monitoring with FileSystemWatcher + polling
- Player tracking integration
- Console command injection via Windows messages

#### **ConfigController.cs**
REST API endpoints for configuration:

```csharp
[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    // GET /api/config/basic - Get all basic config
    // PUT /api/config/basic - Update basic config
    // GET /api/config/tracks - Get event loop tracks
    // PUT /api/config/tracks - Update event loop tracks
    // GET /api/config/tracks/collection-name - Get collection name
}
```

**Key Features:**
- Parses server_config.cfg (INI-style format)
- Preserves comments and structure
- Event loop section management (el_add, el_gamemode, etc.)
- Collection name tracking (#CollectionName comment)

---

### Services

#### **ServerManager.cs** (Core Service)
Manages Wreckfest server process lifecycle:

```csharp
public class ServerManager
{
    // Server control
    Task<(bool Success, string Message)> StartServerAsync()
    Task<(bool Success, string Message)> StopServerAsync()
    Task<(bool Success, string Message)> RestartServerAsync()
    Task<(bool Success, string Message)> UpdateServerAsync()  // Via SteamCmd
    Task<(bool Success, string Message)> SendCommandAsync(string command)

    // Process management
    (bool Success, string Message) AttachToExistingProcess(int pid)
    ServerStatus GetStatus()

    // Log monitoring
    (bool Success, string Message, string? LogFilePath, List<string>? Lines) GetLogFileContent(int lines)

    // Player tracking
    PlayerListResponse GetPlayerList()

    // WebSocket subscription
    void SubscribeToConsoleOutput(Action<string> callback)
}
```

**Key Implementation Details:**
- **PID Tracking**: Only tracks servers started via API or manually attached
- **Log Monitoring**: FileSystemWatcher + 2-second polling fallback
- **Player Tracking**: Integrates PlayerTracker with console output monitoring
- **Track Detection**: Parses "Current track loaded! (track_name)" from logs

**Log File Resolution:**
1. Tries to parse `log=` setting from server_config.cfg
2. Falls back to `appsettings.json` WreckfestServer:LogFilePath

#### **ConfigService.cs**
Manages server_config.cfg parsing and writing:

```csharp
public class ConfigService
{
    // Read/write basic config
    ServerConfig ReadBasicConfig()
    void WriteBasicConfig(ServerConfig config)

    // Read/write event loop tracks
    List<EventLoopTrack> ReadEventLoopTracks()
    void WriteEventLoopTracks(string collectionName, List<EventLoopTrack> tracks)

    // Get collection name
    string GetCurrentCollectionName()
}
```

**Key Implementation Details:**
- **Config File Path**: Extracted from `WreckfestServer:ServerArguments` (e.g., `server_config=server_config.cfg`)
- **Parsing**: Line-by-line parsing of INI-style config
- **Event Loop Section**: Detected by `# Event Loop` comment
- **Preserves Structure**: Keeps comments, order, unknown settings
- **Collection Name**: Stored as `#CollectionName <name>` comment

#### **PlayerTracker.cs**
Log-based player tracking:

```csharp
public class PlayerTracker
{
    void ProcessLogLine(string line)  // Parse join/leave events
    List<Player> GetOnlinePlayers()
    (int OnlineCount, int TotalCount) GetPlayerCount()
    void Clear()  // Clear all tracked players
}
```

**Parses Log Patterns:**
- `"PlayerName has joined."` → Player joined
- `"PlayerName has quit"` → Player left
- `"*BotName has joined."` → Bot joined (prefix `*`)

#### **TrackChangeTracker.cs**
Detects track changes from logs:

```csharp
public class TrackChangeTracker
{
    void ProcessLogLine(string line)  // Detect "Current track loaded!"
    string GetCurrentTrack()
    void SubscribeToTrackChanges(Action<string> callback)
}
```

**Webhook Integration:** Sends webhook to Laravel when track changes.

#### **LaravelWebhookService.cs**
Sends webhooks to Laravel:

```csharp
public class LaravelWebhookService
{
    Task SendPlayerJoinedAsync(string playerName)
    Task SendPlayerLeftAsync(string playerName)
    Task SendTrackChangedAsync(string trackName)
}
```

**Configuration:** `appsettings.json` → `Laravel:WebhookBaseUrl`

---

### Models

#### **ServerConfig.cs**
Complete server_config.cfg model (57 properties):

```csharp
public class ServerConfig
{
    // Basic server info
    public string ServerName { get; set; }
    public string WelcomeMessage { get; set; }
    public string Password { get; set; }
    public int MaxPlayers { get; set; } = 24;

    // Network
    public int SteamPort { get; set; } = 27015;
    public int GamePort { get; set; } = 33540;
    public int QueryPort { get; set; } = 27016;

    // Game settings
    public string Track { get; set; }
    public string Gamemode { get; set; }
    public int Bots { get; set; }
    public string AiDifficulty { get; set; } = "expert";
    public int Laps { get; set; } = 3;

    // ... 40+ more properties
}
```

#### **EventLoopTrack.cs**
Track rotation entry model:

```csharp
public class EventLoopTrack
{
    public string Track { get; set; }          // Required: track/location/variant
    public string? Gamemode { get; set; }      // Optional: racing, derby
    public int? Laps { get; set; }
    public int? Bots { get; set; }
    public int? NumTeams { get; set; }
    public int? CarResetDisabled { get; set; }
    public int? WrongWayLimiterDisabled { get; set; }
    public string? CarClassRestriction { get; set; }
    public string? CarRestriction { get; set; }
    public string? Weather { get; set; }
}
```

**server_config.cfg Format:**
```ini
## Add event 1 to Loop
el_add=track/wild_valley/valley_edge_short
el_gamemode=racing
el_laps=3
el_bots=8
el_weather=clear
```

---

## API Endpoints

### Server Control

**GET /api/server/status**
```json
{
  "isRunning": true,
  "processId": 12345,
  "uptime": "01:23:45",
  "currentTrack": "track/wild_valley/valley_edge_short"
}
```

**POST /api/server/start**
- Starts Wreckfest server using configured path/arguments
- Monitors log file for real-time output
- Returns: `{ "message": "Server started successfully. Process: Wreckfest_x64 (PID: 12345)" }`

**POST /api/server/stop**
- Kills server process (entire process tree)
- Stops log monitoring
- Clears player tracking
- Returns: `{ "message": "Server stopped successfully" }`

**POST /api/server/restart**
- Stops server, waits 2 seconds, starts server
- Returns: Same as start response

**POST /api/server/update**
- Stops server
- Runs SteamCmd to update (via `SteamCmd:SteamCmdPath` config)
- Starts server
- Returns: `{ "message": "Server updated and restarted successfully" }`

**POST /api/server/command**
```json
{
  "command": "help"
}
```
- Sends command to server console via Windows messages
- Uses ConsoleWriter service

**POST /api/server/attach/{pid}**
- Attaches to already-running Wreckfest process
- Useful for tracking servers started outside the API

**GET /api/server/logfile?lines=100**
- Returns last N lines from server log file
- Uses FileShare.ReadWrite to read while server is writing

**GET /api/server/players**
```json
{
  "totalPlayers": 3,
  "maxPlayers": 24,
  "players": [
    {
      "name": "PlayerOne",
      "isBot": false,
      "isOnline": true,
      "slot": 0,
      "joinedAt": "2025-01-05T14:30:00Z",
      "lastSeenAt": "2025-01-05T14:35:00Z"
    },
    {
      "name": "Ultimate Night of Super Se",
      "isBot": true,
      "isOnline": true,
      "slot": 1,
      "joinedAt": "2025-01-05T14:31:00Z",
      "lastSeenAt": "2025-01-05T14:35:00Z"
    }
  ],
  "lastUpdated": "2025-01-05T14:35:00Z"
}
```

---

### Configuration Management

**GET /api/config/basic**
- Returns complete ServerConfig object (all 57 properties)

**PUT /api/config/basic**
```json
{
  "serverName": "My Wreckfest Server",
  "welcomeMessage": "Welcome!",
  "maxPlayers": 24,
  "bots": 8,
  "laps": 5
  // ... all properties
}
```
- Updates server_config.cfg basic settings
- Preserves event loop section

**GET /api/config/tracks**
```json
{
  "count": 3,
  "tracks": [
    {
      "track": "track/wild_valley/valley_edge_short",
      "gamemode": "racing",
      "laps": 3,
      "bots": 8,
      "weather": "clear"
    }
    // ... more tracks
  ]
}
```

**PUT /api/config/tracks**
```json
{
  "collectionName": "Weekend Rotation",
  "tracks": [
    {
      "track": "track/wild_valley/valley_edge_short",
      "gamemode": "racing",
      "laps": 3,
      "bots": 8
    }
  ]
}
```
- Replaces entire event loop section
- Stores collection name as `#CollectionName Weekend Rotation`

**GET /api/config/tracks/collection-name**
```json
{
  "collectionName": "Weekend Rotation"
}
```

---

## Real-time Features

### WebSocket Endpoints

**WS /ws/console**
- Streams real-time server console output
- Connects to ServerManager console subscribers

**JavaScript Example:**
```javascript
const ws = new WebSocket('ws://localhost:5100/ws/console');

ws.onmessage = (event) => {
  console.log('Server output:', event.data);
};
```

**WS /ws/players**
- Streams player join/leave events
- Connects to PlayerTracker events

**WS /ws/track-changes**
- Streams track change events
- Connects to TrackChangeTracker events

---

### Player Tracking

**Two Methods:**

1. **Log-based (Default)**: PlayerTracker.cs
   - Parses server log for join/leave messages
   - Reliable, no dependencies
   - Handles multi-word names and bots (prefixed with `*`)

**Log Patterns:**
```
[INFO] PlayerName has joined.
[INFO] *BotName has joined.
[INFO] PlayerName has quit
[INFO] Current track loaded! (track/wild_valley/valley_edge_short)
[INFO] Event started!
```

---

## Configuration System

WreckfestController uses a **hybrid configuration system** with two layers:

### Configuration Files

**1. appsettings.json** (Application Defaults - Ships with app)
- Read-only defaults that ship with the application
- Provides fallback values if user settings don't exist
- Safe to overwrite on application updates

**2. user-settings.json** (User Overrides - Customizable Location)
- User-editable settings that override appsettings.json
- Default location: `%LocalAppData%\WreckfestController\user-settings.json`
- Can be customized via `UserSettingsPath` in appsettings.json
- Managed through Configuration tab UI

### Configuration Loading Priority

```
1. appsettings.json (defaults)
2. user-settings.json (overrides)
3. Environment variables (highest priority)
```

### appsettings.json Structure

```json
{
  "UserSettingsPath": "",  // Empty = %LocalAppData%\WreckfestController\user-settings.json
                           // Or specify custom path for multi-instance setups

  "WreckfestServer": {
    "ServerPath": "",
    "ServerArguments": "-s server_config=server_config.cfg",
    "WorkingDirectory": "",
    "LogFilePath": ""
  },
  "SteamCmd": {
    "SteamCmdPath": "C:\\Path\\To\\steamcmd.exe",
    "WreckfestAppId": "361580",
    "InstallDirectory": "C:\\Path\\To\\Wreckfest Dedicated Server"
  },
  "Laravel": {
    "WebhookBaseUrl": "http://localhost:8000/api/webhooks"
  },
  "Kestrel": {
    "Urls": "http://0.0.0.0:5100;https://0.0.0.0:5101"
  },
  "UseKestrel": false
}
```

### user-settings.json Structure

Created automatically when saving settings via Configuration tab:

```json
{
  "WreckfestServer": {
    "ServerPath": "C:\\Games\\Wreckfest\\Wreckfest_x64.exe",
    "WorkingDirectory": "C:\\Games\\Wreckfest",
    "ServerArguments": "-s server_config=server_config.cfg",
    "LogFilePath": "C:\\Games\\Wreckfest\\log.txt"
  },
  "Laravel": {
    "WebhookBaseUrl": "https://wreckfestweb.test/api/webhooks"
  },
  "Kestrel": {
    "Urls": "http://0.0.0.0:5100;https://0.0.0.0:5101"
  }
}
```

### Multi-Instance Configuration

To run multiple WreckfestController instances on the same machine:

**Instance 1** (`C:\WreckfestController-Server1\appsettings.json`):
```json
{
  "UserSettingsPath": "C:\\WreckfestController-Server1\\user-settings.json",
  "Kestrel": {
    "Urls": "http://0.0.0.0:5100;https://0.0.0.0:5101"
  }
}
```

**Instance 2** (`C:\WreckfestController-Server2\appsettings.json`):
```json
{
  "UserSettingsPath": "C:\\WreckfestController-Server2\\user-settings.json",
  "Kestrel": {
    "Urls": "http://0.0.0.0:5200;https://0.0.0.0:5201"
  }
}
```

Each instance will maintain its own separate configuration.

### Configuration Tab UI

The Configuration tab provides a user-friendly interface for managing settings:

**Features:**
- **Settings Location Display** - Shows where user-settings.json is stored
- **Server Settings Section**:
  - Server executable path (with browse button)
  - Working directory (with browse button)
  - Server arguments
  - Log file path (optional, with browse button)
- **Network Settings Section**:
  - WreckfestWeb webhook base URL
  - API server URLs (Kestrel)
- **Action Buttons**:
  - Reset to Defaults
  - Save Settings (requires restart to apply)
- **Validation**: Warns if paths don't exist before saving

**Using the Configuration Tab:**
1. Open the Configuration tab
2. Click Browse buttons to select paths
3. Modify settings as needed
4. Click "Save Settings"
5. Restart application for changes to take effect

### SettingsService

**Services/SettingsService.cs** manages user settings:

```csharp
public class SettingsService
{
    string GetUserSettingsPath()              // Get resolved settings path
    UserSettings LoadSettings()               // Load settings from file
    void SaveSettings(UserSettings settings)  // Save settings to file
}
```

**Key Features:**
- Path resolution with environment variable expansion
- Automatic directory creation
- JSON serialization/deserialization
- Error handling and logging

**Important Settings:**
- **ServerPath**: Direct path to Wreckfest_x64.exe
- **ServerArguments**: Must include `server_config=<filename>`
- **WorkingDirectory**: Server installation folder
- **LogFilePath**: (Optional) Override log file path; otherwise parsed from server_config.cfg
- **Kestrel:Urls**: API server listening addresses (change ports for multi-instance)

---

### server_config.cfg Format

**INI-style configuration file:**

```ini
# Basic Server Info
server_name=My Wreckfest Server
welcome_message=Welcome!
password=
max_players=24

# Network Settings
lan=0
steam_port=27015
game_port=33540
query_port=27016

# Game Settings
session_mode=normal
bots=8
ai_difficulty=expert
laps=3
track=track/wild_valley/valley_edge_short
gamemode=racing

# Event Loop
#CollectionName Default Rotation

## Add event 1 to Loop
el_add=track/wild_valley/valley_edge_short
el_gamemode=racing
el_laps=3
el_bots=8
el_weather=clear

## Add event 2 to Loop
el_add=track/hilltop_stadium/oval
el_gamemode=derby
el_bots=10
```

**Key Details:**
- Comments start with `#`
- Settings: `key=value`
- Event loop section starts after `# Event Loop` comment
- Collection name stored as `#CollectionName <name>`
- Each track entry starts with `el_add=<track>`
- Followed by optional `el_*` settings

---

## Server Process Management

### Start/Stop Flow

**StartServerAsync():**
1. Check if already running → return error if yes
2. Validate server path exists
3. Parse config file path from ServerArguments
4. Create Process with:
   - FileName: ServerPath
   - Arguments: ServerArguments
   - WorkingDirectory: WorkingDirectory
   - UseShellExecute: false
   - CreateNoWindow: true
5. Start process
6. Track PID: `_actualServerPid = process.Id`
7. Start log file monitoring (FileSystemWatcher + polling)
8. Wait 500ms to check for immediate exit
9. Return success with PID

**StopServerAsync():**
1. Check if running → return error if not
2. Kill process tree: `process.Kill(entireProcessTree: true)`
3. Wait for exit (10 second timeout)
4. Clean up launcher process if needed
5. Stop log monitoring
6. Clear player tracking
7. Reset state: `_serverProcess = null`, `_actualServerPid = null`

**RestartServerAsync():**
1. Stop server
2. Wait 2 seconds
3. Start server

---

### Log File Monitoring

**Dual Approach:**
1. **FileSystemWatcher**: Monitors log file for changes (primary)
2. **Polling Timer**: Reads log every 2 seconds (fallback)

**Why Both?**
- FileSystemWatcher can miss rapid writes
- Polling ensures we don't miss events

**Implementation:**
```csharp
private void StartLogFileMonitoring()
{
    // Get log path from server_config.cfg or appsettings.json
    var logFilePath = GetLogFilePathFromConfig() ?? _configuration["WreckfestServer:LogFilePath"];

    // Track file position to only read new lines
    _lastLogFilePosition = new FileInfo(logFilePath).Length;

    // FileSystemWatcher
    _logFileWatcher = new FileSystemWatcher(directory, fileName);
    _logFileWatcher.Changed += OnLogFileChanged;  // Debounced to 100ms
    _logFileWatcher.EnableRaisingEvents = true;

    // Polling timer (every 2 seconds)
    _pollingTimer = new Timer(_ => ReadNewLogLines(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
}

private void ReadNewLogLines()
{
    using var fileStream = new FileStream(_currentLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fileStream.Seek(_lastLogFilePosition, SeekOrigin.Begin);

    using var reader = new StreamReader(fileStream);
    string? line;
    while ((line = reader.ReadLine()) != null)
    {
        // Process line
        NotifyConsoleOutputSubscribers(line);  // WebSocket subscribers
        _playerTracker.ProcessLogLine(line);   // Player tracking
        _trackChangeTracker.ProcessLogLine(line);  // Track changes
    }

    _lastLogFilePosition = fileStream.Position;
}
```

---

### PID Tracking

**Why PID Tracking?**
- Multiple Wreckfest instances may be running
- Need to control specific server instance
- Avoid interfering with other servers

**How It Works:**
- When server starts via API: `_actualServerPid = process.Id`
- Status checks: `Process.GetProcessById(_actualServerPid)`
- Manual attachment: `POST /api/server/attach/{pid}`

---

## Laravel Integration

### API Calls (Laravel → C#)

**Laravel WreckfestWeb** calls C# API for:
- Server control (start/stop/restart)
- Configuration updates (PUT /api/config/basic)
- Track rotation updates (PUT /api/config/tracks)
- Player list retrieval (GET /api/server/players)
- Log file viewing (GET /api/server/logfile)

**Implementation:**
- Laravel: `app/Services/WreckfestApiClient.php`
- Calls: `https://localhost:5101/api/*` (configurable via WRECKFEST_API_URL)

---

### Webhooks (C# → WreckfestWeb)

**C# sends webhooks to WreckfestWeb for:**

**Player Events:**
- Player joined
- Player left
- Players updated (current player list)

**Track Events:**
- Track changed

**Event System:**
- Event activated (when scheduled event triggers)

**Server Lifecycle Events (NEW):**
- Server started (PID, process name, start time)
- Server stopped (PID, stop method: graceful/force)
- Server restarted (old PID, new PID, restart method: command/full)
- Server attached (when GUI attaches to running process)
- Server restart pending (countdown warnings with minutes remaining)

**Implementation:**
```csharp
public class WreckfestWebWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookBaseUrl;  // From appsettings.json

    // Player Events
    public async Task SendPlayersUpdatedAsync(List<Player> players)
    public async Task SendPlayerJoinedAsync(string playerName, bool isBot)  // Obsolete
    public async Task SendPlayerLeftAsync(string playerName)  // Obsolete

    // Track Events
    public async Task SendTrackChangedAsync(string trackId)

    // Event System
    public async Task SendEventActivatedAsync(int eventId, string eventName)

    // Server Lifecycle Events
    public async Task SendServerStartedAsync(ServerStartedEvent serverEvent)
    public async Task SendServerStoppedAsync(ServerStoppedEvent serverEvent)
    public async Task SendServerRestartedAsync(ServerRestartedEvent serverEvent)
    public async Task SendServerAttachedAsync(ServerAttachedEvent serverEvent)
    public async Task SendServerRestartPendingAsync(ServerRestartPendingEvent serverEvent)
}
```

**Event Models:**
```csharp
// Models/ServerEvents.cs
public class ServerStartedEvent
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; }
    public DateTime StartTime { get; set; }
}

public class ServerStoppedEvent
{
    public int ProcessId { get; set; }
    public string StopMethod { get; set; }  // "Graceful" or "Force"
}

public class ServerRestartedEvent
{
    public int? OldProcessId { get; set; }
    public int NewProcessId { get; set; }
    public string RestartMethod { get; set; }  // "Command" or "Full"
}

public class ServerAttachedEvent
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; }
    public DateTime StartTime { get; set; }
}

public class ServerRestartPendingEvent
{
    public int MinutesRemaining { get; set; }
    public string? EventName { get; set; }
    public int? EventId { get; set; }
    public DateTime? ScheduledRestartTime { get; set; }
}
```

**WreckfestWeb receives webhooks at:**

Player & Game State:
- `POST /api/webhooks/players-updated`
- `POST /api/webhooks/track-changed`
- `POST /api/webhooks/event-activated`
- `POST /api/webhooks/player-joined` (legacy)
- `POST /api/webhooks/player-left` (legacy)

Server Lifecycle:
- `POST /api/webhooks/server-started`
- `POST /api/webhooks/server-stopped`
- `POST /api/webhooks/server-restarted`
- `POST /api/webhooks/server-attached`
- `POST /api/webhooks/server-restart-pending`

**Webhook Payload Examples:**

**Server Started:**
```json
{
  "processId": 12345,
  "processName": "Wreckfest_x64",
  "startTime": "2025-01-15T20:30:00Z",
  "timestamp": "2025-01-15T20:30:02Z"
}
```

**Server Stopped:**
```json
{
  "processId": 12345,
  "stopMethod": "Graceful",
  "timestamp": "2025-01-15T21:00:00Z"
}
```

**Server Restarted:**
```json
{
  "oldProcessId": 12345,
  "newProcessId": 12567,
  "restartMethod": "Command",
  "timestamp": "2025-01-15T21:05:00Z"
}
```

**Server Restart Pending:**
```json
{
  "minutesRemaining": 3,
  "eventName": "Weekend Derby Event",
  "eventId": 42,
  "scheduledRestartTime": "2025-01-15T21:10:00Z",
  "timestamp": "2025-01-15T21:07:00Z"
}
```

**Players Updated:**
```json
{
  "players": [
    {
      "name": "PlayerName",
      "playerId": 1,
      "score": 150,
      "vehicle": "Speedbird GT",
      "slot": 0,
      "isBot": false,
      "joinedAt": "2025-01-15T20:30:00Z",
      "lastSeenAt": "2025-01-15T21:00:00Z"
    },
    {
      "name": "*Bot_01",
      "playerId": 2,
      "score": 80,
      "vehicle": "Killerbee",
      "slot": 1,
      "isBot": true,
      "joinedAt": "2025-01-15T20:35:00Z",
      "lastSeenAt": "2025-01-15T21:00:00Z"
    }
  ]
}
```

**Track Changed:**
```json
{
  "trackId": "sandpit_derby_2"
}
```

**Event Activated:**
```json
{
  "eventId": 42,
  "eventName": "Weekend Derby Event",
  "timestamp": "2025-01-15T21:10:00Z"
}
```

**WreckfestWeb Implementation:**
- `app/Http/Controllers/WebhookController.php`
- Broadcasts events to frontend via Laravel Reverb
- Stores server lifecycle history in database

---

## Events System Implementation

**Status:** ✅ Fully Implemented (January 2025)

### Overview

The Events System allows Laravel to schedule server configurations (name, welcome message, track rotations) that the C# controller **autonomously activates** at the scheduled time with intelligent restart logic that minimizes player disruption.

**How It Works:**
```
Laravel Admin Panel → Creates Event → POST /api/Events/schedule → C# Stores in JSON
                                                                        ↓
C# EventSchedulerService (Background Timer - 30s intervals)
                ↓
        Checks for due events
                ↓
  Event start_time <= now? → YES → SmartRestartService
                                            ↓
                            5-Minute Countdown (In-Game Messages)
                                            ↓
                         Waits for Lobby (Track Change Detected)
                                            ↓
                                    Server Restart
                                            ↓
                            Apply Event Configuration
                                            ↓
                        Webhook Laravel (Event Activated)
                                            ↓
                        Recurring? → Reschedule Next Instance
```

### Architecture

**Components:**
- **EventSchedulerService** (IHostedService) - Background timer that checks for due events every 30 seconds
- **SmartRestartService** - Handles graceful restarts with player warnings
- **EventStorageService** - Persists schedule to `Data/event-schedule.json`
- **RecurringEventService** - Calculates next instances for daily/weekly patterns
- **EventsController** - 7 REST API endpoints for schedule management
- **LaravelWebhookService** - Extended to notify Laravel when events activate

### Features Implemented

✅ **Event Models & Data Structures**
- `Models/Event.cs` - Event model with server config, tracks, and recurring patterns
- `Models/EventSchedule.cs` - Schedule container with 15+ helper methods
- JSON serialization with proper attributes

✅ **API Endpoints** (`Controllers/EventsController.cs`)
- `POST /api/Events/schedule` - Receive complete schedule from Laravel (with validation)
- `GET /api/Events/current` - Get currently active event
- `GET /api/Events/upcoming` - List all upcoming events (sorted by start time)
- `GET /api/Events/due` - List events that need activation
- `GET /api/Events/summary` - Schedule status summary (counts)
- `GET /api/Events/{id}` - Get specific event by ID
- `POST /api/Events/{id}/activate` - Manually trigger event activation (reserved for future use)

✅ **Smart Restart System** (`Services/SmartRestartService.cs`)
- **5-Minute Countdown**: Sends in-game messages every minute (T-5, T-4, T-3, T-2, T-1)
- **Lobby Detection**: Waits for track change event (players return to lobby between races)
- **Player-Aware**: Restarts immediately if no players online
- **10-Minute Timeout**: Forces restart if lobby not detected within timeout
- **State Machine**: Idle → Warning → Pending → Restarting → Completed
- **Server Messages**: Uses `ConsoleWriter` to send in-game chat messages

✅ **Event Scheduler Service** (`Services/EventSchedulerService.cs`)
- **Background IHostedService**: Runs automatically when application starts
- **30-Second Timer**: Checks for due events every 30 seconds
- **Automatic Activation**: Initiates smart restart when event start_time is reached
- **Configuration Application**: Updates server_config.cfg and deploys track rotation
- **Recurring Events**: Automatically reschedules weekly/daily events after activation
- **Missed Events Detection**: Logs events that were scheduled while service was offline (doesn't activate them)
- **Laravel Webhook Integration**: Sends `event-activated` webhook with eventId, eventName, timestamp

✅ **Recurring Events** (`Services/RecurringEventService.cs`)
- **Daily Patterns**: Event repeats every day at specified time
- **Weekly Patterns**: Event repeats on specific days of week (e.g., Friday 8 PM, Monday/Wednesday/Friday 3 PM)
- **Occurrence Limits**: Optional limit on number of recurrences
- **Next Instance Calculation**: Automatically calculates next occurrence after activation
- **Pattern Descriptions**: Human-readable descriptions ("Weekly on Mon, Wed, Fri at 15:00")

✅ **File Persistence** (`Services/EventStorageService.cs`)
- **JSON Storage**: `Data/event-schedule.json` (auto-created on first use)
- **Atomic Writes**: Uses temp file to prevent corruption
- **Backup Support**: `BackupSchedule()` method creates timestamped backups
- **Error Handling**: Gracefully handles missing/corrupt files

✅ **Laravel Integration**
- **Webhook**: `POST {Laravel:WebhookBaseUrl}/event-activated` with `{eventId, eventName, timestamp}`
- **Retry Logic**: 3 retries with exponential backoff (doesn't block event activation on failure)
- **Configuration**: `appsettings.json` → `Laravel:WebhookBaseUrl`

✅ **Comprehensive Logging**
- All services use ILogger with structured logging
- Log levels: Information (activation), Debug (checks), Warning (failures), Error (exceptions)
- Sample logs:
  - `"Event Weekend Racing (ID 1) is due for activation (scheduled: 2025-01-15 20:00)"`
  - `"5 players online - starting 5-minute countdown"`
  - `"Track changed to speedway2_inner_oval - lobby detected, initiating restart"`
  - `"Event Weekend Racing activated successfully"`

✅ **Error Handling & Recovery**
- Missing schedule file → Empty schedule created
- Schedule validation → HTTP 400 with detailed error messages
- Restart failures → Retry up to 3 times, log error, skip event
- Webhook failures → Log error, continue activation (event still activates)
- Overlapping events → First-come-first-served (by start time)

✅ **Unit Tests**
- `EventScheduleTests.cs` - 22 tests for schedule helpers (GetUpcomingEvents, ActivateEvent, etc.)
- `RecurringEventServiceTests.cs` - 17 tests for daily/weekly pattern calculation
- `EventsControllerTests.cs` - 16 tests for all API endpoints

### File Structure

**New Files Created:**
```
Models/
  ├── Event.cs                      - Event model with ServerConfig, Tracks, RecurringPattern
  └── EventSchedule.cs              - Schedule container with 15+ helper methods

Services/
  ├── EventStorageService.cs        - JSON persistence (save/load/backup)
  ├── EventSchedulerService.cs      - Background IHostedService (30s timer)
  ├── SmartRestartService.cs        - Countdown + lobby detection (5-min warnings)
  ├── RecurringEventService.cs      - Daily/weekly pattern calculation
  └── LaravelWebhookService.cs      - Extended with SendEventActivatedAsync()

Controllers/
  └── EventsController.cs           - 7 REST API endpoints

Data/
  └── event-schedule.json           - Persisted schedule (auto-created)

WreckfestController.Tests/Services/
  ├── EventScheduleTests.cs         - 22 unit tests
  ├── RecurringEventServiceTests.cs - 17 unit tests
  └── EventsControllerTests.cs      - 16 unit tests (Controllers/)
```

### Usage Examples

**1. Laravel Pushes Event Schedule**
```json
POST /api/Events/schedule
{
  "events": [
    {
      "id": 1,
      "name": "Weekend Racing",
      "description": "Special weekend event",
      "startTime": "2025-01-10T20:00:00Z",
      "isActive": false,
      "serverConfig": {
        "serverName": "Weekend Special Server",
        "welcomeMessage": "Welcome to weekend racing!",
        "bots": 12
      },
      "tracks": [
        { "track": "speedway2_inner_oval", "laps": 15 },
        { "track": "woodland_banger", "gamemode": "derby" }
      ],
      "collectionName": "Weekend Rotation",
      "recurringPattern": {
        "type": "Weekly",
        "days": [5, 6],  // Friday, Saturday
        "time": "20:00:00",
        "occurrences": null
      }
    }
  ]
}
```

**2. C# Activates Event Automatically**
- EventSchedulerService detects `startTime <= now`
- SmartRestartService initiates 5-minute countdown
- Players see in-game messages: "Server will restart in 5 minutes."
- At T-0: "Server will restart at the next lobby."
- When track changes (lobby detected): Restart begins
- After restart: server_config.cfg updated, track rotation deployed
- Webhook sent to Laravel: `POST /event-activated {eventId: 1, eventName: "Weekend Racing"}`
- Event marked `isActive: true` in schedule
- If recurring: Next Friday 8 PM instance scheduled automatically

**3. Get Current Event**
```http
GET /api/Events/current
Response: { "id": 1, "name": "Weekend Racing", "isActive": true, ... }
```

**4. Get Schedule Summary**
```http
GET /api/Events/summary
Response: {
  "totalEvents": 5,
  "activeEvents": 1,
  "upcomingEvents": 3,
  "dueEvents": 1,
  "lastUpdated": "2025-01-15T14:30:00Z"
}
```

### Configuration

**appsettings.json**
```json
{
  "Laravel": {
    "WebhookBaseUrl": "http://localhost:8000/api/webhooks"
  },
  "WreckfestServer": {
    "WorkingDirectory": "C:\\Path\\To\\WreckfestServer"
  }
}
```

**Data File** (`Data/event-schedule.json`)
```json
{
  "events": [...],
  "lastUpdated": "2025-01-15T14:30:00Z"
}
```

### Testing

**Run Unit Tests:**
```bash
dotnet test --filter "FullyQualifiedName~EventScheduleTests"
dotnet test --filter "FullyQualifiedName~RecurringEventServiceTests"
dotnet test --filter "FullyQualifiedName~EventsControllerTests"
```

**Manual End-to-End Test:**
1. Create event in Laravel for 2 minutes from now
2. Verify C# receives schedule (check logs)
3. Wait for countdown messages to appear in-game
4. Join server and verify messages appear
5. Wait for track change → server restarts
6. Verify new config/rotation applied
7. Check Laravel receives webhook

### Known Limitations

- **Server Message Command**: Uses `say <message>` - may need adjustment for actual Wreckfest server
- **One Active Event**: Only one event can be active at a time (earlier event deactivated)
- **Overlapping Events**: No priority system - first scheduled event wins
- **Manual Activation**: `POST /api/Events/{id}/activate` returns 501 (not fully wired to UI yet)

### Future Enhancements (Not Implemented)

- Event priority system for overlapping events
- Event history tracking (`Data/EventHistory.json`)
- Configurable countdown duration (currently hardcoded to 5 minutes)
- Configurable timeout (currently hardcoded to 10 minutes)
- Event activation dry-run / preview mode
- Event activation cancellation API endpoint

---

## Development Workflows

### Opening in Visual Studio 2022

1. Double-click `WreckfestController.sln` or open from VS 2022
2. Set **WreckfestController** as startup project (right-click project → Set as Startup Project)
3. Select **http** or **https** profile from debug dropdown
4. Press **F5** to run with debugging

---

### Running from Command Line

```bash
# Run in development mode
dotnet run

# Run tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Build release
dotnet build -c Release

# Publish as single executable
dotnet publish -c Release -r win-x64 -o "C:\WreckfestController"
```

---

### Debugging

**Visual Studio 2022:**
- Set breakpoints by clicking left margin
- F5: Start debugging
- F10: Step over
- F11: Step into
- View variables in Locals, Autos, Watch windows

**Common Debug Points:**
- `ServerController.StartServer()`: Server start flow
- `ServerManager.ReadNewLogLines()`: Log parsing
- `ConfigService.WriteEventLoopTracks()`: Config writing
- `PlayerTracker.ProcessLogLine()`: Player tracking

---

### Adding New Endpoints

**Example: Add GET /api/server/uptime**

1. **Add method to ServerController.cs:**
```csharp
[HttpGet("uptime")]
public IActionResult GetUptime()
{
    var status = _serverManager.GetStatus();
    return Ok(new { uptime = status.Uptime });
}
```

2. **Run and test:**
- F5 to run
- Navigate to Swagger: `https://localhost:5101/swagger`
- Test new endpoint

3. **Add test in WreckfestController.Tests:**
```csharp
[Fact]
public async Task GetUptime_ReturnsUptime_WhenServerRunning()
{
    // Arrange
    var mockServerManager = new Mock<ServerManager>();
    mockServerManager.Setup(m => m.GetStatus())
        .Returns(new ServerStatus { Uptime = TimeSpan.FromHours(1) });

    var controller = new ServerController(mockServerManager.Object, Mock.Of<ILogger<ServerController>>());

    // Act
    var result = controller.GetUptime();

    // Assert
    var okResult = Assert.IsType<OkObjectResult>(result);
    // ... more assertions
}
```

---

### Working with Configuration Files

**Reading server_config.cfg:**
```csharp
var config = _configService.ReadBasicConfig();
Console.WriteLine($"Server name: {config.ServerName}");
```

**Writing server_config.cfg:**
```csharp
var config = _configService.ReadBasicConfig();
config.ServerName = "New Name";
config.MaxPlayers = 32;
_configService.WriteBasicConfig(config);
```

**Reading event loop tracks:**
```csharp
var tracks = _configService.ReadEventLoopTracks();
foreach (var track in tracks)
{
    Console.WriteLine($"{track.Track} - {track.Gamemode} - {track.Laps} laps");
}
```

**Writing event loop tracks:**
```csharp
var tracks = new List<EventLoopTrack>
{
    new EventLoopTrack
    {
        Track = "track/wild_valley/valley_edge_short",
        Gamemode = "racing",
        Laps = 3,
        Bots = 8
    }
};
_configService.WriteEventLoopTracks("My Rotation", tracks);
```

---

## Testing

### Test Structure

```
WreckfestController.Tests/
├── Controllers/
│   ├── ServerControllerTests.cs
│   └── ConfigControllerTests.cs
└── Services/
    ├── ServerManagerTests.cs
    ├── ConfigServiceTests.cs
    └── PlayerTrackerTests.cs
```

**Test Coverage:** 51+ tests covering:
- Server control operations
- Player tracking (including multi-word names)
- Configuration management
- Event loop parsing
- Process attachment

---

### Running Tests

**Visual Studio 2022:**
1. Open Test Explorer (Test > Test Explorer or Ctrl+E, T)
2. Click "Run All Tests"
3. View results in Test Explorer

**Command Line:**
```bash
# Run all tests
dotnet test

# Run specific test
dotnet test --filter "FullyQualifiedName~PlayerTrackerTests.ProcessLogLine_DetectsPlayerJoin"

# Run with detailed output
dotnet test --verbosity normal
```

---

### Writing Tests

**Example: Testing PlayerTracker**

```csharp
[Fact]
public void ProcessLogLine_DetectsPlayerJoin_WithMultiWordName()
{
    // Arrange
    var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), Mock.Of<LaravelWebhookService>());

    // Act
    tracker.ProcessLogLine("[INFO] Player With Spaces has joined.");

    // Assert
    var players = tracker.GetOnlinePlayers();
    Assert.Single(players);
    Assert.Equal("Player With Spaces", players[0].Name);
    Assert.False(players[0].IsBot);
}

[Fact]
public void ProcessLogLine_DetectsBot_WithAsteriskPrefix()
{
    // Arrange
    var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), Mock.Of<LaravelWebhookService>());

    // Act
    tracker.ProcessLogLine("[INFO] *BotName has joined.");

    // Assert
    var players = tracker.GetOnlinePlayers();
    Assert.Single(players);
    Assert.True(players[0].IsBot);
}
```

---

## Troubleshooting

### Server Won't Start

**Symptom:** POST /api/server/start returns error

**Checks:**
1. Verify `appsettings.json` paths are correct:
   - `WreckfestServer:ServerPath` points to Wreckfest_x64.exe
   - `WreckfestServer:WorkingDirectory` is correct
2. Check if server_config.cfg exists at path specified in ServerArguments
3. Check if process exits immediately (exit code in logs)
4. Review logs: Check `_logger.LogError()` messages

**Solution:**
```bash
# Test server manually
cd "C:\Path\To\Wreckfest Dedicated Server"
.\Wreckfest_x64.exe -s server_config=server_config.cfg
```

---

### Server Running But API Says "Not Running"

**Symptom:** Server is running but GET /api/server/status shows `isRunning: false`

**Cause:** Server was started outside the API (no PID tracking)

**Solution:**
```bash
# Find PID
tasklist | findstr /i "wreck"

# Attach to process
curl -X POST http://localhost:5100/api/server/attach/12345
```

---

### Log File Not Found

**Symptom:** GET /api/server/logfile returns "Log file not found"

**Checks:**
1. Check server_config.cfg for `log=<filename>` setting
2. Check `appsettings.json` for `WreckfestServer:LogFilePath`
3. Verify server has started and created log file

**Solution:**
```ini
# In server_config.cfg, add:
log=log.txt
```

Or in appsettings.json:
```json
{
  "WreckfestServer": {
    "LogFilePath": "C:\\Full\\Path\\To\\log.txt"
  }
}
```

---

### Players Not Tracking

**Symptom:** GET /api/server/players returns empty list

**Checks:**
1. Is server running?
2. Is log file monitoring active? (check logs for "Starting log file monitoring")
3. Are players actually joining? (check log file manually)
4. Check log patterns match expected format

**Debug:**
```csharp
// Add logging in PlayerTracker.ProcessLogLine()
_logger.LogInformation("Processing log line: {Line}", line);
```

---

### Port Already in Use

**Symptom:** Error starting API: "Address already in use"

**Solution:**
```powershell
# Find what's using the port
netstat -ano | findstr :5100

# Kill the process (replace <PID> with actual PID)
taskkill /PID <PID> /F

# Or change port in Properties/launchSettings.json
"applicationUrl": "http://localhost:6100"
```

---

## Cross-Project Documentation

### Local Documentation (This Project)
- **[README.md](README.md)** - Main project documentation
- **[INSTALL.md](INSTALL.md)** - Installation and deployment guide
- **[MATERIALDESIGN.md](MATERIALDESIGN.md)** - Material Design UI guidelines
- **[CLAUDE_GUIDE.md](CLAUDE_GUIDE.md)** - This file (AI development guide)

### Laravel WreckfestWeb Project
- **F:\Projects\Web\WreckfestWeb\CLAUDE_GUIDE.md** - Laravel project guide
- **F:\Projects\Web\WreckfestWeb\README.md** - Laravel project README

### Cross-Project Guides (F:\claude\)
- **[F:\claude\FILAMENT4_GUIDE.md](F:\claude\FILAMENT4_GUIDE.md)** - Filament 4 reference (breaking changes, forms, tables)
- **[F:\claude\PEST4_GUIDE.md](F:\claude\PEST4_GUIDE.md)** - Pest 4 testing framework
- **[F:\claude\LARAVEL_BOOST_GUIDE.md](F:\claude\LARAVEL_BOOST_GUIDE.md)** - Laravel Boost MCP server tools
- **[F:\claude\LARAVEL_GUIDE.md](F:\claude\LARAVEL_GUIDE.md)** - Laravel 12.x best practices
- **[F:\claude\MCP_GUIDE.md](F:\claude\MCP_GUIDE.md)** - Model Context Protocol overview

---

## For New AI Sessions

**When starting a new session with Claude Code in this project, say:**

> "Read CLAUDE_GUIDE.md for context"

This will load all project context, architecture, and development guidelines.

**For cross-project work:**
- Read Laravel guide: `F:\Projects\Web\WreckfestWeb\CLAUDE_GUIDE.md`
- Reference shared guides in `F:\claude\` for Laravel/Filament specifics

---

**Good luck with development! 🚀**
