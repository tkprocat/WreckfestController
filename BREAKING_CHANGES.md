# Breaking Changes

This document tracks breaking changes between versions that require user action.

## Version: WPF Desktop UI Migration (feature/readconsole)

### 🚨 Console Application → WPF Desktop Application

**What Changed:**
The application has been converted from a console application to a WPF desktop application with a graphical user interface.

**Why:**
To enable `ReadConsole()` functionality for reading Wreckfest server console output in real-time. `ReadConsole()` requires a Windows console handle, which can only be obtained by attaching to a process that has a console window. The WPF GUI allows us to:
1. Scan for running Wreckfest server processes
2. Attach to their console to read output in real-time
3. Parse console output for player joins/leaves and track changes
4. Provide a visual interface for server management

**Impact:**
- The application now launches a GUI window instead of running as a console application
- Previous command-line workflows are replaced by the GUI
- The application must be run in desktop mode (not headless/service mode for console monitoring features)

**Migration Steps:**
1. Launch the application - a WPF window will open
2. Use the **Process Manager** tab to scan for running Wreckfest servers
3. Click **Attach** to connect to a server's console for monitoring
4. Configure settings via the **Configuration** tab (replaces manual appsettings.json editing)

---

### 🔄 Configuration Changes

**What Changed:**
- Configuration section renamed: `Laravel` → `WreckfestWeb`
- New hybrid configuration system with `user-settings.json`
- Service renamed: `LaravelWebhookService` → `WreckfestWebWebhookService`

**Why:**
- "Laravel" refers to the framework, not the project. The project is called "WreckfestWeb"
- User-friendly configuration via GUI requires separating user settings from application defaults
- Multi-instance support requires configurable settings location

**Migration Steps:**

#### 1. Update appsettings.json

**Before:**
```json
{
  "Laravel": {
    "WebhookBaseUrl": "http://localhost:8000/api/webhooks"
  }
}
```

**After:**
```json
{
  "WreckfestWeb": {
    "WebhookBaseUrl": "http://localhost:8000/api/webhooks"
  }
}
```

#### 2. Configure via GUI (Recommended)

Instead of manually editing `appsettings.json`, use the **Configuration** tab:
1. Open WreckfestController
2. Go to **Configuration** tab
3. Set all paths and URLs
4. Click **Save Settings**

Settings will be saved to `%LocalAppData%\WreckfestController\user-settings.json` by default.

#### 3. Multi-Instance Setup (Optional)

If running multiple WreckfestController instances, configure custom settings paths:

**appsettings.json (Instance 1):**
```json
{
  "UserSettingsPath": "C:\\WreckfestController-Server1\\user-settings.json",
  "WreckfestWeb": {
    "WebhookBaseUrl": "http://localhost:8000/api/webhooks"
  },
  "Kestrel": {
    "Urls": "http://0.0.0.0:5100;https://0.0.0.0:5101"
  }
}
```

**appsettings.json (Instance 2):**
```json
{
  "UserSettingsPath": "C:\\WreckfestController-Server2\\user-settings.json",
  "WreckfestWeb": {
    "WebhookBaseUrl": "http://localhost:8000/api/webhooks"
  },
  "Kestrel": {
    "Urls": "http://0.0.0.0:5200;https://0.0.0.0:5201"
  }
}
```

---

### 📋 Launch Profile Changes

**What Changed:**
Launch profiles converted from web API profiles to desktop application profiles.

**Before:**
- `http` profile (launched browser to Swagger)
- `https` profile (launched browser to Swagger)

**After:**
- `WreckfestController` profile (launches WPF desktop app)
- `WreckfestController (Production)` profile (launches in production mode)

**Impact:**
- No browser window opens on launch
- F5 in Visual Studio now launches the desktop GUI
- The embedded REST API still starts in the background

---

## Questions?

If you encounter issues with these breaking changes:
1. Check the [INSTALL.md](INSTALL.md) for setup instructions
2. Review the [CLAUDE_GUIDE.md](CLAUDE_GUIDE.md) for architecture details
3. Open an issue on GitHub with details about your configuration
