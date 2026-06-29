# KhaozEngine.Server.Admin

Opt-in HTTPS admin endpoint for a KhaozEngine game server. A minimal Kestrel listener (TLS + a single bearer token)
exposing the generic `ServerAdmin` surface as a small REST API: list/teleport/kick/broadcast online players,
enumerate persisted accounts, ban/unban.

This is the only KhaozEngine package that references ASP.NET Core (via a `FrameworkReference`), and it is **not**
bundled in the `KhaozEngine.Server` umbrella - add it explicitly when you want an admin endpoint, so a sim server
that does not never pulls the web stack.

```csharp
var admin = new ServerAdmin(worldServer, new WorldStoreBanStore(store), store);
await using var endpoint = new AdminHttpServer(admin, new AdminEndpointOptions
{
    Port = 9443,
    BearerToken = "<long-random-secret>",
    Certificate = AdminTlsCertificate.CreateSelfSigned("my-game-admin"),
});
await endpoint.StartAsync();
```

Routes (under `/admin`, all require `Authorization: Bearer <token>`): `GET /online`, `POST /teleport`, `POST /kick`,
`POST /broadcast`, `GET /accounts?prefix=`, `GET /bans`, `POST /ban`, `POST /unban`. See
`docs/USING-KHAOZENGINE.md` ("Server administration").
