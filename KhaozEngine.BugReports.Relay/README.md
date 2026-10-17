# KhaozEngine.BugReports.Relay

The host-agnostic in-game bug report relay. It turns an authenticated client bundle and an optional server
snapshot into a GitHub issue through a durable work queue. Storage, the work queue, quota counters, reporter
authentication and GitHub sit behind seams, and the package ships in-memory implementations of the storage, queue
and quota seams for tests and local runs. It is opt-in and sits in no umbrella. Add it explicitly.

Depends on `KhaozEngine.BugReports`, `KhaozEngine.BugReports.Contracts`, `KhaozEngine.Diagnostics` and
`KhaozEngine.Http`. No web framework and no third-party package.

`PublicMediaBase` must be an absolute URL ending in `/`, use HTTPS off loopback, and contain at most 512
characters. Server snapshot intake checks the service key before reading the body, stores the first snapshot,
and queues a late append only for that first write. Snapshots are matched to the report's authenticated subject.
The worker validates section keys and serializes the snapshot again before writing `server.json`. The public
viewer renders with no reporter identity. Issue composition uses the private report record.

Issue and comment writes take separate create-only claims in the Pending area at
`claims/<kind>-<reportId>-<epoch>.json`. A claim holds `claimedAt` and has a ten-minute lease. Duplicate work
during that lease does nothing. A later delivery can take over with the next epoch, up to sixteen claims per
operation. Keep `claims/` with the report state. A one-day snapshot lifecycle rule must target `snapshots/` only.

GitHub failures and storage faults propagate to the host. Redelivery must reach a failed operation after its
lease expires, since delivery within an active lease is a no-op. Exhausting sixteen epochs fails closed and
needs operator recovery. The remaining duplicate window is a holder that creates the GitHub issue or comment,
then dies before saving the result. A takeover after lease expiry can repeat that write. A holder must finish
within its lease, since this storage seam cannot fence a GitHub call from an expired holder.
