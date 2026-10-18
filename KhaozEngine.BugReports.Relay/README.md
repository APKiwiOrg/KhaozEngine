# KhaozEngine.BugReports.Relay

The host-agnostic in-game bug report relay. It turns an authenticated client bundle and an optional server
snapshot into a GitHub issue through a durable work queue. Reporter authentication, media storage, the work
queue, quota counters and GitHub sit behind `IReporterAuthenticator`, `IReportMediaStore`, `IReportWorkQueue`,
`IReportQuotaStore` and `IGitHubIssueClient`. `InMemoryReportMediaStore`, `InMemoryReportWorkQueue` and
`InMemoryReportQuotaStore` serve tests and local runs. The package is opt-in and sits in no umbrella.
Add it explicitly.

Depends on `KhaozEngine.BugReports`, `KhaozEngine.BugReports.Contracts`, `KhaozEngine.Diagnostics` and
`KhaozEngine.Http`. No web framework and no third-party package. Both relay packages target `net10.0`,
with no `net8.0` target. The first consumer uses Azure Functions Flex Consumption with .NET 10 isolated.

## Operations and responses

Construct `BugReportRelay(options, authenticator, media, work, quotas, github, time)` once with host-owned
seams and a `TimeProvider`. `BugReportRelayOptions` requires `GameId`, `GameDisplayName`, `RepositoryOwner`,
`RepositoryName`, `Labels`, `PublicMediaBase` and `ServiceKey`. `PublicMediaBase` must be an absolute URL
ending in `/`, use HTTPS off loopback, and contain at most 512 characters. The service key must hold at
least 32 characters. `Logger` is an optional engine `ILogger`, defaulting to `Log.Get("BugReports.Relay")`.

| Operation | Host binding | Behavior |
| --- | --- | --- |
| `IntakeAsync(request, cancellationToken)` | HTTP POST `bug-reports` | Authenticates the bearer token, checks blocked subjects and quota before reading the body, validates and stores the bundle, then enqueues `CreateIssue`. |
| `ServerSnapshotIntakeAsync(reportId, request, cancellationToken)` | HTTP POST `bug-reports/{reportId}/server` | Checks `X-Report-Service-Key` before reading the body, stores the first snapshot and enqueues `AppendServerSnapshot` for an accepted report. |
| `RevokeAsync(reportId, cancellationToken)` | Host administration | Writes a create-only private tombstone. Follow the folder deletion procedure in Host duties. |
| `ProcessWorkAsync(item, cancellationToken)` | Queue trigger | Creates an issue or appends a late snapshot. Completed work returns normally. Failures throw for host redelivery. |

`RelayRequest` carries headers, the unread body stream and an optional content length. Forward
`RelayResponse.StatusCode`, `JsonBody` and every response header unchanged. Do not buffer, deserialize or
otherwise read an HTTP body before the relay. The client sends a canonical report id in `Idempotency-Key`
and the matching bundle manifest. The relay also checks the configured game id.

| Status | Meaning |
| --- | --- |
| 202 | Accepted, with an `IntakeAccepted` report id and reference. Repeated accepted ids return the same reference. |
| 400 | Bad request, including a missing or invalid idempotency key, an id mismatch or malformed snapshot. |
| 401 | Missing, invalid, expired or revoked bearer token. |
| 403 | Blocked authenticated subject, or missing or incorrect server service key. |
| 413 | Body exceeds the cap, 16 MiB for bundles by default and 1 MiB for server snapshots. |
| 422 | Bundle validation failed, including a game id mismatch. |
| 429 | Quota exceeded or optimistic updates exhausted, with `Retry-After` in whole seconds. |

Quota defaults are 5 reports per UTC hour and 20 per UTC day per account, and 100 per UTC day globally.
Quota checks precede validation and idempotency, so invalid bundles and accepted resends consume quota.
The three counters are not a transaction. A later conflict can leave an earlier increment counted even
when intake returns 429. Five optimistic attempts fail closed with `Retry-After: 5`.
Every accepted resend whose issue number is still unset enqueues `CreateIssue` before returning 202,
regardless of age. An enqueue failure fails the request so the client retains its bundle. Worker claims
and result markers suppress duplicate work.

## Isolated-worker host sketch

The Functions dependencies belong to the host, never this package. Register the relay and its seams with
the host's dependency injection. This sketch uses the isolated worker's built-in HTTP model and a string
queue binding. `BugReportsStorage` is the host's queue connection configuration. With Functions' default
`/api` route prefix, pass a relay base ending in `/api/` to `HttpRelaySink` and `ServerSnapshotPoster`.
The [HTTP trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-http-webhook-trigger)
and [queue trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue-trigger)
references describe the host-owned bindings.

