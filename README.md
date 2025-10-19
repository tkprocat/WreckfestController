# Wreckfest Server Controller

A REST API wrapper for controlling your Wreckfest Dedicated Server with real-time console monitoring, player tracking, and server configuration management.

## Requirements

- .NET 8.0 SDK or later
- Visual Studio 2022 (recommended) or Visual Studio Code
- Wreckfest Dedicated Server

## Quick Start (Visual Studio 2022)

1. **Open the solution**: Double-click `WreckfestController.sln` or open it from Visual Studio 2022
2. **Configure server path**: Edit `appsettings.json` with your Wreckfest server path
3. **Run**: Press **F5** to start debugging
4. **Test**: Browser opens automatically to Swagger UI - try the endpoints!
5. **Run tests**: Open Test Explorer (Ctrl+E, T) and click "Run All Tests"

## Features

- **Start/Stop/Restart** server via REST API
- **Send commands** to the server console
- **Real-time console monitoring** via WebSockets
- **Real-time log file monitoring** with automatic player tracking
- **Player tracking** - Track online/offline players, join/leave events
- **Server status** endpoint with detailed process information
- **Server configuration** management (read/update server_config.cfg)
- Swagger UI for API documentation
- Comprehensive test suite (51+ unit tests)

## Configuration

⚠️ **IMPORTANT**: You must edit `appsettings.json` with your server paths before running!

Edit `appsettings.json` to set your Wreckfest server path:

```json
{
  "WreckfestServer": {
    "ServerPath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server\\Wreckfest_x64.exe",
    "ServerArguments": "-s server_config=server_config.cfg",
    "WorkingDirectory": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server",
    "LogFilePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server\\log.txt"
  }
}
```

**Config File Paths:**
- The `server_config.cfg` path in `ServerArguments` can be:
  - **Relative**: `server_config=server_config.cfg` (looks in WorkingDirectory)
  - **Absolute**: `server_config=C:\\Full\\Path\\To\\server_config.cfg`
- The `WorkingDirectory` must be set to the server's installation folder

**Important Configuration Options:**

- `ServerPath` - Path to the server executable
  - Point directly to `Wreckfest_x64.exe` (or your server executable)

- `ServerArguments` - Command-line arguments for the server
  - Example: `"-s server_config=server_config.cfg"`

- `LogFilePath` - Path to the server's log file (**required for player tracking**)
  - Example: `"C:\\Path\\To\\wreckfest_server.log"`
  - The server writes all output to this log file
  - Used for real-time monitoring and player tracking
  - Use `/api/server/logfile` to view the server logs

**Network Configuration (for standalone Kestrel deployment):**

- `Kestrel:Urls` - URLs to listen on when using Kestrel (not IIS)
  - Example: `"http://0.0.0.0:5100;https://0.0.0.0:5101"`
  - `0.0.0.0` means listen on all network interfaces
  - Use `localhost` or `127.0.0.1` to only allow local connections
  - Separate multiple URLs with semicolons

- `UseKestrel` - Set to `true` to force Kestrel configuration in production
  - Default: `false` (uses IIS bindings when deployed to IIS)
  - Only needed if running standalone without IIS

**Note:** When deployed to IIS, the IP and port are controlled by IIS bindings, not the application configuration.

## Visual Studio 2022

### Opening the Project

1. Open Visual Studio 2022
2. Click **File > Open > Project/Solution**
3. Navigate to `F:\Projects\C#\WreckfestController\WreckfestController.sln`
4. Click **Open**

### Running from Visual Studio

1. Set **WreckfestController** as the startup project (right-click project > Set as Startup Project)
2. Select either **http** or **https** profile from the debug dropdown
3. Press **F5** to run with debugging or **Ctrl+F5** to run without debugging
4. Your browser will automatically open to the Swagger UI

### Running Tests in Visual Studio

1. Open **Test Explorer** (Test > Test Explorer or Ctrl+E, T)
2. Click **Run All Tests** button or right-click and select **Run**
3. View test results in the Test Explorer window

