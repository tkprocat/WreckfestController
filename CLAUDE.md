# WreckfestController

.NET 10 WPF desktop app + hosted ASP.NET Core API for controlling a Wreckfest dedicated server.

See `CLAUDE_GUIDE.md` for architecture, API endpoints and component detail.
See `docs/finding-rvas.md` before touching any hardcoded game offset.

## Build and test

```bash
dotnet build WreckfestController.csproj -c Debug
dotnet test
dotnet test --filter-class WreckfestController.Tests.Services.PlayerTrackerTests
```

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

Nothing works until the hook is injected (Process Manager -> INJECT).

Joins, quits and privilege changes come from the game's server-event ring
(`Services/ServerEventReader.cs`), not from text. Chat still arrives as console text and
is parsed in `ServerManager.ProcessChatCommandLine` — replacing that is tracked work, so
prefer structured sources over new regexes.

## Conventions

- Hardcoded RVAs are tied to one Wreckfest build and are documented in
  `docs/finding-rvas.md`. Confirm an offset by changing state and re-reading, never by
  inference alone.
- Chat messages sent to the game must stay under 127 characters.
- `/message` is used for chat; non-chat server modifiers are sent without it.
