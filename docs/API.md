# HTTP API

WreckfestController hosts a REST API alongside the desktop application. Browsers sign
in with a cookie; scripts send an API key. There is no assumption about what is on the
other end.

All routes are prefixed `api/` and return JSON.

## Authentication

Every endpoint requires an authenticated caller, in one of two ways:

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

Authorization uses a fallback policy, so an endpoint is protected unless it is
explicitly marked `[AllowAnonymous]`. A test pins the list of anonymous endpoints
(`GET auth/state`, `GET auth/antiforgery`, `POST auth/login`, `POST auth/logout`, and
the [live-update hub](#live-updates--hubsserver)), so one cannot appear by accident. Requests to paths that match no
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
| `HttpPort` / `HttpsPort` | defaults 5100 / 5101 |

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
on one Windows host. A value outside 1–65535 is ignored with a warning and the
default is used.

> HTTPS URLs are filtered out when no valid certificate is available — a startup
> safeguard for running as a WPF app — so the configured HTTPS port may not
> actually be listened on.

## Endpoints

The auth and users endpoints return validation failures as **400**
`ValidationProblemDetails`: `{ "errors": { "<field>": ["message", ...] } }`, with fields
named as in the request. The older controllers below still answer `{ message }`.

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
| GET | `version` | Controller version |
| GET | `status` | Running state and tracked process |
| POST | `start` | Start the server |
| POST | `stop` | Graceful stop |
| POST | `forcestop` | Force stop |
| POST | `restart` | Graceful restart |
| POST | `forcerestart` | Force restart |
| POST | `update` | Run the server update |
| POST | `command` | Send a console command. Body: `ServerCommandRequest` |
| POST | `attach/{pid}` | Attach to an existing process |
| POST | `inject/{pid}` | Inject the console hook into a process |
| POST | `inject` | Inject into the already-tracked process |
| GET | `logfile?lines=100` | Tail the server's log file from disk — the one deliberate exception to hook-only I/O (see below) |
| GET | `players` | Current roster |

`logfile` is the only endpoint that reads server output from disk rather than from the
injected hook. It is kept on purpose: WreckfestWeb's log viewer depends on it, and it is
the only way to see output from before attachment. Nothing else in the app consumes it —
no tracker, roster or chat path is fed from the file — so the hook-only contract still
holds for everything that drives state. Treat what it returns as history, not live state:
it answers even when no hook is injected, and its content can predate the current
process.

Injection is refused unless the target process is already attached, and refused when
the detected game build does not match `WreckfestServer:SupportedBuild`. Both return
a failure result rather than throwing.

### Configuration — `api/config`

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `basic` | Basic server configuration |
| PUT | `basic` | Update it. Body: `ServerConfig` |
| GET | `tracks` | Event-loop tracks |
| PUT | `tracks` | Update them. Body: `UpdateEventLoopTracksRequest` |
| GET | `tracks/collection-name` | Current track collection name |
| GET | `serverinfo` | Server info snapshot |

`PUT basic` is a **partial update**: send only the `ServerConfig` fields to change
(names are case-insensitive), and every omitted field keeps its current value. The
request is rejected with 400, and nothing is written, when it names an unknown field,
gives a value of the wrong type or `null`, or puts a line break in a string.

`PUT tracks` replaces the whole event loop. It is rejected with 400 unless
`collectionName` is non-empty (at most 128 characters), `tracks` is present, every
entry's `track` is a game id (`^[A-Za-z0-9_]{1,64}$`), `laps`, `bots` and `numTeams`
are not negative, `carResetDisabled` and `wrongWayLimiterDisabled` are `0` or `1`,
text values are at most 128 characters, and no value contains a line break. An empty
`tracks` list is allowed. Collections are checked by the same rules.

### Events — `api/events`

Scheduled events: server settings and a rotation applied through a smart restart at a
set time, once or on a repeat. They live in the controller's database.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | | Every event, past ones included: `{ count, events }`, upcoming soonest first, then finished ones |
| POST | | Create. Body: `EventRequest`. 201 with an `ETag` |
| GET | `{id}` | One event, with its `ETag` |
| PUT | `{id}` | Replace. Needs `If-Match` |
| DELETE | `{id}` | Delete. Needs `If-Match` |
| GET | `current` | The active event, or 204 when there is none |
| GET | `upcoming` | Events whose next occurrence is more than 5 minutes away |
| GET | `due` | Events the scheduler will start at its next check |
| GET | `summary` | `{ totalEvents, activeEvents, upcomingEvents, dueEvents, lastUpdated }` |
| POST | `{id}/activate` | Activate now. 202; the event becomes active when the restart succeeds |

`EventRequest` is `{ name, description?, startTime, timeZone?, repeat?, serverConfig?,
collectionId?, tracks?, collectionName? }`:
- `startTime` must carry an offset (`2026-10-02T18:00:00Z`); an unzoned time is 400.
- `timeZone` is an IANA id such as `Europe/Copenhagen`, default `UTC`. A repeat's
  `time` and `days` are wall-clock values in that zone, so a weekly 20:00 event stays
  at 20:00 local across daylight saving.
- `repeat` is `{ frequency: "daily" | "weekly", days: [0-6], time: "HH:MM" }`, days
  counting from Sunday = 0. Weekly needs at least one day.
- `serverConfig` overrides only the fields that are set. Text values must not contain
  line breaks.
- Give the rotation either as `collectionId`, which deploys that collection's tracks
  as they are **at activation**, or inline as `tracks` (checked like `PUT
  api/config/tracks`) with an optional `collectionName`. Neither leaves the server's
  rotation alone. Deleting a collection copies its tracks into the events linked to
  it, so they still deploy them.

Responses add `repeatDescription`, `nextOccurrence`, `lastOccurrence`, `lastOutcome`, `isActive`,
`activatedAt`, `createdBy` (the signed-in user who created it; null for API-key
callers), `createdAt`, `updatedAt` and `version`. For a linked event, `tracks` and
`collectionName` are the collection's current ones.

**Concurrency.** `version` and the ETag cover what an admin edits. PUT and DELETE
without `If-Match` get 428, and with a stale one 409 with the current event. The
scheduler's own fields (`nextOccurrence`, `lastOccurrence`, `lastOutcome`, `isActive`, `activatedAt`)
change without a new version, so the scheduler finishing an occurrence never
invalidates an open editor, and an edit that leaves `startTime`, `timeZone` and
`repeat` alone never moves the schedule.

**Scheduling.** An occurrence starts 5 minutes early, for the players' countdown.
Each occurrence gets one attempt, and then the event moves to its next occurrence (or
finishes, for a one-off event). `lastOutcome` records how it ended:
- `Activated`: the restart succeeded, or the event was already active;
- `Failed`: the settings could not be written, or the restart failed;
- `Cancelled`: an admin cancelled the restart;
- `Missed`: not started within 15 minutes of its time, because the app was not running
  or another restart ran too long.

Nothing is retried. Every outcome is sent to signed-in clients as
`EventOccurrenceEnded`, and anything but `Activated` is also logged as a warning.
Running a missed or failed event anyway is the admin's call, with `activate`. At most one event is active; activating another deactivates it. A manual
activation within the 5 minutes before an occurrence counts as that occurrence.

1.x's `POST schedule` (the Laravel bulk push) is gone, and 2.0 starts with no events:
`event-schedule.json` is neither read nor deleted.

`activate` answers 409 when the event is already active, another restart is running,
or the event's settings cannot be written to the server config (with a `reason`).

### Catalogue — `api/catalogue`

The tracks, variants, tags, weather and mods the controller knows. The database ships
with a built-in catalogue: the base-game tracks, plus the workshop tracks from 1.x's
default vote list. Every built-in variant starts allowed for voting. Built-in rows are fully editable,
but a built-in track's `key` and a built-in variant's `variantId` are fixed, and they
cannot be deleted: hide them instead, and `reset` restores what shipped.

Hidden tracks and variants are left out of lists unless `includeHidden=true`, but stay
readable by id.

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

A SignalR hub pushes server events to the web UI, replacing the outbound webhooks.
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
| `EventActivated` | public | `{ eventId, eventName, timestamp }` |
| `ServerStarted` | public | `{ processId, processName, startTime, timestamp }` |
| `ServerStopped` | public | `{ processId, stopMethod, timestamp }` — `Graceful` or `Force` |
| `ServerRestarted` | public | `{ oldProcessId, newProcessId, restartMethod, timestamp }` |
| `ServerAttached` | public | `{ processId, processName, startTime, timestamp }` |
| `ServerRestartPending` | public | `{ minutesRemaining, eventName, eventId, scheduledRestartTime, timestamp }` |
| `EventOccurrenceEnded` | admin | `{ eventId, eventName, occurrence, outcome, timestamp }` — a scheduled occurrence was dealt with; `outcome` is `Activated`, `Failed`, `Cancelled` or `Missed` |
| `ConsoleLog` | admin | `{ logs: [string] }` — console lines, batched about once a second (at most 1000 per message) |

Timestamps are UTC. Events raised while no client is connected, or while the API is
stopped, are not queued. The payload types are the records in `Hubs/IServerHubClient.cs`.

```js
const connection = new signalR.HubConnectionBuilder().withUrl("/hubs/server").build();
connection.on("TrackChanged", ({ trackId }) => console.log(trackId));
await connection.start();
```

While the database is unavailable (recovery mode) the hub answers 503, like the API.

## ⚠️ Breaking change

Authentication was introduced after the API had been in use unauthenticated. Any
existing client calling `api/*` without an `X-Api-Key` header now receives **401** —
including server control, configuration updates, track rotation and player list.

With `AllowRemote: false`, a client must also run on the same host.

Existing integrations must be updated to send the header, and `Api:Enabled` must
be set to `true` — the API no longer starts by default.
