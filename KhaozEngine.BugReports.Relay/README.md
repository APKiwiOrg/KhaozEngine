# KhaozEngine.BugReports.Relay

The host-agnostic in-game bug report relay. It turns an authenticated client bundle and an optional server
snapshot into a GitHub issue through a durable work queue. Storage, the work queue, quota counters, reporter
authentication and GitHub sit behind seams, and the package ships in-memory implementations of the storage, queue
and quota seams for tests and local runs. It is opt-in and sits in no umbrella. Add it explicitly.

Depends on `KhaozEngine.BugReports`, `KhaozEngine.BugReports.Contracts`, `KhaozEngine.Diagnostics` and
`KhaozEngine.Http`. No web framework and no third-party package.
