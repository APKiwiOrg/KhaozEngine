# KhaozEngine.Http

A reusable **bounded send-with-retry** helper over a caller-supplied `HttpClient`. Absorbs a cold or
momentarily-stalled backend (a warming-up App Service, a transient 503 behind a load balancer) so the caller
sees a real response instead of a client timeout, without retrying forever or retrying a definitive rejection.

This started as a bounded retry Nullwake added to its sign-in client (`IdentityBootstrap.SendWithRetryAsync`)
to absorb a free-tier App Service cold start, which surfaced client timeouts on roughly half of first-attempt
sign-ins. Every game HTTP client wants the same policy (save, name-check, wallet clients, and more), so it
moved here as a reusable engine helper. Pure .NET, zero dependencies.

## Usage

```csharp
using KhaozEngine.Http;

using var client = new HttpClient();

HttpResponseMessage response = await client.SendWithRetryAsync(
    () => new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/sign-in")
    {
        Content = JsonContent.Create(request),
    },
    ct: cancellationToken);
```

`HttpRetry.SendAsync(client, requestFactory, policy, ct)` is the static form the extension method forwards to,
for a call site that would rather not extend `HttpClient`.

The request builder is a **factory**, not a shared `HttpRequestMessage`, because a sent request cannot be
resent. Each attempt builds and disposes its own message, so build the request body fresh in the lambda rather
than capturing a pre-built one.

## Policy

`HttpRetryPolicy` is an immutable options record. Every property validates on assignment, including through a
`with` expression, so an invalid policy can never be built.

```csharp
var policy = HttpRetryPolicy.Default with
{
    MaxAttempts = 5,
    Backoff = attempt => TimeSpan.FromSeconds(attempt + 1),
};

HttpResponseMessage response = await client.SendWithRetryAsync(BuildRequest, policy, cancellationToken);
```

- **`MaxAttempts`** (default 3): the first attempt plus up to `MaxAttempts - 1` retries.
- **`PerAttemptTimeout`** (default 12 seconds): the time budget for a single attempt, via a linked
  `CancellationTokenSource`. This is deliberately SHORTER than a typical `HttpClient`-wide timeout, so one
  stalled attempt is abandoned and retried instead of eating the caller's whole request budget on a single
  hung connection. Set to `Timeout.InfiniteTimeSpan` to disable it.
- **`Backoff`** (default 700 ms / 1600 ms / 3000 ms): maps the zero-based attempt index that just failed to
  the delay before the next attempt. The injectable no-sleep seam for tests: pass `_ => TimeSpan.Zero`.
- **`IsRetryableStatus`** (default 408, 500, 502, 503, 504): decides whether a completed response's status is
  worth retrying. Only consulted on a non-final attempt.

## Semantics

- The caller's `ct` is never retried. A cancellation already requested when `SendAsync` is called throws
  before any attempt is made. A cancellation that arrives mid-attempt propagates immediately.
- A per-attempt timeout (as opposed to the caller's own `ct`) surfaces as a transport fault and is retried
  like any other one, distinguished from the caller's cancellation by checking `ct.IsCancellationRequested`.
- A retryable status on a non-final attempt disposes the response and waits `Backoff(attempt)` before
  retrying. On the final attempt the response is returned as-is, so the caller sees the real status.
- A non-retryable status (401, 400, or anything `IsRetryableStatus` rejects) is returned immediately on the
  first attempt. Definitive rejections are never retried.
- A transport fault (`HttpRequestException`, or a `TaskCanceledException` / `OperationCanceledException` that
  is not the caller's own cancellation) is retried with backoff, and rethrown once the final attempt fails.
- Backoff delays honor the caller's `ct`, via `Task.Delay(delay, ct)`.

## Frameworks: `net8.0` and `net10.0`

This package multi-targets `net8.0` alongside the engine-wide `net10.0`, the same precedent 10.90.0 set for
`ServerStatus` / `Diagnostics` / `Primitives`: it has no dependencies, so it is exactly what an Azure Functions
isolated-worker app on the Linux Consumption (Y1) plan can also reference (that plan supports .NET 8, its
newest LTS, but not .NET 10).
