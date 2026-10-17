# HTTP API

WreckfestController hosts a REST API alongside the desktop application. People sign in at
the portal (WreckfestPortal, INSTALL.md step 4), which vouches for them to the controller
with a token; scripts send an API key. The accounts are the portal's: its own API is
described [at the end](#the-portals-api).

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
  when `Api:Key` is blank, no key is accepted.
- **The portal's token** (the web site): a bearer token the portal adds to every request it
  forwards for someone signed in who may manage this server, a global admin or a user
  granted it.

  ```
  Authorization: Bearer <token>
  ```

  It is a JWT signed with HS256 using `Api:Portal:Secret`, issued by `WreckfestPortal`,
  addressed to `Api:Portal:ServerId`, and valid for 5 minutes (30 seconds of clock skew).
  Its claims are `sub` (the portal's account id), `name` (the username) and, when set,
  `display_name`. A token for another server, signed with another secret, expired, or
  unsigned is no one. Without `Api:Portal`, no token is accepted and only the key signs
  in. Set both `Secret` (32 characters or more) and `ServerId`, or neither: one alone
  stops the API with the reason.

When the `X-Api-Key` header is present it alone decides: a wrong key is rejected even
if a token is also sent.

A missing or rejected credential returns **401**, never a redirect.

### Anonymous endpoints

Authorization uses a fallback policy, so an endpoint is protected unless it is
explicitly marked `[AllowAnonymous]`. A test pins the list of anonymous endpoints
(`GET public/overview`, `GET public/races`, `GET public/players`, `GET public/feed`, the public track pages (`GET public/tracks`, `public/tracks/{key}`, `public/variants/{variantId}`), the [AI assistant](#ai-assistant) (`GET public/assistant`, `POST public/assistant/chat`), the [catalogue images](#images--apiimages)
under `GET images/...`, and the [live-update hub](#live-updates--hubsserver)), so one
cannot appear by accident. Requests to paths that match no
endpoint also get 401 rather than 404.

### CSRF

The controller has no cookies, so a page elsewhere cannot make a browser act on it. The
portal guards the browser's side: a change to `api/` it forwards in a signed-in user's name
must carry the portal's antiforgery token (`X-XSRF-TOKEN`, from the `XSRF-TOKEN` cookie
`GET /api/auth/antiforgery` sets at the portal's root), or the portal answers **400** and
does not forward it.

There is deliberately **no local exemption**: loopback requests need credentials too.

> The key and the portal's settings are read once when the API server starts, so changing
> them requires a restart.

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
| `Enabled: true`, `Key` blank | the API starts; only the portal's token is accepted |
| `AllowRemote: false` (default) | binds `127.0.0.1` only |
| `AllowRemote: true` | binds `0.0.0.0` |
| `TrustedProxies` | reverse proxies, such as the portal, whose `X-Forwarded-For` / `X-Forwarded-Proto` are honoured; default none |
| `Portal:Secret`, `Portal:ServerId` | the portal's settings for this server: its token's signing secret and audience; default none, when only `Key` signs in. See [Authentication](#authentication) |
| `HttpPort` / `HttpsPort` | defaults 5100 / 5101; HTTPS listens only with `Https` |
| `Https` | HTTPS from the certificate store or a file, and what plain HTTP from other computers gets: see [https.md](https.md) |

### Behind a reverse proxy

The web site reaches the controller through WreckfestPortal (INSTALL.md step 4), which is
a reverse proxy. List it, or any other proxy, in `TrustedProxies`, as addresses or CIDR ranges:
`["192.168.1.1"]` or `["10.0.0.0/24"]`. A comma-separated string also works. The
controller then takes the client IP from `X-Forwarded-For`, for the rate limits and logs,
and the scheme from `X-Forwarded-Proto`, for HTTPS enforcement. The paths in this document
are the controller's own; through the portal they are under the server's path
(`/tvtp2/api/server/status`). `X-Forwarded-Host` and `X-Forwarded-Prefix` are not used.

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
cups, catalogue, and the settings that change game behaviour. Anything that names
or touches programs, paths, files, or processes other than the dedicated server is set
in the desktop app (or its config files) only:

- launch settings (`serverPath`, `serverArguments`, `workingDirectory`, `logFilePath`,
  `steamCmdPath`) and `log=` in server_config.cfg: neither read nor written over the API;
- the API's own binding and key, the AI assistant's settings, and the database: no route;
- database backups: a desktop button, not an endpoint (#74);
- processes: `attach` and `inject` accept only the configured dedicated server (below);
- no response carries a local path.

A new endpoint is checked against this before it is added.

Validation failures are **400** `ValidationProblemDetails`:
`{ "errors": { "<field>": ["message", ...] } }`, with fields named as in the request. A
request the current state does not allow is **409** `ProblemDetails`, its `title` saying
why. Numbers must be JSON numbers: `"5"` for an integer is a 400.

### Public — `api/public`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `overview` | Anonymous. The public home page's data, below. |
| GET | `races` | Anonymous. The latest 10 finished races, newest first, below. |
| GET | `players` | Anonymous. All-time standings: the top 25 players by wins, below. |
| GET | `feed` | Anonymous. The front page's results, below. |
| GET | `tracks` | Anonymous. Every track for the public track pages, below. |
| GET | `tracks/{key}` | Anonymous. One track and its layouts. **404** for a hidden or unknown track. |
| GET | `variants/{variantId}` | Anonymous. What the race log says about one layout. **404** for a hidden one. |
| GET | `assistant` | Anonymous. Whether the AI assistant is on: `{ enabled }`. |
| POST | `assistant/chat` | Anonymous. The AI assistant's answer to a question, below. |

`overview` is `{ serverName, maxPlayers, status: { isRunning, uptimeSeconds }, currentTrack,
players: { humans, bots, list: [{ name, isBot }] }, rotation: { name, tracks: [{ id, name,
trackKey, gameMode, laps }] }, activeCup: { name, activatedAt, phase, startsAt, endsAt },
upcomingCups: [{ name, description, nextOccurrence, repeat, warmupAt, endsAt }], updatedAt }`.
- The active cup's `phase` is `Warmup` until `startsAt`, then `Running` until `endsAt`
  (null: no end). An upcoming cup's `warmupAt` is when the server restarts into it: its
  `nextOccurrence` when it has no warmup.
- A track's `name` is the catalogue's "Track - Variant", or its id when the catalogue does
  not know it. `trackKey` is its catalogue track's key, for a link to `/tracks/{trackKey}/{id}`;
  null when the catalogue does not know it, or the track or the variant is hidden. `serverName`, `maxPlayers` and the rotation are null or empty while the
  server config cannot be read.
- It is built from public fields only: never the server password, the admin or moderator
  Steam ids, or a cup's server settings.
- 60 requests a minute per client IP; beyond that **429** with `Retry-After`. Live changes
  come from the hub's public group, so a page need not poll.

`races` is `[{ id, startedAt, endedAt, track: { id, name, trackKey }, laps, cupName, warmup, entries: [{
position, name, isBot, vehicleName, outcome, timeMs, bestLapMs }] }]`, the latest 10.
- `entries` are in finishing order, bots in their real places; a car the game never placed
  has a null `position` and comes last. `outcome` is `Finished`, `Projected` (a bot's time
  the game estimated when the last human finished), `DidNotFinish` or `Unknown`.
- `startedAt` is null when the hook attached mid-race; `cupName` is null for a race outside
  a cup. `warmup` is true for a race that ended in its cup's warmup. Track names work as
  in `overview`. No Steam ids.
- It shares `overview`'s rate limit. `RaceRecorded` on the hub says when to reload.

`players` is `[{ name, wins, podiums, races, lastRaced }]`, over every race logged: the top 25
by wins, then podiums (top-3 finishes, wins included), then races.
- Bots are left out. A player is counted as for a layout's `mostWins` (by Steam id when the
  log has one, else by name) and shown by the name they last raced under. No Steam ids.
- It shares `overview`'s rate limit. Reload on `RaceRecorded`.

`feed` is the front page's results, newest first: `[{ kind, race, cup }]`. A `Race` item
(`race` set, `cup` null) is one race outside any cup, shaped as in `races`. A `Cup` item
(`cup` set, `race` null) is one cup run - one activation of a cup and every race in it - as
`{ name, activatedAt, status, startsAt, endsAt, standings: [{ position, name, isBot, points
}], raceCount, races }`.
- A cup run counts as one item however many races it had, so it stays on the page until
  enough newer items push it off. How many items there are is the `publicSite` setting
  `feedItemCount` (default 10).
- `status` is `Warmup` or `Running` for the active cup, which leads the feed from its
  activation even before its first race, and `Ended` for any other. `startsAt` and
  `endsAt` are the active run's, and null for an ended one.
- `standings` are each car's cup points total after the last race it was in, as the game
  keeps it (so a server restart mid-cup resets them, as it does in game). Only races once
  the cup started count: the warmup's points are reset at the start. Humans are told apart
  by Steam ID, which is not returned; bots by name. Equal totals share a place (1, 2, 2,
  4). The list is empty in the warmup and for a run without cup points.
- `races` are the newest 50 of `raceCount`, warmup races included and marked.
- It shares `overview`'s rate limit. Reload on `RaceRecorded`, `CupActivated`,
  `CupStarted` and `CupEnded`.

**Track pages** (#261). Tracks and layouts are named by `key` and `variantId`, ignoring
case, never by database id, so links survive a re-import. Hidden tracks and hidden layouts
are left out everywhere; so are admin fields (voting, tags, built-in) and Steam ids.

`tracks` is `[{ key, name, origin, modName, images, variants: [{ variantId, name, gameMode,
trackLengthM, images, raceCount }] }]`, by name, for tracks with at least one visible
layout. `tracks/{key}` is the same for one track, plus `workshopId`, with each layout's
`raceCount` filled in. `images` is `{ small, large, layout }`: the hash of the picture each
[image link](#images--apiimages) answers with, or null where it answers 404, so a page can
link with `?v=<hash>` and the image is cached for good. `layout` is a layout's outline, to
draw over its picture.

`variants/{variantId}` is `{ variantId, raceCount, typicalLapMs, typicalLapFinishes,
recentRaces, fastestLaps: [{ name, vehicleName, lapMs, endedAt }], mostWins: [{ name, wins,
races }], cups: [{ name, isActive, nextOccurrence, repeat }] }`.
- `recentRaces` are its latest 10, shaped as in `races`.
- `fastestLaps` and `mostWins` are the top 5 players, bots left out. A player is counted by
  Steam id when the log has one, else by name, and shown by the name they last raced under.
- `typicalLapMs` is the median of race time over laps for every finish here, bots' too, so
  it includes the standing start and traffic; the page multiplies it by a lap count for an
  estimated race time. Null before anyone has finished here.
- `cups` are those whose rotation has the layout (a linked collection's, else the cup's own
  list): the active one first, then by next occurrence.
- All three share `overview`'s rate limit.

#### AI assistant

Visitors ask about the server in plain language on the web app's `/ai` page. The controller
sends the question to an OpenAI-compatible Chat Completions API (OpenAI by default, or
OpenRouter, a local Ollama and the like) with its own key, which never reaches a browser. Its
settings are the `Assistant` section of user-settings.json (`Enabled`, `ApiKey`, `Model`,
`BaseUrl`, `DailyRequestLimit`), read when the API starts; there is no endpoint for them. The model looks answers up with read-only tools that call
the public endpoints above - `overview` (status, players, rotation; cups), `feed`, `races`,
`players`, `tracks`, `tracks/{key}` and `variants/{variantId}` - so it sees exactly what the public
pages show and nothing else.

`assistant` is `{ enabled }`: true when the section has `Enabled` on and a key set. The web
app shows its AI link only then. It shares `overview`'s rate limit.

`assistant/chat` takes `{ messages: [{ role, content }] }`, the conversation so far (the
browser keeps it; the controller stores none), and answers `{ reply }` as plain text.
- `role` is `user` or `assistant`, and the last message is the visitor's question. At most
  20 messages of 1-2000 characters; anything else is **400** with a `messages` error.
- **404** while the assistant is off. **503** when the model cannot be reached, times out
  (90 seconds) or answers with nothing. The log gets the status and the error's `code` and
  `type`, never the API's message, which can quote the key back.
- 10 questions a minute per client IP (**429** with `Retry-After`), and the section's
  `DailyRequestLimit` across all visitors per UTC day (**429** once used up). The day's
  count is kept in memory, so a restart starts it again.
- The model may call tools for up to 6 rounds per question; the last round offers none, so
  it answers with what it has. One question gets at most 12 lookups and 64,000 characters
  of their output; one lookup's output is cut at 24,000. Once either runs out, tools are no
  longer offered, and calls already asked for get a short refusal. Each model call asks for at most
  4,000 tokens (`max_completion_tokens`, which Ollama ignores).
- A reply longer than 2000 characters is cut at a word, so the browser can send it back.
- The request body may be at most 256 KiB, checked before it is read.

### Admin AI assistant — `api/assistant`

The admin panel's **AI assistant** page, for everyone who manages the server. It uses the
`Assistant` section's key, model and address, but needs only the key: `Enabled` turns on
the public page alone. It is not held to `DailyRequestLimit`.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | — | `{ available }`: true when a key is set. |
| POST | `chat` | The answer to a conversation, and the changes it proposes. |

`chat` takes the same `{ messages }` as the [public assistant](#ai-assistant), with the same
limits, and answers `{ reply, proposals }`. **404** without a key; **503** when the model
cannot answer (the log has the reason).

The model has the public tools, plus reads of the rotation, collections, cups and the server
settings below, and tools that **propose** a change. Nothing is written: each proposal is
`{ kind, summary, targetId, version, body }`, the request an admin page would send, and the
web app's **Apply** button sends it with the admin's own session:

| `kind` | Request |
| --- | --- |
| `setRotation` | `PUT /api/config/tracks`, `If-Match: version` |
| `createCollection` | `POST /api/collections` |
| `updateCollection` | `PUT /api/collections/{targetId}`, `If-Match: version` |
| `deployCollection` | `POST /api/collections/{targetId}/deploy` (`body` is null) |
| `createCup` | `POST /api/cups` |
| `updateCup` | `PUT /api/cups/{targetId}`, `If-Match: version` |
| `updateServerConfig` | `PUT /api/config/basic`, only the fields to change |

- A proposal is checked by the endpoint's own rules when it is made, and again when it is
  applied. An update carries the version the model gives from the lookup it is based on,
  as If-Match would from any client, so a change made after that lookup is a **409** on
  Apply; an update without a version is refused.
- The model never sees the server password, the admin and moderator Steam ids, or a cup's
  password. Server settings it may read and propose are the race and lobby ones: name,
  welcome message, max players, lobby countdown, ready players, session mode, grid order,
  track vote, idle kick, bots, AI difficulty, teams, laps, time limit, elimination interval,
  damage, car restrictions, car resets, wrong-way limiter, weather and frequency. Not the
  password, Steam id lists, ports, LAN, log or mods.
- A cup update keeps the cup's password; a proposal that sets one is refused.
- At most 5 proposals per question.
- Users, starting, stopping and updating the server, and launch settings are out of its
  reach.

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
| POST | `{id}/activate` | Activate now. 202; the cup becomes active when the restart succeeds. Its settings are written to `server_config.cfg` at once, and taken back out if the restart is cancelled or fails (unless the file changed meanwhile) |

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
`maxLapsAllowed`, `messageDelayMs`, `suppressCommandsDuringRace`) and `publicSite`
(`feedItemCount`); later settings pages add sections for what changes game behaviour,
never programs or paths. The WPF Configuration tab edits the same `vote` section;
`publicSite` is edited on the web only. The AI assistant's settings are in
user-settings.json, not here (see [AI assistant](#ai-assistant)).

| Method | Path | Purpose |
| --- | --- | --- |
| GET | | Every section: `{ vote, publicSite }` (`SettingsResponse`), each with its `version` |
| GET | `vote` | The voting settings (`VoteSettingsResponse`), with its `ETag` |
| PUT | `vote` | Change the fields in the body; the rest keep their values. Needs `If-Match`. Answers `VoteSettingsResponse` |
| GET | `publicSite` | The front page's settings (`PublicSiteSettingsResponse`), with its `ETag` |
| PUT | `publicSite` | As for `vote`. Answers `PublicSiteSettingsResponse` |

Each section has its own typed route, so the web app gets types for it; a section that
does not exist has no route (404).

- A PUT with a stale `If-Match` gets 409 with the section as it is now; without one, 428.
  A body that repeats `version` is fine: `If-Match` is what counts.
- Unknown fields, wrong types and out-of-range values are 400 with a field error:
  `mode` is `Off`, `Voting` or `Direct` (any case); `directCooldownSeconds` 0-3600,
  `voteTimeoutSeconds` 1-3600, `maxLapsAllowed` 1-999, `messageDelayMs` 0-5000,
  `feedItemCount` 1-50, `baseUrl` an http or https address, `dailyRequestLimit` 1-100000.
- `apiKey` is **write-only**: no answer ever carries it, only `apiKeySet` (true when one is
  stored). Send a new key to replace it and `""` to remove it; a body that repeats
  `apiKeySet` is fine. A blank `baseUrl` or `model` goes back to the default
  (`https://api.openai.com/v1`, `gpt-5-mini`). The key is kept in the controller's database
  as it is, like the rest of the section, so treat the database and its backups as secret.
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
| POST | `import/trackdump` | The content of a WreckfestTrackDump `tracks.json` as the JSON body. Adds what the catalogue lacks (below) and answers with a report. **400** for anything but `formatVersion` 1, **413** over 16 MB. |
| POST | `import/trackdump/images` | `multipart/form-data`: `dump` (the `tracks.json`) and `images` (the PNGs `wftracks imagedump` wrote, each named `<source>/<name>.png`). Stores the menu images (below). **400** for a bad dump or over 500 files, **413** over 64 MB. |
| GET | `images` | Every stored image without its bytes: `{ trackId, variantId, size, origin, hash, bytes, isOverlay }`. |
| PUT | `tracks/{id}/images/{size}`, `variants/{id}/images/{size}` | `multipart/form-data` with `file`: the admin's own image of that size (`small` or `large`), which shows instead of an imported one. PNG, JPEG or WebP, by its bytes, up to 4 MB. Answers the row's images. **400** for anything else. |
| DELETE | `tracks/{id}/images/{size}`, `variants/{id}/images/{size}` | 204: removes the admin's upload; an imported image shows again. **404** when there is none. |

**Concurrency.** A track or variant carries a `version`, and `GET {id}` returns it as
`ETag: "3"`. `PUT {id}` must send it back as `If-Match: "3"`: without the header the answer is
**428**, and when someone saved in between it is **409** with the row as it is now
(and its new `ETag`), so the editor can offer to reload or overwrite.

A **409** for a row in use names the collections in its title and lists them in
`collections`, so the Track Browser can say what to change first.

**Importing a track dump** (#121). WreckfestTrackDump
(`wftracks dump <dir> -o tracks.json`) reads the game's and mods' files on whatever PC has
them; the admin uploads the resulting file from the Tracks page. Only the file's content is
sent: the controller reads no game files and takes no path. The import follows the rule of
the catalogue's own data migrations: **it adds what is missing and never changes what is
there**, built-in or admin-added, edited or not.

- **Variants** match by `gameId` against `variantId`, ignoring case. A match keeps its name,
  mode, voting flag, tags, hidden flag and track. Only its `trackLengthM` (metres per lap,
  the game's `.tcat` value, which nothing else sets and `reset` leaves alone) is filled in
  or refreshed when the dump has one; derby arenas have none. A new variant is added with
  `isBuiltIn: false` and `allowedForVoting: false`, so nothing reaches votes until an admin
  allows it. An id outside `^[A-Za-z0-9_]{1,64}$`, a game mode other than 1 (racing) or 4
  (derby), or an id a second source in the same file repeats (the base game's wins) is
  skipped.
- **Tracks** are never changed. A new variant joins the track that holds most of its
  siblings from the same dump track, so a new layout of a built-in joins the built-in.
  Otherwise it goes to the track an earlier import made for the same dump track (kept
  internally as its dump id, `<source>/<folder>`), then to a built-in or admin track whose
  key is exactly the folder name, from the same source, unless that track already holds
  another folder's variants. Failing both, a new track is made:
  not built-in, every weather, origin `BaseGame` for the base game (the dump does not say
  which DLC a track is from) or `Workshop` for a mod, linked to its mod. Its key is the
  first one no track has: the folder (with characters outside `[A-Za-z0-9_]` made `_`),
  then `<folder>_<workshop id>`, then numbered. Two dump tracks are never merged because
  their keys collide: `foo-bar` and `foo_bar` get tracks of their own. A dump track whose
  id is over 200 characters (a very long folder name) is skipped whole, since a track made
  from it could not be found again.
- **Mods** match by `folderName` (the workshop id for a workshop item). A match keeps its
  name; only a missing `workshopId` is filled in. A new mod is added for each mod in the
  file that has tracks, named from its `modinfo.modi`. Existing tracks are not linked to it:
  the built-in workshop tracks keep `mod: null` until an admin sets it.
- Collection entries saved with a variant id the catalogue did not know are linked to it.
- **Names** of what it adds lose the game's colour codes (`^0`-`^9`, which would show as text)
  and doubled spaces. A new variant's name also loses its track's name in front, as a whole
  word, since the catalogue shows "Track - Variant": "Tomelilla 01" on Tomelilla becomes
  "01". A name that would be left empty ("Gardemoen" on Gardemoen) is kept. Names already in
  the catalogue are never changed.
- Importing the same file again changes nothing. The whole import is one transaction.

The report: `{ tool, generatedAt, mods, tracks, variants, skipped, diagnostics }`. `mods`,
`tracks` and `variants` are `{ added, updated, unchanged, skipped }` (tracks are never
`updated`); `skipped` lists the first 100 `{ id, reason }`; `diagnostics` counts the dump's
own diagnostics as `{ code, severity, count, meaning }`.

**No text from the dump that could hold a path is answered.** The dump's diagnostic messages
can name local folders (`duplicate-source` does, and read failures carry exception text),
so they are never passed on: `meaning` is the controller's own sentence for the code, and an
unknown code counts as `other`. A skipped item's `id` is rebuilt as `source/folder/gameId`
from parts that are plain names, with `?` for any part that is not. `tool` and `generatedAt`
are null unless they are plain text and a date. Source `path`s are not read at all.

**Track and variant images** (#260). Each track and variant can have a *small* (250x116,
for lists) and a *large* (832x386, for a track page) menu image, kept in the database. The
loading, skew and results sizes the game also has are not kept. An image comes from a track
dump or from an admin's upload; an upload wins, and only the admin removes it.

- **Importing.** `wftracks imagedump <dir> -o <folder>` writes the images a dump names; the
  Tracks page's import takes that folder (with its `tracks.json`), imports the `tracks.json`
  first, then sends the small and large images in batches, each with the `tracks.json`.
  Only the file contents are sent. Variants match by `gameId`; a track matches by its dump
  id, else as the track holding most of the dump track's variants, which is how a built-in
  track (no dump id; `madman_stadium` is folder `bigstadium`) finds its images. Images go
  only to rows the catalogue has: import the `tracks.json` first. A re-import replaces an
  imported image, and removes one the dump no longer names; uploads are never touched.
  The report: `{ stored, unchanged, removed, notUsed, refused, unmatched }`, where `refused`
  counts files that were not PNG or over 4 MB, and `unmatched` the dump's tracks and variants
  with images that the catalogue lacks.
- **The base game's images are not on the dedicated server.** Dump the game itself.
- **Layout outlines.** The base game's variant images are not pictures: they are white
  outlines of the layout on a clear background, which the game draws over the track's
  picture. Mods ship both kinds. A PNG whose pixels are mostly clear is stored as an outline
  (`isOverlay`): it is never shown as a picture, and is served as the variant's `layout`.

### Images — `api/images`

Anyone may fetch the catalogue's images: they are the game's own pictures, or ones an admin
chose to show. Tracks and variants are named by `key` and `variantId`, ignoring case, so a
link survives a re-import. Rate-limited per client, more loosely than `api/public`.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `tracks/{key}/{size}` | The track's picture of that size: its own, else its first visible variant's (by name). |
| GET | `variants/{variantId}/{size}` | The variant's picture, else its track's. |
| GET | `variants/{variantId}/layout` | The variant's layout outline, to draw over its picture. |

A picture is chosen by size first (a missing `small` falls back to `large`, never the other
way), then the variant's own before its track's, then an upload before an import. Layout
outlines are never pictures. **404** when there is none, **400** for a size other than
`small` or `large`.

Every answer has an `ETag` (the image's hash) and `X-Content-Type-Options: nosniff`, and
honours `If-None-Match`. It is cached for five minutes, or for a year when the link's `?v=`
names the hash of the image it gets: a page that knows the hash links the new image when it
changes. The content type is the one the bytes were checked to be, never what an upload
claimed.

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
- **`admin`** — a connection whose caller passes the Admin policy: the portal's token
  (the portal adds it to the negotiate request and the WebSocket upgrade it forwards for
  someone who manages this server) or, for scripts, `X-Api-Key`. Membership is decided
  once, when the connection opens: an open connection outlives the token, and losing
  access, signing out or a lock takes effect on the next connection.

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
| `RaceRecorded` | public | `{ raceId, trackId, endedAt }` — a finished race was saved; `GET api/public/races` and `GET api/public/feed` list it |
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

## The portal's API

WreckfestPortal (`Portal/WreckfestPortal`) holds the web accounts. Its own API is at the
site's root, beside the servers' paths; the web app's types come from
`web/src/api/portal-openapi.json`, which `OpenApiContractTests` in the portal's tests keeps
in step. Everything needs a signed-in user unless marked anonymous; changes need the
antiforgery token as described under [CSRF](#csrf). Signing in sets an ASP.NET Core Identity
cookie, `.WreckfestPortal.Auth`, for the whole site: `HttpOnly`, `SameSite=Strict`, `Secure`
over HTTPS, sliding over 14 days, its keys in a `keys` folder beside `portal.db` protected
with DPAPI. The account is reloaded on every request, so a lock, a password change or a lost
admin role applies at once. The portal never forwards its own cookies to a controller.

There are two kinds of account: a **global admin** (`isAdmin`) manages every server and the
accounts; anyone else manages only the servers in `servers`, by their `Id` in the portal's
settings. Accounts are made by admins, or by `WreckfestPortal.exe create-admin <user> <email>`
on the portal's PC, which is also the recovery when every admin is locked out. Passwords need
at least 10 characters; five failed sign-ins lock an account for 15 minutes.

### Servers — `api/servers`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | — | Anonymous. `{ title, intro, servers: [{ id, name, description, link, state, ... }] }`: the front page's cards, as the portal last polled them. Never a controller's address. |

### Auth — `api/auth`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `state` | Anonymous. `{ authenticated, user, setupRequired }`. `setupRequired` is true while no account exists. |
| GET | `antiforgery` | Anonymous. Sets the `XSRF-TOKEN` cookie for the whole site; 204. |
| POST | `login` | Anonymous. `{ login, password, remember }`. `login` matches the username, then the email. 200 with the user; **401** for a wrong login or password (the same answer either way); **423** while the account is locked. 10 attempts a minute per client IP (**429** with `Retry-After`), counting successes and failures alike. `remember` makes the cookie outlive the browser session. |
| POST | `logout` | Anonymous, so an expired session can still clear its cookie. Signs out of every server. 204. |
| GET | `me` | The caller's account. |
| PUT | `me` | `{ email, displayName, timeZone }`. `timeZone` is an IANA id (`Europe/Copenhagen`) or null for the browser's zone. Changing the email ends the caller's other sessions. |
| POST | `me/password` | `{ currentPassword, newPassword }`. 204. Keeps this session and ends the others. |

### Users — `api/users` (global admins)

| Method | Path | Purpose |
| --- | --- | --- |
| GET | — | All accounts, by username. |
| GET | `{id}` | One account. |
| POST | — | `{ userName, email, password, displayName, timeZone, isAdmin, servers }`. 201. `password` is a temporary one for the owner to change. `servers` are server ids; one the portal does not list is **400**, and an admin's are ignored. |
| PUT | `{id}` | `{ userName, email, displayName, timeZone, isAdmin, servers }`. A new username or email ends that account's sessions (not the caller's own); a new role or servers apply on its next request. **409** for demoting yourself, or the last admin who can sign in. |
| DELETE | `{id}` | 204. **409** for your own account, or the last admin who can sign in. |
| POST | `{id}/password` | `{ newPassword }`. Admin reset: 204, ends the account's sessions. A rejected password leaves the old one in place. |
| POST | `{id}/lock` | Locks until unlocked and ends the account's sessions. **409** for your own account, and for the last admin not locked by an admin: a failed-sign-in lockout ends by itself, so it does not count. |
| POST | `{id}/unlock` | Lifts an admin lock or a failed-sign-in lockout. |

Any account write answers **409** when the account was changed by another request at the
same moment; reload it and try again. The checks that keep an admin happen inside SQLite's
write lock, so two admins removing each other at once leave one.

Account responses never include password hashes or security stamps:
`{ id, userName, email, displayName, timeZone, isAdmin, servers, isLockedOut, lockoutEnd }`.