### Debugging

- Set breakpoints by clicking in the left margin of the code editor
- Use F5 to start debugging
- Step through code with F10 (Step Over) or F11 (Step Into)
- View variables in the Locals, Autos, and Watch windows

**💡 For more Visual Studio 2022 tips and shortcuts, see [VS2022_TIPS.md](VS2022_TIPS.md)**

## Running the Application

### Development (using dotnet run)

```bash
dotnet run
```

The API will be available at `https://localhost:5101` (or `http://localhost:5100`)

### Standalone (using the .exe)

**1. Build/Publish the application:**

The project is configured to publish as a single executable file:

```bash
# Framework-dependent (requires .NET 8 runtime on target machine) - Recommended
dotnet publish WreckfestController.csproj -c Release -r win-x64 -o "C:\WreckfestController"

# Self-contained (includes .NET runtime, larger file, no dependencies)
dotnet publish WreckfestController.csproj -c Release -r win-x64 --self-contained true -o "C:\WreckfestController"
```

This creates a single `WreckfestController.exe` (~4 MB) plus `appsettings.json` and a few small support files.

**2. Configure appsettings.json:**

Set `UseKestrel: true` to enable IP/port configuration:

```json
{
  "Kestrel": {
    "Urls": "http://0.0.0.0:5100;https://0.0.0.0:5101"
  },
  "UseKestrel": true
}
```

**3. Run the executable:**
```powershell
cd C:\WreckfestController
.\WreckfestController.exe
```

**Or specify URLs via command line:**
```powershell
.\WreckfestController.exe --urls "http://0.0.0.0:5100"
```

The API will be available at the configured URLs (default: `http://0.0.0.0:5100`)

## Running Tests

```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Run tests from the test project directory
cd WreckfestController.Tests
dotnet test
```

## API Endpoints

### Server Control

- **GET /api/server/status** - Get current server status (running/stopped, uptime, process ID, tracked PID)
- **GET /api/server/logfile?lines=100** - Get last N lines from the server's log file
- **GET /api/server/players** - Get current online players with real-time tracking
- **POST /api/server/start** - Start the Wreckfest server
- **POST /api/server/stop** - Stop the Wreckfest server
- **POST /api/server/restart** - Restart the Wreckfest server
- **POST /api/server/attach/{pid}** - Attach to an already running server by process ID

### Server Configuration

- **GET /api/config** - Get all server configuration settings
- **GET /api/config/{key}** - Get a specific configuration value
- **PUT /api/config/{key}** - Update a configuration value
- **GET /api/config/event-loop** - Get the event loop configuration (multiple tracks/modes)
- **PUT /api/config/event-loop** - Update the event loop configuration

**How PID tracking works:**
- When you start a server via the API, it automatically captures and tracks the process ID (PID)
- The API **only** tracks the specific server instance it started - it won't interfere with other Wreckfest instances
- This allows precise control of specific server instances (critical when running multiple servers)
- Use `/api/server/attach/{pid}` to manually track an already-running server
- Status endpoint shows `trackedByPid: true` and `trackedPid` when PID tracking is active

## WebSocket Endpoint

### WS /ws/console
Connect to receive real-time console output from the server

**Example (JavaScript):**
```javascript
const ws = new WebSocket('ws://localhost:5100/ws/console');

ws.onmessage = (event) => {
  console.log('Server output:', event.data);
};

ws.onopen = () => {
  console.log('Connected to server console');
};
```

## Swagger UI

Access the Swagger UI for interactive API documentation at:
- `https://localhost:5101/swagger` (or `http://localhost:5100/swagger`)

## Example Usage

**Using curl:**

```bash
# Start server
curl -X POST http://localhost:5100/api/server/start

# Check status
curl http://localhost:5100/api/server/status

# Get online players
curl http://localhost:5100/api/server/players

# Get server log
curl http://localhost:5100/api/server/logfile?lines=50

# Stop server
curl -X POST http://localhost:5100/api/server/stop
```

