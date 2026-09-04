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

`Services/VotingService.cs`:

| Constant | RVA | What it is |
| --- | --- | --- |
| `RvaEventLoopCount` | `0x01857630` | int32, number of `el_add` entries |
| `RvaEventLoopIndex` | `0x0122B270` | int32, current rotation entry; `-1` when the loop is off |
| `RvaSessionLobby` | `0x019146E0` | byte, `1` in lobby, `0` while racing |
| `RvaSessionRacing` | `0x019146EC` | byte, `1` while racing or on the post-race vote screen |

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

### Player struct, cross-confirmed

The decompiled chat handler walks `serverObject + 0x30` -> player table, stride
`0x138`, name at `+0x48` - byte-for-byte identical to `ReadPlayersNoThrow` in
`NativeHooks/WreckfestConsoleHook/WreckfestConsoleHook.cpp`. Two independent
sources agree, so `param_3` is the SERVER object. The hook additionally uses
`+0xA6` status, `+0x82` flags, `+0xA8` ping.

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
that never changes.

Read a value, change the state, read again:

```
# event loop, in lobby
__hook_read 122B270 4      -> 00000000  (0, enabled)
/eventloop
__hook_read 122B270 4      -> ffffffff  (-1, disabled)

# session state, lobby vs race
__hook_read 19146E0 1      -> 01 in lobby, 00 while racing
__hook_read 19146EC 1      -> 00 in lobby, 01 while racing
```

Cross-check against a second source wherever one exists. Privilege flags were
confirmed three ways: toggling with `/op` and `/demote`, the `A`/`M` marker in
`list` output, and the decompiled command handler.

### 5. Watch for state machines, not booleans

Two bytes gave three observed states, not two:

| `0x19146E0` | `0x19146EC` | state |
| --- | --- | --- |
| `01` | `00` | lobby, idle |
| `00` | `01` | racing |
| `01` | `01` | post-race track vote |

There are likely more (loading, countdown, results). Code that gates on this
should test for the state it positively recognises and fall through otherwise,
rather than trying to enumerate every case.

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
