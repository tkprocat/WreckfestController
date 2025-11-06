# Wreckfest Server Controller

A REST API wrapper for controlling your Wreckfest Dedicated Server with real-time console monitoring, player tracking, and server configuration management.

Part of the WreckfestWeb ecosystem - integrates with [WreckfestWeb](https://github.com/tkprocat/WreckfestWeb) for a complete web admin panel.

## Features

- **🎮 Server Control** - Start/Stop/Restart via REST API
- **⚡ Real-time Monitoring** - WebSocket streaming of console output
- **👥 Player Tracking** - Track online/offline players, join/leave events
- **📊 Event Scheduling** - Automated server configuration changes with smart restart logic
- **🔧 Configuration Management** - Read/update server_config.cfg remotely
- **🌐 WebSocket Streams** - Console output, player tracking, track changes
- **🔄 SteamCmd Integration** - Automatic server updates
- **📡 WreckfestWeb Webhooks** - Player events, track changes, event activation
- **📝 Swagger UI** - Interactive API documentation
- **✅ Comprehensive Tests** - 79+ unit tests covering all functionality

## Installation

📖 **See [INSTALL.md](INSTALL.md) for complete installation instructions** including:
- Pre-compiled releases
- Configuration guide
- Running as a Windows Service
- WreckfestWeb integration

## Requirements

### For Pre-compiled Releases
- .NET 8.0 Runtime ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- Wreckfest Dedicated Server

### For Building from Source
- .NET 8.0 SDK or later
- Visual Studio 2022 (recommended) or Visual Studio Code
- Wreckfest Dedicated Server

## Quick Start (Visual Studio 2022)

1. **Open the solution**: Double-click `WreckfestController.sln` or open it from Visual Studio 2022
2. **Configure server path**: Edit `appsettings.json` with your Wreckfest server path
3. **Run**: Press **F5** to start debugging
4. **Test**: Browser opens automatically to Swagger UI - try the endpoints!
5. **Run tests**: Open Test Explorer (Ctrl+E, T) and click "Run All Tests"

## Configuration

Edit `appsettings.json` to set your Wreckfest server paths:

```json
{
  "WreckfestServer": {
    "ServerPath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server\\Wreckfest_x64.exe",
    "WorkingDirectory": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server",
    "LogFilePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server\\log.txt"
  }
}
```

For detailed configuration options, see [INSTALL.md](INSTALL.md).

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

### Development
```bash
dotnet run
```

The API will be available at `https://localhost:5101` or `http://localhost:5100`

### Production Deployment

For production deployment, Windows Service setup, and advanced configuration, see [INSTALL.md](INSTALL.md).

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

This project is licensed under the MIT License - see the [LICENSE.txt](LICENSE.txt) file for details.
