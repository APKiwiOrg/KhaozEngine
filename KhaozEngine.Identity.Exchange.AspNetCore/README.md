# KhaozEngine.Identity.Exchange.AspNetCore

The ASP.NET Core half of a game's auth service: `MapAuthExchange` mounts `POST /auth/exchange` over the game's
`AuthExchange` from `KhaozEngine.Identity.Exchange`, and `AddAuthExchangeHosting` configures forwarded headers and an
optional server-wide body cap. The decision itself stays in the core, which has no HTTP type in it.

Opt-in, in NO umbrella. It references `KhaozEngine.Identity.Exchange` and the `Microsoft.AspNetCore.App` shared
framework (a `FrameworkReference`) and nothing else. With `KhaozEngine.Server.Admin` it is one of the two engine
packages that reference ASP.NET Core, so only a dedicated auth service carries it. A game server and a game client
never need it.

## Mounting it

```csharp
byte[] key = SigningSecret.Load(Environment.GetEnvironmentVariable, "MYGAME_TOKEN_SECRET")
    ?? throw new InvalidOperationException("MYGAME_TOKEN_SECRET is not set.");
var exchange = new AuthExchange(
    new IIdentityValidator[] { new DiscordTokenValidator(clientId, discordHttp) },
    accounts, key, new AuthExchangeOptions { TokenLifetime = TimeSpan.FromDays(7) });

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddAuthExchangeHosting(new AuthExchangeHostingOptions
{
    TrustedProxies = TrustedProxyNetworks.PrivateRanges,   // empty by default: forwarded headers off
    MaxRequestBodyBytes = 8 * 1024,                         // optional server-wide Kestrel cap
});
WebApplication app = builder.Build();
app.UseAuthExchangeHosting();                               // forwarded headers, before the endpoints

app.MapGet("/healthz", () => Results.Ok());                 // game-owned, untouched by the exchange limits
app.MapAuthExchange(exchange, new AuthExchangeEndpointOptions { IncludeBanDetails = false });
app.Run();
```

`UseAuthExchangeHosting` throws when `AddAuthExchangeHosting` was not called. `MapAuthExchange` works without either,
because the endpoint carries its own bounds, but it then logs a warning at mapping time that the per-client window
keys on the connection's peer address. Once `AddAuthExchangeHosting` is called, `MapAuthExchange` throws until
`UseAuthExchangeHosting` has run, because without the middleware the named proxies are ignored and every caller
behind them shares one bucket.

## Public API

