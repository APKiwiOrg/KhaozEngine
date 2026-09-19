# The blob content origin and the origin fill

Status: complete, shipped in `19.5.0`. Engine issue
[#1016](https://github.com/APKiwiOrg/KhaozEngine/issues/1016). Consumer
[Grimhollow #261](https://github.com/APKiwiOrg/Grimhollow/issues/261).

Rationale only. The API and how to call it are the `KhaozEngine.Catalog.AzureBlob` and
`KhaozEngine.Catalog` package READMEs and the "Filling a client origin" section of
[USING-KHAOZENGINE.md](../USING-KHAOZENGINE.md). Nothing here restates them.

## The problem

A server is the PRODUCER of its clients' content origin. An operator publishes a version through the admin
console, the version becomes active at the next restart, the connect door then refuses every client still on
the old one, and each of those clients fetches the new pack from the ONE origin its build was configured with.

With the deploy as the only producer, that chain breaks at the last link. The deploy uploads the committed
bundle's pack, so a console publish followed by a restart activates a version whose objects no producer ever
put in the origin, and every hosted client is locked out until someone ships a deploy. The capability the
catalog exists to give back, changing content without a client release, is exactly the capability that
sequence removes.

## Decision 1, where the uploader lives

Criteria scored 1 to 10, equal weights, higher is better. Dependency weight scores LIGHTNESS, so 10 means no
new dependency is taken.

| Option | Correctness | Reuse across games | Operations cost | Effort | Dependency weight | Total |
|---|---:|---:|---:|---:|---:|---:|
| A. Opt-in ENGINE package | 10 | 10 | 9 | 5 | 6 | 40 |
| B. Game side code in Grimhollow | 10 | 2 | 9 | 9 | 7 | 37 |
| C. No uploader, the deploy stays the only producer | 3 | 5 | 2 | 10 | 10 | 30 |

A wins because Ruinborne needs the same uploader against the same storage account shape, and the engine-first
rule says build a thing once rather than twice. Its price is real and accepted: one more package to release,
and an Azure SDK dependency, which only a game that opts in ever links.

B is the fastest route to a working hosted Grimhollow and is the one option that is certain to be duplicated.
The second consumer would copy the file, and the two copies would then drift in the key layout, which is the
one place a drift is silent until a client cannot find a chunk.

C is the status quo, and it is what makes a hosted retune cost a bundle export, a client pack regeneration, a
commit and a deploy. That is the rebuild and release cycle the content catalog was built to remove, so scoring
it on correctness against the goal rather than on effort is what puts it last.

Owner decision recorded 2026-09-19.

## Decision 2, hash objects only and no pointer half

The blob store holds hash objects and writes no `versions/<n>` pointer.

The pointer format carries the SERVER manifest hash beside the client one (`KhaozEngine.Catalog/IPackStore.cs`),
an origin is public, and no client ever reads a pointer anyway: a client learns its version number and its
CLIENT manifest hash from the connect door, which is the authenticated channel the whole trust chain hangs on.
A pointer in a public container would therefore publish a name for the server projection and buy nothing.

The consequence is a SAFETY property rather than a gap. `ContentPublishCommit` resolves the pointer half of the
store it is handed at construction, and `ContentPackRebuild` resolves it at the call, and both throw a
`ContentAuthoringException` with the reason `no-pack-store` when there is none
(`KhaozEngine.Catalog.Authoring/ContentAuthoringException.cs`). That refusal happens at RUN time and not at
compile time: the store satisfies `IPackStore`, so it compiles wherever one is taken. What the design buys is
that a publish or a rebuild pointed at a public container fails at the first construction, before a byte moves,
instead of quietly writing a full server pack where anyone can read it.

The alternative weighed and rejected is a COMPLETE pack store over blob, pointer half included, used as a
durable private pack root. It was option C when the hosted pack root problem was decided
([Grimhollow #263](https://github.com/APKiwiOrg/Grimhollow/issues/263)) and lost there to rebuilding the pack
from the authoring store, because it adds a second durable store that can lose a version the database still
names. That reasoning did not change here, so the blob provider deliberately stops short of being one.

## Decision 3, the copier is the fetch loop pointed the other way

`ContentOriginFill` runs `ContentFetchLoop` with the origin as the local half and the server's own pack store
as the remote one. A second closure walker would be a second place for the client closure to be computed, and
neither side's tests would catch a divergence until a chunk was missing in production.

Four things the implementation found and corrected in flight, which are the expensive part to reconstruct:

- **The loop writes the manifest FIRST**, through its caching pair, before it fetches a chunk. That is right
  for a client cache and wrong for an origin: a fill that then failed would leave a public manifest naming
  absent chunks, which is a hole every client would follow. The wrapper holds the manifest bytes back in a
  small write view and commits them last, so a stopped fill leaves content addressed chunks and nothing
  followable.
- **A server manifest handed in as a client one is refused by the manifest codec's own `side` byte**, because
  nothing can tell the two apart by hash. That refusal lands before any object is written, and the
  manifest-last view means nothing reaches the origin even so.
- **The write view deliberately does not expose the origin's pruning half**, so the caching pair cannot delete
  from a public origin on a verify failure of its own.
- **The client build gate is bypassed** (`ClientBuild` at its maximum), because a server is not a client, and a
  version that raises the minimum client build is precisely the version whose closure has to reach the origin.
  The gate that matters is the connect door's, which enforces the floor on the clients themselves.

One attempt and no backoff, because both stores are the server's own: a failure there is a fault to report
rather than a flaky network to wait out.

It never prunes. A caller that wants stale objects gone does it itself, and a hosted origin should think twice
before it does, because a client may be part way through downloading the PREVIOUS version and a chunk deleted
under it turns an update into a refusal at the door.

## Decision 4, the SDK boundary and testing

The store takes a ready `BlobContainerClient`. The host already owns an identity stack, so the package needs no
`Azure.Identity` dependency and the engine never chooses a credential for a game.

The SDK sits behind ONE internal seam, `IBlobContainer`, with five members and a single adapter implementing
it. Each member is written so the DECISION lives at the seam rather than at the call site: the upload is
conditional and answers whether it wrote, the download answers null for absent and refuses an oversize object
from its declared length, and the delete answers whether the object was there. The seam is `internal` and
visible to `KhaozEngine.Catalog.AzureBlob.Tests` through `InternalsVisibleTo`, so the suite drives the store
through an in-memory container with no account, no network and no credential, and one environment gated fact
talks to a real container. That is the same shape as the SQL Server catalog leg, gated on `KE_PACKSTORE_BLOB`
with the container name required to carry `-packstore-test-`, because the live leg deletes what it writes.

The alternative rejected is mocking the SDK's own client types, which means faking its response, pageable and
exception shapes for every call, and then maintaining those fakes against an SDK the engine does not own.

Put-if-absent is the `If-None-Match: *` conditional upload rather than an existence check followed by a write,
so there is no read then write window and no assumption about clocks. Two publisher hosts writing the same
chunk is the ordinary case, and the loser of the race wrote the same bytes, because the name is the content.

One key layout rule, `FileSystemPackStore.RelativeKeyFor`, is shared by all three pack store providers. That is
what lets `HttpPackStore` read what the blob store wrote with no client change at all, and a second copy of the
rule is the one drift that would be invisible until a fetch missed.

## What is deliberately NOT built

- **Pruning a hosted origin from the fill.** A client may be mid download of the previous version, so deletion
  is an operator decision with its own timing, not a side effect of publishing the next one.
- **Repairing an origin object that went bad in place.** A fill trusts `ExistsAsync`, which is what makes a
  second fill cheap. The documented remedy is to delete the object and fill again.
- **Any listing or pointer surface on a public container.** Both would name things a client has no need to
  learn, and the empty listing is also the publish sweep's skip condition, so nothing here can authorize a
  delete.
- **A credential helper.** The moment the engine builds a credential it has an opinion about how a game is
  hosted, and the host's own identity stack is better than any opinion this package could hold.

## What a host must provide

The identity needs `Storage Blob Data Contributor` scoped to the ONE container and never to the account. A game
server that can write any container in the account can write the client updater feed, which is a far worse
thing to lose than a content origin.

The container allows anonymous BLOB read and no listing, which is what a client needs to fetch a key it already
knows and is nothing more than that.
