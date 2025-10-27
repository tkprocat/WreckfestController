# WreckfestController - Project Structure

This solution contains the main WreckfestController API and several utility applications.

## Projects

### Main Application
- **WreckfestController** - ASP.NET Core Web API for managing Wreckfest Dedicated Server
  - REST API endpoints for server control (start/stop/restart/update)
  - OCR-based player tracking
  - Server log monitoring
  - Player list management
  - Configuration management

### Utility Applications
- **AddBotsApp** - Console app to add bots to running Wreckfest server
- **TestOcrApp** - Test harness for OCR functionality
- **EvaluateOcrApp** - OCR accuracy evaluation tool (compares OCR results vs ground truth)

### Tests
- **WreckfestController.Tests** - Unit tests for main application

## Building the Solution

```bash
# Build everything
dotnet build WreckfestController.sln

# Build specific project
dotnet build WreckfestController.csproj
dotnet build AddBotsApp/AddBotsApp.csproj
dotnet build TestOcrApp/TestOcrApp.csproj
dotnet build EvaluateOcrApp/EvaluateOcrApp.csproj
```

## Running Applications

### Main API
```bash
# Start the API server
dotnet run --project WreckfestController.csproj

# API will be available at:
# - HTTP: http://localhost:5100
# - HTTPS: https://localhost:5101
```

### Utility Apps (while main API is running)

**Add Bots:**
```bash
dotnet run --project AddBotsApp/AddBotsApp.csproj
```

**Test OCR:**
```bash
dotnet run --project TestOcrApp/TestOcrApp.csproj
```

**Evaluate OCR Accuracy:**
```bash
# Build without triggering main project rebuild
dotnet build EvaluateOcrApp/EvaluateOcrApp.csproj --no-dependencies

# Run without rebuild
dotnet run --project EvaluateOcrApp/EvaluateOcrApp.csproj --no-build
```

## API Endpoints

### Server Management
- `POST /api/server/start` - Start Wreckfest server
- `POST /api/server/stop` - Stop Wreckfest server
- `POST /api/server/restart` - Restart Wreckfest server
- `POST /api/server/update` - Update server via SteamCmd and restart
- `POST /api/server/command` - Send command to server console (e.g., `/bot`, `list`, `kick 5`)
- `GET /api/server/status` - Get server status
- `GET /api/server/players` - Get current player list
- `GET /api/server/logfile?lines=100` - Get server log file content
- `POST /api/server/attach/{pid}` - Attach to existing server process

### Configuration
- `GET /api/config` - Get basic server configuration

## Common API Examples

### Add Bots (Smart Way)

**Using the included scripts (recommended):**

```powershell
# Fill to 24 players (default)
.\add-bots.ps1

# Fill to custom target
.\add-bots.ps1 -Target 20
```

Or on Linux/Mac:
```bash
./add-bots.sh 24
```

The scripts will:
1. Check current player count
2. Calculate bots needed
3. Only add the necessary amount
4. Verify final count

**Manual approach:**
```bash
# 1. Check current players
curl http://localhost:5100/api/server/players

# 2. Calculate bots needed (e.g., current=5, target=24, need 19)

# 3. Add specific number of bots
for i in {1..19}; do
  curl -X POST http://localhost:5100/api/server/command \
    -H "Content-Type: application/json" \
    -d '{"command": "/bot"}'
  sleep 0.5
done
```

### Kick a Player
```bash
# First get player list to see IDs
curl http://localhost:5100/api/server/players

# Then kick by ID
curl -X POST http://localhost:5100/api/server/command \
  -H "Content-Type: application/json" \
  -d '{"command": "/kick 5"}'
```

### Get Player List
```bash
curl http://localhost:5100/api/server/players
```

### Send Any Console Command
```bash
curl -X POST http://localhost:5100/api/server/command \
  -H "Content-Type: application/json" \
  -d '{"command": "list"}'
```

## OCR Training Data

Training data for Tesseract OCR is located in `bin/Debug/net8.0/`:
- `ocr_debug_*.png` - Console screenshots
- `ocr_debug_*.gt.txt` - Ground truth transcriptions
- `TESSERACT_TRAINING_README.md` - Training instructions
- `TRAINING_DATA_SUMMARY.md` - Training data overview

### Tesseract Models
- Location: `tessdata/`
- Current model: `eng.traineddata` (tessdata_best - 15MB)
- To use custom trained model: Replace `eng.traineddata` or add `wreckfest.traineddata`

## Configuration

Main configuration file: `appsettings.json`

Key settings:
```json
{
  "WreckfestServer": {
    "ServerPath": "Path to Wreckfest_x64.exe",
    "ServerArguments": "-s server_config=server_config.cfg",
    "WorkingDirectory": "Server install directory",
    "EnableOcrPlayerTracking": true
  },
  "SteamCmd": {
    "SteamCmdPath": "C:\\steamcmd\\steamcmd.exe",
    "WreckfestAppId": "361580",
    "InstallDirectory": "Server install directory"
  }
}
```

## Development Notes

### File Locking
When the main API is running, you cannot rebuild `WreckfestController.csproj` because the executable is locked. Use these approaches:

1. **Stop the API first:**
   ```bash
   # Kill the running API
   taskkill /IM WreckfestController.exe /F

   # Then rebuild
   dotnet build
   ```

2. **Build utility apps without dependencies:**
   ```bash
   dotnet build AddBotsApp/AddBotsApp.csproj --no-dependencies
   ```

3. **Run already-built utilities:**
   ```bash
   dotnet run --project AddBotsApp/AddBotsApp.csproj --no-build
   ```

### Project References
All utility apps reference `WreckfestController.csproj` to access shared services like:
- `ConsoleOcr` - OCR engine
- `ConsoleWriter` - Send commands to console
- `ConsoleReader` - Capture console screenshots
- `ServerManager` - Server management (tests only)

### Adding New Utility Apps

1. Create new console project:
   ```bash
   dotnet new console -n MyUtilityApp -o MyUtilityApp
   ```

2. Add reference to main project:
   ```bash
   cd MyUtilityApp
   dotnet add reference ../WreckfestController.csproj
   ```

3. Add to solution:
   ```bash
   dotnet sln add MyUtilityApp/MyUtilityApp.csproj
   ```

4. Exclude from main project build in `WreckfestController.csproj`:
   ```xml
   <Compile Remove="MyUtilityApp\**" />
   <Content Remove="MyUtilityApp\**" />
   <EmbeddedResource Remove="MyUtilityApp\**" />
   <None Remove="MyUtilityApp\**" />
   ```

## Testing

```bash
# Run unit tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Publishing

```bash
# Publish as single file (framework-dependent)
dotnet publish -c Release -r win-x64 --self-contained false

# Publish as fully self-contained
dotnet publish -c Release -r win-x64 --self-contained true
```
