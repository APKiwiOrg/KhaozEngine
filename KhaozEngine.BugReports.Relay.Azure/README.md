# KhaozEngine.BugReports.Relay.Azure

Opt-in Azure Storage implementations of the bug report relay's media, work queue and quota seams,
plus a poison queue requeuer. Add this package explicitly, outside every umbrella. The host supplies
ready clients and owns credentials. Hosts must build every work and poison `QueueClient` with
`ReportQueueClientOptions.Create(logger)`, which sets `MessageEncoding = QueueMessageEncoding.Base64`
to match the Azure Functions queue trigger default. Pass an optional engine logger to the factory
to route decoding warnings, or leave it null to use `Log.Get("BugReports.Relay")`. An undecodable
message stays in its queue until its visibility lease expires. The handler logs one warning per
encounter naming the queue and message id, never the body, and receiving continues with valid messages.
The package never takes an
`Azure.Identity` dependency or provisions a container, queue or table.

Both relay packages target `net10.0`, with no `net8.0` target. The first consumer hosts them on
Functions Flex Consumption with .NET 10 isolated. The [relay README](../KhaozEngine.BugReports.Relay/README.md)
owns the HTTP and queue binding sketch, [Host duties](../KhaozEngine.BugReports.Relay/README.md#host-duties)
and [Guarantees and residuals](../KhaozEngine.BugReports.Relay/README.md#guarantees-and-residuals).

## Constructor wiring

The host supplies `publicContainer` and `pendingContainer` as ready `BlobContainerClient` instances,
`tableClient` as a ready `TableClient`, and the queue URIs and credential. It owns their lifetime,
configuration and authentication. `logger` below is an optional `KhaozEngine.Diagnostics.ILogger`.
Keep Azure SDK using directives above any file-scoped namespace containing `Azure`.

```csharp
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;

var workClient = new QueueClient(workQueueUri, credential, ReportQueueClientOptions.Create(logger));
var poisonClient = new QueueClient(poisonQueueUri, credential, ReportQueueClientOptions.Create(logger));
var media = new BlobReportMediaStore(publicContainer, pendingContainer);
var work = new StorageQueueReportWorkQueue(workClient);
var quotas = new TableReportQuotaStore(tableClient);
var requeuer = new PoisonQueueRequeuer(poisonClient, workClient,
    time: TimeProvider.System, logger: logger);
var relay = new BugReportRelay(options, authenticator, media, work, quotas, github, TimeProvider.System);

// In the host's daily scheduled function:
await requeuer.RequeueAsync(cancellationToken);
```

`ReportQueueClientOptions.Create(ILogger? logger = null)` returns fresh `QueueClientOptions` with
`MessageEncoding = QueueMessageEncoding.Base64`. This is the required
`QueueClientOptions.MessageEncoding` setting for the Functions trigger's Base64 default, plus a decoding
failure handler. `StorageQueueReportWorkQueue` sends raw JSON to the SDK, so the SDK encodes it exactly once.
`PoisonQueueRequeuer` takes `maxRequeues = 3`, an optional `TimeProvider` and an optional engine `ILogger`.
The host must use the factory for both queues even when they share the same credentials.

## Permissions and provisioning

Provision the public and private containers, the work and poison queues and the quota table before
starting the host. Map Public to the `reports` container and serve it at `PublicMediaBase`, without
exposing listings. Pending must never be served. Grant the runtime identity these
[Azure Storage data roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage),
scoped to the relevant resources:

| Resource | RBAC role |
| --- | --- |
| Public and Pending containers | Storage Blob Data Contributor |
| Work and poison queues | Storage Queue Data Contributor |
| Quota table | Storage Table Data Contributor |

Management-plane Contributor alone does not grant these data permissions. The host separately owns its
Functions host-storage permissions and connection configuration. Credentials may use managed identity
through a host-owned identity package. This backend does not reference `Azure.Identity`.
Apply the relay's prefix-specific retention rules to Pending and remove expired quota entities yourself.

## Storage behavior

`BlobReportMediaStore` maps Public and Pending to separate containers. Uploads carry the supplied
content type and cache policy. Every media store enforces the relay's public `ReportPaths.IsValid`
rule. Create-only uploads use `If-None-Match: *`. Downloads reject a declared
length over the caller's cap before reading the body. Only a 404 with `BlobNotFound` reads as absent.
`ContainerNotFound` and other failures
propagate so queue work can be redelivered. `ListAsync` returns all full blob paths under a validated
folder prefix in ordinal order. `DeleteAsync` returns false for an absent blob and propagates other faults.

`StorageQueueReportWorkQueue` sends `ReportWorkItemJson` unchanged to the SDK, which performs the one
base64 encoding. Its delayed `EnqueueAsync` accepts zero to seven days, sets that visibility timeout
and sets the time to live to the delay plus seven days. The item retains a full seven-day delivery
window after it becomes visible.

`TableReportQuotaStore` uses the quota key as PartitionKey, RowKey `q`, a long `Count` and `ExpiresAt`. Creates use add and updates use a matching ETag with replace. The host owns expired
entity cleanup, since Azure Table Storage does not expire entities from an `ExpiresAt` property.

## Poison recovery

The requeuer owns receives on its poison `QueueClient`. Receives made outside it are not counted.
Overlapping requeuer receives on the same client serialize from counter reset through the SDK receive
and final count collection.

`PoisonQueueRequeuer` drains batches of up to 32 until a receive has neither decoded messages nor
undecodable messages omitted by the SDK. A fully undecodable batch does not end the drain. It increments
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

Run the requeuer daily, with the queue trigger configured for `maxDequeueCount` 5 and
`visibilityTimeout` at least `00:03:00`. Keep the 10-minute claim lease and its accepted duplicate
windows in mind when recovering poison work.

## Offline tests and the live leg

The ordinary tests link the relay conformance suites and use in-memory seams plus in-process SDK
HTTP transports. Every conformance fact has a named, gated live counterpart, enforced by an offline
reflection test. The live facts skip unless `KE_BUGREPORTS_STORAGE` names a test storage account.
An authorized live run uses DefaultAzureCredential against pre-created `kebugtest-public` and
`kebugtest-pending` containers, the dedicated, empty `kebugtest-work` queue and the `kebugtestquota`
table. It creates and deletes only its own blobs, messages and entities inside those resources.
It never creates or deletes the resources themselves.

`KE_BUGREPORTS_STORAGE` is the storage account name, not a connection string or URL. Set it only for an
explicitly authorized live test run. The test project, rather than this backend, references `Azure.Identity`
for `DefaultAzureCredential`. Its identity needs the same three data roles on the pre-created test resources.
Leave the variable unset for offline verification. The work queue must be dedicated and empty because live
queue conformance reads it. No live Azure run is part of ordinary documentation verification.
