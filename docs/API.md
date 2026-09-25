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
- **Cookie** (the web UI): an ASP.NET Core Identity cookie, issued by the sign-in
  endpoints that arrive with the web UI. It is `HttpOnly`, `SameSite=Strict`, marked
  `Secure` when the request came over HTTPS, and slides over 14 days. Its encryption
  keys are kept in a `keys` folder beside the database, protected with DPAPI, so
  restarting the controller does not sign anyone out. Changing a user's password or
  locking them out ends their other sessions on the next request.

When the `X-Api-Key` header is present it alone decides: a wrong key is rejected even
if a valid cookie is also sent.

A missing or rejected credential returns **401** with no body, never a redirect.

Authorization uses a fallback policy, so an endpoint is protected unless it is
explicitly marked `[AllowAnonymous]`. A test pins the list of anonymous endpoints
(none yet), so one cannot appear by accident. Requests to paths that match no
endpoint also get 401 rather than 404.

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