```csharp
using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class ReportFunctions(BugReportRelay relay)
{
    [Function("ReportIntake")]
    public async Task<HttpResponseData> Intake(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bug-reports")]
        HttpRequestData request, CancellationToken ct)
        => await Reply(request, await relay.IntakeAsync(Adapt(request), ct), ct);

    [Function("ReportServerSnapshot")]
    public async Task<HttpResponseData> Snapshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bug-reports/{reportId}/server")]
        HttpRequestData request, string reportId, CancellationToken ct)
        => await Reply(request, await relay.ServerSnapshotIntakeAsync(reportId, Adapt(request), ct), ct);

    [Function("ReportWork")]
    public Task Work(
        [QueueTrigger("bug-reports-work", Connection = "BugReportsStorage")]
        string message, CancellationToken ct)
        => relay.ProcessWorkAsync(ReportWorkItemJson.Parse(message), ct);

    static RelayRequest Adapt(HttpRequestData request)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value),
            StringComparer.OrdinalIgnoreCase);
        long? length = headers.TryGetValue("Content-Length", out string? value)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long count)
            ? count : null;
        return new RelayRequest(headers, request.Body, length);
    }

    static async Task<HttpResponseData> Reply(HttpRequestData request, RelayResponse result,
        CancellationToken ct)
    {
        HttpResponseData response = request.CreateResponse((HttpStatusCode)result.StatusCode);
        foreach (var header in result.Headers)
            response.Headers.Add(header.Key, header.Value);
        if (result.JsonBody is not null)
        {
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(result.JsonBody), ct);
        }
        return response;
    }
}
```

`AuthorizationLevel.Anonymous` leaves authentication to the relay, which checks the game's bearer token
or server service key. It does not make report intake unauthenticated. Let worker and parse exceptions
escape the queue function. Do not acknowledge failed work or retry GitHub issue and comment POSTs in-process.
`GitHubAppIssueClient(http, credentials, owner, repository, time)` uses `GitHubAppCredentials` for the app id,
installation id and RSA PEM key. Only installation-token exchanges use `HttpRetry`. The caller owns the
HTTP client and disposes the GitHub client at shutdown. Grant the app issue write access to the selected repository.

## Host duties

- Configure the queue trigger with `maxDequeueCount` 5 and `visibilityTimeout` at least `00:03:00`.
  The backoff lets busy redeliveries outlast the 10-minute claim lease before a message is poisoned.
  For the sketch above, failed work goes to `bug-reports-work-poison`.
- Run `PoisonQueueRequeuer.RequeueAsync(cancellationToken)` daily. It is supplied by
  [`KhaozEngine.BugReports.Relay.Azure`](../KhaozEngine.BugReports.Relay.Azure/README.md).
- Build both work and poison `QueueClient` instances with `ReportQueueClientOptions.Create(logger)`.
  It sets `MessageEncoding = QueueMessageEncoding.Base64` for the Functions trigger and installs the
  undecodable message handler. Do not base64 encode the JSON a second time.
- Use prefix-scoped lifecycle rules in the private Pending container, never one container-wide rule:

  | Prefix | Retention |
  | --- | --- |
  | `snapshots/` | 1 day. |
  | `accepted/` | At least `BugReportOfflineQueue.MaxAge` (7 days) or the report retention. |
  | `records/` | The report retention, 180 days for the first consumer. |
  | `claims/` | The report retention, 180 days for the first consumer, including release and result markers. |
  | `revoked/` | At least the report retention and the client queue's 7-day maximum age. |

  Keep public report folders for the report retention too. Removing the accepted index or private state
  early can break retry deduplication. The one-day rule covers `snapshots/` only.
- Intake runs only with the subject the authenticator returned, never a client-supplied one. A game
  server likewise derives its snapshot subject from the authenticated connection.