**Using PowerShell:**

```powershell
# Start server
Invoke-WebRequest -Uri "http://localhost:5100/api/server/start" -Method POST

# Check status
Invoke-WebRequest -Uri "http://localhost:5100/api/server/status"

# Get online players
Invoke-WebRequest -Uri "http://localhost:5100/api/server/players"

# Get server log
Invoke-WebRequest -Uri "http://localhost:5100/api/server/logfile?lines=50"

# Stop server
Invoke-WebRequest -Uri "http://localhost:5100/api/server/stop" -Method POST
```

## Player Tracking

The API automatically tracks players in real-time by monitoring the server's log file:

**Features:**
- Tracks when players join and leave the server
- Distinguishes between bots (prefixed with `*`) and human players
- Supports player names with spaces
- Maintains online/offline status
- Logs all join/leave events to the console

**API Endpoint:**
```bash
# Get current online players
curl http://localhost:5100/api/server/players
```

**Example Response:**
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
      "joinedAt": "2025-10-18T14:30:00Z",
      "lastSeenAt": "2025-10-18T14:35:00Z"
    },
    {
      "name": "Ultimate Night of Super Se",
      "isBot": true,
      "isOnline": true,
      "slot": 1,
      "joinedAt": "2025-10-18T14:31:00Z",
      "lastSeenAt": "2025-10-18T14:35:00Z"
    }
  ],
  "lastUpdated": "2025-10-18T14:35:00Z"
}
```

**How it works:**
1. Monitors the server's log file in real-time (via FileSystemWatcher + polling)
2. Parses join/leave events from log lines
3. Tracks player status and metadata
4. Provides REST API access to current player list

## Troubleshooting

### Server Status Shows "Not Running" After Starting

If the server process starts but isn't detected by the API:

**Option 1: Check if the process is running**
```bash
# Find running Wreckfest processes
tasklist | findstr /i "wreck"
```

**Option 2: Manually attach to a running server**
```bash
# Attach to it using the PID from tasklist
curl -X POST http://localhost:5100/api/server/attach/YOUR_PID
```

**Note:** The API only tracks servers started via the API or manually attached. It won't automatically detect other Wreckfest instances to avoid conflicts when running multiple servers.

### Port Already in Use

If you get an error about ports 5100/5101 being in use, you can:

1. **Check what's using the port:**
   ```powershell
   netstat -ano | findstr :5100
   ```

2. **Change the port** in `Properties/launchSettings.json`:
   ```json
   "applicationUrl": "http://localhost:YOUR_PORT"
   ```

3. **Kill the process** (if it's safe to do so):
   ```powershell
   # Find the PID from netstat, then:
   taskkill /PID <process_id> /F
   ```

**Note:** Port 5000 is often reserved by Windows (Hyper-V), which is why this project uses 5100/5101 by default.

## Development

### Project Structure

```
WreckfestController/
├── Controllers/          # API endpoints
│   ├── ServerController.cs
│   └── ConfigController.cs
├── Services/            # Business logic
│   ├── ServerManager.cs     # Server process management
│   ├── PlayerTracker.cs     # Real-time player tracking
│   └── ConfigService.cs     # Server configuration management
├── Models/              # Data models
├── WebSockets/          # WebSocket handlers
└── WreckfestController.Tests/  # Unit tests
    ├── Controllers/
    └── Services/
```

### Running Tests

All tests should pass before committing:

```bash
dotnet test
```

Current test coverage: 51+ tests covering:
- Server control operations
- Player tracking (including multi-word names)
- Configuration management
- Event loop parsing
- Process attachment

### Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Run tests (`dotnet test`)
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

### Code Style

- Follow standard C# conventions
- Use async/await for I/O operations
- Add unit tests for new features
- Update README for user-facing changes

## License

This project is provided as-is. Add a LICENSE file if you plan to open source this project.
