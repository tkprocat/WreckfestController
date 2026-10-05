# Finding Wreckfest RVAs after a game update

Every hardcoded offset in this project is tied to one Wreckfest build. A patch
moves them, and the symptoms are quiet rather than loud: the hook refuses to
install, the event loop reads as unavailable, or a command dispatches into the
wrong function. This is the method used to find them, written so it can be
repeated - by a person or by handing this file to an AI along with a fresh
Ghidra export.

Confirmed against **Wreckfest 1.308438** (`SizeOfImage 0x0B43A000`, linker
timestamp `0x6509731D`).

## The offsets in use

`NativeHooks/WreckfestConsoleHook/WreckfestConsoleHook.cpp`:

| Constant | RVA | What it is |
| --- | --- | --- |
| `ConsolePrintRva` | `0x00F1A050` | low-level console print; the hook patches this |
| `CommandDispatcherRva` | `0x00F18B30` | console command dispatcher, called with a `CommandTokens` struct |
| `RegistryLookupRva` | `0x00E37140` | registry namespace lookup, used to reach the SERVER object |
| `RegistryTablePtrRva` | `0x0127E7F8` | pointer to the registry table |
| `ServerNamespaceTagRva` | `0x065E6308` | tag for the SERVER namespace |
| `ChatHandlerRva` | `0x0038FC10` | unified input handler for the server console and player chat; the hook patches this too |

`Services/Voting/VotingService.cs`:

| Constant | RVA | What it is |
| --- | --- | --- |
| `RvaEventLoopCount` | `0x01857630` | int32, number of `el_add` entries |
| `RvaEventLoopIndex` | `0x0122B270` | int32, current rotation entry; `-1` when the loop is off |

Whether the server is racing comes from the session state below, through the hook's
`__hook_session`.

### Session state

The SERVER object (the registry object `ServerNamespaceTagRva` reaches) holds the
session state machine: an int at `+0x4`, and a timer in ms at `+0x10` that counts down
in the states that have one and reads `-100000` when idle.

| `+0x4` | State | Set by | Timer |
| --- | --- | --- | --- |
| 0 | lobby | `Countdown canceled.`, and after the results | `-100000`; a short countdown (3000) on the way out of the results |
| 1 | countdown | `Event starting in %d seconds...` | from `dedicated.ddst +0xA0`; cut to 3000 when everyone is ready |
| 2 | racing | the `Event started!` branch of `machine_code_server` (`0x140394D20`) | `-100000` |
| 3 | results screen | the `Event ended!` branch | at most 20000; it ends early when the humans move on |
| 4 | handover to the next event, event loop on | `FUN_140390650` calling `FUN_140394b70` | 15000 |

Confirmed live on 2026-10-04 (Wreckfest 1.308438), polling `__hook_session` every
second through two races with the event loop on. Each state held steady, with no
flicker, and changed exactly at the matching server line:

| Time | State | Counter | Ended | Timer | Server log |
| --- | --- | --- | --- | --- | --- |
| 22:16:40 | 0 | 0 | 0 | -100000 | lobby |
| 22:18:50 | 1 | 0 | 0 | 2808 | `Event starting in 30 seconds...` |
| 22:18:54 | 2 | 1 | 0 | -100000 | `Event started!` |
| 22:19:37 | 3 | 1 | 1 | 19168 | `Event ended!` |
| 22:19:56 | 4 | 1 | 0 | 14760 | |
| 22:20:06 | 0 | 1 | 0 | -100000 | lobby |
| 22:21:02 | 2 | 2 | 0 | -100000 | `Event started!`, second race |
| 22:21:32 | 3 | 2 | 1 | 19984 | `Event ended!` |
| 22:21:39 | 4 | 2 | 0 | 14440 | |
| 22:21:54 | 0 | 2 | 0 | 2392 | `Changing to next event in the loop...` |

With `SuppressCommandsDuringRace` on, `!help` was refused during the second race (the
case the old check got wrong) and answered in the lobby after it.

The game groups them itself: `FUN_1402cd250(state)` is true for 0, 1 and 4. The race
end check and the idle-car kicker both test `state == 2`, and `Changing to next event
in the loop...` runs only when `state != 2`. `VotingService` treats 2 as racing and
every other value, or a failed read, as not racing.

