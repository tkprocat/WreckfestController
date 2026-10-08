# WreckfestController

.NET 10 WPF desktop app + hosted ASP.NET Core API for controlling a Wreckfest dedicated server.

See `docs/finding-rvas.md` before touching any hardcoded game offset.

Setup and configuration are in `INSTALL.md`, the HTTP API in `docs/API.md`, and the web
app's visual rules in `docs/design-system.md`.

## Build and test

```bash
dotnet build WreckfestController.csproj -c Debug
dotnet test
dotnet test --filter-class WreckfestController.Tests.Services.Tracking.PlayerTrackerTests
```

`dotnet build` also builds the injected C++ hook. The csproj shells out to full
MSBuild (located via `vswhere`) for `NativeHooks/WreckfestConsoleHook/WreckfestConsoleHook.vcxproj`,
because the .NET SDK does not ship the C++ targets - a plain `ProjectReference`
to a `.vcxproj` fails with `MSB4278` and takes the whole C# build down with it.
The step is skipped when the DLL is newer than its sources, and the build now
**fails** rather than silently shipping an app that cannot inject. Building
requires Visual Studio with the Desktop development with C++ workload.

MinHook is vendored under `NativeHooks/WreckfestConsoleHook/third_party/minhook`
so the build needs no network.

`dotnet build` also builds the Vue web app in `web/` (`BuildWebApp`): `npm ci` when the
lockfile changes, `npm run build` when a source changes, then `web/dist` is copied to
`wwwroot` next to the exe, where `ApiServer` serves it. It needs Node.js on PATH;
`-p:SkipWebBuild=true` skips it for C#-only work. `web/` uses Naive UI, not PrimeVue
(PrimeVue 5 left MIT): check a package's license before adding it.

The web app is typed against `web/src/api/openapi.json`. `OpenApiContractTests` fails when
it drifts from the API; `WFC_UPDATE_OPENAPI=1 dotnet test --filter-class
WreckfestController.Tests.Api.OpenApiContractTests` rewrites it, then `npm run gen:api` in
`web/` regenerates `schema.d.ts`. Commit both with any endpoint change.

**`npm test` (Vitest) cannot run from a path containing `#`** (such as a checkout under
`C#\`): Vitest drops everything after the `#`. `npm test` says so and stops. The
repository lives under `F:\Projects\CSharp\` for this reason; the web build and the C#
tests are unaffected either way.

### Never pass `--nologo` to `dotnet test`

The test project is **xunit.v3 on Microsoft.Testing.Platform** (`global.json` selects the
MTP runner). `--nologo` is a **VSTest-only** option. In MTP mode it is forwarded to the
test app as an unmatched token, and the run exits with code 5 (`InvalidCommandLine`)
*before discovering a single test*. The output is:

```
Zero tests ran
Test run completed with non-success exit code: 5
```

which looks exactly like a broken project rather than a bad flag. This has already cost
one agent its entire task — it committed unverified work believing tests could not run.
See https://github.com/dotnet/sdk/issues/55309.

If `dotnet test` misbehaves, these are equivalent and unaffected:

```bash
dotnet run --project WreckfestController.Tests/WreckfestController.Tests.csproj -c Debug
./WreckfestController.Tests/bin/Debug/net10.0-windows10.0.19041.0/WreckfestController.Tests.exe
```

xunit.v3 test projects are standalone executables, so `WreckfestController.Tests.csproj`
must keep `<OutputType>Exe</OutputType>`. Reverting that breaks the runner.

## Server I/O is hook-only

Nothing reads a log file or scrapes a console window. `NativeHooks/WreckfestConsoleHook`
is injected into the running game; it patches the game's `ConsolePrint` and forwards text
over a named pipe, sends commands through the game's own dispatcher, and exposes
`__hook_read` / `__hook_info` / `__hook_players` for module-relative memory reads.

Nothing works until the hook is injected. `ServerManager` injects it after every start and
restart, and reattaches on startup to the running server carrying this controller's
`-wfc_controller=<id>` marker (`ControllerInstance`, #201); Process Manager -> INJECT is
the manual fallback.

Joins, quits and privilege changes come from the game's server-event ring
(`Services/Hook/ServerEventReader.cs`), not from text. Chat comes from the hook too, as a
structured record carrying the sender and message separately; `Services/Hook/HookChatRecord.cs`
interprets it. Console text is never parsed for chat — a line that looks like a chat
command but arrived without a record is logged, not acted on. Prefer structured sources
over new regexes.

Race results come from the hook as well: when the results screen opens, its race watcher
reads every car from the game's memory and sends one `RACE` record, which
`Services/Hook/HookRaceRecord.cs` interprets and `ServerManager.RaceFinished` raises. The
layout is in `docs/finding-rvas.md` under "Race results".

The one deliberate exception is `GET /api/server/logfile`
(`ServerManager.GetLogFileContent`), which tails the server's log file from disk for
the web app's Server Control page. It is a read-only view of history — it answers with no hook
injected, and can return lines from before attachment. Nothing else reads it: no
tracker, roster or chat path is fed from the file. Do not add a second one.

## Conventions

- Hardcoded RVAs are tied to one Wreckfest build and are documented in
  `docs/finding-rvas.md`. Confirm an offset by changing state and re-reading, never by
  inference alone.
- Chat messages sent to the game must stay under 127 characters.
- `/message` is used for chat; non-chat server modifiers are sent without it.
