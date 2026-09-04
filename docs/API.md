# HTTP API

WreckfestController hosts a REST API alongside the desktop application. Any client
that holds the API key can drive it — there is no assumption about what is on the
other end.

All routes are prefixed `api/` and return JSON.

## Authentication

Every request to `api/*` must carry the configured key:

```
X-Api-Key: <Api:Key>
```

`ApiKeyMiddleware` runs once, ahead of `MapControllers`, so the rule is uniform
across all controllers and a newly added controller is protected by default. The key
is compared with `CryptographicOperations.FixedTimeEquals`.

A missing or non-matching key returns **401** with no body.

There is deliberately **no local exemption** — loopback requests need the key too.

If `Api:Key` is empty or unset the API does not start at all. The desktop
application still runs; only the HTTP API is absent.

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

The API is **opt-in**, and starts only when `Enabled` is `true` **and** `Key` is
non-blank. Either missing means no port is bound at all, with a startup line saying
which condition failed. Binding a port that could only answer 401 would serve
nothing and still take the port from another instance.

| Setting | Effect |
| --- | --- |
| `Enabled: false` (default) | the API does not start; no port is bound |
| `Enabled: true`, `Key` blank | the API does not start either |
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
| GET | `logfile?lines=100` | Tail the log file |
| GET | `players` | Current roster |

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
