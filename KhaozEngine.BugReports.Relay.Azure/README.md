# KhaozEngine.BugReports.Relay.Azure

Opt-in Azure Storage implementations of the bug report relay's media, work queue and quota seams,
plus a poison queue requeuer. Add this package explicitly, outside every umbrella. The host supplies
ready clients and owns credentials. Hosts must build every work and poison `QueueClient` with
`ReportQueueClientOptions.Create()`, which sets `MessageEncoding = QueueMessageEncoding.Base64`
to match the Azure Functions queue trigger default. Pass an optional engine logger to the factory
to route decoding warnings, or leave it null to use `Log.Get("BugReports.Relay")`. An undecodable
message stays in its queue until its visibility lease expires. The handler logs one warning per
encounter naming the queue and message id, never the body, and receiving continues with valid messages.
The package never takes an
`Azure.Identity` dependency or provisions a container, queue or table.

`BlobReportMediaStore` maps Public and Pending to separate containers. Uploads carry the supplied
content type and cache policy. Every media store enforces the relay's public `ReportPaths.IsValid`
rule. Create-only uploads use `If-None-Match: *`. Downloads reject a declared
length over the caller's cap before reading the body. Only a 404 with `BlobNotFound` reads as absent.
`ContainerNotFound` and other failures
propagate so queue work can be redelivered.

`StorageQueueReportWorkQueue` sends `ReportWorkItemJson` unchanged to the SDK, which performs the one
base64 encoding. `TableReportQuotaStore` uses the quota key as PartitionKey, RowKey `q`, a long `Count`
and `ExpiresAt`. Creates use add and updates use a matching ETag with replace. The host owns expired
entity cleanup, since Azure Table Storage does not expire entities from an `ExpiresAt` property.

`PoisonQueueRequeuer` drains batches of up to 32 until no visible messages remain. It increments
`Requeues`, sends to the work queue, then deletes from poison. The default cap is 3. Malformed and
capped items stay and log only their message id once per drain. A drain returns its moved count after one hour and
receives with two hours of visibility so retained items cannot starve later batches. On exit it
releases retained and unprocessed messages, with a separate 30-second cleanup deadline. A failed
release logs a warning. Cleanup timeout preserves the moved count. Other release faults propagate unless
the drain already failed or reached its deadline, preserving the original outcome. Leases eventually expire.
An optional `TimeProvider` drives the deadlines and defaults to `TimeProvider.System`.
Pass an optional engine logger to `PoisonQueueRequeuer` to route its malformed, capped and cleanup
warnings. A null logger uses `Log.Get("BugReports.Relay")`.
Only caller cancellation throws `OperationCanceledException`. Delivery remains at least once if sending
succeeds but deleting fails. The host owns poison routing after 5 dequeues and when this requeuer runs.

The ordinary tests link the relay conformance suites and use in-memory seams plus in-process SDK
HTTP transports. Every conformance fact has a named, gated live counterpart, enforced by an offline
reflection test. The live facts skip unless `KE_BUGREPORTS_STORAGE` names a test storage account.
An authorized live run uses DefaultAzureCredential against pre-created `kebugtest-public` and
`kebugtest-pending` containers, the dedicated, empty `kebugtest-work` queue and the `kebugtestquota`
table. It creates and deletes only its own blobs, messages and entities inside those resources.
It never creates or deletes the resources themselves.