| Type | What it does |
|---|---|
| `AuthExchangeEndpoints` | `MapAuthExchange(routes, exchange, options?)` returns the `RouteHandlerBuilder`. `ToHttpResult(result, includeBanDetails)`, `Unavailable()` and `StatusCodeFor(outcome)` are the mapping, public for a game that maps a route of its own over the core. |
| `AuthExchangeEndpointOptions` | `Pattern` (`/auth/exchange`), `MaxRequestBodyBytes` (8 KiB, at most 1 MiB), `PermitsPerClientPerMinute` (5), `Ipv6PartitionPrefixLength` (64), `MaxConcurrentExchanges` (20), `MaxQueuedExchanges` (20), `IncludeBanDetails` (false). Validated when the endpoint is mapped. |
| `AuthExchangeHosting` | `AddAuthExchangeHosting(builder, options?)` and `UseAuthExchangeHosting(app)`. |
| `AuthExchangeHostingOptions` | `TrustedProxies` (empty, which turns forwarded headers off, and never a catch-all: any /0, `default(IPNetwork)` included, or an IPv6 network holding the whole IPv4-mapped block is refused) and `MaxRequestBodyBytes` (null, which leaves Kestrel's default). |
| `TrustedProxyNetworks` | `PrivateRanges` (RFC 1918 in both families), `Loopback` (a same-host proxy), and `WithBothFamilies(networks)`, the rule the hosting applies to any list, which refuses a catch-all. |
| `AuthExchangeClientKey` | `For(peer, ipv6PrefixLength)`, the per-client partition key: the IPv4 address, a mapped address folded to IPv4, the IPv6 /64, or one shared key for a connection with no address. |

## The answers

| Outcome | HTTP | Body |
|---|---|---|
| `Ok` | 200 | `ok` with token, expiry, subject and display name |
| `NotWhitelisted` | 403 | `not_whitelisted` with subject and display name |
| `Banned` | 403 | `banned` with subject and display name, reason and expiry only with `IncludeBanDetails` |
| `InvalidCredential` | 401 | `invalid_credential` |
| `Unavailable` | 503 | `unavailable`, whichever dependency failed |
| `Malformed`, or a body that is not the request's JSON | 400 | none |
| body over `MaxRequestBodyBytes` | 413 | none |
| per-client window spent, or global bound and queue full | 429 | none. The per-client refusal carries `Retry-After: 60`, the global one carries none |

Every answer carries `Cache-Control: no-store`. None carries a CORS header or a cookie. The body is read and written
with fixed web-default JSON options, so a host's global JSON settings cannot change the wire. A client turns the bare
429 into `AuthExchangeStatuses.RetryLater`, which the service never sends.

## The bounds, and their order

The endpoint carries every bound itself, so each holds whatever middleware the host runs, and nothing depends on the
host calling `UseRateLimiter` or leaving its global limiter alone. Per request, in order:

1. **Per-client window.** A fixed one minute window with no queue, keyed by `AuthExchangeClientKey`. IPv4 callers are
   keyed on the address, IPv4-mapped IPv6 callers are folded to IPv4 first, and IPv6 callers share one window per /64,
   so one host rotating through its address block is one client.
2. **Body cap.** Endpoint metadata that routing applies to the server's per-request limit, and the handler's own
   bounded read. A declared length over the cap is refused before a byte is read.
3. **Global bound.** 20 exchanges at once with a 20 deep oldest-first queue, scoped to this endpoint, so a health probe
   beside it is never starved. The body is read before this bound, so a slow upload holds a connection and never a
   place reserved for work.
4. **Parse, then the exchange** under the caller's cancellation. A caller who disconnects cancels the provider call and
   the store call through the core, and no outage is logged for it.

A flood is refused before any JSON is parsed. The core adds its own bounds before any provider cost: a credential of at
most 4096 characters, all visible ASCII, and a 10 s provider deadline (`AuthExchangeOptions`).

## Forwarded headers

Behind a TLS-terminating proxy every request arrives from the proxy, so an unconfigured service puts every caller in
one bucket and the limiter becomes a self-inflicted outage. `TrustedProxies` names the networks `X-Forwarded-For` and
`X-Forwarded-Proto` are believed from:

- **Off unless named.** An empty list believes no peer, loopback included. The framework's default loopback entries
  are removed whenever the hosting is added, so "trusted" means exactly the listed networks.
- **Both address families.** A dual-mode socket reports an IPv4 proxy as `::ffff:10.0.0.1`, and the same host with IPv6
  disabled reports it as `10.0.0.1`. Every listed network is registered with its twin in the other family, so a list
  typed in either form trusts the proxy in both.
- **Last hop only.** The forward limit is one, so only the address the trusted proxy appended is read, and a client
  cannot choose its bucket by prepending addresses of its own.
- **No catch-all.** A /0 in either family, which is what an unset `IPNetwork` is, or an IPv6 network holding the whole
  IPv4-mapped block would let every caller name its own bucket. `AddAuthExchangeHosting` and `WithBothFamilies` refuse
  one, naming its position in the list and not its value.
- **`PrivateRanges` trusts every private host.** Any machine that can reach the service over RFC 1918 can name its own
  client address. Name the proxy's own subnet when the private network is shared with anything you do not control.

## Logging

One line per exchange, in the `KhaozEngine.Identity.Exchange.AspNetCore.AuthExchangeEndpoints` category, with the
outcome, the cause (`AuthExchangeCause`, or `BodyTooLarge`, `UnreadableBody` or `UnparseableBody` for a body that never
reached the core), the provider id, the HTTP status and the elapsed milliseconds. `Unavailable` logs at Warning with
the `Fault` exception, and everything else at Information. An exception the core did not turn into an outcome is a
defect, logs at Error and still answers the 503 envelope. A rate-limit refusal and a caller cancellation log at Debug,
so a flood cannot turn the limiter into a log-volume amplifier.

No line the exchange writes carries the credential, the session token, the signing key, the subject, the display
name, the ban reason or the client address. The provider id is logged only once the core matched it against a
registered validator, so an arbitrary string a caller posts never reaches the log. A game's own store may quote
account data in an exception message, so route the Warning line where account data may go.
