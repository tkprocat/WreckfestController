# WreckfestController Installation Guide

This guide will walk you through setting up the WreckfestController API to manage your Wreckfest Dedicated Server.

## Prerequisites

### Required Software
- **Windows Server** or Windows 10/11
- **.NET 8.0 Runtime** - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Wreckfest Dedicated Server** - Installed via SteamCmd or Steam
- **SteamCmd** (optional, for automatic updates) - [Download](https://developer.valvesoftware.com/wiki/SteamCMD)

### Required for WreckfestWeb Integration
- **WreckfestWeb** - Web admin panel for managing your server ([https://github.com/tkprocat/WreckfestWeb](https://github.com/tkprocat/WreckfestWeb))
  - Webhook integration enables player tracking, event notifications, and track changes
  - WreckfestController can run standalone, but webhook features will be disabled without WreckfestWeb

## Installation Steps

### 1. Download WreckfestController

**Recommended:** Download the latest pre-compiled release from the [Releases page](https://github.com/tkprocat/WreckfestController/releases).

Extract the ZIP file to a directory of your choice, e.g., `C:\WreckfestController\`

**Alternative - Build from Source:**
If you want to build from source:
```bash
git clone https://github.com/tkprocat/WreckfestController.git
cd WreckfestController
dotnet build -c Release
```

### 2. Configure the Application

Copy the example configuration file:
```bash
copy appsettings.example.json appsettings.json
```

Edit `appsettings.json` and update the following settings:

#### Wreckfest Server Configuration
```json
"WreckfestServer": {
  "ServerPath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server\\Wreckfest_x64.exe",
  "ServerArguments": "-s server_config=server_config.cfg",
  "WorkingDirectory": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server",
  "LogFilePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server\\log.txt"
}
```

**Update these paths to match your Wreckfest installation location.**

#### SteamCmd Configuration (Optional)
```json
"SteamCmd": {
  "SteamCmdPath": "C:\\steamcmd\\steamcmd.exe",
  "WreckfestAppId": "361580",
  "InstallDirectory": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Wreckfest Dedicated Server"
}
```

Only needed if you want automatic server updates via the API.

#### WreckfestWeb Webhook Configuration
```json
"Laravel": {
  "WebhookBaseUrl": "https://your-wreckfestweb-domain.com/api/webhooks"
}
```

**Required for WreckfestWeb integration.** Set this to your WreckfestWeb installation's webhook endpoint URL.

If running standalone without WreckfestWeb, webhooks will be disabled but the API will still function for server control.

#### Network Configuration
```json
"Kestrel": {
  "Urls": "http://0.0.0.0:5100;https://0.0.0.0:5101"
}
```

The API will listen on ports 5100 (HTTP) and 5101 (HTTPS). Change if needed.

### 3. Configure Wreckfest Server

The WreckfestController manages the Wreckfest server's `server_config.cfg` file. This file should exist in your Wreckfest server's working directory.

If you don't have one yet, create `server_config.cfg` in your Wreckfest server directory with basic settings:
```ini
game_mode=derby
server_name=My Wreckfest Server
max_players=24
password=
```

The API provides endpoints to read and modify this configuration.

### 4. Run the Application

Navigate to the extracted directory (or `bin/Release/net8.0` if you built from source) and run:

```bash
WreckfestController.exe
```

**Or if running from source:**
```bash
dotnet run --configuration Release
```

### 5. Verify Installation

Once running, the API will be available at:
- **HTTP:** http://localhost:5100
- **HTTPS:** https://localhost:5101
- **Swagger UI:** http://localhost:5100/swagger

Open the Swagger UI to explore the API endpoints.

## Running as a Windows Service (Optional)

To run WreckfestController as a Windows Service, use the Windows Service Control tool:

```bash
sc create WreckfestController binPath="C:\path\to\WreckfestController.exe"
sc start WreckfestController
```

To remove the service:
```bash
sc stop WreckfestController
sc delete WreckfestController
```

## API Endpoints Overview

Once installed, you can manage your server using these endpoints:

### Server Control
- `POST /api/server/start` - Start the server
- `POST /api/server/stop` - Stop the server
- `POST /api/server/restart` - Restart the server
- `POST /api/server/update` - Update server via SteamCmd
- `GET /api/server/status` - Get server status
- `POST /api/server/command` - Send console command (e.g., `/bot`)

### Configuration Management
- `GET /api/config` - Get current server_config.cfg
- `PUT /api/config` - Update server_config.cfg

### Player Tracking
- `GET /api/server/players` - Get current player list
- WebSocket: `ws://localhost:5100/ws/players` - Real-time player updates

### Event Scheduling
- `GET /api/events/schedule` - Get scheduled events
- `PUT /api/events/schedule` - Update event schedule
- Events automatically change server configuration at scheduled times

### WebSocket Endpoints
- `ws://localhost:5100/ws/console` - Real-time server console output
- `ws://localhost:5100/ws/players` - Real-time player tracking
- `ws://localhost:5100/ws/track-changes` - Real-time track change notifications

## Firewall Configuration

If accessing the API from another machine, open these ports:
```bash
# Windows Firewall
netsh advfirewall firewall add rule name="WreckfestController HTTP" dir=in action=allow protocol=TCP localport=5100
netsh advfirewall firewall add rule name="WreckfestController HTTPS" dir=in action=allow protocol=TCP localport=5101
```

## Troubleshooting

### Server Won't Start
- Verify `ServerPath` in `appsettings.json` points to `Wreckfest_x64.exe`
- Verify `WorkingDirectory` exists and is correct
- Check file permissions - the API needs read/write access to the Wreckfest directory

### Configuration Changes Not Saving
- Ensure the API has write permissions to `server_config.cfg`
- Check the API logs for error messages

### SteamCmd Updates Fail
- Verify `SteamCmdPath` points to `steamcmd.exe`
- Ensure SteamCmd is installed and working
- Check if SteamCmd requires authentication (use anonymous login for Wreckfest)

### WebSockets Not Working
- Check firewall settings
- Verify WebSocket protocol is allowed by your proxy/reverse proxy if using one

## Logging

Logs are written to the console by default. Configure logging in `appsettings.json`:

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning",
    "WreckfestController.Services.ServerManager": "Debug"
  }
}
```

## WreckfestWeb Integration

When integrated with WreckfestWeb ([https://github.com/tkprocat/WreckfestWeb](https://github.com/tkprocat/WreckfestWeb)), the API sends webhooks for:
- Player join/leave events
- Track changes
- Event activation

Configure your WreckfestWeb webhook endpoint in `appsettings.json`:
```json
"Laravel": {
  "WebhookBaseUrl": "https://your-wreckfestweb-domain.com/api/webhooks"
}
```

WreckfestController sends data to these WreckfestWeb endpoints:
- `POST /api/webhooks/player-joined`
- `POST /api/webhooks/player-left`
- `POST /api/webhooks/track-changed`
- `POST /api/webhooks/event-activated`

## File Locations

After installation, these files will be created/managed:

- **event-schedule.json** - In Wreckfest working directory (alongside server_config.cfg)
  - Stores scheduled events for automatic server configuration changes

## Next Steps

1. **Test the API** - Use Swagger UI to test endpoints
2. **Start Your Server** - `POST /api/server/start`
3. **Configure Events** - Set up scheduled events via `/api/events/schedule`
4. **Monitor Players** - Connect to WebSocket endpoints for real-time updates

## Support

- **Documentation:** See [CLAUDE_GUIDE.md](./CLAUDE_GUIDE.md) for detailed API documentation
- **Issues:** Report bugs on the GitHub Issues page
- **Development:** See [VS2022_TIPS.md](./VS2022_TIPS.md) for development setup

## Security Considerations

⚠️ **Important Security Notes:**

1. **No Authentication** - This API currently has no built-in authentication. Do not expose it to the public internet without adding authentication or using a reverse proxy with auth.

2. **Local Network Only** - By default, configure to listen only on your local network.

3. **Firewall** - Use Windows Firewall to restrict access to trusted IPs only.

4. **HTTPS** - For production, configure proper SSL certificates instead of using the development certificate.

## License

See [LICENSE.txt](./LICENSE.txt) for license information.
