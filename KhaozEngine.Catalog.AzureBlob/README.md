# KhaozEngine.Catalog.AzureBlob

Azure Blob Storage `IPackStore` backend over `Azure.Storage.Blobs`. It is the WRITE side of a public content
origin: a game server fills a blob container with the pack objects its clients then read over HTTPS through
`HttpPackStore`, under the same keys that store already requests, so a client needs no change at all. Opt-in:
it is in no umbrella, and a game client never links it, because a client only ever FETCHES from a container
and `KhaozEngine.Catalog` already does that with no SDK.

```csharp
using Azure.Identity;
using Azure.Storage.Blobs;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.AzureBlob;

var container = new BlobContainerClient(
    new Uri("https://mygame.blob.core.windows.net/content"),
    new DefaultAzureCredential());

var origin = new AzureBlobPackStore(container);

// serverPackStore is the server's own IPackStore, clientManifestHash is the version's CLIENT manifest hash.
ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
    serverPackStore,
    origin,
    new ContentVersionIdentity(versionNumber, clientManifestHash),
    registry);

if (!fill.Filled)
{
    throw new InvalidOperationException(
        fill.RefusalReason + " at " + fill.RefusalHash + " " + fill.RefusalDetail);
}
```

`ContentOriginFill` in `KhaozEngine.Catalog` is the way to fill an origin, and the reason this package needs
no copy loop of its own. It walks the CLIENT manifest's closure once, refuses a server manifest handed in by
mistake on the file's own side byte before anything is written, writes the manifest last so a stopped fill
leaves nothing a client can follow into a hole, and reports a refusal as a result. A caller that copies by
hand instead owns every one of those properties itself, `PutAsync` per hash being the only part this package
provides.

One constructor, taking a container client the CALLER built and owns. That is why this package has no
`Azure.Identity` dependency: the host already has an identity stack (a managed identity, a workload identity,
a shared-access signature) and the engine never picks one for it. `Azure.Identity` above is the sample's, not
the package's.

## Hash objects only, and no pointer half

The store holds hash objects and nothing else. It implements `IPackStore` and `IPackStorePruning`, and it
implements NEITHER `IPackVersionPointerStore` nor `IContentVersionPointerSource`.

That is a safety property rather than a missing feature. `ContentPublishCommit` resolves the pointer half in
its CONSTRUCTOR and `ContentPackRebuild` inside its `RunAsync`, and each refuses a store that has none with
the `no-pack-store` reason, before any byte is written, so a full server pack cannot be published into a
public container by mistake. It is a RUN time refusal rather than a compile time impossibility, and
`ContentPackRebuild.RunAsync` also takes an explicit pointer store parameter, so a caller who passes one on
purpose is not stopped by any of this.

A public origin gets no `versions/<n>` pointer either, and it is the same decision rather than a second one.
The pointer format carries the SERVER manifest hash beside the client one, and no client ever reads a
pointer: a client learns the version and the client manifest hash from the connect door, which is the
authenticated channel the whole trust chain hangs on. Consequently `ListAsync` yields NOTHING, whatever
version it is asked for, which is also the publish sweep's own skip condition: nothing here can ever
authorize a delete.

## The key layout is `HttpPackStore`'s

One object per hash under the two-level shard, which is `FileSystemPackStore.RelativeKeyFor`, the engine's
one statement of that rule:

```
<hash[0..2]>/<hash[2..4]>/<hash>.kec
```

Lower case, forward slashes, no leading slash. A client pointed at `https://<account>.blob.core.windows.net/<container>/`
through `HttpPackStore` fetches exactly those keys.

## Immutable objects, conditional writes

Every object is stored with content type `application/octet-stream` and cache control
`public, max-age=31536000, immutable` (`AzureBlobPackStore.ObjectContentType` and `ObjectCacheControl`). A
year and `immutable` are safe PRECISELY because the name is the content: an object under a content address
never changes, and a republish writes a new name.

`PutAsync` verifies the digest of the bytes it was handed before it writes them, refusing a name that is not
a content address and bytes that do not digest to it, both with `ContentPackException`. The upload itself is
conditional (`If-None-Match: *`), so there is no read-then-write window: two publisher hosts putting the same
chunk is the ordinary case, the loser of the race wrote the same bytes, and a 409 `BlobAlreadyExists` or a
412 is an idempotent success. A hash object is never overwritten.

`GetAsync` answers null for an absent object rather than throwing, and so does a read the service REFUSED: an
expired signature, a throttle and a body that died mid transfer are the same answer as an absent object,
because the caller's next move is the same for all of them, and `IPackStore.GetAsync` promises null so a
sweep or a validation pass is never taken down by one lookup. The two WRITES stay loud, deliberately, because
an origin that silently did not write is an origin a client is about to be sent to. `GetAsync` also refuses an
object larger than `HttpPackStore.MaxObjectBytes` from its DECLARED length, before a body is buffered.

## The identity a host needs

`Storage Blob Data Contributor`, scoped to the container and nothing wider. The store writes, reads, lists and
deletes blobs within one container and touches no account-level operation, so a role assignment at account
scope buys nothing and widens the blast radius of a leaked credential.

## The security rule the CALLER enforces

Server-only chunks never go into a public container. Nothing in the STORE can enforce that, because it is
handed a hash and bytes and has no way to know which side's manifest named them. `ContentOriginFill` is what
enforces it, one layer up: it copies the CLIENT manifest's closure only, and a server manifest handed to it
by mistake is refused by the file's own side byte with nothing written. A caller that copies by hand carries
that rule itself, and copying a version's whole keep set, or pointing a publish at this store, would put the
server projection into a container anyone can read.

## The live leg

The test suite runs entirely against an in-memory double of the internal container seam, so it needs no
account, no network and no credential. That double stops at the seam, which means `BlobContainerAdapter` is
covered by the live leg ALONE: the conditional upload's 409 and 412 classification, the 404 arms, the
`Content-Length` ceiling applied before a body is buffered, `DeleteIfExistsAsync`'s answer, and a read the
service refused being answered as absent. A release that touches that file runs this leg first.

The facts are skipped unless `KE_PACKSTORE_BLOB` holds a container URL whose container NAME contains
`-packstore-test-`. The leg deletes what it writes, so the marker is checked before it writes a byte, the
same rule the SQL Server legs apply to their database names, and every fact cleans up its own objects in a
`finally`.

```
KE_PACKSTORE_BLOB='https://mygame.blob.core.windows.net/ke-packstore-test-1?<sas>' dotnet test KhaozEngine.Catalog.AzureBlob.Tests
```
