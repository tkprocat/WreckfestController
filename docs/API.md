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
(`GET auth/state`, `GET auth/antiforgery`, `POST auth/login`, `POST auth/logout`), so
one cannot appear by accident. Requests to paths that match no
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

There is deliberately **no local exemption**: loopback requests need credentials too.

> The key is read once when the API server starts, so changing it requires a
> restart.

## Binding

```json
"Api": {
  "Enabled": false,
  "Key": "",
  "AllowRemote": false,
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
| `HttpPort` / `HttpsPort` | defaults 5100 / 5101 |

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
`collectionName` is non-empty, `tracks` is present, every entry has a non-empty
`track`, and no value contains a line break. An empty `tracks` list is allowed.

### Events — `api/events`

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `schedule` | Replace the schedule. Body: `EventScheduleRequest` |
| GET | `current` | Currently active event |
| GET | `upcoming` | Future events |
| GET | `due` | Events due now |
| GET | `summary` | Schedule summary |
| GET | `{id}` | One event |
| POST | `{id}/activate` | Activate an event now |

## ⚠️ Breaking change

Authentication was introduced after the API had been in use unauthenticated. Any
existing client calling `api/*` without an `X-Api-Key` header now receives **401** —
including server control, configuration updates, track rotation and player list.

With `AllowRemote: false`, a client must also run on the same host.

Existing integrations must be updated to send the header, and `Api:Enabled` must
be set to `true` — the API no longer starts by default.

## Header casing

Inbound is `X-Api-Key`; the outbound webhooks this application *sends* use
`X-API-Key` (see [Webhooks.md](Webhooks.md)). HTTP header names are case-insensitive,
so both are correct — do not "fix" one to match the other.
