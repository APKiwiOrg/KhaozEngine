# KhaozEngine.BugReports.Relay

The host-agnostic in-game bug report relay. It turns an authenticated client bundle and an optional server
snapshot into a GitHub issue through a durable work queue. Storage, the work queue, quota counters, reporter
authentication and GitHub sit behind seams, and the package ships in-memory implementations of the storage, queue
and quota seams for tests and local runs. It is opt-in and sits in no umbrella. Add it explicitly.

Depends on `KhaozEngine.BugReports`, `KhaozEngine.BugReports.Contracts`, `KhaozEngine.Diagnostics` and
`KhaozEngine.Http`. No web framework and no third-party package.

`PublicMediaBase` must be an absolute URL ending in `/`, use HTTPS off loopback, and contain at most 512
characters. Server snapshot intake checks the service key before reading the body, stores the first snapshot,
and queues a late append for that first write. A resend that loses the pending write also repairs a missed
enqueue when the accepted report has an issue number and its snapshot state is `None`. Comment claims make
duplicate appends harmless. Snapshots are matched to the report's authenticated subject.
The worker validates section keys and serializes the snapshot again before writing `server.json`. The public
viewer renders with no reporter identity. Issue composition uses the private report record.

Issue and comment writes take separate create-only claims in the Pending area at
`claims/<kind>-<reportId>-<epoch>.json`. A claim holds `claimedAt` and has a ten-minute lease. Completed work
returns normally. Unfinished work with an active, unreleased claim throws an internal `ReportClaimBusyException`
so the host redelivers it. A failed holder writes `claims/<kind>-<reportId>-<epoch>.released` with
create-if-absent before rethrowing, even when the work token is cancelled. The next delivery can claim the
next epoch immediately. A crash without a release marker requires lease expiry. There are at most 64 claims
per operation. Keep claims and release markers with the report state. A one-day snapshot lifecycle rule must
target `snapshots/` only.

GitHub failures and storage faults propagate to the host. If writing the release marker also fails, recovery
falls back to lease expiry. Exhausting 64 epochs still throws for unfinished work and needs operator recovery.
The remaining duplicate window is a successful GitHub write followed by a failed record save or a crash before
the result is saved. A takeover can repeat that write. A holder must finish within its lease, since this
storage seam cannot fence a GitHub call from an expired holder.
