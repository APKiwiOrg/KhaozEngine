# KhaozEngine.Server.Admin

Opt-in HTTPS admin endpoint for a KhaozEngine game server. A minimal Kestrel listener (TLS + a single bearer token)
exposing the generic `ServerAdmin` surface as a small REST API: list/teleport/kick/broadcast online players,
enumerate persisted accounts, ban/unban, and any game-registered admin actions.

This is the only KhaozEngine package that references ASP.NET Core (via a `FrameworkReference`), and it is **not**
bundled in the `KhaozEngine.Server` umbrella - add it explicitly when you want an admin endpoint, so a sim server
that does not need one never pulls the web stack.

```csharp
var admin = new ServerAdmin(worldServer, new WorldStoreBanStore(store), store);
admin.RegisterAction("set-time", payload =>
{
    float t = payload?.GetProperty("timeOfDay").GetSingle() ?? 0f;
    gameClockQueue.Enqueue(t);
    return AdminActionResult.Accepted();
});
await using var endpoint = new AdminHttpServer(admin, new AdminEndpointOptions
{
    Port = 9443,
    BearerToken = "<long-random-secret>",
    Certificate = AdminTlsCertificate.CreateSelfSigned("my-game-admin"),
});
await endpoint.StartAsync();
```

`Port = 0` asks the OS for a free port instead, and `endpoint.BoundPort` reports the one Kestrel took once
`StartAsync` has returned. Prefer that over picking a port from a throwaway probe socket: the probe has to release
the port before Kestrel can bind it, and another listener on the host can take it in that window.

## Pre-auth exposure

The TLS handshake completes before the bearer token is ever read, so an unauthenticated peer can make this endpoint
do RSA work and hold connections open no matter what the token is. Three `AdminEndpointOptions` knobs bound that,
written onto `KestrelServerLimits`, and they default tighter than Kestrel's own because an admin endpoint serves an
operator and a script rather than the public web:

| Option | Default | Kestrel's default | What it bounds |
|---|---|---|---|
| `MaxConcurrentConnections` | 64 | unlimited | How many handshakes an unauthenticated peer can hold at once. |
| `RequestHeadersTimeout` | 10 s | 30 s | Slowloris. Runs before the bearer check, so it is the pre-auth one. |
| `KeepAliveTimeout` | 30 s | 130 s | How long an idle connection keeps occupying a slot. |

Set any of them to `null` to leave Kestrel's own value. A non-positive value throws from the `AdminHttpServer`
constructor rather than later inside Kestrel's start. None of this replaces the two real mitigations: keep the
endpoint on loopback or behind a tunnel, and keep the token long and random.

Routes (under `/admin`, all require `Authorization: Bearer <token>`): `GET /online`, `POST /teleport`, `POST /kick`,
`POST /broadcast`, `GET /accounts?prefix=`, `GET /bans`, `POST /ban`, `POST /unban`, `GET /actions` (lists registered
action names), `GET /actions/{name}` (dispatches with a null payload), `POST /actions/{name}` (dispatches with an
optional JSON body, an absent, empty, whitespace-only, or JSON-null body all reaching the handler as null). See
`docs/USING-KHAOZENGINE.md` ("Server administration").
