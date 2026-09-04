# Outbound webhooks

WreckfestController pushes events to an HTTP endpoint as they happen. Any endpoint
that can receive a JSON POST works — nothing here assumes a particular consumer.

Webhooks are optional. With no base URL configured they are disabled and the
application runs normally.

## Configuration

```json
"Webhooks": {
  "BaseUrl": "https://example.invalid/api/webhooks",
  "ApiKey": ""
}
```

| Setting | Meaning |
| --- | --- |
| `BaseUrl` | Prefix for every webhook path below |
| `ApiKey` | Sent as `X-API-Key` on each request when set |

Both are also editable from the Configuration tab.

> **This is the outbound key — the one this application presents to your endpoint.**
> It is not `Api:Key`, which is the inbound key callers must present to *us* (see
> [API.md](API.md)). Never set them to the same value: the outbound key is
> transmitted on every webhook POST, to a URL that may be plain HTTP, so reusing it
> would hand full server control to anyone who observed a single request.

## Transport

```
POST {BaseUrl}/{event}
Content-Type: application/json
X-API-Key: {Webhooks:ApiKey}      # only when configured
```

Delivery is best-effort. A failure is logged and the event is dropped — there is no
retry queue, so a receiver that is down loses those events.

## Events

| Path | Sent when |
| --- | --- |
| `player-joined` | A player or bot joins |
| `player-left` | A player or bot leaves |
| `players-updated` | The roster changes; carries the full current list |
| `track-changed` | The track changes |
| `event-activated` | A scheduled event activates |
| `server-started` | The server process starts — PID, name, start time |
| `server-stopped` | The server stops — PID, stop method |
| `server-restarted` | Restart — old PID, new PID, method |
| `server-attached` | The controller attaches to a running process |
| `server-restart-pending` | Countdown warning — minutes remaining |
| `console-logs` | Buffered console output, flushed on a timer |

`console-logs` differs from the rest: it is sent by `ConsoleLogWebhookSender` on a
timer with a payload of `{ "logs": [ "line", ... ] }`, batching whatever accumulated
since the last flush, rather than one request per event.

## Receiving them

A receiver needs to accept `POST` on each path above, compare `X-API-Key` against its
own copy of the key, and return 2xx. Anything non-2xx is logged here and the payload
is discarded.

Only implement the events you care about — unimplemented paths simply return 404 and
are logged as failures on this side.