- Provision resources, supply credentials and grant the [Azure backend's RBAC roles](../KhaozEngine.BugReports.Relay.Azure/README.md#permissions-and-provisioning).
  Keep Pending private. Serve only Public media, with HTTPS off loopback, and own expired quota cleanup.
- Protect the service key and GitHub App private key as server secrets. Logs carry report ids and
  references, never a bearer token, service key, installation token, JWT or subject.

To revoke a report:

1. Call `BugReportRelay.RevokeAsync(reportId, cancellationToken)` to create its durable private tombstone.
2. Delete its public capability folder.
3. Delete the folder again after the 10-minute claim lease. A holder that passed its check before
   revocation can still finish a write inside its lease.

Keep the tombstone and idempotency state. A missing public `manifest.json` also means revoked, and
the relay writes a tombstone when it discovers that deletion.

The relevant `host.json` settings are:

```json
{
  "version": "2.0",
  "extensions": {
    "queues": {
      "maxDequeueCount": 5,
      "visibilityTimeout": "00:03:00",
      "messageEncoding": "base64"
    }
  }
}
```

## Storage layout and content types

Map `ReportArea.Public` to a container named `reports`. A folder is
`<gameId>/<yyyy-mm>/<reportId>-<token>/`, so its public URL includes
`reports/<gameId>/<yyyy-mm>/<reportId>-<token>/`. The month is the relay's received UTC month.
The 128-bit random token is 32 lowercase hexadecimal characters. Anyone who holds the URL can read
that report, so treat it as a capability and avoid exposing container listings.

Public folders contain every validated bundle entry under its own name, including `manifest.json`,
sections, screenshot and clip files when present, plus `viewer.html` and `server.json` once a snapshot
merges. The relay-private report record, including reporter identity and the issue number, lives at
`records/<reportId>.json` in Pending. It is not a public `report.json`.

Pending also holds `accepted/<reportId>.json` (folder, subject and received time),
`snapshots/<reportId>.json` (the first raw snapshot), `revoked/<reportId>.json` (the first revocation time),
and the `claims/` files described below.
Every media store enforces the public `ReportPaths.IsValid(path)` rule. It allows one or more
`/`-separated segments of `[a-z0-9._-]`, with no leading or trailing slash, empty segment, `.` or `..` segment.

Every blob gets an explicit content type. Validation guarantees image bytes match their type, and the only
HTML served is the engine's viewer page. Viewer data is escaped and the viewer is rendered without reporter
identity. The worker validates snapshot section keys and writes only `reportId`, `capturedAt` and `sections`
to public `server.json`. The subject stays in private state. Issue composition uses the private report record.

| Extension | Content type | Cache control |
| --- | --- | --- |
| `.json` | `application/json; charset=utf-8` | `no-cache` |
| `.html` | `text/html; charset=utf-8` | `no-cache` |
| `.png` | `image/png` | `public, max-age=86400` |
| `.jpg` | `image/jpeg` | `public, max-age=86400` |
| `.mp4` | `video/mp4` | `public, max-age=86400` |

## Guarantees and residuals

- **Record saves merge monotonic progress without compare-and-swap.** A concurrent write between reload and save can briefly leave the cache behind, but result markers prevent duplicates and the next reconcile heals it.
- **One issue and at most one snapshot comment per report.** Epoch claims and result markers enforce this
  within the lease and saved-result limits below. A matching snapshot is included in the issue body when
  available, or appended as one comment later. A subject mismatch is dropped with a report-id-only warning
  and still answers 202, so the response does not reveal the report's owner.
- **A holder that crashes after GitHub accepted the write holds the claim until its 10-minute lease expires.**
  This covers a crash or a failed result-marker write before the result is saved. If that holder crashed
  before writing its result marker, a later holder can post a duplicate.
- **Leases are unfenced.** A holder working past 10 minutes is outside the guarantee.
- **Revocation is durable.** Tombstones and missing public manifests stop new issue, comment and public
  snapshot or viewer writes. Snapshot POSTs and bundle resends return 202 without enqueueing revoked work.
  A holder that passed its check before revocation can still finish a write inside its 10-minute lease.
  Follow the two deletions in Host duties. A holder running beyond the lease is the unfenced residual above.
- **A transport timeout can hide a POST that succeeded.** The failure releases the claim, so redelivery can
  repeat that write. Issue and comment POSTs are sent once per attempt, with no `HttpRetry`.
- **Exhausting the 64 claim epochs needs operator recovery.** It throws the internal
  `ReportClaimExhaustedException`, which names the report and the work kind. An operator must inspect and
  clear the report's claims before it can run again. Redelivery alone cannot recover it.

Issue and comment writes take separate create-only claims in Pending at
`claims/<kind>-<reportId>-<epoch>.json`, holding `claimedAt`. Completed work returns normally. An active,
unreleased claim throws an internal `ReportClaimBusyException` for host redelivery. An active latest
lease remains busy even at epoch 64. Once all 64 epochs are used and the latest is released or expired,
the distinct exhaustion failure above applies.

A holder that fails before its GitHub call succeeds writes a create-only `.released` marker before
rethrowing, even when its work token is cancelled. The next delivery can claim the next epoch immediately.
If the release marker write also fails, recovery waits for lease expiry. A successful holder writes a
create-only `.done` marker holding the issue number or `commented`, then saves the record. Every delivery
checks result markers before claiming, reloads the record and reconciles a saved result without another
GitHub call, preserving its current snapshot state and skipping work another delivery already completed.

Snapshot intake stores only the first snapshot and queues its late append once a report is accepted.
A resend that loses the pending write repairs a missed enqueue while the record's snapshot state is `None`,
even before its issue number exists. The append worker throws an internal `ReportNotReadyException` for
redelivery until the issue exists, then no-ops when the snapshot is already `InBody` or `Commented`.
GitHub failures and storage faults propagate to the host. Durable redelivery, poison routing and recovery
remain host duties.
