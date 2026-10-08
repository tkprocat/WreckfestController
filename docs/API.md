# HTTP API

WreckfestController hosts a REST API alongside the desktop application. Browsers sign
in with a cookie; scripts send an API key. There is no assumption about what is on the
other end.

All routes are prefixed `api/` and return JSON. The OpenAPI description is at
`/openapi/v1.json` (signed in or with the key); the web app's TypeScript types are
generated from it.

## Authentication

Every endpoint requires an authenticated caller, except the few
[anonymous ones](#anonymous-endpoints) below. A caller authenticates in one of two ways:

- **API key** (scripts, live testing): send the configured key.

  ```
  X-Api-Key: <Api:Key>
  ```

  The key is compared with `CryptographicOperations.FixedTimeEquals`. It is optional:
  when `Api:Key` is blank, no key is accepted and only cookie sign-in works.
- **Cookie** (the web UI): an ASP.NET Core Identity cookie, issued by
  `POST /api/auth/login`. It is `HttpOnly`, `SameSite=Strict`, marked
  `Secure` when the request came over HTTPS, and slides over 14 days. Its encryption
  keys are kept in a `keys` folder beside the database, protected with DPAPI, so
  restarting the controller does not sign anyone out. Changing a user's password or
  locking them out ends their other sessions on the next request.

When the `X-Api-Key` header is present it alone decides: a wrong key is rejected even
if a valid cookie is also sent.

A missing or rejected credential returns **401** with no body, never a redirect.

### Anonymous endpoints

Authorization uses a fallback policy, so an endpoint is protected unless it is
explicitly marked `[AllowAnonymous]`. A test pins the list of anonymous endpoints
(`GET auth/state`, `GET auth/antiforgery`, `POST auth/login`, `POST auth/logout`,
`GET public/overview`, and the [live-update hub](#live-updates--hubsserver)), so one
cannot appear by accident. Requests to paths that match no
endpoint also get 401 rather than 404.

### CSRF

Browser requests must also prove they came from the web UI. Every unsafe request
(anything but GET, HEAD, OPTIONS, TRACE) that does **not** carry `X-Api-Key` must send
the antiforgery token:

1. `GET /api/auth/antiforgery` sets a JS-readable `XSRF-TOKEN` cookie.
2. Copy its value into the `X-XSRF-TOKEN` header on each unsafe request.
3. The token is tied to the signed-in identity, so fetch a fresh one **after login
   and logout**; a token from before login is rejected after it.

A missing or stale token returns **400**. API-key requests are exempt: they carry no
cookies, and a browser cannot add a custom header cross-site without a CORS preflight,
which this API never grants.

### Accounts

Every account is an admin in v1. There is **no web setup page**: the first account is
created in the desktop app, which offers a "Create admin account" dialog at startup
when the API is enabled and no accounts exist. The Configuration tab has the same
button, which also works as recovery if every account is locked out or its password
lost. Passwords need at least 10 characters; five failed sign-ins lock an account for
15 minutes.

`POST auth/login` also allows **10 attempts per minute per client IP**, counting
successes and failures alike. Over the limit it answers **429** with a `Retry-After`
header, even for the right password. Lockout protects one account; this slows a client
trying many. Behind a reverse proxy, set `TrustedProxies` (below) or every client
shares the proxy's limit.

There is deliberately **no local exemption**: loopback requests need credentials too.

> The key is read once when the API server starts, so changing it requires a
> restart.

## Binding

```json
"Api": {
  "Enabled": false,
  "Key": "",
  "AllowRemote": false,
  "TrustedProxies": [],
  "HttpPort": 5100,
  "HttpsPort": 5101
}
```

The API is **opt-in**, and starts only when `Enabled` is `true`. Otherwise no port
is bound at all. `Key` no longer affects whether it starts.

| Setting | Effect |
| --- | --- |
| `Enabled: false` (default) | the API does not start; no port is bound |
| `Enabled: true`, `Key` blank | the API starts; only cookie sign-in is accepted |
| `AllowRemote: false` (default) | binds `127.0.0.1` only |
| `AllowRemote: true` | binds `0.0.0.0` |
| `TrustedProxies` | reverse proxies whose `X-Forwarded-For` / `X-Forwarded-Proto` are honoured; default none |
| `HttpPort` / `HttpsPort` | defaults 5100 / 5101; HTTPS listens only with `Https` |
| `Https` | HTTPS from the certificate store or a file, and what plain HTTP from other computers gets: see [https.md](https.md) |

### Behind a reverse proxy

List the proxy in `TrustedProxies`, as addresses or CIDR ranges:
`["192.168.1.1"]` or `["10.0.0.0/24"]`. A comma-separated string also works. The
controller then takes the client IP from `X-Forwarded-For`, for the login rate limit and
logs, and the scheme from `X-Forwarded-Proto`, so the sign-in cookie is marked `Secure`
when the browser used HTTPS. `X-Forwarded-Host` is not used.

The headers are ignored from every other address, loopback included, so a client cannot
pick its own IP by sending them. An entry that is not an address or range is logged and
skipped.

Ports are configurable so several controller instances can manage separate servers
on one Windows host. A port that is not 1–65535, the same port for HTTP and HTTPS (with
`Https`), or an `Https` section that is incomplete or unusable stops the API, with the reason in the
desktop app and the log; it never falls back to another port or to plain HTTP.
`Kestrel:Endpoints` and `urls` are not used.

`GET /api/https` (signed in) reports HTTPS: off, or the certificate's names, issuer,
expiry, a 14-day warning, `lastRefresh` (the last successful check, even when the
certificate did not change) and the last reload's error. No key, password or path.

## Endpoints

### What the web can reach

**The web API runs the game server; the desktop app runs the PC.** Over the API an admin
starts, stops and restarts the server, sends commands, and manages its config, rotation,
cups, catalogue, users, and the settings that change game behaviour. Anything that names
or touches programs, paths, files, or processes other than the dedicated server is set
in the desktop app (or its config files) only:

- launch settings (`serverPath`, `serverArguments`, `workingDirectory`, `logFilePath`,
  `steamCmdPath`) and `log=` in server_config.cfg: neither read nor written over the API;
- the API's own binding and key, and the database: no route;
- database backups: a desktop button, not an endpoint (#74);
- processes: `attach` and `inject` accept only the configured dedicated server (below);
- no response carries a local path.

A new endpoint is checked against this before it is added.

Validation failures are **400** `ValidationProblemDetails`:
`{ "errors": { "<field>": ["message", ...] } }`, with fields named as in the request. A
request the current state does not allow is **409** `ProblemDetails`, its `title` saying
why. Numbers must be JSON numbers: `"5"` for an integer is a 400.

### Auth — `api/auth`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `state` | Anonymous. `{ authenticated, user, setupRequired, degraded }`. `setupRequired` is true while no account exists. `user` is null for anonymous and API-key callers. |
| GET | `antiforgery` | Anonymous. Sets the `XSRF-TOKEN` cookie; 204. |
| POST | `login` | Anonymous. `{ login, password, remember }`. `login` matches the username, then the email. 200 with the user; **401** for a wrong login or password (the same answer either way); **423** while the account is locked. `remember` makes the cookie outlive the browser session. |
| POST | `logout` | Anonymous, so an expired session can still clear its cookie. 204. |
| GET | `me` | The caller's profile. **403** for API-key callers, who are not a user. |
| PUT | `me` | `{ email, displayName, timeZone }`. `timeZone` is an IANA id (`Europe/Copenhagen`) or null for the browser's zone. Changing the email ends the caller's other sessions. |
| POST | `me/password` | `{ currentPassword, newPassword }`. 204. Keeps this session and ends the others. |

### Public — `api/public`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `overview` | Anonymous. The public home page's data, below. |

`overview` is `{ serverName, maxPlayers, status: { isRunning, uptimeSeconds }, currentTrack,
players: { humans, bots, list: [{ name, isBot }] }, rotation: { name, tracks: [{ id, name,
gameMode, laps }] }, activeCup: { name, activatedAt, phase, startsAt, endsAt },
upcomingCups: [{ name, description, nextOccurrence, repeat, warmupAt, endsAt }], updatedAt }`.
- The active cup's `phase` is `Warmup` until `startsAt`, then `Running` until `endsAt`
  (null: no end). An upcoming cup's `warmupAt` is when the server restarts into it: its
  `nextOccurrence` when it has no warmup.
- A track's `name` is the catalogue's "Track - Variant", or its id when the catalogue does
  not know it. `serverName`, `maxPlayers` and the rotation are null or empty while the
  server config cannot be read.
- It is built from public fields only: never the server password, the admin or moderator
  Steam ids, or a cup's server settings.
- 60 requests a minute per client IP; beyond that **429** with `Retry-After`. Live changes
  come from the hub's public group, so a page need not poll.

### Users — `api/users`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | — | All accounts, by username. |
| GET | `{id}` | One account. |
| POST | — | `{ userName, email, password, displayName, timeZone }`. 201. `password` is a temporary one for the owner to change. |
| PUT | `{id}` | `{ userName, email, displayName, timeZone }`. A new username or email ends that account's sessions (not the caller's own). |
| DELETE | `{id}` | 204. **409** for your own account or the last one. |
| POST | `{id}/password` | `{ newPassword }`. Admin reset: 204, ends the account's sessions. A rejected password leaves the old one in place. |
| POST | `{id}/lock` | Locks until unlocked and ends the account's sessions. **409** for your own account. |
| POST | `{id}/unlock` | Lifts an admin lock or a failed-sign-in lockout. |

Any account write answers **409** when the account was changed by another request
at the same moment; reload it and try again. Nothing is half-applied: a lock and the
sign-out it causes are one save, and concurrent deletes are serialized so the last
account always survives.

Account responses never include password hashes or security stamps:
`{ id, userName, email, displayName, timeZone, isLockedOut, lockoutEnd }`.

### Server — `api/server`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `version` | `{ version, assemblyVersion, product }` |
| GET | `status` | `{ isRunning, processId, uptimeSeconds, currentTrack }` |
| POST | `start` | Start the server |
| POST | `stop` | Graceful stop |
| POST | `forcestop` | Force stop |
| POST | `restart` | Graceful restart |
| POST | `forcerestart` | Force restart |
| POST | `update` | Run the server update |
| POST | `command` | Send a console command. Body: `{ command }` (required) |
| GET | `processes` | The running dedicated servers attach and inject accept: `[{ processId, startTime, isAttached }]` |
| POST | `attach/{pid}` | Attach to a running dedicated server (after a restart, say) |
| POST | `inject/{pid}` | Inject the console hook into it |
| POST | `inject` | Inject into the already-tracked process |
| GET | `logfile?lines=100` | Tail the server's log file from disk — the one deliberate exception to hook-only I/O (see below). `lines` 1-10000. `{ lines, source, output }` |
| GET | `players` | Current roster: `{ totalPlayers, maxPlayers, players, lastUpdated }` |

The actions (`start` through `inject`) answer `{ message }` (`inject` adds `processId`).
One the server's state does not allow (already running, not running, no process to
inject into, a failed update) answers **409** with the reason as the problem's `title`.
A `pid` that is not a positive number is a **400** naming `pid`.

`attach/{pid}` and `inject/{pid}` accept only a running Wreckfest dedicated server (started
with `-s`) whose executable is the `serverPath` set in the desktop app: never the game
client, which has the same file name in another folder, a server from another install,
or any other process. Nor a server another controller on this PC started: its command line
carries that controller's `-wfc_controller=<id>`, where servers started by this one carry
this controller's id. A server with no id (started by hand) is accepted. Anything else is
a **409**, as is any pid when no `serverPath` is set.
Attach decides what `forcestop` kills and what `inject` loads the hook into, so this is
what keeps those to the server. `processes` lists exactly the processes that pass. The
desktop app's Process Manager may still pick another install's or another controller's
server, after asking.

`start`, `restart` and `forcerestart` inject the hook into the new process themselves,
retrying once; their `message` ends with whether that worked. A failed inject does not
fail the action, and `inject` stays available to try again.

`logfile` is the only endpoint that reads server output from disk rather than from the
injected hook. It is kept on purpose: the web app's Server Control page shows it, and it
is the only way to see output from before attachment. Nothing else in the app consumes it —
no tracker, roster or chat path is fed from the file — so the hook-only contract still
holds for everything that drives state. Treat what it returns as history, not live state:
it answers even when no hook is injected, and its content can predate the current
process.

It reads the file named by `log=` in server_config.cfg, resolved against the server's
working directory, and only when that path stays inside it; otherwise the desktop app's
log file path. Neither can be set over the API.

Injection is refused (409) unless the target process is already attached, and when
the detected game build does not match `WreckfestServer:SupportedBuild`.

### Configuration — `api/config`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `basic` | Basic server configuration (`ServerConfig`; no `log`, which names a file) |
| GET | `basic/fields` | Per setting: `{ field, key, savable, reason }` - whether server_config.cfg can take a change to it, and if not, what to fix in the file |
| PUT | `basic` | Update it. Body: the `ServerConfig` fields to change. Returns the settings read back from the file |
| GET | `tracks` | Event-loop tracks: `{ count, tracks }` |
| PUT | `tracks` | Replace them. Body: `{ collectionName, tracks }`. Returns `{ count, tracks }` read back from the file |
| GET | `tracks/collection-name` | `{ collectionName }` |
| GET | `serverinfo` | Live settings asked of the running server (`ServerConfig`) |

A file that cannot be read or written, or a server that cannot answer `serverinfo`,
gives **409** with the reason as the problem's `title`.

`PUT basic` is a **partial update**: send only the `ServerConfig` fields to change
(names are case-insensitive), and every omitted field keeps its current value. The
request is rejected with 400, naming the field in `errors`, and nothing is written, when
it names an unknown field, gives a value of the wrong type or `null`, or puts a line
break in a string (`body` when the body is not an object). `log` is never written: it
names the file `logfile` returns, so it is set in server_config.cfg by hand.
Only keys that already have an active `key=value` line can be changed: a field whose line
is missing or commented out, or that is set again below `# Event Loop` (where the later
value wins), is a **409** naming the key, and nothing is written.

`PUT tracks` replaces the whole event loop. It is rejected with 400, naming the field, unless
`collectionName` is non-empty (at most 128 characters), `tracks` is present, every
entry's `track` is a game id (`^[A-Za-z0-9_]{1,64}$`), `laps`, `bots` and `numTeams`
are not negative, `carResetDisabled` and `wrongWayLimiterDisabled` are `0` or `1`,
text values are at most 128 characters, and no value contains a line break. An empty
`tracks` list is allowed. Collections are checked by the same rules. A server_config.cfg
without a `# Event Loop` heading has nowhere to put the loop: that is a **409**, and
nothing is written. Activating a cup with a rotation checks this first, so none of the
cup's other settings are written either.

### Cups — `api/cups`

A cup is an event loop (the rotation) and the rules it runs under, applied through a
smart restart at a set time, once or on a repeat. The name follows Wreckfest's own
Cup Mode: in the game an *event* is one race of the loop, and a cup is a run of events
scored with cup points. Cups live in the controller's database.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | | Every cup, past ones included: `{ count, cups }`, upcoming soonest first, then finished ones |
| POST | | Create. Body: `CupRequest`. 201 with an `ETag` |
| GET | `{id}` | One cup, with its `ETag` |
| PUT | `{id}` | Replace. Needs `If-Match` |
| DELETE | `{id}` | Delete. Needs `If-Match` |
| GET | `current` | The active cup, or 204 when there is none |
| GET | `upcoming` | Cups whose next occurrence is more than 5 minutes away |
| GET | `due` | Cups the scheduler will start at its next check |
| GET | `summary` | `{ totalCups, activeCups, upcomingCups, dueCups, lastUpdated }` |
| POST | `{id}/activate` | Activate now. 202; the cup becomes active when the restart succeeds |

`CupRequest` is `{ name, description?, startTime, timeZone?, repeat?, warmupTime?, endTime?,
restartRotationAtStart?, serverConfig?, sessionMode?, gridOrder?, collectionId?, tracks?,
collectionName? }`:
- `startTime` must carry an offset (`2026-10-02T18:00:00Z`); an unzoned time is 400.
- `timeZone` is an IANA id such as `Europe/Copenhagen`, default `UTC`. A repeat's
  `time` and `days` are wall-clock values in that zone, so a weekly 20:00 cup stays
  at 20:00 local across daylight saving.
- `repeat` is `{ frequency: "daily" | "weekly", days: [0-6], time: "HH:MM" }`, days
  counting from Sunday = 0. Weekly needs at least one day.
- `warmupTime` and `endTime` are `"HH:MM"` wall-clock times in `timeZone`, like the
  repeat's `time` (see *Scheduling*). The warmup is the latest such time at or before
  the start, at most 12 hours before it (the evening before, for a warmup at 23:50 and
  a start at 00:10); the end is the first after it (the next day, for a cup running past
  midnight). They are checked against the repeat's `time`, or a one-off start in
  `timeZone`. A warmup at the start is no warmup, and an end at the start is 400. A first
  occurrence set off the repeat's clock whose warmup would be more than 12 hours earlier
  gets none.
- `restartRotationAtStart` sends the event loop back to its beginning at the start.
- `serverConfig` overrides only the fields that are set. Text values must not contain
  line breaks.
- `sessionMode` and `gridOrder` are the cup's scoring, written as `session_mode` and
  `grid_order`. Omitted, the server keeps its own. `sessionMode` is a cup points system
  (`30p-aggr`, `25p-aggr`, `25p-mod`, `24p-lin`, `16p-lin`, `10p-double`, `10p-lin`,
  `35p-folk`, `f1-1991`, `f1-2003`, `f1-2010`, `player_count_1`), `normal` for no cup
  points, or `qualify-sprint` / `qualify-lap`. `gridOrder` is `random`, `perf_normal`,
  `perf_reverse`, `qualifying`, `cup_normal` or `cup_reverse`. Case is ignored; the
  value is stored as the server spells it, and anything else is 400.
- Give the rotation either as `collectionId`, which deploys that collection's tracks
  as they are **at activation**, or inline as `tracks` (checked like `PUT
  api/config/tracks`) with an optional `collectionName`. Neither leaves the server's
  rotation alone. Deleting a collection copies its tracks into the cups linked to
  it, so they still deploy them.

Responses add `repeatDescription`, `nextOccurrence`, `lastOccurrence`, `lastOutcome`, `isActive`,
`activatedAt`, `nextWarmup` and `nextEnd` (the next occurrence's window), `phase`,
`currentStart` and `currentEnd` (the active cup's run, whose occurrence the scheduler has
already moved past), `createdBy` (the signed-in user who created it; null for API-key
callers), `createdAt`, `updatedAt` and `version`. For a linked cup, `tracks` and
`collectionName` are the collection's current ones.

**Concurrency.** `version` and the ETag cover what an admin edits. PUT and DELETE
without `If-Match` get 428, and with a stale one 409 with the current cup. The
scheduler's own fields (`nextOccurrence`, `lastOccurrence`, `lastOutcome`, `isActive`, `activatedAt`)
change without a new version, so the scheduler finishing an occurrence never
invalidates an open editor, and an edit that leaves `startTime`, `timeZone` and
`repeat` alone never moves the schedule. An edit that changes them picks the first
occurrence of the new schedule that has not already run, failed, been cancelled or
been missed: every occurrence dealt with is kept in the cup's history, so an edit
never runs one twice.

**The restart itself** (a cup's, from its schedule or `activate`) depends on who is on
the server. With no real players (empty, or bots only) it restarts at once, without a
countdown. With players online, they get a 5-minute countdown in chat
(`ServerRestartPending` each minute), then "Server will restart at the next lobby." The
restart happens at the next track change, or as soon as every player has left. After 10
minutes of waiting for a lobby it is forced ("Server restarting now (timeout).").

**Scheduling.** An occurrence's restart begins 5 minutes before its warmup (its start,
without one), for the players' countdown. Restarting at the warmup rather than the start
lets players join before the cup without being disconnected as it begins. From the
restart on, the cup is active, and its run goes:
- **Warmup** (`phase: Warmup`): the cup's rotation, scoring and overrides are in force.
  Five minutes before the start players are told "*name* starts in 5 minutes."
- **Start**: at the first lobby at or after the start, `/cupreset` clears the warmup's cup
  points, then, with `restartRotationAtStart`, the event loop is turned off and on (the
  next event is then the rotation's second entry: the game resets its position without
  loading anything). The reset goes first: turning the loop off may move the lobby to the
  game's track vote for a moment, and a reset sent just after the toggle was seen to have
  no effect. Players are told "*name* has started - good luck!"; a race still on
  at the start is announced with "*name* starts after this race." The cup is then
  `Running`, and `CupStarted` is sent. A run whose restart finished after its start (one
  that ran long, say) starts at once. `/cupreset` is only ever sent in a lobby the server
  confirms, never mid-race. One that cannot go through yet is retried (even across a
  controller restart) for at most 15 minutes after the start; then the cup starts without
  it, keeping the warmup's points, with a warning in the log. Until then the cup still
  shows as warming up.
- **End** (with an `endTime`): players are told "*name* is over - thanks for racing!", the
  cup is no longer active, and `CupEnded` is sent. A cup that ran with cup points - its own
  `sessionMode`, or the server's when it keeps that - has them turned off at the next
  lobby: `session_mode=normal`, live and in server_config.cfg. The rotation stays. This
  is kept in the database until done, so neither a controller restart nor deleting the
  cup loses it, and any activation drops it: the activated cup's settings rule then. The run's end is fixed when it begins: an
  edit while it runs moves later occurrences, not this one.

A lobby is waited for at most 15 minutes; then, or when the session state cannot be
read, it goes ahead anyway. A cup with no `endTime` stays active until another one
replaces it; when its next occurrence comes round it needs no restart, but starts a new
run, so the start still resets the cup points.

Each occurrence gets one attempt, and then the cup moves to its next occurrence (or
finishes, for a one-off cup). `lastOutcome` records how it ended:
- `Activated`: the restart succeeded, or the cup was already active;
- `Failed`: the settings could not be written, or the restart failed;
- `Cancelled`: an admin cancelled the restart;
- `Missed`: not started within 15 minutes of its time, because the app was not running
  or another restart ran too long.

Nothing is retried. Every outcome is sent to signed-in clients as
`CupOccurrenceEnded`, and anything but `Activated` is also logged as a warning.
Running a missed or failed cup anyway is the admin's call, with `activate`. At most one cup is active; activating another deactivates it. A manual
activation from 5 minutes before an occurrence's warmup counts as that occurrence, and
runs it from warmup or start, depending on the time. Otherwise it runs no occurrence:
active, with no start to wait for and no end.

`activate` answers 409 when the cup is already active, another restart is running,
or the cup's settings cannot be written to the server config (with a `reason`).

### Settings — `api/settings`

The settings a person edits over the web, kept in the controller's database one section
at a time. Today that is `vote` (`mode`, `directCooldownSeconds`, `voteTimeoutSeconds`,
`maxLapsAllowed`, `messageDelayMs`, `suppressCommandsDuringRace`); later settings pages
add sections for what changes game behaviour, never programs or paths. The WPF
Configuration tab edits the same sections.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | | Every section: `{ vote }` (`SettingsResponse`), each with its `version` |
| GET | `vote` | The voting settings (`VoteSettingsResponse`), with its `ETag` |
| PUT | `vote` | Change the fields in the body; the rest keep their values. Needs `If-Match`. Answers `VoteSettingsResponse` |

Each section has its own typed route, so the web app gets types for it; a section that
does not exist has no route (404).

- A PUT with a stale `If-Match` gets 409 with the section as it is now; without one, 428.
  A body that repeats `version` is fine: `If-Match` is what counts.
- Unknown fields, wrong types and out-of-range values are 400 with a field error:
  `mode` is `Off`, `Voting` or `Direct` (any case); `directCooldownSeconds` 0-3600,
  `voteTimeoutSeconds` 1-3600, `maxLapsAllowed` 1-999, `messageDelayMs` 0-5000.
- A change takes effect at once: voting reads it on the next command, and a saved `vote`
  change ends a `!voting` override.
- The startup settings (`Api:*`, `Database:Path`) are not here and have no route. They stay
  in user-settings.json, edited by hand, so a lockout can be fixed without the web UI.
  The controller never writes that file.
- The launch settings are not here either, neither read nor written, and have no route:
  the server's `serverPath`, `serverArguments`, `workingDirectory` and `logFilePath` (no
  `wreckfestServer` section), and SteamCMD's `steamCmdPath`, which is **hidden** - not
  even readable - along with the fixed `wreckfestAppId` (no `steamCmd` section). They
  decide which programs the controller starts and which files it reads and writes, so a
  web admin who could change them could run any program or read any file on the PC, and
  reading them would only reveal local paths. They are set in the WPF app only. The web
  keeps the server actions, Update included (`POST /api/server/update`).

### Catalogue — `api/catalogue`

The tracks, variants, tags, weather and mods the controller knows. The database ships
with a built-in catalogue: the base-game tracks, plus a set of popular workshop
tracks. Every built-in variant starts allowed for voting. Built-in rows are fully editable,
but a built-in track's `key` and a built-in variant's `variantId` are fixed, and they
cannot be deleted: hide them instead, and `reset` restores what shipped.

Hidden tracks and variants are left out of lists unless `includeHidden=true`, but stay
readable by id.

**Voting.** In-game `!track`, `!vote` and `!lucky` pick from the catalogue: every variant
allowed for voting (`PUT variants/{id}/voting`), except hidden ones and those of a hidden
track, named "Track - Variant". A change counts at once.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `tracks` | Tracks with their variants, by name. Filters: `tag` (slug), `gameMode` (`Racing`/`Derby`), `weather`, `origin` (`BaseGame`/`Dlc`/`Workshop`/`Custom`), `dlc`, `mod` (id), `availableOnly` (no mod, or a mod in the server config's `mods=`; **409** when the config cannot be read), `includeHidden`. |
| GET | `tracks/{id}` | One track with all its variants. Sends an `ETag`. |
| POST | `tracks` | `{ key, name, origin, dlcName, modId }`. 201. Supports every weather until `weather` is set. `dlcName` only for `Dlc`, `modId` only for `Workshop`. |
| PUT | `tracks/{id}` | Same body. Needs `If-Match`. |
| DELETE | `tracks/{id}` | 204, with its variants. **409** for a built-in track, or while a collection uses one of its variants. |
| POST | `tracks/{id}/hide`, `tracks/{id}/unhide` | Retire or restore without deleting. |
| POST | `tracks/{id}/reset` | Built-in only: restores name, origin and weather. Not the hidden flag or the variants. |
| PUT | `tracks/{id}/weather` | `{ weather: ["clear", ...] }`. Replaces the supported weather. |
| GET | `variants` | Variants, by track then name. Filters: `search` (id, name or track name), `trackId`, `tag`, `gameMode`, `weather`, `votingOnly`, `includeHidden`. |
| GET | `variants/{id}` | One variant. Sends an `ETag`. |
| POST | `variants` | `{ trackId, variantId, name, gameMode, allowedForVoting }`. 201. `variantId` is `^[A-Za-z0-9_]{1,64}$` and unique ignoring case. |
| PUT | `variants/{id}` | `{ variantId, name, gameMode }`. Needs `If-Match`. |
| DELETE | `variants/{id}` | 204. **409** for a built-in variant, or while a collection uses it. |
| POST | `variants/{id}/hide`, `variants/{id}/unhide` | As for tracks. |
| POST | `variants/{id}/reset` | Built-in only: restores name, mode, voting flag and tags, recreating a shipped tag that was deleted. |
| PUT | `variants/{id}/voting` | `{ allowed }`. |
| PUT | `variants/{id}/tags` | `{ tags: ["oval", ...] }` by slug. Replaces the tags. |
| GET, POST | `tags` | List, or create `{ name, slug, color }`. `slug` is lower-case words joined by `-`; `color` is `#RRGGBB`. |
| GET, PUT, DELETE | `tags/{id}` | Deleting a tag removes it from every variant. |
| GET | `weather` | The weather names, in the game's order. |
| GET, POST | `mods` | List, or create `{ name, folderName, workshopId }`. `folderName` is the name `mods=` uses. |
| DELETE | `mods/{id}` | 204. **409** while tracks still come from it. |

**Concurrency.** A track or variant carries a `version`, and `GET {id}` returns it as
`ETag: "3"`. `PUT {id}` must send it back as `If-Match: "3"`: without the header the answer is
**428**, and when someone saved in between it is **409** with the row as it is now
(and its new `ETag`), so the editor can offer to reload or overwrite.

A **409** for a row in use names the collections in its title and lists them in
`collections`, so the Track Browser can say what to change first.

### Collections — `api/collections`

Named track rotations. A collection is saved whole: `{ name, tracks }`, where `tracks`
is in rotation order and each entry has the shape `GET /api/config/tracks` returns
(`track`, `gamemode`, `laps`, `bots`, `numTeams`, `carResetDisabled`,
`wrongWayLimiterDisabled`, `carClassRestriction`, `carRestriction`, `weather`), so a
deployed rotation can be saved as a collection as is. Names are unique, ignoring case.

An entry whose `track` the catalogue knows is linked to that variant, and its response
carries `variant: { id, name, trackId, trackName, gameMode, isHidden }`. An unknown id,
such as a workshop track the catalogue does not have yet, is kept with `variant: null`,
and is linked when that variant is added. A linked entry deploys the variant's current
id, so renaming an admin-added variant carries its collections along. Hiding a variant
leaves it in collections, with `isHidden: true`.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | | `{ id, name, version, trackCount, createdAt, updatedAt }` per collection, by name. |
| GET | `{id}` | One collection with its tracks. Sends an `ETag`. |
| POST | | `{ name, tracks }`. 201. |
| PUT | `{id}` | Same body; replaces the name and every track, which is also how to reorder. Needs `If-Match`. |
| DELETE | `{id}` | 204. |
| POST | `{id}/duplicate` | Optional `{ name }`, else "*name* (copy)", then "(copy 2)" and on. 201. |
| POST | `{id}/deploy` | Writes the tracks to the server config's event loop, with the name on `#CollectionName`. `{ message, collectionName, count }`. **409** for an empty collection, or when the config cannot be written; then `reason` is `accessDenied` (no write permission, or the file is read-only), `fileInUse` (another program has it open), `notFound`, `notConfigured` or `ioError`, and `title` says what to fix. |

Concurrency works as for the catalogue: `version`, `ETag`, `If-Match`, 428 and 409.

## Live updates — `/hubs/server`

A SignalR hub pushes server events to the web UI.
Clients only listen; the hub has no methods to call. It is outside `api/` and answers
anonymously, but what a connection receives depends on its group:

- **`public`** — every connection.
- **`admin`** — a connection whose caller passes the Admin policy: the browser's
  sign-in cookie (sent automatically, same origin) or, for scripts, `X-Api-Key`.
  Membership is decided once, when the connection opens; sign out or a revoked
  session takes effect on the next connection.

| Message | Group | Payload |
| --- | --- | --- |
| `PlayersUpdated` | public | `{ players: [{ name, playerId, score, vehicle, slot, isBot, joinedAt }] }` |
| `PlayerJoined` | public | `{ playerName, isBot }` |
| `PlayerLeft` | public | `{ playerName }` |
| `TrackChanged` | public | `{ trackId }` |
| `CupActivated` | public | `{ cupId, cupName, timestamp }` |
| `CupStarted` | public | `{ cupId, cupName, timestamp }` — the warmup is over, cup points reset |
| `CupEnded` | public | `{ cupId, cupName, timestamp }` — the end time passed; no cup is active |
| `ServerStarted` | public | `{ processId, processName, startTime, timestamp }` |
| `ServerStopped` | public | `{ processId, stopMethod, timestamp }` — `Graceful` or `Force` |
| `ServerRestarted` | public | `{ oldProcessId, newProcessId, restartMethod, timestamp }` |
| `ServerAttached` | public | `{ processId, processName, startTime, timestamp }` |
| `ServerRestartPending` | public | `{ minutesRemaining, cupName, cupId, scheduledRestartTime, timestamp }` |
| `CupOccurrenceEnded` | admin | `{ cupId, cupName, occurrence, outcome, timestamp }` — a scheduled occurrence was dealt with; `outcome` is `Activated`, `Failed`, `Cancelled` or `Missed` |
| `ConsoleLog` | admin | `{ logs: [string] }` — console lines, batched about once a second (at most 1000 per message) |

Timestamps are UTC. Events raised while no client is connected, or while the API is
stopped, are not queued. The payload types are the records in `Hubs/IServerHubClient.cs`.

```js
const connection = new signalR.HubConnectionBuilder().withUrl("/hubs/server").build();
connection.on("TrackChanged", ({ trackId }) => console.log(trackId));
await connection.start();
```

While the database is unavailable (recovery mode) the hub answers 503, like the API.
