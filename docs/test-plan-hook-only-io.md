# Test Plan: Hook-Only I/O

Covers the removal of console read/write (`ConsoleMonitor`, `ConsoleWriter`,
`ConfiguredServerInputWriter`) and the log-file output mode, leaving the injected
DLL hook as the only input and output path. Also covers the hook's new
tokenization echo and PE layout guard.

Injection remains **manual** (Process Manager -> INJECT). Before injecting, the
controller can start/stop/attach to a server but has no console I/O. That is
expected.

## Already verified - do not retest

| Check | Result |
| --- | --- |
| Automated suite | 165/165 pass |
| Main project build | clean |
| Native hook build (Debug + Release) | clean, no warnings |
| All 5 RVAs vs. real `Wreckfest_x64.exe` | PASS - `.text` for code, `.data` for data |
| Stale `user-settings.json` keys | Safe - unknown keys skipped on deserialize |

Observed `SizeOfImage` for the current build: **`0x0B43A000`** (188,981,248 bytes).

## Phase 1 - Cold start, no injection

Confirms the accepted degraded state fails cleanly rather than crashing.

1. Launch controller. Configuration tab shows the "Server I/O" note; no Output
   Mode or Input Mode dropdowns.
2. Server Control -> START. Process starts, status Running, PID populated.
3. Console pane shows exactly one line:
   `[Controller] Use Process Manager -> INJECT to start output capture.`
4. Send any command (e.g. `list`). Expect a clean failure
   (`Injected hook input failed: ...` or `... timed out on <pipe>`).
   Must not throw, freeze the UI, or silently no-op.
5. `GET /api/server/status` correct. `GET /api/server/logfile?lines=50` still
   returns log content - this endpoint was deliberately kept.
6. STOP. Process tree killed, status Stopped.

Fail signal: unhandled exception, UI freeze, or a command reporting success with
no hook attached.

## Phase 2 - Injection and core I/O

7. Server running -> Process Manager -> select PID -> INJECT. Success dialog;
   `ProcessHookOutput` auto-checks.
8. Hook log shows:
   `WreckfestConsoleHook module layout status=1 imageSize=0x0B43A000 expected=0x00000000`
   `status=1` is the pass code. Anything else means the guard rejected the build
   and nothing will dispatch.
9. Console pane streams live server output.
10. Players join/leave -> Players tab updates; bots excluded from human counts.

## Phase 3 - Command surface and tokenization echo

Each command now returns the hook's own split. Verify the echo, then verify the
command actually took effect in-game - the echo proves tokenization, not
application.

| Command | Expected echo |
| --- | --- |
| `list` | `OK dispatched command=list argument=` |
| `?` | `command=? argument=` |
| `track=sandpit_derby_2` | `command=track argument=sandpit_derby_2` |
| `laps=3` | `command=laps argument=3` |
| `/message hello world` | `command=/message argument=hello world` |
| `/kick 3` | `command=/kick argument=3` |

11. Run each command above.
12. The `=` cases are critical: `command=track`, not `command=track=sandpit...`.
    This is the regression b4b8f2c fixed.
13. Edge case: `/message =foo` arrives as argument `foo` - the leading `=` is
    consumed by the skip loop. Known, cosmetic.

## Phase 4 - Behavioral changes (highest risk)

Not covered by the automated suite.

14. **Attach path.** Start a server outside the controller -> ATTACH -> INJECT.
    Attach succeeds and output flows after injection. `AttachToExistingProcess`
    now calls `StartOutputMonitoring()` instead of unconditionally starting
    console monitoring.

15. **Restart via command.** `/restart` spawns a new PID, so the hook dies with
    the old process. Restart detection scans `_outputBuffer`, which the dead hook
    no longer fills, so it will time out at 30s, log
    `Restart completion not detected via logs within timeout, proceeding with PID detection anyway`,
    then continue via PID diffing.
    This is not a regression - the old code also always timed out in hook mode
    because `_lastLogFilePosition` never advanced. The timeout is non-fatal.
    Expect: restart succeeds, new PID tracked, ~32s elapsed.
    You must re-INJECT into the new PID to regain output.
    Fail signal: `No new process detected`, or the new PID is not tracked.

16. **`ProcessConsoleHookOutput` toggle.** Uncheck mid-session: output stops
    feeding trackers. Re-check: resumes.

## Phase 5 - Version guard negative test

The guard is unproven until it has been seen to reject something.

17. Temporarily set `ConsolePrintRva` to an out-of-range value (e.g.
    `0x7F000000`), rebuild, inject. Expect `status=3` (RvaOutOfRange),
    `WreckfestConsoleHook aborted: offsets do not match this Wreckfest build`,
    no hook install, commands refused with
    `refused dispatch: module layout not validated`, and `__hook_players`
    returning `ERR player snapshot module layout not validated`.
18. Revert the RVA, rebuild, confirm `status=1`.
19. **Decide on pinning.** Setting `ExpectedImageSize = 0x0B43A000` gives
    exact-build detection: any Wreckfest patch blocks injection instead of
    running on shifted offsets. Cost: anyone on a different Wreckfest build is
    also blocked, so leave it `0` if the DLL ships on the releases page. Pin it
    for a local-only server. Currently `0`.

## Phase 6 - Voting end to end

Exercises `track=`, `laps=` and `/message` together.

20. With 2+ humans: `!vote` -> `!confirm <n>` -> `!yes` / `!no`. Majority uses
    human players only; bots excluded.
21. Single human: immediate-pass path still fires.
22. On pass, `track=` then `laps=` dispatch in order. They are two separate
    calls, so a brief half-applied window is expected. Track changes in-game.
23. `!config` prints `Config: hookConnected=yes, outputPrimary=yes`. The
    `input=` / `output=` fields are gone.
24. Chat messages stay under Wreckfest's 127-character limit.

## Phase 7 - Settings migration

25. Existing `user-settings.json` contains `UseConsoleMonitoring` and
    `InputMode`. Configuration tab loads without error; values populate.
26. Save. Both stale keys are dropped; `OutputMode` forced to `InjectedHook`.
27. Delete `user-settings.json`, restart. Defaults regenerate with
    `OutputMode: InjectedHook` only.

## Regression watch list

- `GET /api/server/logfile` still works - WreckfestWeb's log viewer depends on it.
- Webhooks `server-started`, `server-stopped`, `server-restarted`,
  `server-attached` all still fire.
- `SmartRestartService` countdown messages use the same `/message` dispatch path
  as voting, but fire unattended during a scheduled event.
- Multi-instance: worth one pass with two servers. `ConsoleWriter.FindConsoleWindow()`
  used to grab any console window; the hook is per-process by pipe name, so this
  should now be correct where it previously was not.

## Suggested order

Phases 1-3 give confidence in about an hour without a populated server. Phase 4
deserves the most attention - those changes are unproven live. Phase 6 needs real
players, so batch it with a session already running.
