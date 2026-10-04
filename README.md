# Wreckfest Server Controller

A Windows desktop app that runs a Wreckfest dedicated server, with a built-in web admin
site for managing it from a browser.

The desktop app starts, stops and updates the server and injects a small hook into it.
The hook carries the console, chat, the player list and race results. The same process
hosts a web site and an HTTP API: a public home page showing who is on and what is
running, and an admin area for the server's config, track rotation, cups, the track
catalogue and user accounts.

Version 2.0 replaces the separate Laravel site, WreckfestWeb, which works only with
1.x (`v1-final`).

## Features

- **Server control**: start, stop (graceful or forced), restart and update through
  SteamCMD, from the desktop app or the web.
- **Hook-based I/O**: console output and commands go through a hook injected into the
  game, never through log files or console scraping. Joins, quits, chat and race results
  come from the game's own data structures.
- **Web admin**: dashboard, server control with a live console, server config, track
  rotation, saved track collections, a track catalogue with tags, cups, users and
  settings.
- **Public home page**: the server's name, status, players, the current rotation and
  upcoming cups, with live updates.
- **Cups**: a rotation plus its rules (cup points, grid order, server settings), applied
  through a smart restart at a set time, once or on a daily or weekly repeat. Players
  get a countdown in chat.
- **In-game track voting**: players pick tracks with chat commands, either by vote or
  directly with a cooldown.
- **Live updates**: a SignalR hub pushes players, track changes, server events and the
  console to the browser.
- **Accounts**: sign-in with a username or email and a password, a lockout after
  repeated failures, and an API key for scripts.
- **HTTPS**: a certificate from the Windows store or a file, with automatic reloads on
  renewal, or a reverse proxy in front.

## Installation

See [INSTALL.md](INSTALL.md) for the requirements, first start, creating the first admin
account and putting the web site behind a reverse proxy.

## In-game chat commands

`!help` lists the commands for the current mode.

| Command | Voting mode | Direct mode |
| --- | --- | --- |
| `!track <trackId> [laps]` (or `!vote`) | starts a vote | changes the track now |
| `!laps <laps>` | votes on the laps only | changes the laps now |
| `!yes` / `!no` | votes on the active vote | - |
| `!search <text>`, then `!more` | finds track ids | finds track ids |
| `!lucky` (or `!ifeellucky`) | votes on a random track | picks a random track |

Players can pick only tracks the catalogue allows for voting (the web Tracks page), up
to the maximum number of laps. In direct mode a change starts a cooldown, which admins
bypass.

Track and lap changes are refused in both modes while the server's event loop is on,
since the loop picks the tracks; players are told so in chat. Turn the loop off to let
players choose.

Admins and moderators have two more commands, hidden from `!help` and ignored for other
players:

- `!eventloop` shows whether the event loop is on; `!eventloop on` and `!eventloop off`
  switch it.
- `!voting on` switches to voting mode, and `!voting off` to direct changes, cancelling
  an active vote. This lasts until the controller restarts or the voting settings are
  saved.

When the vote timer ends, only votes from players still on the server count: more yes
than no passes, and ties or no votes fail. The player who started the vote votes yes
automatically. A vote finishes early once a majority of the human players online agree.

## Documentation

| Document | Contents |
| --- | --- |
| [INSTALL.md](INSTALL.md) | Requirements, setup, the first admin account, reverse proxies, file locations |
| [docs/API.md](docs/API.md) | The HTTP API and the live-update hub |
| [docs/https.md](docs/https.md) | Serving the web site over HTTPS |
| [docs/LetsEncrypt.md](docs/LetsEncrypt.md) | A free certificate with win-acme |
| [docs/finding-rvas.md](docs/finding-rvas.md) | The game memory offsets the hook uses, and how to find them again for a new game build |

## Building from source

Requirements:

- Windows 10 or 11
- .NET 10 SDK
- Visual Studio 2022 or later with the **Desktop development with C++** workload, for
  the hook (`NativeHooks/WreckfestConsoleHook`)
- Node.js LTS, for the web app in `web/`

```bash
dotnet build WreckfestController.csproj -c Release
dotnet test
```

One `dotnet build` builds everything:

- **The C++ hook.** The build finds Visual Studio's MSBuild itself and fails if the hook
  cannot be built. It skips the hook when the DLL is newer than its sources. MinHook is
  vendored, so no download is needed.
- **The web app.** It runs `npm ci` when the lockfile changes and `npm run build` when a
  source changes, then copies the result to `wwwroot` next to the exe.
  `-p:SkipWebBuild=true` skips this for C#-only work; the controller then serves the web
  app from the last build, or only the API if there is none.

To publish a single-file build:

```bash
dotnet publish WreckfestController.csproj -c Release
```

Copy everything in the publish folder: the exe, `appsettings.json`, the hook DLL and
`wwwroot`.

### Web app

The web app (`web/`) is Vue 3 with Naive UI. Its API types are generated from
`web/src/api/openapi.json`, and a test fails when that file no longer matches the API.
After changing an endpoint:

```bash
WFC_UPDATE_OPENAPI=1 dotnet test --filter-class WreckfestController.Tests.Api.OpenApiContractTests
cd web && npm run gen:api
```

Commit both files. For live development, `npm run dev` in `web/` serves the app with hot
reload and forwards `/api` and `/hubs` to a controller on port 5100. `npm test` runs the
web tests. It cannot run from a path containing `#`.

### Tests

The C# tests use xunit.v3 on Microsoft.Testing.Platform. Do not pass `--nologo` to
`dotnet test`: on this runner it ends the run with exit code 5 before any test runs.

```bash
dotnet test
dotnet test --filter-class WreckfestController.Tests.Services.Tracking.PlayerTrackerTests
```

## License

MIT; see [LICENSE.txt](LICENSE.txt).
