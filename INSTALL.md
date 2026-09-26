# WreckfestController Installation Guide

This guide will walk you through setting up the WreckfestController API to manage your Wreckfest Dedicated Server.

## Prerequisites

### Required Software
- **Windows Server** or Windows 10/11
- **.NET 10.0 Runtime** - [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Wreckfest Dedicated Server** - Installed via SteamCmd or Steam
- **SteamCmd** (optional, for automatic updates) - [Download](https://developer.valvesoftware.com/wiki/SteamCMD)

### WreckfestWeb
- **WreckfestWeb** ([https://github.com/tkprocat/WreckfestWeb](https://github.com/tkprocat/WreckfestWeb)) works only with WreckfestController 1.x (`v1-final`).
  - 2.0 no longer sends webhooks; its live updates come from the SignalR hub described in [docs/API.md](docs/API.md#live-updates--hubsserver).

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

#### Network Configuration
```json
"Api": {
  "Key": "replace-with-a-long-random-secret",
  "AllowRemote": false,
  "TrustedProxies": [],
  "HttpPort": 5100,
  "HttpsPort": 5101
}
```

Every `/api/*` request needs credentials: either this `Key`, sent as the `X-Api-Key` header, or a signed-in web UI session. The key is optional and only needed by scripts; if it is empty, no key is accepted. Web accounts are created in the desktop app: when the API is enabled and no account exists, it offers a "Create admin account" dialog at startup, and the Configuration tab has the same button. The web UI itself is still to come, so tools that use the API today should keep using the key. See [docs/API.md](docs/API.md#authentication).

With `AllowRemote` set to `false` (the default), the API binds to `127.0.0.1`. Set it to `true` to bind to all network interfaces.

If a reverse proxy (for example HAProxy on OPNsense) terminates HTTPS in front of the controller, add its address to `TrustedProxies`, such as `["192.168.1.1"]`. The controller then sees each browser's real IP for the login rate limit, and knows the connection was HTTPS. Forwarded headers from any other address are ignored. See [docs/API.md](docs/API.md#behind-a-reverse-proxy).

`HttpPort` and `HttpsPort` default to 5100 and 5101. Give each instance its own pair when running several controllers on one Windows host, otherwise the second instance fails to bind. A value outside 1-65535 is ignored with a warning and the default is used.

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

Navigate to the extracted directory (or `bin/Release/net10.0-windows` if you built from source) and run:

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

## Live Updates

The controller pushes player, track, event and server notifications to connected
web clients through a SignalR hub at `/hubs/server`, on the same port as the API.
There is nothing to configure beyond enabling the API. See
[docs/API.md](docs/API.md#live-updates--hubsserver) for the messages.

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

## Security Considerations

⚠️ **Important Security Notes:**

1. **No Authentication** - This API currently has no built-in authentication. Do not expose it to the public internet without adding authentication or using a reverse proxy with auth.

2. **Local Network Only** - By default, configure to listen only on your local network.

3. **Firewall** - Use Windows Firewall to restrict access to trusted IPs only.

4. **HTTPS** - For production, configure proper SSL certificates instead of using the development certificate.

## License

See [LICENSE.txt](./LICENSE.txt) for license information.