SERVER is on the heap, out of `__hook_read`'s module-relative reach, so the hook
resolves it per call: `__hook_session` answers `OK session state=2 timer=-100000
counter=3 ended=0`, with the two globals below alongside.

**Two globals that were mistaken for this state** (issue #189):

- `0x19146E0` is a parity bit. The server tick (`FUN_140392bd0`) flips it with
  `e0 = (e0 - 1) & 1` and uses it to pick between two packet ids for car state, so it
  flickers in every state.
- `0x19146EC` is the event counter, incremented in the `Event started!` branch. It read
  `01` while racing only in the first race after boot.

Everything else the project needs is reached through the command dispatcher, on
purpose: one offset buys every console command, so each extra one is recurring
maintenance.

## Chat handler and the input ring

Found during the live chat investigation, confirmed against **Wreckfest
1.308438** (`SizeOfImage 0x0B43A000`); the module base was observed at
`0x7FF726C20000` across four launches.

### Chat handler

| Constant | RVA | What it is |
| --- | --- | --- |
| `ChatHandlerRva` | `0x0038FC10` | `FUN_14038fc10(int ringIndex, char* text, void* serverObject)` - unified input handler for server console *and* player chat |
| chat print wrapper | `0x000F1AB7A` | calls `ConsolePrint`; note the command path uses a *different* wrapper at `0x000F1B7CB` |

It formats its console line with `"^8%s%s^0%s"`, which matches the captured
output `^9* 21:12:07^0 ^8Procat: ^0Hello world!`.

Messages beginning `/` break out to the command dispatcher *before* formatting.
`!` messages do **not**, which is why the controller's chat commands are visible
as ordinary chat.

### Input ring array

| Constant | RVA | What it is |
| --- | --- | --- |
| `RvaInputRingBase` | `0x19149A0` | start of the array; 24 entries, stride `0x1010` |
| - | `+0x00` / `+0x08` | cumulative byte cursor (both updated, kept equal) |
| - | `+0x10` | 4096-byte data window, masked `& 0xfff` |

The layout self-checks: `0x19149A0 + 24 * 0x1010 = 0x192CB20`, and the
server-event ring cursor already documented at `0x192CB28` begins 8 bytes later.
24 is `max_players`.

Behaviour, verified live:

- Messages **accumulate**, newline-delimited, NUL-padded past the cursor.
- The cursor is cumulative and counts the trailing newline (`Hello` plus a
  newline -> 6; that plus a 127-character message and another newline -> 134).
- The longest accepted message is **127 characters**, matching the limit in
  `docs/test-plan-hook-only-io.md`.
- Content is stored verbatim - no truncation or transformation.
- A reader must trust the cursor rather than scanning for NULs, because stale
  bytes persist past it after a wrap.

**The index space is not the player table's.** Entry 0 is the server console
(observed holding a newline-terminated `/bot` five times). A single human
reported as `slot=1` by the hook (player-table index 0) used ring index **10**, and kept index 10 across
a full server restart, so it is not connection order either. This mapping is
**unresolved**. It is also not resolvable from outside the process: the player
table is a heap pointer and `__hook_read` is deliberately bounded to module
memory. That is why the structured chat work hooks the handler - which computes
the player-table index internally - rather than polling this ring.

### The handler's `char* text` cannot be trusted

`param_2` does **not** point at the start of the message. It arrives offset by
`length & ~7`, so reading a C string from it yields only the final partial 8-byte
block - and nothing at all when the length is an exact multiple of 8, because the
pointer then lands on the NUL terminator.

Measured live against 1.308438, seven for seven, with the last two rows predicted
before being typed:

| Typed | len | `len & ~7` | Readable through `text` |
| --- | ---: | ---: | --- |
| `!help` | 5 | 0 | `!help` |
| `!lucky` | 6 | 0 | `!lucky` |
| `!search tunnel` | 14 | 8 | `tunnel` |
| `Hello world` | 11 | 8 | `rld` |
| `!track tunnel_s01 2` | 19 | 16 | `1 2` |
| `abcdefgh` | 8 | 8 | *(empty)* |
| `abcdefghijklmnop` | 16 | 16 | *(empty)* |

Whether the argument is a mis-derived signature or a pointer the game advances by
whole words is unresolved, and did not need resolving: the input ring above holds
the message whole, so the hook reads it from there and keeps the `text` fragment
only as a check. The fragment is a suffix of the real message by construction, so
a ring read that disagrees with it is rejected.

### The ring is appended *after* the handler returns

Timing matters and is not guessable - it cost two wrong attempts before being
measured. At handler entry, and still at the moment the game prints the formatted
console line, the message is **not** in the ring: the cursor is unchanged and
still ends on the previous message. It is appended by the time the handler
returns.

Confirmed by instrumenting the rejection path and typing two messages in
sequence:

```
!search tunnel  ->  cursor=0   read=FAILED  ring=[]
!help           ->  cursor=15  read=ok      ring=[!search tunnel]
```

`15` is `"!search tunnel"` plus its newline - the *previous* message. So the hook
emits its record after calling the original handler, and holds the console line
back until then so the record still reaches the controller first.

### Player struct, cross-confirmed

The decompiled chat handler walks `serverObject + 0x30` -> player table, stride
`0x138`, name at `+0x48` - byte-for-byte identical to `ReadPlayersNoThrow` in
`NativeHooks/WreckfestConsoleHook/WreckfestConsoleHook.cpp`. Two independent
sources agree, so `param_3` is the SERVER object. The hook additionally uses
`+0xA6` status, `+0x82` flags, `+0xA8` ping.

## Race results

Found and confirmed live on 2026-10-02 against **Wreckfest 1.308438**: four races were
checked row by row against the in-game results screen. The hook's race watcher
(`RaceWatcherThread`) uses these, and `HookRaceRecord` interprets what it sends.

### Registry objects

Reached with the same lookup as SERVER (`RegistryLookupRva`), each with its own tag:

| Constant | Tag RVA | Object |
| --- | --- | --- |
| `GameplayRuleDataTagRva` | `0x065E6278` | `gameplay_rule_data`: the game's `pRuleData` |
| `EventSettingsTagRva` | `0x065E6890` | `event_settings`: the current event |
| `RacePositionsDataTagRva` | `0x065E6224` | `race_positions_data`: diagnostic dump only |

`event_settings`: `+0x38` game mode (int), `+0xB0` track id (`char*`, the string the
server prints in `Current track loaded! (%s)`), `+0x108` laps (int).

### Car records

`pRuleData->cars` is a block at the head of `gameplay_rule_data`: pointer at `+0x00`,
count at `+0x08` (24), heading at `+0x10`. The heading's type records the element size
at `+0x08`, which reads `0x110`, so a moved structure is caught before it is trusted.

**Car record *i* belongs to player-table slot *i*.** The server tick (`FUN_140392bd0`)
advances a car pointer (`+0x110`) and a player pointer (`+0x138`) in the same loop, and
a live `__hook_players` listed the same 11 names in the same order, bots included.

| Offset | Field | Evidence |
| --- | --- | --- |
| `+0x20` | flags: `0x01` finished, `0x10` DNF, `0x40` crossed the line | `0x01`/`0x40` live; `0x10` from the decompile's debug print |
| `+0x24` | position, 0-based byte; `0xFF` unplaced | live, 4 races |
| `+0x25` | current lap (2 after one completed lap) | live |
| `+0x40` | best lap, ms | live |
| `+0x44` | finish time, ms; 0 for a projected bot | live |
| `+0x50` | class index: 0 A, 1 B, 2 C | live, all rows |
| `+0x54` | performance rating | live, all rows |
| `+0x68` | vehicle object pointer | live |
| `+0x88` | name, `char*`, with colour codes (`^2*^0eRacer` is bot eRacer) | live |
| `+0x90` | race time, ms | live |
| `+0xEC` | cup points, cumulative over the session (read as `u16`) | live: 30 then 60 |

Vehicle object: `+0x08` localisation key (`VEHICLE_NAME_2244970999_13`), `+0x18`
display name (`Sunrise Super`). Two bots in the same model share the key, which is what
ruled out per-car fields. The car model is not in the car record itself: every offset
was tested against the screen's car column and none matched.

Player record (`0x138`, already used for `__hook_players`): `+0x100` Steam ID (`u64`, the
value the server prints in `Player %s (%llu) disconnected.`), and flag `0x08` at `+0x82`
marks a bot (the game tests the same bit).

### When to read them

`0x19146E8` rises to 1 in the `Event ended!` branch of the session state machine
(`machine_code_server`, which also sets SERVER `+0x4` to 3), which runs when the server tick finds **no connected human still
driving**. Bots do not hold it open. At that moment the game simulates the remaining laps
of every bot still on track and gives each a finishing time, flags `0x01` without `0x40`,
and `+0x44` 0: `HookRaceRecord` calls these *projected*. About 20 s later the server
returns to the lobby and the records are reset.

`0x19146EC` increments at every `Event started!`; the watcher uses it to time the start.

Still open: the DNF flag has not been seen live, and no race yet had a bot cross the line
ahead of the last human, which would show whether `0x40` marks every real finish or only a
human's.

## Tooling

- **`search-decompiled.ps1`** - parallel search of the Ghidra export. It is ~42k
  single-function `.c` files, enough that a plain recursive grep times out; a
  full pass takes about five minutes. `-Rva 0x00F18B30` jumps straight to a
  function's file, because Ghidra names each file after the function's absolute
  address (image base `0x140000000`).
- **`x64dbg-api.ps1`** - talks to the MCPx64dbg HTTP plugin for live inspection.
- **`__hook_read <rvaHex> <size>`** - reads module-relative memory through the
  injected hook, with no debugger attached. Bounded by `SizeOfImage` and
  read-only. Reachable through `POST /api/server/command`.
- **`__hook_info`** - reports the live module base, image size and layout status.
- **`__hook_session`** - the session state, its timer, the event counter and the ended
  flag, as in "Session state" above.
- **`__hook_results`** - dumps the race state for diagnosis: the session globals, the
  `gameplay_rule_data` header, every car record with the fields above parsed, and each
  car's vehicle object one pointer level deep, with the bytes shown as text. It answers
  over the input pipe only; `POST /api/server/command` returns just the first line.
- **`F:\Ghidra\Wreckfest_x64.exe.c`** - the same decompile as one 60 MB file. `rg` over
  it takes well under a second, against minutes for the per-function export.

## Method

### 1. Search for strings, not symbols

Ghidra renders string literals as symbol references, not inline text. Searching
for `"has joined"` finds nothing; the symbol is `s_SERVER_PLAYER_HAS_JOINED_140fea408`.
Search for the underscored form (`_has_joined`, `_PRIVILEGES`) or the
localisation key (`SERVER_NEW_MODERATOR`).

Many user-facing strings are localisation keys rather than the displayed text,
so search for the key you would expect a developer to write, not what a player
sees.

### 2. Follow vtables, not call sites

Several interesting functions have no direct callers because they are dispatched
through a vtable. `FUN_140443ce0` looked unreferenced until a search turned up
`puVar2[0x59] = FUN_140443ce0;` inside a constructor. To find who calls a vtable
slot, search for its byte offset: slot `0x59` is `0x59 * 8 = 0x2C8`, so search
for `0x2c8))` to find `(**(code **)(... + 0x2c8))(...)`.

### 3. Decode RIP-relative operands to find globals

The event loop globals were found this way rather than by searching. The getter
`FUN_1402dd490` is 24 bytes; read them with `__hook_read 2DD490 24`:

```
83 3d 99a15701 00    cmp dword [rip+0x0157A199], 0     ; A
7e 0c                jle -> return 0
83 3d d0ddf400 ff    cmp dword [rip+0x00F4DDD0], -1    ; B
7e 03                jle -> return 0
b0 01                mov al, 1
c3                   ret
32 c0                xor al, al
c3                   ret
```

RIP-relative displacements are from the address of the **next** instruction:

```
A: next = 0x2DD490 + 7 = 0x2DD497;  0x2DD497 + 0x0157A199 = 0x1857630
B: next = 0x2DD4A0;                 0x2DD4A0 + 0x00F4DDD0 = 0x122B270
```

So `enabled = A > 0 && B > -1`, which then gets confirmed by experiment.

### 4. Confirm by changing state, never by inference alone

Inference was wrong twice during this work. `FUN_1404477a0` was assumed to write
to chat because of where it was called; it is actually a colour-code stripper.
`DAT_1419146d0` was assumed to be the in-lobby flag; it is a startup mode flag
that never changes. Then `0x19146E0` and `0x19146EC` were taken for lobby and racing
flags from one race's readings; they are a packet parity bit and an event counter
(issue #189).

Read a value, change the state, read again:

```
# event loop, in lobby
__hook_read 122B270 4      -> 00000000  (0, enabled)
/eventloop
__hook_read 122B270 4      -> ffffffff  (-1, disabled)

# session state, through a lobby, a race and its results
__hook_session             -> state=0 in lobby, 1 counting down, 2 racing, 3 on results
```

`0x122B270` is the index of the event currently loaded, little-endian (`01000000` is
entry 1). The count is at `0x1857630`. Confirmed live on 2026-10-05 (#204), in a lobby
with a four-entry loop: `/rotate` loads the next entry at once, and wraps from the last
entry to 0. `/eventloop` off sets the index to -1, and on again sets it to 0 *without
loading anything*, so the next `/rotate` or event is entry 1. `/rotate` with the loop off
does nothing. An `el_add` the game does not know loads `track=` instead, as at startup.

Read more than once per state, and across more than one cycle: a single reading per
state is how the parity bit passed for a lobby flag.

Cross-check against a second source wherever one exists. Privilege flags were
confirmed three ways: toggling with `/op` and `/demote`, the `A`/`M` marker in
`list` output, and the decompiled command handler.

### 5. Watch for state machines, not booleans

The session has five states, not two (see "Session state"), and the game may add more.
Code that gates on it should test for the state it positively recognises and fall
through otherwise, rather than trying to enumerate every case.

## Redoing this after an update

1. Rebuild the Ghidra export for the new binary.
2. Check what actually changed: `SizeOfImage` and the linker timestamp are in
   the PE header, and `__hook_info` reports the live image size.
3. For each RVA, find the anchor that does not move - a string, a localisation
   key, a command name - and re-derive the address from it rather than adjusting
   the old value by a delta. Deltas are not uniform across sections.
   - `ConsolePrintRva` / `CommandDispatcherRva`: find the command handler by
     searching for `"/demote"` or `"/eventloop"`, then follow the dispatcher.
   - Privilege levels: the handler sets level 1 for `/op`, 2 for `/admin`,
     0 for `/demote`; the flag bits are 4 (privileged) and 5 (admin).
   - Event loop globals: find the getter, read its bytes, decode the
     displacements as in step 3.
4. Confirm each one by experiment before trusting it.
5. Update `ExpectedImageSize` in the hook if you pin builds.

## Known traps

- **Ghidra's line numbers can be wrong.** `search-decompiled.ps1` reported a
  match at line 183 of a 102-line file. Trust the file, verify the line.
- **The MCPx64dbg plugin emits invalid JSON** - Windows paths are embedded
  unescaped, so `GetModuleList` cannot be parsed strictly. It also wedges
  occasionally; restarting x64dbg clears it. Endpoint paths differ from the
  Python wrapper's function names (`MemoryRead` is `Memory/Read`), and a wrong
  path returns a connection reset that reads as a broken plugin.
- **ASLR relocates the image.** Ghidra uses base `0x140000000`; the live base
  came back as `0x7FF6B4880000`. `x64dbg-api.ps1 -Rva` does the conversion.
- **A breakpoint freezes every thread**, including the hook's pipes, so players
  disconnect. Investigate with nobody on the server.
- **`dotnet run --no-build` skips copying the hook DLL into `bin/`**, so a
  freshly built hook is silently not the one injected.
- **The MCPx64dbg plugin wedges reproducibly on `Debug/Run` after a breakpoint
  hit.** The endpoint stops answering and its listener on 8888 disappears
  entirely, which needs an x64dbg restart. Clear the breakpoint and resume from
  the GUI (F9) instead. Worth restating the related trap, which cost time during
  the chat investigation: pass the **endpoint path** (`Debug/Run`,
  `Is_Debugging`), not the wrapper name (`DebugRun`, `IsDebugging`).
