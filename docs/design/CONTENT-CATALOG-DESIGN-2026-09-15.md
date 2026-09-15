# Content catalog: the versioned content system (Scope A)

**Status:** Spec draft, awaiting owner gate 1. Nothing here is implemented. This is Scope A of the content
foundation, [#882](https://github.com/APKiwiOrg/KhaozEngine/issues/882), written against the gate 0 contracts
in [CONTENT-CONTRACTS-DESIGN-2026-09-14.md](CONTENT-CONTRACTS-DESIGN-2026-09-14.md), which are BINDING on this
document. Scope B is owned item instances, affixes, sockets, crafting and the stat evaluation base,
[#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884), specified in parallel against the same contracts.
Consumers are [Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208) and
[Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465). Neither spec is implemented before the
owner approves both.

Every format below is byte level, every algorithm is stated so it can be coded without guessing, and every
claim about existing code cites a file and a line. Where a choice is genuinely contested it carries a weighted
comparison with a recommendation. Where this spec needs something the contracts forbid, it is written in
section 20 as a change request and the rest of the spec is designed on the contracts as written.

File and line citations come from three read-only surveys taken on 2026-09-14 against KhaozEngine `1eb60de7`,
Grimhollow `36f0139a` and Ruinborne `1b85e1aa`, plus direct reads of this worktree at `d4990be5`. Survey
citations are written `a-engine.md:NNN`, `b-grimhollow.md:NNN` and `c-ruinborne.md:NNN`. Where a survey and
this document disagree, the survey is the fact and this document is the decision.

## 1. Goals and non-goals

### 1.1 What this program builds

One engine-owned system for every piece of tunable game content: what a thing is, what it is worth, and what
it requires. The owner authors it in a database through each game's admin console. Publishing produces
immutable, hashed, content-addressed packs. Servers load packs into arrays and clients download and cache
them. The server stays authoritative for every decision (#882 body, "Goal").

Concretely, Scope A ships:

1. A content type registry, so the engine, Scope B and each game can each declare their own families of rows
   without colliding (section 3, contracts 4).
2. An authoring store with SQLite and SQL Server providers, holding temporal rows, one open draft as a change
   set, a field-level audit and an id allocator (section 4).
3. One pure validator shared by publish, boot and tests (section 5, contracts 10.4).
4. A publish pipeline that assigns a version, rebuilds only affected chunks, writes two manifests and advances
   an active pointer atomically (section 6).
5. A byte-level pack format: chunk files, a manifest file, a remap rule chunk and per-language text chunks
   (section 7, contracts 15).
6. A pack store abstraction with a filesystem provider and an HTTP provider, plus the client fetch, verify and
   cache path (section 8).
7. A server runtime that loads the active version into arrays indexed by id, immutable, swapped atomically,
   with no lock per lookup (section 9).
8. An authoring API for game consoles over the existing `ServerAdmin` action mechanism (section 10).
9. `LootRoller`, ONE implementation of the `loot_table` composition rule this spec defines, so a game that
   authors a drop table gets the draw as well as the schema (section 3.5, budget P9).

The ninth is the newest and is here for a reason worth stating. This spec defined `loot_table` and
`loot_entry`, specified exactly how guaranteed entries, weighted picks, nested tables and tag draws compose,
built the prefix-summed arrays the draw needs at load and budgeted the draw at P9, and then shipped no code
that performs it. Scope B disclaims the roll deliberately, because deciding WHICH base drops is not an
instance concern. So the roll fell between the two specs, and every consumer would have written the
composition rule again against the engine's own arrays: Grimhollow for `monster_drop`, Ruinborne to replace
its `LootRoll`, and the two would have disagreed about how `guaranteed` interacts with `roll_count` on the
first table that used both. The owner of a rule is the code that runs it.

### 1.2 What it explicitly does not build

Each of these is out of scope for v1 by the owner's decision (#882 body, "Out of scope for v1"), and each is
named here with what its absence costs, so a later phase knows what it is buying.

| Non-goal | Why it is out | What its absence costs now |
|---|---|---|
| Live apply | v1 applies a new content version at server RESTART (#882 body, owner decision, and contracts 1.3 item 8). | An operator publishes and then restarts. The pack is already written and hashed at publish time, so the restart is a load rather than a build. |
| Staging promotion | There is no staging environment yet (#882 comment 2, 2026-09-14). | A publish is live the moment the server restarts onto it. Section 12 keeps the pin action so an operator can hold a version back. |
| Multi-world activation | One world now, separate worlds to hop between later (#882 comment 2). | The active-version pointer is a single row. Section 4.9 says exactly what a second world would add, and it is one column. |
| Market index | No trade market and no search index (#884, "Out of scope for v1"). | Nothing in the pack format assumes a searchable index. Tag ids (contracts 4.6) are the query surface a later index would build on. |
| An engine admin UI | The engine ships the API, each game ships its console (owner framing, contracts 1.4: "edited in each game's admin app"). | The field schema of contracts 4.7 is what makes one generic editor possible per game, rather than a bespoke screen per type (section 10.3). |
| Family generators | Generating a family of items from a template is deferred. | Families still exist as contiguous id blocks (contracts 5.2, section 3.7). Only the generator is absent. |
| Definition inheritance, implemented | Deferred to a later phase, but "the row model must allow it" (#882 body). | Section 3.8 specifies the optional parent reference the row model carries from phase 1, validated as unset until the resolver ships. |

### 1.3 The inheritance hook, stated once

Inheritance is the one non-goal with a phase-1 obligation, so it is written out here rather than left to a
later reader. Every content row carries an optional `parent_id` int in the same type's id space, 0 meaning no
parent (section 3.8, section 4.4). In phase 1 the validator REFUSES any non-zero `parent_id` with finding
`KEC0031`, so no data can come to depend on a resolver that does not exist. When the resolver ships, publish
resolves the chain field by field before the row codec runs, so a pack chunk contains only FULLY RESOLVED rows
and no runtime reader ever learns that inheritance happened. That placement is the whole point: resolving at
publish keeps the pack format, the chunk hash, the runtime arrays and every client unchanged when inheritance
arrives, and the only thing that changes is what the authoring store sends to the encoder.

## 2. Packages and layering

### 2.1 The five packages

Exactly the contracts' section 3.2 set for Scope A. `KhaozEngine.ItemInstances` is Scope B's and is named here
only where an edge touches it. `KhaozEngine.Content` is NOT reused, NOT renamed and NOT absorbed: it stays a
JSON config loader plus a schema validator, 143 lines across two files (contracts 3.1, `a-engine.md:250-299`).

| Package | Umbrella | Depends on | Ships |
|---|---|---|---|
| `KhaozEngine.Catalog` | `Foundation` | `KhaozEngine.Primitives` | The registry, the codecs, the hashes, the pack format, the validator, the pack store seam, the read-side runtime, the loot roller. |
| `KhaozEngine.Catalog.Authoring` | `Server` | `KhaozEngine.Catalog` | The authoring store contract, drafts, change sets, audit, publish, diff, bulk import and export. No SQL. |
| `KhaozEngine.Catalog.Sqlite` | none, opt-in sibling | `Catalog.Authoring`, `KhaozEngine.Sqlite`, `Microsoft.Data.Sqlite` | The SQLite authoring provider. |
| `KhaozEngine.Catalog.SqlServer` | none, opt-in sibling | `Catalog.Authoring`, `Microsoft.Data.SqlClient` | The SQL Server authoring provider. |
| `KhaozEngine.Catalog.Netcode` | `Server` | `Catalog`, `KhaozEngine.Netcode` | The content handshake layer and its gate authenticator. |

**Three EXISTING packages change, and no sixth is added.** The authoring API is registered actions on the
admin surface (section 10.1), and that surface cannot express the 409 or the structured error body this
spec's status codes require:

| Package | Change |
|---|---|
| `KhaozEngine.NetWorld` | `AdminActionStatus.Conflict`, and an object-carrying error payload on `AdminActionResult` beside the existing string one. Additive, no caller breaks. |
| `KhaozEngine.Server.Admin` | The matching arm in `AdminHttpServer.DispatchActionAsync`, so `Conflict` is a 409 with a JSON body and an object-carrying `BadRequest` is a 400 with one. |
| `KhaozEngine.Primitives` | Gains `IRandomSource`, `SeededRandomSource` and `CryptographicRandomSource` (contracts 3.2 and 14.1). Additive, and it is the CONTRACTS' change rather than this spec's: it is listed here because this spec takes a dependency on the result. |

The first two land in phase 1 milestone 1.4 (section 18.1) and neither is a contract change. The third lands
whenever either scope needs it first, which is milestone 1.3 here, and it is already written into the
contracts. Section 10.1 spends the reasoning for the admin pair.

The layering rules are the README's, surveyed at `a-engine.md:1373-1407`. A pure catalog with no SQL belongs in
`Foundation` beside `Items` and `Stats`. Anything with a SQL provider is an opt-in SIBLING pair and is never
bundled in an umbrella, stated twice in the README (lines 101 and 104) and followed by both `WorldStore` and
`Commerce`. `Foundation` cannot reference `Server`-side packages, which is why the handshake gate is its own
small package rather than a type inside `KhaozEngine.Catalog`.

**`KhaozEngine.Catalog` takes no third-party dependency and that is load bearing.** A game CLIENT needs the
read side and must never pull a database dependency, so the client half of a pack has to decode with no
authoring type in the graph (contracts 3.2, rationale). It uses `System.IO.Compression` and
`System.Security.Cryptography`, both in box, and no third-party package at all. Its one engine dependency is
`KhaozEngine.Primitives`, which is where contracts 3.2 and 14.1 put the `IRandomSource` seam and its two
implementations, beside the `DeterministicRng` that `SeededRandomSource` wraps. That edge is what lets the
loot roller of 3.5 take a random source without `Catalog` depending on Scope B, which depends on `Catalog`
and would close a cycle. `Primitives` is the bottom of the render and runtime stack and is already in every
game's graph, so the dependency costs a consumer nothing. In particular it does NOT take `JsonSchema.Net`, which is contained to
`KhaozEngine.Content` by an architecture test (`KhaozEngine.Tests/ArchitectureTests.cs:121`).

### 2.2 `KhaozEngine.Catalog` public types

| Type | One line |
|---|---|
| `ContentTypeId` | A `readonly record struct` wrapping the `ushort` of contracts 4.3, with the three range predicates. |
| `ContentKey` | A `readonly struct` over the UTF-8 key bytes of contracts 5.3, ordinal, materialising a string only on demand, section 9.1. |
| `ContentVisibility` | `Client` or `ServerOnly`, contracts 11.1's content vocabulary. |
| `ContentFieldKind` | `Int`, `ScaledInt`, `Bool`, `KeyReference`, `TagList`, `LocalizedTextKey`, `OpaqueBytes`, contracts 4.7. |
| `ContentFieldSchema` | One type's ordered field list: name, kind, scale, reference target, visibility, required. |
| `IContentRowCodec` | `Encode(row, IBufferWriter<byte>)` and `TryDecode(ReadOnlySpan<byte>, out row, out reason)`, never throwing. |
| `IContentValidator` | One type's own checks, run after the engine's, contracts 4.4. |
| `ContentTypeRegistry` | Registration, the freeze at first pack load, and lookup by type id or type key. |
| `ContentRow` | The generic row a codec-free consumer sees: id, key, parent id, an ordered field-value list. |
| `ItemRow` | The typed `ref struct` view over the engine `item` type's four hot fields, section 9.1. |
| `IContentSnapshot` | The narrow READ side every consumer outside this package compiles against, below. |
| `ContentSnapshot` | A complete candidate or loaded version: every type's rows plus the version header. Implements `IContentSnapshot`. |
| `ContentValidationReport` | `(bool IsValid, IReadOnlyList<ContentFinding> Findings)`, accumulating, contracts 10.4. |
| `ContentFinding` | `(ContentTypeId Type, int Id, string Code, string Message)`. |
| `ContentValidator` | The one pure validator of section 5. |
| `RemapRule`, `RemapRuleKind` | Contracts 8.1 and 8.2, with the byte codec of contracts 8.4. |
| `RemapRuleSet` | The full ordered list, its idempotence check and its `Apply` pass. |
| `ContentChunk`, `ContentChunkCodec` | The `KECC` chunk of section 7.2. |
| `ContentManifest`, `ContentManifestCodec` | The `KECM` manifest of section 7.4. |
| `ContentTextChunk`, `ContentTextChunkCodec` | The `KECT` per-language text chunk of section 7.6. |
| `ContentHash` | `kec/` domain separation, `SchemeVersion`, the chunk and manifest digests of section 7.8. |
| `ContentPackReader` | Verifies, decompresses and decodes one chunk, and assembles a snapshot lazily. |
| `IPackStore` | Content-addressed `GetAsync(hash)`, `PutAsync(hash, bytes)`, `ListAsync(version)`, section 8.1. |
| `FileSystemPackStore` | The local provider, one file per chunk hash under a sharded directory. |
| `HttpPackStore` | The read-only cloud provider over `HttpClient`, section 8.3. |
| `CachingPackStore` | A decorator: a local store in front of a remote one, hash verified on every read. |
| `ContentRuntime` | The loaded active version: arrays indexed by id per type, immutable, atomically swapped. |
| `LootRoller`, `LootDraw` | The one implementation of section 3.5's composition rule, and the struct one roll returns. |
| `ContentVersionIdentity` | `(int Number, string ManifestHash)`, the pair that travels together, contracts 7.1. |
| `ContentStringCatalog` | The layered `IStringCatalog` of contracts 12.4, content first then the game's resx. |
| `ContentVarint` | The LEB128 and zig-zag primitives of contracts 15, shared with Scope B. |

**`IContentSnapshot` is the read side everything outside this package is written against**, and it exists so
Scope B and a game compile against an INTERFACE rather than against whichever concrete holder is live. Both
`ContentSnapshot` (a candidate, or a version loaded but not yet active) and `ContentRuntime` (the active one)
implement it, which is what lets the validator, a test and the running server all be handed the same shape:

```csharp
public interface IContentSnapshot
{
    int VersionNumber { get; }                                  // contracts 7.1, the pack's number
    ContentVersionIdentity Identity { get; }                     // the number and its manifest hash
    bool TryGetRow(ContentTypeId type, int id, out ContentRow row);
    bool TryGetId(ContentTypeId type, ContentKey key, out int id);
    IReadOnlyList<ContentRow> Rows(ContentTypeId type);          // ordered by id, the whole type
    IReadOnlyList<RemapRule> Rules { get; }                      // the full ordered set, contracts 8.1
    bool IsRetired(ContentTypeId type, int id);                  // 3.9, the retired bit without a row walk
}
```

Seven members and no more. It carries no authoring concept, no chunk, no hash beyond the identity pair, and
no mutation, so a client holds one with the pure read graph of 2.1. `TryGetRow` is the generic path and
`ItemRow` (below) is the typed one over the same storage. **It ships in milestone 1.1**, with the registry
and the codecs, ahead of everything that consumes it, because Scope B's phase 1 is designed behind it and the
two phase 1s are meant to land in either order.

### 2.3 `KhaozEngine.Catalog.Authoring` public types

| Type | One line |
|---|---|
| `IContentAuthoringStore` | The provider seam: draft edits, publish, version listing, audit, id allocation, bulk import and export. |
| `ContentAuthoringSchemaMode` | `AutoCreate` or `ValidateOnly`, the journal's shape (`SqliteJournalSchema.cs:9-13`). |
| `ContentDraft` | The one open draft: its base version, its edit list and its opened-by and opened-at stamps. |
| `ContentEdit` | One edit: type, id or key, operation (`Add`, `Update`, `Retire`, `Fork`), and the field values it sets. |
| `ContentChangeSet` | An ordered, deduplicated edit list, the durable form of a draft. |
| `ContentAuditEntry` | One audited field change: who, when, note, type, id, field, before and after. |
| `ContentVersionRecord` | A published version's row: number, both manifest hashes, minimum builds, generation, publisher, note. |
| `ContentFamily`, `ContentFamilyBlock` | Contracts 5.2: a named family and its ordered aligned id blocks. |
| `ContentIdAllocator` | Reserve-then-issue over a family's blocks, with the durable high-water mark of contracts 6.2. |
| `ContentPublisher` | The publish pipeline of section 6. |
| `ContentDiff`, `ContentDiffEntry` | The field-level diff between two versions, contracts 4.7. |
| `ContentBundle` | The bulk import and export envelope of section 10.9. |
| `ContentAuthoringException` | The one exception type, carrying the offending type, id and reason. |

### 2.4 `KhaozEngine.Catalog.Netcode` public types

| Type | One line |
|---|---|
| `ContentIdentityGateAuthenticator` | Contracts 7.5, modelled on `WorldIdentityGateAuthenticator` (`KhaozEngine.Netcode/ConnectionGate.cs:53-96`). |
| `ContentIdentityLayer` | Wrap and unwrap of the `<versionNumber>\|<clientManifestHash>` layer value. |
| `ContentRefusal` | The two stable refusal tokens of contracts 7.5 and their parsers. |

### 2.5 Dependency edges

```
KhaozEngine.Primitives         (Foundation, already in every graph, owns IRandomSource)
        ^
        |
KhaozEngine.Catalog            (Foundation, no third-party)
        |                \
        |                 +--> KhaozEngine.Catalog.Netcode --> KhaozEngine.Netcode
        v
KhaozEngine.Catalog.Authoring  (Server, pure .NET)
        |                \
        v                 v
Catalog.Sqlite          Catalog.SqlServer
   |                          |
   v                          v
KhaozEngine.Sqlite      Microsoft.Data.SqlClient
```

Scope B's `KhaozEngine.ItemInstances` depends on `KhaozEngine.Catalog` and `KhaozEngine.Items` (contracts 3.2)
and nothing here depends on it. The edge is one way, which is what lets Scope A ship and be adopted before
Scope B exists. `KhaozEngine.Catalog` depends DOWNWARD on `KhaozEngine.Primitives`, which sits below both, so
the one-way edge survives the loot roller needing a random source.

**The shared surface is not one type, and pretending it was hid the coupling.** Scope B compiles against all
of this, and each member is named where this spec defines it:

| Shared surface | Where Scope B reads it | Defined in |
|---|---|---|
| `IContentSnapshot` | `ContainerLoad`, the stat evaluator's constructor | 2.2, shipped in milestone 1.1 |
| Per-type id-to-row lookup and `ContentRow` | Every definition read | 2.2, 9.1 |
| `ItemRow` | The four hot `item` fields, per operation | 2.2, 9.1 |
| The content version number | The page stamp and the connect door | 2.2 `ContentVersionIdentity` |
| `RemapRule`, `RemapRuleKind`, `RemapRuleSet` | The instance remap pass | 2.2, section 7.7 |
| Key lookup, key to id per type | Authoring and boot resolution | 9.4 |
| `stat` rows and tag ids | The stat fold and every tag scope | 3.2, 3.4 |
| `ContentVarint` | The payload codec | 2.2 |
| `IRandomSource` | Generation, via `KhaozEngine.Primitives` | contracts 14.1 |

`ContentVarint` is still the one type whose BYTES both specs write, so the varint definition of contracts 15
has exactly one implementation in the tree. It is not the whole seam.

**No cycle, checked mechanically.** `KhaozEngine.Catalog` never references `Catalog.Authoring`, which is what
keeps the client graph free of the authoring types. A new architecture test in
`KhaozEngine.Tests/ArchitectureTests.cs` asserts it, in the same shape as the existing JsonSchema.Net
containment test at line 121.

### 2.6 Where the tests live

AGENTS.md is explicit that a test project references ONLY the engine projects its tests use, because push CI
selects test projects by the reference graph and an over-broad reference silently degrades selection. That
rules out putting catalog tests in `KhaozEngine.Game.Tests`, which already references 20-plus projects
including `Render3D` and `Physics.Bepu` (`a-engine.md:1593-1600`, discovered-work candidate 3).

| Suite | Project | References |
|---|---|---|
| Registry, codecs, hashes, pack format, validator, runtime, remap rules, loot roller, golden files, decoder fuzzing | **`KhaozEngine.Catalog.Tests`** (new) | `KhaozEngine.Catalog` only, which brings `KhaozEngine.Primitives` transitively. |
| Draft, change set, publish pipeline, diff, id allocator, bundle import and export, all against an in-memory store | `KhaozEngine.Catalog.Tests` | plus `KhaozEngine.Catalog.Authoring`. |
| Provider conformance: `ContentAuthoringStoreConformance` abstract class, `SqliteContentAuthoringStoreTests` and `SqlServerContentAuthoringStoreTests` subclasses | `KhaozEngine.Server.Tests/Catalog/` | The existing home of every Commerce and WorldStore provider test (`a-engine.md:781-788`). |
| The connect-door layer and its refusal tokens | `KhaozEngine.TileWorld.Netcode.Tests` | Where the existing gate tests are. |
| Scale runs at 50,000 and 1,000,000 definitions | `KhaozEngine.Benchmarks` plus a structural test in `KhaozEngine.Server.Tests` | The journal's shape (`a-engine.md:1322-1349`). |

**The fuzzer's seeded random needs no extra reference.** `SeededRandomSource` ships in
`KhaozEngine.Primitives` (contracts 14.1), which `KhaozEngine.Catalog` already references, so 15.2's seeding
and the loot roller's own tests both compile against the single `KhaozEngine.Catalog` reference above. Adding
a second reference for it would be the over-broad reference AGENTS.md warns about, for a type that is already
in the graph.

The new `KhaozEngine.Catalog.Tests` csproj sets `<IsPackable>false</IsPackable>` and pins
`<RootNamespace>KhaozEngine.Tests</RootNamespace>`, per AGENTS.md, so its declared namespaces are
`KhaozEngine.Tests.Catalog.*`. It is added to `KhaozEngine.slnx` beside `KhaozEngine.Foundation.Tests`.

**The SQL Server leg is env gated.** `SqlServerContentAuthoringStoreTests` uses a new
`KhaozEngine.Server.Tests/Catalog/CatalogSqlServerFactAttribute.cs`, a `FactAttribute` subclass that sets
`Skip` unless `KE_CATALOG_SQLSERVER` holds a reachable connection string. It is a byte-for-byte copy of
`KhaozEngine.Server.Tests/Commerce/SqlServerFactAttribute.cs:11-18` with the variable name changed, and its doc
says the same thing: CI has no SQL Server, so these run locally or against a test database on demand. A
SEPARATE variable rather than reusing `KE_COMMERCE_SQLSERVER`, because the two suites create different schemas
and an operator should be able to run one without the other.

The SQLite subclass uses the per-test unique in-memory database idiom from
`KhaozEngine.Server.Tests/Commerce/SqliteWalletStoreTests.cs:7-17`,
`$"Data Source=catalog_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"`, and disposes it.

**No new `DisableParallelization` collection is needed**, because nothing in Scope A writes process-global
state. The registry is per `ContentTypeRegistry` instance, deliberately, and is NOT a static. That is a
departure from `GrimhollowEconomy.Current` and `GrimhollowSkilling.Current`, both process-global ambients
(`b-grimhollow.md:247-252`, `b-grimhollow.md:392-403`), and it is deliberate: AGENTS.md records
`GuiTheme.Default` (#349) as the failure an ambient static causes in a parallel xUnit assembly, and a
registry-per-instance costs one field on the host.

### 2.7 File sizes

The KESIZE ratchet caps a file at 800 lines (`FileSizeAnalyzer.cs:25`, `a-engine.md:1350-1372`), and AGENTS.md
is explicit that the fix for a breach is a new TYPE rather than an arbitrary split. Three files in this design
are candidates to breach it, and each is split by TYPE from the start rather than later:

- The SQLite DDL. It goes in `SqliteCatalogSchema.cs` holding only the const DDL string plus the version and
  migration constants, with validation in `SqliteCatalogSchemaValidation.cs`, mirroring the journal's split
  between `SqliteJournalSchema.cs` and its validation helpers.
- The publish pipeline. `ContentPublisher.cs` holds the ordered steps and delegates each to a named type:
  `ContentIdAllocation`, `ContentChunkBuilder`, `ContentManifestBuilder`, `ContentPublishCommit`.
- The validator. `ContentValidator.cs` holds the sweep and the finding list. Each check family is its own
  internal static class (`ContentReferenceChecks`, `ContentKeyChecks`, `ContentRemapChecks`,
  `ContentVisibilityChecks`, `ContentSchemaChecks`), so adding a check never grows the sweep.

## 3. Data model

### 3.1 The engine content types

Six types register in the ENGINE range `1` to `255` of contracts 4.3. The range holds 255 ids and six are
spent, which is deliberate headroom: an engine release adding a type can never collide with a game's, which is
the property `ReplicationRegistry.FirstExtensionTypeId` already gives components
(`TileProtocol.Components.cs:17-30`, `a-engine.md:880-916`).

| Type id | Type key | What it is | Default visibility | Default chunk slots |
|---|---|---|---|---|
| `1` | `tag` | The tag vocabulary of contracts 4.6. | `Client` | 4,096 |
| `2` | `item` | The item base. | `Client` | 1,024 |
| `3` | `stat` | The stat definition of contracts 13.1. | `Client` | 4,096 |
| `4` | `loot_table` | A named drop or reward table. | `ServerOnly` | 4,096 |
| `5` | `loot_entry` | One weighted row of one loot table. | `ServerOnly` | 16,384 |
| `6` | `base_socket` | One socket an item base is authored with, in authored order. | `Client` | 16,384 |
| `7` to `255` | reserved | Future engine types. Never assigned by a game. | | |

**`item` takes 1,024 slots and every other type takes more, which is a download decision rather than a
storage one.** A chunk is the unit of re-download, so the type that is edited one row at a time wants the
smallest chunk that still amortizes a manifest entry. At 1,024 slots and the item row of section 14.1, one
chunk is about 133 KB uncompressed and a one-item edit re-downloads it alone, which is what puts budget P6
inside its target instead of three times over it. The cost is four times the manifest entries, which at 50,000
definitions is 49 rather than 13, and at the stress figure 977 rather than 245. Question Q3 raised this and
section 19 puts it in the expensive column, so it is settled HERE, before the first publish, rather than
measured afterwards.

`loot_entry` takes a larger chunk than its parents because entries outnumber tables by roughly the branching
factor, and a chunk is a transport unit sized for download economics rather than an authoring unit (contracts
4.5). At 4,096 slots a table of 50 entries would straddle chunks for no reason. `base_socket` takes the same
16,384 for the same reason, being a child that outnumbers its parent.

Those two are also the only engine types that declare their own row cap, `maxRowBytes: 512` rather than the
1,024 default, because 16,384 slots at the default would break the registration bound of section 7.1. Their
rows are about 20 bytes for a loot entry and under 10 for a base socket, so 512 is already generous.

**Why loot entries are their OWN content type and not a repeated field on the table.** This is the one
structural choice in the data model that is genuinely contested, because contracts 4.7's value kinds are a
flat list with no repeated group, so the alternative is `opaque bytes` carrying an encoded entry list.

| Criterion | Entries as their own type | Entries as `opaque bytes` on the table |
|---|---|---|
| Renders in the generic editor with no bespoke screen | 10 | 2 |
| Field-level audit names the changed entry and its before value | 10 | 2 |
| Field-level publish diff | 10 | 2 |
| Cross-reference validation reaches the item id | 9 | 5 |
| Bytes in the pack | 6 | 9 |
| Matches the consumer shape being adopted | 10 (Ruinborne has `loot_table` plus `loot_table_entry`, `c-ruinborne.md:104-109`) | 4 |
| Cost to add a field to an entry | 9 | 5 |
| Total | 64 | 29 |

Recommendation: entries as their own content type. The `opaque bytes` option wins only on pack size, and the
margin is a varint per entry for the parent key reference against a count prefix. Everything the field schema
exists to give (contracts 4.7: a generic editor, a field-level audit, a field-level diff) is lost the moment a
repeating group hides inside a blob, and Ruinborne's `item_ability_modifier` is the cautionary version of the
blob answer already in production: its tags are a comma-joined string in one column, which makes a set
membership test a substring search (`c-ruinborne.md:67-72`).

The same rule generalizes: **any repeating child structure in engine or game content is its own content type
with a key reference to its parent, never a blob field.** Section 20 does not raise a change request for a
repeated-group value kind, because this rule removes the need for one.

### 3.2 The `tag` type

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `name` | localized text key (derived marker) | | `Client` | yes |
| `sort` | int | | `Client` | no |

**A `localized text key` field is a MARKER. It carries no value, and the row carries no bytes for it.** The
key is DERIVED as `<type key>.<content key>.<field>` (contracts 12.1), so `name` on the `tag` row `metal` is
`tag.metal.name` and can be nothing else, computed identically by the encoder, the console and every reader.
That is contracts 12.1's own rule, "derivation rather than authoring, because an authored key rots
independently of the row it names", carried all the way through to the storage. A stored value that can only
ever hold one legal string is a field that can hold an ILLEGAL one: authored, `stone_sword.name` can read
`item.iron_sword.name` and silently show another row's display name, and no check can catch it without
deriving the right answer and comparing, at which point the stored copy was the only thing that could be
wrong.

The consequences, stated once here because every localized field in section 3 inherits them:

- `catalog_row_field` writes NO row for a marker field, in neither the published table nor the draft one
  (section 4.4). There is no value to hold and no version at which it differs.
- The row codec writes NO bytes for it. Section 7.9's worked example is 15 bytes per row shorter for exactly
  this reason, and every pack is smaller by one derived key per localized field per row (section 14.1).
- The derived key and the translated string live in the TEXT chunk (section 7.6), which is the one place a
  localized string lives at all.
- A console shows the derived key READ ONLY beside its resolved text, and an edit payload never carries one
  (sections 10.3 and 10.5).
- `KEC0030` checks the DERIVED key, and it still has teeth: a 64-character type key, a 64-character content
  key and a 64-character field name derive a 194-character key, past contracts 12.1's 192-character bound.
- `required` on a marker field means the console expects a string for it, not that the row carries one. A
  derivable key is always present, so `KEC0005` cannot fire on a marker. Whether a LANGUAGE carries a string
  is the localization catalog's business, layered and non fatal (section 5.5), with section 15.7's opt-in
  coverage sweep for a game that wants the stronger guarantee.

Section 20's CCR-3 asks contracts 4.7 to say this in its kind list, because `localized text key` currently
reads there as a value kind like any other.

A tag row is an id, a key and a display name. That is all contracts 4.6 asks for: tags are referenced BY ID
from item bases, mods, stats, stores and drop tables, a row's tags are an ORDERED LIST whose authored order is
preserved and never sorted, and strings are never the tag representation in a pack, a payload, on the wire or
in a durable row.

`sort` exists only so a console can list tags in an authored order rather than by id. It is the single case in
this spec where a field exists for the editor's benefit, and it is named so nobody later mistakes it for a
gameplay field.

### 3.3 The `item` type

The schema below is the union of what the three surveys found in production plus the `tradable` flag absorbed
from Grimhollow's `feature/item-drop` branch (contracts 7.6, `b-grimhollow.md:1332-1358`). Every field is
justified by a real consumer field it replaces, and nothing is speculative.

| Field | Value kind | Reference target | Visibility | Required | Replaces |
|---|---|---|---|---|---|
| `name` | localized text key (derived marker) | | `Client` | yes | Grimhollow `ItemStrings.NameFor` 35-arm switch (`b-grimhollow.md:673-685`), Ruinborne `item_def.display_name` (`c-ruinborne.md:49-60`). |
| `examine` | localized text key (derived marker) | | `Client` | no | Grimhollow `ItemStrings.ExamineFor` (`b-grimhollow.md:679-681`). |
| `tags` | tag list | `tag` | `Client` | no | Grimhollow `CanBeAHatchet` and `CanBeAPickaxe` predicates (`b-grimhollow.md:56-59`), Ruinborne `item_def.item_type` and `.slot` bare varchars (`c-ruinborne.md:63-66`, Ruinborne [#199](https://github.com/APKiwiOrg/Ruinborne/issues/199)). |
| `stackable` | bool | | `Client` | yes | Grimhollow `GrimhollowItems.Stackable` (`b-grimhollow.md:52`), Ruinborne `item_def.stackable`. |
| `max_stack` | int | | `Client` | yes | Ruinborne `item_def.max_stack`. Grimhollow has no cap today, so its import writes `int.MaxValue` for a stackable and 1 otherwise. |
| `tradable` | bool | | `Client` | yes | Grimhollow `assets/config/items.jsonc` `tradable` (item-drop design section 1). |
| `value` | scaled int, scale 1 | | `Client` | yes | Grimhollow `item.<key>.value` economy row (`b-grimhollow.md:208-234`). |
| `icon` | opaque bytes, asset reference | | `Client` | no | Grimhollow `ItemIcons.RosterIcons` (`b-grimhollow.md:686-705`), Ruinborne `item_def.icon_id`. |
| `mesh` | opaque bytes, asset reference | | `Client` | no | Grimhollow `GroundItemMeshes.Roster` and its duplicated 35-arm switch (`b-grimhollow.md:706-724`). |
| `held_mesh` | opaque bytes, asset reference | | `Client` | no | Grimhollow `GrimhollowHeldMeshes.For` 12-arm switch (`b-grimhollow.md:725-736`). |
| `ground_pose` | int | | `Client` | no | Grimhollow's 12-id lie-flat list inside `GroundTransformFor` (`b-grimhollow.md:715-721`). 0 upright, 1 lie flat. |
| `icon_tilt`, `icon_spin` | scaled int, scale 1000 | | `Client` | no | The two per-item switches in `tools/SnapshotTool/IconShots.cs` (`b-grimhollow.md:737-754`). |
| `durability_max` | int | | `Client` | no | New. 0 means the base has no durability. Scope B reads it. |
| `socket_max` | int | | `Client` | no | New. The CAP, not the authored set. 0 means the base takes no sockets. The sockets a base is authored WITH are `base_socket` rows (below). Scope B reads both. |
| `equip_profile` | key reference | game type | `Client` | no | Grimhollow `GrimhollowEquipmentRoster.For` 9-arm switch (`b-grimhollow.md:78-104`), Ruinborne `weapon_def` and `item_stat` (`c-ruinborne.md:78-95`). |

**`socket_max` is a cap and `base_socket` is the declaration, and one int could not be both.** A count says
HOW MANY sockets a base may end up with. It cannot say which socket TYPES those sockets are, nor in what
order, and Scope B's generator seats "the base's authored socket declaration, in authored order, every socket
empty". With only the int, every base generated from an `item` row would produce sockets with socket type 0,
meaning no restriction, and the decision that socket types restrict what a socket accepts and that the
restriction is CONTENT would be unreachable for anything except a unique template, which is the one row
carrying its own forced socket list. The `socket_type` rows would exist with nothing pointing at them.

So a base's sockets are `base_socket` rows, a sixth engine content type, by the same rule that made
`loot_entry` its own type rather than a blob on `loot_table`:

| `base_socket` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `item` | key reference | `item` | `Client` | yes |
| `sort` | int | | `Client` | yes |
| `socket_type` | key reference | game or Scope B type key `socket_type` | `Client` | yes |

`sort` is the AUTHORED ORDER, which is what makes "the second socket" a stable phrase across a republish, and
it is the order the generator seats them in. `socket_type` is a key reference resolved at registry freeze, the
same late binding as `equip_profile` above: Scope B registers the type under that key, and a game that does
not use instances leaves the type unregistered and authors no `base_socket` rows at all.

**`socket_max` stays, and it is the cap the `AddSocket` primitive checks.** The two numbers answer different
questions and both are read. The authored rows say what a base STARTS with, and crafting adds sockets beyond
that up to the cap, so a base authored with one socket and a cap of three is a perfectly ordinary row.
`socket_max` below the base's `base_socket` count is refused at publish, because a base that starts above its
own cap is authored nonsense. The finding comes from Scope B's band, `KEC0100` to `KEC0199`, rather than from
an engine code, because the AddSocket rule it contradicts is Scope B's and the engine has no opinion about
what a socket is for.

**`equip_profile` points at a GAME type and the engine does not own its shape.** An equip slot vocabulary is
game specific: Grimhollow has eleven slots whose numbers are durable because they shipped in that order
(`b-grimhollow.md:80-85`), Ruinborne has a nullable `slot NVARCHAR(32)` with no reference table. The engine
declares the FIELD and the game registers the type it points at, which is exactly the `IProductCatalog` seam's
stated shape: "The engine defines the shape, the game supplies entries"
(`KhaozEngine.Commerce/IProductCatalog.cs:5`, `a-engine.md:668-676`).

**The mechanism, stated concretely, because "points at a game type" does not name one.** The engine's `item`
schema declares `equip_profile` as a key reference whose target is the type KEY `equip_profile`. A key, not an
id: contracts 4.7 says a key reference names "the content type key it points at", and a key is a name the
engine can write down without knowing who will answer to it. A game that wants equip profiles registers a type
under that key in its OWN id range, which is what Grimhollow does at type id `1026`, key `equip_profile`
(section 3.6). Nothing about the engine's schema is mutated by that, which matters because contracts 4.4
forbids a game replacing an engine type's codec and says nothing about letting one edit an engine schema: the
engine wrote the target key once, at registration, and it is the same string in every game.

The target is resolved at REGISTRY FREEZE rather than at compile time, which is what makes the key work as a
late binding. Two outcomes and no third:

- A type is registered under the key `equip_profile`. Every `item` row's `equip_profile` field is a key
  reference into it and `KEC0006` checks that the row it names is live.
- No type is registered under that key. Then the field MUST be 0 on every row, and a non-zero value is refused
  by `KEC0007`, which is exactly "a key reference names a content type that was never registered". A game that
  leaves equip profiles unwired therefore leaves the field at 0 and never notices it exists.

The same pattern is available to any engine field that has to reach into game-owned content, and `socket_type`
on `base_socket` is the second use of it.

**An `equip_profile` row holds the SLOT and the ARCHETYPE, and the NUMBERS are stat lines.** This is the one
place where this plan and Scope B's were pointed at the same nine-arm switch with two different destinations:
this spec mapped it to an `equip_profile` row carrying `slot`, `accuracy`, `strength`, `defence` and
`weapon_archetype` as plain ints, and Scope B mapped it to `stat` rows plus `Flat` lines per equipable base
that its evaluator folds. Both cannot be right, and a game would have implemented one and discovered the other
at the point where the shipped `item` field set is expensive to change. The reconciliation is a split rather
than a winner:

| Lives on the `equip_profile` row | Lives on child stat line rows |
|---|---|
| `slot`, the game's own equip slot vocabulary | `accuracy`, `strength`, `defence` and anything else numeric |
| `weapon_archetype`, which selects the attack speed row | Each as a `Flat` line against a `stat`, at source kind 1 |

Three ints on a game row are invisible to the stat evaluator: it folds LINES, and a line is a stat reference
plus a combine kind plus a value. Three ints as stat lines are invisible to the equipment code that asks which
slot a thing goes in, because a slot is not a number to fold. So the row keeps what is structural and the
lines carry what is numeric, `item.equip_profile` survives with something to point at, and Scope B's source
kind 1 has rows to fold.

The child type is the GAME's, registered in the game range under a key of its own, and it is the same shape
as Scope B's own `stat_line`: a key reference to its parent row, a `stat` key reference, a `combine` kind and
a value. It is NOT registered under the key `stat_line`, which Scope B takes for the child of a mod tier, and
a type key is unique across the whole registry. Grimhollow registers it as `equip_stat_line` (3.6). The
evaluator does not care what the type is called, because it reads lines through the snapshot at source kind 1,
and the key is only how an author and the generic editor name the rows.

**Asset references are `opaque bytes` with a declared shape, and section 20 asks for better.** Contracts 4.7's
value kinds have no plain-string kind, and a mesh reference like `kit/unknown_item.glb`
(`b-grimhollow.md:709`) cannot be a content key, because keys are `a-z0-9_` only with no dot and no slash
(contracts 5.3). So on the contracts as written, an asset reference is `opaque bytes` whose codec writes a
varint length followed by UTF-8, and the type's codec constrains the character set to `a-z0-9_./-` and the
length to 128 bytes. That works, and it costs the generic editor: with no way to say in the schema that these
bytes are text, a console renders a hex box. CCR-1 in section 20 asks for an `AssetReference` value kind. The
whole design works either way and only the editor changes.

### 3.4 The `stat` type

Contracts 13.1 fixes this schema and this spec adds nothing to it.

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `name` | localized text key (derived marker) | | `Client` | yes |
| `scale` | int | | `Client` | yes |
| `min` | int | | `Client` | yes |
| `max` | int | | `Client` | yes |
| `tags` | tag list | `tag` | `Client` | no |
| `display_format` | localized text key (derived marker) | | `Client` | yes |

`scale` is a fixed power of ten and the stored integer is the value times `scale` (contracts 13.1). `min` and
`max` are in scaled units and are the inclusive clamp of the evaluation formula (contracts 13.2). The
validator refuses a `scale` that is not a power of ten and refuses `min > max` (`KEC0020`, `KEC0021`).

`name` and `display_format` are derived markers (section 3.2). The row stores neither, and
`stat.<content key>.display_format` resolves through the text chunk like every other string, which is also
what lets a translator move a unit suffix without touching the stat row.

`Client` throughout, because a client tooltip computes a displayed stat with the same integer arithmetic the
server uses (contracts 13.4), and it cannot do that without the scale and the clamp.

### 3.5 The `loot_table` and `loot_entry` types

| `loot_table` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `roll_count` | int | | `ServerOnly` | yes |
| `tags` | tag list | `tag` | `ServerOnly` | no |
| `guaranteed` | bool | | `ServerOnly` | yes |

| `loot_entry` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `table` | key reference | `loot_table` | `ServerOnly` | yes |
| `item` | key reference | `item` | `ServerOnly` | no |
| `nested_table` | key reference | `loot_table` | `ServerOnly` | no |
| `weight` | int | | `ServerOnly` | yes |
| `chance_bp` | int | | `ServerOnly` | yes |
| `min_count` | int | | `ServerOnly` | yes |
| `max_count` | int | | `ServerOnly` | yes |
| `sort` | int | | `ServerOnly` | yes |
| `required_tags` | tag list | `tag` | `ServerOnly` | no |

Both types are `ServerOnly` at the TYPE level, so the whole family is omitted from every client manifest
(contracts 11.3). A drop table is exactly the content a client must not have, and the owner put drop tables in
the same versioned content as items and called it important (#882 comment 2).

Two roll shapes are covered by one schema, and they compose, which is what Grimhollow's goblin already needs:
a `guaranteed` entry rolls its own `chance_bp` independently (Grimhollow's bread, one kill in four,
`b-grimhollow.md:123-136`), and a non-guaranteed entry competes in a weighted draw of `roll_count` picks
(Grimhollow's coin purse, drawn from `CoinDropChoices`). `chance_bp` is basis points out of 10,000, the same
convention as contracts 13.2, so there is one percent representation in the system.

`nested_table` is how a table references another table, which is what a rarity tier or a sub-table needs.

**An entry names its draw exactly one of three ways, and `KEC0023` refuses every other count.** The three are
`item` set, `nested_table` set, and a non-empty `required_tags`. Exactly one, never two, never none. The
earlier wording was "exactly one of `item` and `nested_table`", which made the third shape unreachable: with
`item` unset the rule forced `nested_table` set, so an entry could never be a tag draw at all, and the
paragraph below described a path the validator refused.

**If more than one is present, nothing wins and the entry is refused.** A precedence order would be a silent
choice between two things an author wrote down, and an author who set both an `item` and a `required_tags`
filter meant one of them, which only they know. `KEC0023`'s message names every one that is set.

`required_tags` is the tag filter a drop table uses to name a CLASS of item rather than enumerating ids
(contracts 4.6, first bullet). When it is the one that is set, the entry draws uniformly from live items
carrying every listed tag, resolved at pack load into a precomputed candidate array (section 9.4).

A cycle through `nested_table` is refused (`KEC0024`) by a depth-first walk with a visited set, because a
cyclic loot table is an infinite roll at runtime and the validator is the only place that can see the whole
graph.

**`LootRoller` is the one implementation of everything above.** The composition rule in this section is not
a suggestion a game re-derives, it is executable, and the engine executes it:

```csharp
public sealed class LootRoller
{
    public LootRoller(ContentRuntime runtime, IRandomSource random);
    public int Roll(int tableId, Span<LootDraw> destination);    // returns the count written
    public bool TryRoll(int tableId, Span<LootDraw> destination, out int written);
}

public readonly record struct LootDraw(int ItemId, int Count, int TableId);
```

- **It returns ITEM IDS AND COUNTS, and nothing else.** One `LootDraw` per line of loot, carrying the item's
  definition id, the rolled count between the entry's `min_count` and `max_count`, and the id of the table the
  line came from, which is the one thing a caller cannot reconstruct after a nested draw. It writes into a
  caller-supplied span and returns how many it wrote, so a roll allocates nothing. A destination too small is
  filled and the return is the span's length, with the overflow reported through `TryRoll`'s bool overload, so
  a caller can size up rather than silently lose drops.
- **It does not create anything.** It does not build an instance, does not place a ground stack, does not add
  to an inventory, does not emit an event and does not touch a journal. It reads content and a random source
  and returns numbers. That is what keeps it in `Foundation` next to the arrays it walks, and it is what lets
  Scope B's generator take its output as an input without the two depending on each other.
- **The journal event a game records is the GAME's, not the roller's.** Scope B's `item-generated` event and
  Grimhollow's own drop logging both want to name a source, and this is the type that knows it: the caller
  passes the `TableId` it got back into whatever event it writes. The engine does not write the event, because
  a journal section, a section flag and an event shape are all the game's, and an engine type that logged
  would be making that decision for every consumer.
- **The draw order is fixed and is part of the contract**: every `guaranteed` entry in `sort` order first,
  each rolling its own `chance_bp` independently, then `roll_count` weighted picks over the non-guaranteed
  entries, each pick a `NextInt(0, total)` and a binary search over the prefix-summed array of 9.4. A
  `nested_table` entry recurses at the point it is drawn, with the recursion depth bounded by the acyclicity
  `KEC0024` already guarantees. A `required_tags` entry draws uniformly from its precomputed candidate array.
  Writing the order down is the point: it is exactly the interaction two independent reimplementations would
  have got differently.
- **`IRandomSource` comes in through the constructor** (contracts 14.4, `KhaozEngine.Primitives`), never an
  ambient static and never a default, so a test rolls a seeded table and asserts exact drops.
- **Budgeted at P9**, under 100 ns and zero allocation for one weighted draw over a 200-entry table, which is
  the budget this spec already carried for a draw nothing performed.

### 3.6 How Scope B and a game register their own

Registration is contracts 4.2's call, once, at process start, before any pack loads:

```csharp
registry.RegisterContentType(
    band: ContentRegistrationBand.Game,
    typeId: 1024,
    typeKey: "store",
    codec: new GrimhollowStoreCodec(),
    validator: new GrimhollowStoreValidator(),
    schema: GrimhollowStoreSchema.Create(),
    defaultVisibility: ContentVisibility.ServerOnly,
    chunkSlots: 256,
    maxRowBytes: ContentPackFormat.DefaultMaxRowBytes,
    maxDefinitionId: null,
    loadIndex: null);
```

`maxRowBytes` is optional and defaults to `ContentPackFormat.DefaultMaxRowBytes`. It may not exceed
`MaxContentRowBytes`, and registration refuses a type whose
`chunkSlots * (maxRowBytes + 8) + 36` exceeds `MaxChunkUncompressedBytes` (section 7.1), which is the check
that keeps a publish from building a chunk no reader will load.

`maxDefinitionId` is optional and null means the type's only ceiling is the 31 bits of positive `int` space.
A type declares one when a FORMAT it is carried in cannot hold a bigger number: Scope B declares 255 on
`rarity_rule`, because payload kind 130 writes the rarity as a byte. Section 4.7 is where the rule is spent,
including why retired rows count toward it and why the validator checks it with `KEC0042` as well as the
allocator refusing it. Unlike `chunkSlots` it is NOT frozen at the first publish, because raising it is what a
format widening looks like from this side and refusing that would make a legal change impossible. Lowering it
below the type's `issued_through` is refused at registration, since rows above the new ceiling already exist
and no answer short of deleting them would satisfy it.

`loadIndex` is optional and null means the type derives nothing at load. Section 9.4 specifies
`IContentLoadIndex` and boot step 7b, which is where a type whose gameplay reads want a derived table builds
one without the host wiring a second pass.

**`chunkSlots` is immutable after a type's first publish, and `KEC0029` says so.** It was guarded only by
`KEC0028`'s power-of-two bound, which refuses 1,000 and accepts a change from 256 to 1,024 on a type that has
published forty versions. That change renumbers every chunk (contracts 4.5 calls it out as expensive for
exactly this reason): publish step 6 computes `chunkIndex = id / 1024` for the rows that changed while step 10
copies unaffected chunk rows forward from a version where the same index meant `id / 256`, so the new
manifest names one chunk index under two meanings. It does not corrupt anything at read time, because the
`KECC` header carries `slotBase` and `slotCount` and a reader refuses a mismatch with `chunk-range-mismatch`
(section 7.2), so the publish yields a version that fails every boot closed at exit 3 instead. A version
nobody can load is still a defect worth refusing at the publish that makes it, which is what `KEC0029` now
does.

The registry freezes when the first pack loads and a later registration throws (contracts 4.2). A game MAY
register a type in the game range, supply its own codec and validator, set its default visibility, set its
chunk size, set its row cap, and register an ADDITIONAL validator for an engine type that runs after the
engine's own. A game
MAY NOT register into the engine or Scope B ranges, replace an engine type's codec, weaken an engine
validator, change a type's id or key after the first publish, or register the same type id twice (contracts
4.4).

**`band` is the mechanism behind that MAY NOT, and prose was not one.** Contracts 4.4 states the three-way
split and this spec restated it, but registration threw only for a codec and schema mismatch or a duplicate
id, so a game registering type 300 would have succeeded and collided with Scope B at the next engine release,
silently until the collision. `ContentRegistrationBand` is `Engine`, `Instances` or `Game`, the caller names
its own band, and registration throws `ContentRegistrationException` when the type id falls outside it:

| Band | Type ids | Who passes it |
|---|---|---|
| `Engine` | 1 to 255 | The engine's own registration helper for the six types of 3.1. A game host CALLS that helper, and the helper passes the band, so an adoption step that says "register the engine types" is calling one method rather than claiming a band. |
| `Instances` | 256 to 1023 | Scope B's registration helper, the same way. |
| `Game` | 1024 to 65,535 | Every consumer, directly. |

This is the shape `ReplicationRegistry.FirstExtensionTypeId` already uses for components, which is the
precedent contracts 4.3 cites for the split in the first place, so the engine now enforces the rule in the
same place for content types as it does for components. It costs one comparison at process start and it
converts a silent collision two releases away into a throw on the line that caused it. The band is NOT a
capability: it does not let an `Engine` caller do anything a `Game` caller cannot, it only says which ids the
caller is entitled to. A game that wants an engine type's behaviour still adds a validator rather than
claiming a band.

Scope B's `InstancePropertyRegistry` takes the same argument for property KINDS, which is its spec's to
specify and is the identical failure one layer down.

**The codec is checked against the schema at registration** (contracts 4.7). `IContentRowCodec` exposes
`IReadOnlyList<string> WrittenFields { get; }`, registration compares that set against the schema's field
names, and a mismatch in either direction throws `ContentRegistrationException` naming both sides before the
registry freezes. The check costs one set comparison per type at process start and it is what stops an editor
writing a field nothing reads.

Grimhollow's game types, from #882's phase 1 acceptance list and `b-grimhollow.md` sections 2 to 4:

| Type id | Type key | Source it replaces |
|---|---|---|
| `1024` | `store` | `GrimhollowShop.GeneralStore` and `RatesFor` (`b-grimhollow.md:105-122`). |
| `1025` | `store_shelf` | The eight-element `int[]` draw order inside `GeneralStore`. |
| `1026` | `equip_profile` | The SLOT and the weapon archetype of `GrimhollowEquipmentRoster.For`'s 9-arm switch (`b-grimhollow.md:78-104`). |
| `1035` | `equip_stat_line` | The NUMBERS of the same switch, one `Flat` line per stat per profile, folded at source kind 1 (3.3). |
| `1027` | `food` | The `item.<key>.heals` and `.attackDelayTicks` economy rows (`b-grimhollow.md:208-234`). |
| `1028` | `gathering_node` | `skilling.jsonc` `trees` and `rocks` blocks (`b-grimhollow.md:347-368`). |
| `1029` | `recipe`, `1030` `recipe_input`, `1031` `recipe_output` | `skilling.jsonc` `processing` block. |
| `1032` | `tool_tier` | `skilling.jsonc` `hatchets` and `pickaxes` blocks. |
| `1033` | `skill_curve` | `skilling.jsonc` `xp`, `gathering`, `parents`, `stamina` and `skills` blocks. |
| `1034` | `monster_drop` | The `GrimhollowDrops.Roll` switch, whose STRUCTURE is code today (`b-grimhollow.md:123-136`). |

`1035` is listed out of order because it was added when 3.3 reconciled the two plans for `EquipStats`, and ids
are assigned in the order they are decided rather than renumbered to read tidily. Nothing in a game's range is
reserved by the engine, and Grimhollow may keep going from `1036`.

Ruinborne's are in section 17. Scope B's are `256` to `1023` and are its spec's to assign.

### 3.7 The row model in the authoring store

A content row has THREE identities and they are not interchangeable:

- The `int` DEFINITION ID, unique within its type, allocated by the authoring store, never reused, never
  deleted, 0 reserved for none (contracts 5.1). This is what durable player data and the wire carry.
- The `string` KEY, unique within its type, immutable once published, `a-z0-9_` only, at most 64 characters
  (contracts 5.3). This is what an author, a debug console, a localization key and a cross-content reference
  use.
- The `(type, id, valid_from_version)` ROW VERSION, which is what the authoring store actually stores rows of.

**Rows are temporal.** Every stored row carries `valid_from_version` and `replaced_in_version`:

```
live set at version V  =  rows where valid_from_version <= V
                           and (replaced_in_version is null or replaced_in_version > V)
```

An edit does not update a row. It sets the old row's `replaced_in_version` to the version being published and
inserts a new row with `valid_from_version` set to the same number. So the history of a definition is the
ordered set of its rows, and "what did item 12 look like at version 40" is a query rather than an audit
replay. That is the #882 requirement stated directly: "Each row records the version it became valid in and the
version it was replaced in."

A row that is NOT edited in a publish is not touched at all. Its `valid_from_version` stays where it was and
its `replaced_in_version` stays null. This is what makes "rebuild only affected chunks" (section 6.6) a query
over `valid_from_version = <new version>` rather than a diff of every row.

**The draft is a change set against the last published version**, not a copy of it. There is exactly one open
draft per database (#882 body, "One open draft"). It holds an ordered list of `ContentEdit` records, each one
of:

| Operation | Carries | Effect at publish |
|---|---|---|
| `Add` | type, key, field values, optional family | Allocates an id, writes a row with `valid_from_version = new version`. |
| `Update` | type, id, the changed fields only | Closes the current row and writes a successor with the merged field set. |
| `Retire` | type, id, retire policy, optional replacement id | Closes the current row, writes a successor with `retired = 1`, and appends a `Retired` remap rule. |
| `Fork` | type, id, the copy's new key, the flag field to set on the copy, the changed fields for the ORIGINAL | Allocates a NEW id, copies the source row's whole field set onto it under the new key, sets the named flag field, applies the changed fields to the original, and appends a `MovedToLegacy` remap rule from the original id to the new one. |

**`Fork` is the fourth operation and it exists because kind 3 had no producer.** Contracts 8.2 defines remap
rule kind 3, `MovedToLegacy`, and Scope B's keep-legacy flow is built on it: an author who changes a mod's
ranges chooses between rescaling every stored roll silently and preserving what players already own. The
preserving answer needs the OLD definition to survive at a new id with a legacy flag set while the ORIGINAL
id carries the new numbers, so that stored payloads keep pointing at the values they were rolled against.
With only `Add`, `Update` and `Retire`, that flow cannot be expressed: `Add` plus `Update` gets the rows into
the right shape but emits no rule, so nothing tells a loaded page to move, and `Retire` emits kind 2, which
is a different statement with a different policy set. Kind 3 would have been a dead letter in the contracts
and the keep-legacy choice would have collapsed to the silent rescale by default.

**It is one operation rather than three, because the three are not separable.** Allocating the id, copying the
fields and appending the rule are one atomic statement about one definition, and an author who did them as
three edits could have the publish succeed with the rule missing, which is the state nothing can detect
afterwards: the pages are already migrated and the original values are gone. So `Fork` is refused as a whole
or applied as a whole, and `KEC0041` is its precondition check.

**Why a fourth operation rather than a keep-legacy policy on `Retire`.** The other shape considered was a
policy byte on `Retire`, which would have added no operation and reused a path that already emits a rule. It
was rejected because `Retire` says a definition is GONE and that saying so is irreversible (contracts 8.6,
`KEC0039`). A fork retires nothing: both rows are live afterwards, the original under its own id with new
values and the copy under a new id with the flag set, and either may be edited again. Folding the two into
one operation would have made "is this id retired" a question about a policy byte rather than about the
`retired` column, which is read by the runtime's retired bit (9.4), by `KEC0039`, by the rollback block of
12.2 and by Scope B's retired check. Four of those five readers would have needed the policy byte too, to
answer a question that the fourth operation answers by not touching the column at all.

**The flag field is the CALLER's, and the engine does not name it.** `legacy` on a Scope B `mod` row is the
case that motivated this, but the engine has no opinion about which boolean means "superseded" on a type it
did not define. The edit names a field, the field must exist on the type's schema and must be `Bool`, and
`KEC0041` refuses the edit otherwise. That keeps `Fork` a generic operation over the row model rather than a
Scope B feature the engine happens to host.

An edit is stored as the CHANGED FIELDS ONLY, never the whole row. That is what lets the audit record a field
level before and after with no extra table (section 4.6), and it is what makes two operators editing different
fields of one row a merge rather than a last-write-wins clobber.

**A draft edit that names a field the schema does not declare is refused at the API boundary**, not at
publish, with HTTP 400 and finding `KEC0004`. This is the direct answer to Ruinborne's
`ContentStore.UpsertItemDefAsync`, the one upsert in its whole facade with no `Require(...)` gate
(`c-ruinborne.md:370-383`, Ruinborne [#506](https://github.com/APKiwiOrg/Ruinborne/issues/506)), which lets an
operator save a row the server then rejects at boot while the console reports success.

### 3.8 Families, blocks and the parent reference

A FAMILY is an author-declared grouping within one content type whose members are allocated ids from one
contiguous block, so a membership test is two comparisons rather than a set lookup (contracts 5.2).

- A family reserves a block at creation. Block size is declared then, is a power of two between 16 and 65,536,
  and cannot be changed later.
- A block is aligned to its own size, so membership is `(id & ~(size - 1)) == base`.
- Block boundaries do NOT have to align to chunk boundaries, and nothing in this spec assumes they do.
- When a block fills, a SECOND block is reserved for the same family and the family carries an ordered block
  list. The runtime caches the list, so a membership test is a short loop.
- A family is never deleted. It is retired like a definition.

The `parent_id` column is on every row, is 0 by default, and in phase 1 the validator refuses any non-zero
value (`KEC0031`). When inheritance ships it resolves as: walk the parent chain to its root, then apply each
descendant's field set over its parent's, field by field, with a descendant's presence of a field winning and
its absence inheriting. The chain is capped at 8 (`KEC0032`), the parent must be the same content type
(`KEC0033`), the parent must be live at the version being published (`KEC0034`), and a cycle is refused
(`KEC0035`). All five findings exist in the validator from phase 1 and all five are unreachable until the
resolver ships, which is the cheapest way to make sure the row model really does allow it.

### 3.9 Retirement

A definition that leaves play is RETIRED, which is a flag on the row plus a remap rule, and the row stays in
the pack forever so a stored stack still decodes (contracts 5.1). Grimhollow already does this by hand: 18 of
its 35 item ids are retired and the comment states the rule (`b-grimhollow.md:30-36` citing
`GrimhollowItems.cs:133-151`).

A retired row keeps its chunk slot and its bytes. It gains one bit in the chunk row header (section 7.3), so
a runtime reader can answer `IsRetired(id)` without decoding the row. The retire POLICY is carried by the
remap rule, not by the row, and contracts 8.2 fixes the two policies: `0x01` placeholder and `0x02`
replacement. A retire is IRREVERSIBLE for pages already migrated past it (contracts 8.6), which section 12.4
spends in full.

The engine does NOT make the tradable decision for a retired item. Grimhollow's `items.jsonc` rule that
retired items are left out and answer untradable (item-drop design section 1) becomes, in engine terms, the
retired row keeping whatever `tradable` value it last had, plus the placeholder presentation of contracts
10.2 making it undroppable and untradeable anyway. So the observable behaviour is identical and it comes from
one mechanism rather than two.

### 3.10 The boundary with world data

World geometry and placed objects stay in the tile world document and are NOT content (#882 body, item 11).
The boundary is a REFERENCE BY KEY, validated at boot.

The world side is two surfaces, both already string keyed and both already carrying free-form tags:

- `TileObjectArchetype.Id` is a catalog-unique string and `TileObjectArchetype.Tags` is a `List<string>?` of
  free-form authoring tags (`KhaozEngine.TileWorld/TileWorldCatalogs.cs:55-79`, the only free-form tag surface
  in the tree, `a-engine.md:328-343`).
- `TileMarker.Name` is a document-unique string and `TileMarker.Tags` is the same `List<string>?`
  (`KhaozEngine.TileWorld/TileObject.cs:32-46`).

**The rule.** A world archetype or marker tag that names content does so with a content KEY, resolved once at
boot against the loaded pack. Concretely: Grimhollow places gathering nodes in the world and
`SkillingConfig` already requires every world-placeable node key to have a config entry
(`SkillingConfig.cs:393-399`, "the world places nodes that read it", `b-grimhollow.md:378-380`). After
adoption that check reads the `gathering_node` content type instead of the jsonc, and it stays a boot-time
fail-closed check.

Three things follow, and they are stated so neither this spec nor a consumer drifts:

1. **The world document never carries a content ID.** Ids are allocated by the authoring store and the world
   document is edited by a different tool on a different schedule. A world file naming id 17 would break the
   moment a content database was rebuilt from a bundle. Keys are immutable once published (contracts 5.3), so
   a key is the only stable thing to write into a world file.
2. **The resolution is one way.** Content never references a world object, a marker or a coordinate. A drop
   table names an item, not a tile. This keeps `TileWorldHash` and the content manifest hash independent, so a
   world edit does not invalidate a content pack and a content publish does not invalidate a cached world.
3. **The check is at boot and it fails closed.** An unresolved world-to-content key is a boot failure with the
   offending key and the world source named, matching the fail-closed rule of contracts 10.5 and
   `TileWorldCatalogs.LoadJson`'s own behaviour of throwing a `TileWorldException` naming the source
   (`a-engine.md:315-320`).

## 4. Authoring store and providers

### 4.1 The provider pattern this copies

The engine has one provider pattern and it is verified across two pairs, `Commerce` and `WorldStore`
(`a-engine.md:700-788`). The content authoring store copies it in every particular:

- A provider package is `KhaozEngine.<Area>.<Backend>`, references the core project plus its ADO.NET package,
  says OPT-IN in its `<Description>`, is in no umbrella, and ships its own `README.md` as
  `PackageReadmeFile`.
- Raw parameterized ADO.NET, no EF and no ORM. SQLite uses `$name` parameters, SQL Server uses `@name`.
- Key columns are pinned to a BINARY collation, `COLLATE Latin1_General_100_BIN2` on SQL Server and
  `TEXT COLLATE BINARY` on SQLite. This is contracts 5.3's requirement and it is a non-obvious trap: a
  case-insensitive database default silently merged two accounts once, which is why
  `SqlServerWalletStore.cs:32-42` carries a ten-line comment about it.
- Tests are an abstract contract class with one concrete subclass per backend (section 2.6).

**The schema is the JOURNAL's style, not the wallet's.** The wallet's schema is a single inline `Bootstrap`
const with `CREATE TABLE IF NOT EXISTS` and no version at all (`SqliteWalletStore.cs:20-32`). The journal's has
a `CurrentVersion`, a named `RequiredMigration`, an `AutoCreate` and `ValidateOnly` mode, a
`journal_metadata.schema_version` row and validation of every schema object
(`KhaozEngine.WorldStore.Sqlite/SqliteJournalSchema.cs:17-18, 159-205`). The engine survey's own conclusion is
that a content authoring store should follow the journal style because it needs migrations
(`a-engine.md:741-748`), and this spec agrees: the content schema will gain tables as Scope B's types land and
as inheritance ships, so it needs a migration path from the first release.

**SQLite sits on `SqliteStoreConnection` and this is not optional.** One held connection, one
`SemaphoreSlim(1,1)` gate, and a dispose that calls `SqliteConnection.ClearPool(connection)` BEFORE
`connection.Dispose()` (`KhaozEngine.Sqlite/SqliteStoreConnection.cs:76-82`). The type doc says why it exists:
the same pool-clearing line was copied wrong three times over. Every command runs under a lease from
`EnterAsync`, and a transaction takes the lease FIRST (`SqliteStoreConnection.cs:62-73`).

There is no equivalent shared SQL Server connection type, and this spec does not add one. The SQL Server
provider opens its own pooled `SqlConnection` per call, exactly as `SqlServerWalletStore.cs:47` does, and
relies on an `IsolationLevel.Serializable` transaction for the publish rather than an in-process semaphore.

### 4.2 The schema version and its modes

```csharp
internal const int CurrentVersion = 1;
internal const string RequiredMigration = "catalog-v1-initial";
```

`ContentAuthoringSchemaMode` is `AutoCreate` or `ValidateOnly`, the journal's enum
(`SqliteJournalSchema.cs:9-13`). `AutoCreate` creates the schema when the database is empty and then
validates. `ValidateOnly` refuses an empty or mismatched database rather than creating anything, which is what
a production host sets so a typo in a connection string cannot silently create a second empty catalog.

Validation compares the actual schema objects against the expected DDL text after normalization, which is what
`SqliteJournalSchema.ReadSchemaObjects` does with a `sqlite_master` query
(`SqliteJournalSchema.cs:209-225`). A mismatch throws `ContentAuthoringException` naming the object and the
required migration.

### 4.3 The tables, and what each is for

| Table | Rows | Why it exists |
|---|---|---|
| `catalog_metadata` | exactly 1 | Schema version, store epoch, the ACTIVE version pointer, the operator's PIN. |
| `catalog_type` | one per registered type ever seen | Pins a type id to its key so a rename or a reassignment is refused. |
| `catalog_version` | one per published version | Version number, both manifest hashes, minimum builds, format generation, publisher, note, published-at. |
| `catalog_row` | one per row VERSION | The temporal row of section 3.7. |
| `catalog_row_field` | one per field per row version | The field values, so an audit and a diff are field level without decoding a blob. |
| `catalog_family` | one per family | Name, type, declared block size. |
| `catalog_family_block` | one per reserved block | The aligned `[base, base + size)` ranges of contracts 5.2. |
| `catalog_id_high_water` | one per type | The reserve-before-issue high-water mark of contracts 6.2 applied to definition ids. |
| `catalog_draft` | at most 1 | The one open draft. |
| `catalog_draft_edit` | one per pending edit | The change set of section 3.7. |
| `catalog_draft_edit_field` | one per changed field per edit | The changed fields only. |
| `catalog_audit` | one per field change ever | Who, when, note, type, id, field, before, after. |
| `catalog_remap_rule` | one per rule, append only | Contracts 8.1. |
| `catalog_chunk` | one per (version, type, chunk index, side) | The chunk hash, so a republish knows which chunks changed. Two rows when the client and server bytes differ (section 6.7). |

`catalog_row_field` is the decision that makes the audit and the diff cheap, and it is a deliberate departure
from storing an encoded row blob. Ruinborne's audit is the lesson: its rows record THAT a row changed and
carry no values at all, so the entire durable record of an item edit is "someone edited item_def:sword at time
T" (`c-ruinborne.md:399-420`, Ruinborne [#509](https://github.com/APKiwiOrg/Ruinborne/issues/509)). With one
row per field, the before and after values fall out of the same table the editor writes.

The encoded row blob is NOT stored. It is computed at publish from `catalog_row_field` through the type's
codec, and the only thing persisted about it is the chunk hash in `catalog_chunk`. Storing both would give two
sources of truth for one row and no mechanism to keep them equal.

### 4.4 The SQLite DDL, in full

A C# raw string const in `KhaozEngine.Catalog.Sqlite/SqliteCatalogSchema.cs`, handed to
`SqliteStoreConnection`'s constructor, following `SqliteJournalSchema.Tables`
(`SqliteJournalSchema.cs:20-113`). Every key column is `TEXT COLLATE BINARY`, every size cap is a `CHECK`, and
every foreign key is declared because `PRAGMA foreign_keys = ON` is set in the bootstrap
(`SqliteJournalSchema.BootstrapSql`, `SqliteJournalSchema.cs:154-158`).

```sql
CREATE TABLE IF NOT EXISTS catalog_metadata (
    metadata_key INTEGER NOT NULL PRIMARY KEY CHECK (metadata_key = 1),
    schema_version INTEGER NOT NULL CHECK (schema_version >= 1),
    store_epoch TEXT COLLATE BINARY NOT NULL CHECK (length(store_epoch) IN (32, 36)),
    active_version INTEGER NOT NULL DEFAULT 0 CHECK (active_version >= 0),
    pinned_version INTEGER NULL CHECK (pinned_version IS NULL OR pinned_version >= 1),
    updated_at_utc INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS catalog_type (
    type_id INTEGER NOT NULL PRIMARY KEY CHECK (type_id BETWEEN 1 AND 65535),
    type_key TEXT COLLATE BINARY NOT NULL CHECK (length(type_key) BETWEEN 1 AND 64),
    chunk_slots INTEGER NOT NULL CHECK (chunk_slots BETWEEN 256 AND 65536),
    default_visibility INTEGER NOT NULL CHECK (default_visibility IN (0, 1)),
    max_definition_id INTEGER NULL CHECK (max_definition_id IS NULL OR max_definition_id >= 1),
    first_seen_version INTEGER NOT NULL CHECK (first_seen_version >= 0));
CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_type_key ON catalog_type(type_key);

CREATE TABLE IF NOT EXISTS catalog_version (
    version_number INTEGER NOT NULL PRIMARY KEY CHECK (version_number >= 1),
    server_manifest_hash TEXT COLLATE BINARY NOT NULL CHECK (length(server_manifest_hash) = 64),
    client_manifest_hash TEXT COLLATE BINARY NOT NULL CHECK (length(client_manifest_hash) = 64),
    minimum_server_build INTEGER NOT NULL CHECK (minimum_server_build >= 0),
    minimum_client_build INTEGER NOT NULL CHECK (minimum_client_build >= 0),
    format_generation INTEGER NOT NULL CHECK (format_generation >= 1),
    base_version INTEGER NOT NULL CHECK (base_version >= 0),
    published_by TEXT COLLATE BINARY NOT NULL CHECK (length(published_by) BETWEEN 1 AND 128),
    note TEXT COLLATE BINARY NOT NULL CHECK (length(note) <= 1024),
    published_at_utc INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS catalog_row (
    type_id INTEGER NOT NULL,
    definition_id INTEGER NOT NULL CHECK (definition_id >= 1),
    valid_from_version INTEGER NOT NULL CHECK (valid_from_version >= 1),
    replaced_in_version INTEGER NULL CHECK (replaced_in_version IS NULL
        OR replaced_in_version > valid_from_version),
    content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
    parent_id INTEGER NOT NULL DEFAULT 0 CHECK (parent_id >= 0),
    family_id INTEGER NULL,
    retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
    PRIMARY KEY (type_id, definition_id, valid_from_version),
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
    FOREIGN KEY (valid_from_version) REFERENCES catalog_version(version_number),
    FOREIGN KEY (family_id) REFERENCES catalog_family(family_id));
CREATE INDEX IF NOT EXISTS ix_catalog_row_live ON catalog_row(type_id, replaced_in_version, definition_id);
CREATE INDEX IF NOT EXISTS ix_catalog_row_key ON catalog_row(type_id, content_key, valid_from_version);
CREATE INDEX IF NOT EXISTS ix_catalog_row_changed ON catalog_row(valid_from_version, type_id, definition_id);

CREATE TABLE IF NOT EXISTS catalog_row_field (
    type_id INTEGER NOT NULL,
    definition_id INTEGER NOT NULL,
    valid_from_version INTEGER NOT NULL,
    field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
    field_kind INTEGER NOT NULL CHECK (field_kind BETWEEN 0 AND 6),
    int_value INTEGER NULL,
    text_value TEXT COLLATE BINARY NULL CHECK (text_value IS NULL OR length(text_value) <= 192),
    blob_value BLOB NULL CHECK (blob_value IS NULL OR length(blob_value) <= 4096),
    PRIMARY KEY (type_id, definition_id, valid_from_version, field_name),
    FOREIGN KEY (type_id, definition_id, valid_from_version)
        REFERENCES catalog_row(type_id, definition_id, valid_from_version));
```

`catalog_row_field` stores exactly one of the three value columns per row, chosen by `field_kind`: `int`,
`ScaledInt`, `Bool` and `KeyReference` use `int_value`, and `TagList` and `OpaqueBytes` use `blob_value`.
**`LocalizedTextKey` stores nothing and writes no `catalog_row_field` row at all**, because it is a derived
marker (section 3.2) whose key is a function of the row it sits on. So `text_value` has no user among the
current value kinds, and the column stays regardless: it is where CCR-1's `AssetReference` lands if contracts
4.7 takes it, being constrained text rather than opaque bytes.

A tag list is stored as a `blob_value` of varint tag ids in AUTHORED
ORDER, which is what contracts 4.6 requires and what a column of joined text would lose. The 4,096 byte cap on
`blob_value` is a guard rail, not a budget: a 128-byte asset reference and a 60-tag list are both far under
it.

```sql
CREATE TABLE IF NOT EXISTS catalog_family (
    family_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    type_id INTEGER NOT NULL,
    family_key TEXT COLLATE BINARY NOT NULL CHECK (length(family_key) BETWEEN 1 AND 64),
    block_size INTEGER NOT NULL CHECK (block_size BETWEEN 16 AND 65536),
    retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
    created_in_version INTEGER NOT NULL CHECK (created_in_version >= 1),
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_family_key ON catalog_family(type_id, family_key);

CREATE TABLE IF NOT EXISTS catalog_family_block (
    family_id INTEGER NOT NULL,
    block_ordinal INTEGER NOT NULL CHECK (block_ordinal >= 0),
    base_id INTEGER NOT NULL CHECK (base_id >= 1),
    block_size INTEGER NOT NULL CHECK (block_size BETWEEN 16 AND 65536),
    next_free_id INTEGER NOT NULL CHECK (next_free_id >= base_id),
    reserved_in_version INTEGER NOT NULL CHECK (reserved_in_version >= 1),
    PRIMARY KEY (family_id, block_ordinal),
    FOREIGN KEY (family_id) REFERENCES catalog_family(family_id),
    CHECK (base_id % block_size = 0),
    CHECK (next_free_id <= base_id + block_size));

CREATE TABLE IF NOT EXISTS catalog_id_high_water (
    type_id INTEGER NOT NULL PRIMARY KEY,
    reserved_through INTEGER NOT NULL CHECK (reserved_through >= 0),
    issued_through INTEGER NOT NULL CHECK (issued_through >= 0 AND issued_through <= reserved_through),
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));

CREATE TABLE IF NOT EXISTS catalog_draft (
    draft_key INTEGER NOT NULL PRIMARY KEY CHECK (draft_key = 1),
    base_version INTEGER NOT NULL CHECK (base_version >= 0),
    opened_by TEXT COLLATE BINARY NOT NULL CHECK (length(opened_by) BETWEEN 1 AND 128),
    opened_at_utc INTEGER NOT NULL,
    note TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(note) <= 1024));

CREATE TABLE IF NOT EXISTS catalog_draft_edit (
    edit_ordinal INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    type_id INTEGER NOT NULL,
    definition_id INTEGER NOT NULL DEFAULT 0 CHECK (definition_id >= 0),
    content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
    operation INTEGER NOT NULL CHECK (operation IN (1, 2, 3, 4)),
    retire_policy INTEGER NOT NULL DEFAULT 0 CHECK (retire_policy IN (0, 1, 2)),
    replacement_id INTEGER NOT NULL DEFAULT 0 CHECK (replacement_id >= 0),
    fork_key TEXT COLLATE BINARY NULL CHECK (fork_key IS NULL OR length(fork_key) BETWEEN 1 AND 64),
    fork_flag_field TEXT COLLATE BINARY NULL CHECK (fork_flag_field IS NULL OR length(fork_flag_field) BETWEEN 1 AND 64),
    family_id INTEGER NULL,
    edited_by TEXT COLLATE BINARY NOT NULL CHECK (length(edited_by) BETWEEN 1 AND 128),
    edited_at_utc INTEGER NOT NULL,
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_draft_edit_target
    ON catalog_draft_edit(type_id, definition_id, content_key);

CREATE TABLE IF NOT EXISTS catalog_draft_edit_field (
    edit_ordinal INTEGER NOT NULL,
    field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
    field_kind INTEGER NOT NULL CHECK (field_kind BETWEEN 0 AND 6),
    int_value INTEGER NULL,
    text_value TEXT COLLATE BINARY NULL CHECK (text_value IS NULL OR length(text_value) <= 192),
    blob_value BLOB NULL CHECK (blob_value IS NULL OR length(blob_value) <= 4096),
    PRIMARY KEY (edit_ordinal, field_name),
    FOREIGN KEY (edit_ordinal) REFERENCES catalog_draft_edit(edit_ordinal) ON DELETE CASCADE);
```

The unique index on `(type_id, definition_id, content_key)` is what makes an edit IDEMPOTENT per target: a
console that saves the same row twice updates the one edit rather than queueing two. An ORDINARY `Add` carries
`definition_id = 0` because no id exists yet, so its uniqueness comes from the key half of the index, which is
also what refuses two `Add` edits for the same key in one draft. **The one exception is an `Add` written by
`catalog-import`, which MAY carry the bundle's own id** (sections 6.3 and 10.9). The index holds either way,
because the key half is unique across a bundle and the id half is unique per type, and the publish tells the
two apart by the value rather than by who wrote it.

**The collision is per TARGET and not per operation, so a second edit naming an occupied target with a
DIFFERENT operation collides too.** An `Update` on item 13 followed by a `Retire` of item 13 both name
`(2, 13, "stone_sword")`, and the second is refused rather than silently flipping the first edit's operation
and dropping its fields. A draft holds one pending intent per row, and an operator who wants both a price
change and a retire gets them in two publishes, which is also the order the audit will show.

```sql
CREATE TABLE IF NOT EXISTS catalog_audit (
    audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    occurred_at_utc INTEGER NOT NULL,
    actor TEXT COLLATE BINARY NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    operator TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(operator) <= 128),
    action TEXT COLLATE BINARY NOT NULL CHECK (length(action) BETWEEN 1 AND 32),
    type_id INTEGER NOT NULL DEFAULT 0,
    definition_id INTEGER NOT NULL DEFAULT 0,
    content_key TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(content_key) <= 64),
    field_name TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(field_name) <= 64),
    before_value TEXT COLLATE BINARY NULL CHECK (before_value IS NULL OR length(before_value) <= 4096),
    after_value TEXT COLLATE BINARY NULL CHECK (after_value IS NULL OR length(after_value) <= 4096),
    version_number INTEGER NOT NULL DEFAULT 0,
    note TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(note) <= 1024));
CREATE INDEX IF NOT EXISTS ix_catalog_audit_time ON catalog_audit(occurred_at_utc, audit_id);
CREATE INDEX IF NOT EXISTS ix_catalog_audit_target ON catalog_audit(type_id, definition_id, audit_id);

CREATE TABLE IF NOT EXISTS catalog_remap_rule (
    sequence INTEGER NOT NULL PRIMARY KEY CHECK (sequence >= 1),
    introduced_in INTEGER NOT NULL CHECK (introduced_in >= 1),
    type_id INTEGER NOT NULL,
    kind INTEGER NOT NULL CHECK (kind BETWEEN 1 AND 255),
    from_id INTEGER NOT NULL CHECK (from_id >= 1),
    to_id INTEGER NOT NULL DEFAULT 0 CHECK (to_id >= 0),
    payload BLOB NOT NULL DEFAULT x'' CHECK (length(payload) <= 64),
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
    FOREIGN KEY (introduced_in) REFERENCES catalog_version(version_number));

CREATE TABLE IF NOT EXISTS catalog_chunk (
    version_number INTEGER NOT NULL,
    type_id INTEGER NOT NULL,
    chunk_index INTEGER NOT NULL CHECK (chunk_index >= 0),
    chunk_hash TEXT COLLATE BINARY NOT NULL CHECK (length(chunk_hash) = 64),
    row_count INTEGER NOT NULL CHECK (row_count >= 0),
    uncompressed_bytes INTEGER NOT NULL CHECK (uncompressed_bytes >= 0),
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0),
    visibility INTEGER NOT NULL CHECK (visibility IN (0, 1)),
    PRIMARY KEY (version_number, type_id, chunk_index, visibility),
    FOREIGN KEY (version_number) REFERENCES catalog_version(version_number),
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
CREATE INDEX IF NOT EXISTS ix_catalog_chunk_hash ON catalog_chunk(chunk_hash);

INSERT OR IGNORE INTO catalog_metadata(metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc)
VALUES (1, 1, lower(hex(randomblob(16))), 0, NULL, CAST(strftime('%s', 'now') AS INTEGER) * 1000);
```

**`catalog_metadata` carries three facts and each one has a job.** `active_version` is what the last publish
committed. `pinned_version` is the operator's HOLD, written by `catalog-pin` (section 10.7) and read at boot
by a server configured to take its version from the store, and it is nullable because no pin is the ordinary
state. It carries no foreign key to `catalog_version`, deliberately: `catalog_metadata` is seeded before any
version exists, and the existence check belongs where the operator can be told about it, which is
`catalog-pin` answering 400 for a version that does not exist. `store_epoch` is the IDENTITY OF THIS
DATABASE, minted once at schema creation, and its job is to make "version 12" answerable: a bundle import into
an empty database restarts the version line at 1 (section 10.9), so two databases can hold a version 12 that
share no history, and the epoch is what tells them apart in a `catalog-versions` response, in a bundle and in
an operator's log. It is the same instrument the journal uses for its projection cursors
(`RotateStoreEpochAsync`), pointed at a different question.

**There is no `sealed_flag`, and there was.** A published version is immutable from the moment its
transaction commits, because every table this spec writes at publish is append only or temporal, so a flag
saying "this one is sealed" would be a second representation of a fact the schema already guarantees, with the
usual consequence that it can disagree. Holding a version back from a restart is the PIN, which is one column
on `catalog_metadata` and an operator action rather than a property of the version row.

**A remap rule has no delete path and the schema says so.** Rules are append only (contracts 8.1), so there is
no `UPDATE` and no `DELETE` statement for `catalog_remap_rule` anywhere in either provider. The journal takes
the stronger position for its own operations, a `BEFORE DELETE` trigger raising an abort unless a guarded
maintenance hook is set (`SqliteJournalSchema.cs:70-76`). This spec does NOT copy the trigger, because the
journal's guard exists to permit a retention sweep and there is no retention sweep here: the rule list is
small (contracts 8.4 computes ten thousand rules at about 110 KB) and grows only on a retire. If a later phase
adds any maintenance path that touches rules, it adds the trigger with it.

**`catalog_chunk` has a hash index and no unique constraint on the hash.** Two versions naming the same chunk
hash is the NORMAL case and is the entire point: a chunk that did not change between two versions has the same
hash and a client already holding it fetches nothing (contracts 7.3). The index is what makes "does this
version share a chunk with the last one" one lookup at publish time.

**`visibility` is in `catalog_chunk`'s primary key because one id range can be TWO chunks.** The column names
the SIDE the bytes were encoded for, `0` for client and `1` for server, and it is not a copy of
`catalog_type.default_visibility`. A type whose visibility is `ServerOnly` has exactly one row per chunk, at
`visibility = 1`. A type whose visibility is `Client` and whose fields are all `Client` has exactly one row,
at `visibility = 0`, and both manifests name that one hash. A type whose visibility is `Client` and which
carries at least one `ServerOnly` FIELD has TWO rows per chunk with two different hashes, because the client
bytes omit those fields and the server bytes carry them (section 6.7, contracts 11.3). Without the side in the
key, the client hash of that third case has nowhere to live: the partial republish of section 6.10 step 5
copies a chunk's previous hash forward and the client manifest of section 6.8 is built from chunk hashes, so a
single-hash row makes the client manifest unreconstructable the moment any type takes a per-field override.

### 4.5 The SQL Server DDL

An EMBEDDED RESOURCE, `KhaozEngine.Catalog.SqlServer/CatalogSchemaV1.sql`, following
`KhaozEngine.WorldStore.SqlServer/JournalSchemaV1.sql`. The shape is the SQLite text with the type and
constraint idioms swapped, and the differences are mechanical rather than semantic:

| SQLite | SQL Server |
|---|---|
| `TEXT COLLATE BINARY` | `nvarchar(N) COLLATE Latin1_General_100_BIN2` |
| `INTEGER` | `int`, or `bigint` for the audit id and the timestamps |
| `INTEGER PRIMARY KEY AUTOINCREMENT` | `bigint IDENTITY(1,1)` |
| `BLOB` | `varbinary(max)`, with `CHECK (DATALENGTH(x) <= N)` |
| `CAST(strftime(...))` epoch ms | `datetimeoffset(7)` |
| `INSERT OR IGNORE` | `IF NOT EXISTS (...) INSERT` |
| `CHECK (length(x) <= N)` | `CHECK (LEN(x) <= N)` for `nvarchar`, `DATALENGTH` for `varbinary` |
| inline `CHECK` | `CONSTRAINT ck_<table>_<what> CHECK` |

`JournalSchemaV1.sql:10` is the precedent for the collation and `:33` for the `DATALENGTH` cap. Every
constraint is NAMED on SQL Server, because an unnamed constraint gets a generated name and the schema
validator compares names.

The one genuine behavioural difference is the transaction. SQLite serializes IN PROCESS behind the
`SqliteStoreConnection` gate plus an explicit transaction (`SqliteWalletStore.cs:69-70`). SQL Server takes no
in-process semaphore and uses `IsolationLevel.Serializable` (`SqlServerWalletStore.cs:11-13`), which is what
makes two consoles publishing concurrently a deadlock-or-abort rather than a race (section 11, row 4). The
conformance suite asserts the OBSERVABLE behaviour, never the mechanism, so one suite covers both.

### 4.6 The audit, field level, through the schema

Every write path in section 10 appends audit rows, and the unit of an audit row is ONE FIELD, not one row and
not one request. An `Update` that changes three fields writes three audit rows sharing an `occurred_at_utc`,
an `actor`, an `operator` and a `note`.

| Column | Filled with |
|---|---|
| `actor` | The engine's own actor constant for the admin endpoint, matching Grimhollow's `AdminActors.AdminEndpoint` (`b-grimhollow.md:949-951`). |
| `operator` | The identity the console FORWARDED, section 10.10. Empty when it forwarded none. |
| `action` | `draft-edit`, `draft-discard`, `publish`, `pin`, `rollback`, `bulk-import`, `family-create`. |
| `field_name` | The schema field name. Empty for a row-level action such as a retire. |
| `before_value`, `after_value` | The value rendered through the field's kind, invariant culture, null for absent. |
| `version_number` | 0 for a draft edit, the published number for a publish. |

`before_value` and `after_value` are TEXT even for an int field, deliberately. An audit row is read by a human
and joined by nothing, and rendering at write time means a later schema change cannot make an old audit row
un-renderable. A tag list renders as its comma-joined tag KEYS resolved at write time, because a reader of an
audit row six months later should not have to resolve ids against a version that may have retired them.

**The audit append is IN the same transaction as the edit, not best effort.** Ruinborne's is best effort: it
catches and logs a warning so an audit failure never blocks the edit (`c-ruinborne.md:415-417`), and in its
no-SQL developer mode there is no audit at all. Both are the wrong default for content, because a content edit
with no audit row is indistinguishable from no edit, and the whole reason this store exists is that the owner
authors values a player's economy depends on. If the audit insert fails, the edit fails.

**Which is exactly why `before_value` and `after_value` are capped at 4,096 and not lower.** The audit insert
sharing the edit's transaction means a value the COLUMN refuses is an EDIT the database refuses, arriving as a
SQL constraint error rather than as a finding. At the 512 these columns first carried, a 60-tag list of
64-character keys renders to about 3,900 characters and every edit touching it fails with a message about an
audit column. Four thousand and ninety-six matches `blob_value`'s own cap, so the ordinary case is bounded by
the same number on both sides of the transaction.

**A rendering that STILL would not fit is abbreviated, visibly.** Two kinds can exceed 4,096 characters while
their field stays inside its cap: a tag list, whose `blob_value` of varint ids renders to keys many times
longer than the ids, and `opaque bytes`, whose 4,096 bytes render to 8,192 characters of lower hex. The rule
is the same for both. Write as much of the rendering as fits inside the cap minus the marker, then
`+<n> more` for a tag list or `+<n> bytes` for a blob, so a reader can never take an abbreviated value for a
complete one. Nothing is lost by it: the audit is the record of who changed what and when, and the record of
the VALUE is the temporal row table, which holds every version of every field and is read through
`catalog-get`'s history (section 10.4). Truncating SILENTLY is the option this rules out, because a partial
value that reads as whole is Ruinborne
[#509](https://github.com/APKiwiOrg/Ruinborne/issues/509)'s defect wearing a different hat.

### 4.7 The id allocator

Contracts 5.1 puts allocation in the authoring store and contracts 6.2's reserve-before-issue ORDER is the
contract this copies: **a range is RESERVED durably BEFORE any id in it is issued**, so the worst a crash can
do is skip a block of ids that were never issued, and it can never reissue one.

`catalog_id_high_water` carries two numbers per type:

- `reserved_through` is the highest id the store has DURABLY promised not to hand out twice.
- `issued_through` is the highest id actually stamped onto a row.

`AllocateAsync(typeId, count)`:

1. If `issued_through + count <= reserved_through`, hand out `[issued_through + 1, issued_through + count]`,
   set `issued_through` to the top, and return. No reservation needed.
2. Otherwise compute `newReserved = issued_through + max(count, ReserveBatch)` where `ReserveBatch` is 1,024,
   write `reserved_through = newReserved` and COMMIT that write on its own, then go to step 1.

The separate commit in step 2 is the whole rule. Persisting the reservation after issuing leaves a window in
which a crash hands the next boot an id it has already put on a row, and a duplicate definition id is the one
failure this section exists to prevent. `NetIdAllocator` persists its packed high-water mark for exactly this
reason (`KhaozEngine.Replication/NetIdAllocator.cs:14-60`, `a-engine.md:1158-1175`).

A crash between the two commits skips up to 1,024 ids. That is free: ids are 31 bits of positive `int` space
per type against an owner figure of 50,000 definitions and a stress figure of 1,000,000 (contracts 1.3 item
2), so a server could crash mid-allocation two million times before the gap mattered.

**Family allocation is the same rule on a narrower range.** `AllocateInFamilyAsync(familyId)` takes the
family's blocks in ordinal order, finds the first whose `next_free_id < base_id + block_size`, issues that id
and advances `next_free_id`. When every block is full it reserves a NEW block: it takes the type's
`reserved_through`, rounds UP to the family's declared `block_size` alignment, reserves through the top of the
new block by the step-2 rule above, **advances `issued_through` to that same block top**, inserts the
`catalog_family_block` row, and only then issues. The rounding up is what wastes ids and what makes
`(id & ~(size - 1)) == base` a legal membership test (contracts 5.2), and the waste is bounded by the block
size plus one alignment gap.

**Advancing `issued_through` past the block top is what keeps the plain counter out of the block, and it is
why step 1 needs no knowledge of families at all.** Without it, reserving a block raises only
`reserved_through`, and step 1's single `<= reserved_through` guard then walks the plain counter through the
gap under the block and into it. A fresh `item` type allocates 10 plain ids and sits at
`issued_through = 10, reserved_through = 1024`. A `sword` family with `block_size = 16` rounds 1024 up to
1024, takes the block `[1024, 1040)` and issues 1024 to its first member. Plain adds keep climbing, 11, 12,
and so on to 1023, and the next one hands out 1024 a second time, to a row that is not in the family. At the
same publish the `catalog_row` primary key `(type_id, definition_id, valid_from_version)` refuses the second
insert loudly. At a LATER version it inserts cleanly, and the type now has two live rows claiming id 1024
with whichever the encoder writes last winning the chunk.

The alternative was to teach step 1 to read `catalog_family_block` and skip past any block its next id would
enter. This spec advances the counter instead, for three reasons: step 1 stays one comparison against one
durable number, the reserve-before-issue guarantee that number already carries is exactly the guarantee this
needs, and a skip has to define what a multi-id range straddling a block means where an advance does not. The
cost is ids, which this section already spends 1,024 at a time without noticing.

**The validator checks the outcome anyway, because a counter is a mechanism and the invariant is a property.**
`KEC0036` refuses two live rows of one type sharing a `definition_id`, and `KEC0037` refuses a non-family row
whose id lies inside any block of any family of its type. Both are pass 1 (section 5.3) and both are linear
walks over the candidate, so either one fires at the publish that would write the duplicate rather than at the
boot that decodes it.

An id block exhausted MID-PUBLISH cannot happen, because allocation runs as step 2 of the publish (section
6.3) before anything is written, and a failure there aborts the publish with nothing changed. Section 11 row 9
spends this case.

**`maxDefinitionId` is a per-type CEILING and `AllocateAsync` refuses above it.** A type may declare one at
registration (3.6). When it does, both allocation branches refuse to issue an id above it: step 1 fails rather
than handing out a range that crosses the ceiling, step 2 reserves no further, and
`AllocateInFamilyAsync` refuses a new block whose top would exceed it. The failure is a
`ContentAuthoringException` naming the type, the ceiling and the current high-water mark, and it aborts the
publish with nothing written, which is the same shape as every other allocation failure.

**Why a ceiling exists at all: some ids are a FORMAT constraint rather than a preference.** Scope B's payload
kind 130 is `[RarityId: byte]`, pinned by the contracts' own worked example, so `rarity_rule` ids are capped
at 255 forever and Scope B declares `maxDefinitionId: 255` at registration. Without the ceiling the 256th
rarity rule ever created would publish cleanly, and every payload written with it would truncate silently to
a byte and land on an existing rarity. That is silent damage to durable player data, produced by an
authoring action that reported success, which is precisely the class of failure the reserved ranges and the
fail-closed boot exist to prevent everywhere else.

**Retired rows count toward the ceiling, and that is deliberate.** Ids are never reused (contracts 5.1), so a
retired rarity rule keeps its id and the space it occupies. A ceiling that excluded retired rows would be a
ceiling on LIVE rows, which is not what the format constrains: the format constrains the NUMBER, and a number
freed by a retire and reissued would resolve stored payloads onto the wrong row. So a type that retires
aggressively burns its ceiling, and the answer is to declare the ceiling honestly and watch it rather than to
recycle ids. `catalog-list` reports the remaining headroom for any type that declares one, so the wall is
visible well before it is hit.

**The validator checks it too, with `KEC0042`**, because the allocator is a mechanism and the invariant is a
property of the candidate. An id above the ceiling can also arrive through the carried-id path of 6.3, which
is the empty-database import, and that path does not go through the allocator at all.

### 4.8 One transaction per publish

The entire publish COMMIT is one database transaction (section 6.10). Inside it: the `catalog_version` row,
every `catalog_row` close and insert, every `catalog_row_field` insert, every `catalog_remap_rule` append,
every `catalog_chunk` row, the `catalog_draft` and `catalog_draft_edit` deletion, the audit rows, and the
`catalog_metadata.active_version` update.

Outside it, and BEFORE it: every chunk file write and every manifest file write (section 6.9). That ordering
is the crash-safety property and it is the map document's, which writes changed tiles "at names nothing points
at yet" and then commits with one manifest rename
(`KhaozEngine.MapDoc/MapTiledFile.Save.cs:81-102`). A chunk file is named by its own hash, so writing one is
idempotent and writing one nothing references yet is inert. The database transaction is the only commit point
and it is atomic by construction.

### 4.9 What a second world would add

The owner has one world now and separate worlds later (#882 comment 2). Stated once so nobody designs around
it: a second world adds a `world_key` column to `catalog_metadata`'s active pointer and nothing else. Versions,
rows, chunks, packs and manifests are all world independent, because content is what a thing IS and a world is
where it sits. Two worlds on different active versions is two rows in a renamed `catalog_active_version` table
keyed by world, and every other table is untouched.

## 5. Validation

### 5.1 The one validator

Contracts 10.4 fixes the shape and this spec fills it in:

```csharp
public static ContentValidationReport Validate(
    ContentSnapshot candidate,
    ContentSnapshot? previous,
    IReadOnlyList<RemapRule> rules,
    ContentTypeRegistry registry);
```

- INPUT is a complete candidate snapshot, the PREVIOUS published snapshot or null, and the full ordered rule
  set. Nothing is read from a database, a file or an ambient static inside it.
- OUTPUT is `(bool IsValid, IReadOnlyList<ContentFinding> Findings)`, accumulating rather than stopping at the
  first, following `JsonSchemaValidator.ValidationReport` and its run-to-the-end sweep
  (`KhaozEngine.Content/JsonSchemaValidator.cs:11-101`, `a-engine.md:275-292`).
- NO SIDE EFFECTS. It does not log, does not mutate the candidate, does not touch a counter and does not throw
  for content reasons. A throw from it is a bug in the validator.

**`previous` is the second argument and it is still a pure function.** Three required checks are statements
about a CHANGE rather than about a snapshot, and a change needs two snapshots: a key moving on a published row
(`KEC0003`), a type id or type key moving after its first publish (`KEC0029`), and Scope B's rule that tier
ordinals within one mod are unique and UNCHANGED since the previous published version. None of them can be
evaluated from a candidate alone. Handing the previous snapshot in as an argument is what keeps the function
pure: the alternative, reading the previous version from the store inside the validator, is what contracts
10.4 exists to forbid, and it would make the three callers stop sharing one function.

**Those checks are PUBLISH ONLY, and that is a property of the input rather than a mode flag.** `previous` is
null at boot, because a loaded pack is not a change to anything and the version it replaces is gone. It is
null in a test, unless the test is specifically exercising a change. It is populated at publish, from the
version the draft is based on. Every check that needs it is written to be SKIPPED when it is null, and the
report says so: a validation run with `previous` null carries a `KEC0000` informational finding naming the
checks that did not run, so a clean report from a boot is never mistaken for a clean report from a publish.
`KEC0000` is the one code that does not set `IsValid` to false, which is stated here and in 5.2 because a
finding that is not a failure is the kind of exception a reader has to be told about twice.
Section 5.3 marks the publish-only checks in the sweep, and 5.4 states which of the three callers passes
what.

Because it is pure and takes its whole world as an argument, a test builds a snapshot in memory and asserts on
findings, publish runs it before writing anything, and boot runs it against the loaded pack. Ruinborne's
catalog loader is the shape this avoids: validation lives INSIDE the SQL read delegate, so an
`ArgumentException` from a bad row is routed to the connectivity failure handler and reported as
`Item catalog: SQL read failed`, which sends an operator looking at the network
(`c-ruinborne.md:176-196`, Ruinborne [#325](https://github.com/APKiwiOrg/Ruinborne/issues/325)).

### 5.2 The findings

A finding is `(ContentTypeId Type, int Id, string Code, string Message)`. The CODE is a stable token so a
counter, a test and an operator runbook can all key on it, which is the same rule contracts 9.7 applies to
decode reasons. Codes are never reused and never renumbered, which is why a code this spec withdrew is
withdrawn rather than recycled.

**Forty-one codes are issued, across the number range `KEC0001` to `KEC0042`.** `KEC0013` is withdrawn and
carries no check, and `KEC0032` to `KEC0035` are four codes sharing one row below. `KEC0000` is not a finding
about content and is listed first and separately. Section 15.7 builds one test per issued code.

**The number range is banded, and the bands are as durable as the codes.** `KEC0001` to `KEC0099` are the
engine's own, of which 1 to 42 are issued and 43 to 99 are free for this spec's successors. `KEC0100` to
`KEC0199` are RESERVED for Scope B, so an affix rule and a family block rule are told apart by an operator
reading a code rather than by reading a message. `KEC0200` and above are unallocated and no game takes one: a
game validator's findings come back as `KEC0040` prefixed with the game's type key, which is section 5.3's
rule and does not change.

| Code | Check | Contract |
|---|---|---|
| `KEC0000` | INFORMATIONAL, never a failure. `previous` was null, so the publish-only checks did not run. The message names them. | 10.4, 5.1 here |
| `KEC0001` | A key is not well formed: not `a-z0-9_`, leading digit, leading or trailing underscore, double underscore, over 64 characters. | 5.3 |
| `KEC0002` | A key is not unique within its type. | 5.3 |
| `KEC0003` | A key changed on a row that is already published. | 5.3 |
| `KEC0004` | A row carries a field the type's schema does not declare. | 4.7 |
| `KEC0005` | A live row is missing a field its schema marks required. | 4.7, 10.4 |
| `KEC0006` | A key reference names a row that is not live at this version. | 10.4 |
| `KEC0007` | A key reference names a content type that was never registered. | 3.3 here |
| `KEC0008` | A tag list names a tag id that is not a live `tag` row. | 4.6 |
| `KEC0009` | A definition id is 0 or negative. | 5.1 |
| `KEC0010` | A definition id is outside every block of the family it claims. | 5.2 |
| `KEC0011` | A family block is not aligned to its own declared size. | 5.2 |
| `KEC0012` | Two families of one type claim overlapping blocks. | 5.2 |
| `KEC0013` | WITHDRAWN, and never reissued under any meaning. As written it fired on the legal per-field override of contracts 4.7, which is the case section 6.7 is built around. Its real content is `KEC0014`. | 11.3 |
| `KEC0014` | The CLIENT-side encoded bytes of a chunk still carry a field whose visibility is `ServerOnly`. | 11.3, 10.4 |
| `KEC0015` | The remap rule set is not idempotent: a rule's `ToId` is an earlier rule's `FromId` for the same type. | 8.3, 10.4 |
| `KEC0016` | A remap rule's `FromId` names a row that never existed. | 8.1 |
| `KEC0017` | A remap rule's `ToId` names a row that is not live at `IntroducedIn`. | 8.2 |
| `KEC0018` | Remap rule sequences are not contiguous from 1, or are not strictly ascending. | 8.1 |
| `KEC0019` | A remap rule payload is longer than 64 bytes, or is malformed for its kind. | 8.4 |
| `KEC0020` | A stat `scale` is not a power of ten, or is below 1. | 13.1 |
| `KEC0021` | A stat `min` exceeds its `max`. | 13.1 |
| `KEC0022` | A definition declaring durability or sockets is stackable. | 10.4 |
| `KEC0023` | A loot entry does not set exactly one of `item`, `nested_table` and a non-empty `required_tags`. | 3.5 here |
| `KEC0024` | A loot table graph contains a cycle through `nested_table`. | 3.5 here |
| `KEC0025` | `max_stack` is below 1, or is 1 on a stackable row. | 3.3 here |
| `KEC0026` | A row's encoded bytes exceed its TYPE's `maxRowBytes`. | 7.1 here |
| `KEC0027` | A row's codec round trip is not byte identical. | 7.3 here |
| `KEC0028` | A `chunk_slots` value is not a power of two between 256 and 65,536. | 4.5 |
| `KEC0029` | A type id, type key or `chunk_slots` changed after its first publish. | 4.4, 4.5 |
| `KEC0030` | A row's DERIVED localized text key exceeds 192 characters. Three 64-character parts derive 194, so the bound is reachable. | 12.1, 12.2 |
| `KEC0031` | A `parent_id` is non-zero while inheritance is unimplemented. | 3.8 here |
| `KEC0032` to `KEC0035` | The four inheritance checks of section 3.8, unreachable in phase 1. | 3.8 here |
| `KEC0036` | Two live rows of one type carry the same `definition_id`. | 5.1, 4.7 here |
| `KEC0037` | A row that names no family carries an id inside a block of one of its type's families. | 5.2, 4.7 here |
| `KEC0038` | A chunk's canonical uncompressed bytes exceed `MaxChunkUncompressedBytes`. | 7.1 here |
| `KEC0039` | A rollback would restore a row that has been retired. A retire is irreversible. | 8.6 |
| `KEC0040` | A game validator returned a finding. The message is the game's, the code is prefixed with its type key. | 4.4 |
| `KEC0041` | A `Fork` edit's preconditions fail: its source row is absent or already retired, its `forkKey` is taken or malformed, or its `flagField` is absent from the type's schema or is not `Bool`. | 3.7 here, 8.2 |
| `KEC0042` | A definition id exceeds the `maxDefinitionId` its type declared at registration. | 4.3, 3.6 here |

`KEC0022` and `KEC0025` are the two checks that make Ruinborne's stackable defects impossible to publish:
`Stackable = true, MaxStack = 1` is directly reachable in its admin form today
(`c-ruinborne.md:359-363`) and Ruinborne [#279](https://github.com/APKiwiOrg/Ruinborne/issues/279) asks for
the equippable-plus-stackable rule that `KEC0022` generalizes.

`KEC0027` is the check most likely to be skipped and it is the one that pays. Encoding every row and decoding
it back costs one pass over the candidate at publish time, and it is the only thing that can catch a codec
whose encode and decode disagree before the bytes are hashed into a manifest an operator then treats as an
identity.

### 5.3 The sweep order

The sweep runs in five passes over the candidate, in this order, and never stops early:

1. **Structure.** Ids, keys, families, blocks, chunk sizes, type identity. `KEC0001` to `KEC0003`, `KEC0009`
   to `KEC0012`, `KEC0028`, `KEC0029`, `KEC0036`, `KEC0037`.
2. **Schema.** Every field against its type's declared schema, required fields, value ranges, localized key
   shape, the inheritance guard. `KEC0004`, `KEC0005`, `KEC0020`, `KEC0021`, `KEC0025`, `KEC0030` to
   `KEC0035`.
3. **References.** Key references, tag lists, loot graphs. `KEC0006` to `KEC0008`, `KEC0023`, `KEC0024`.
4. **Visibility and codec.** Field visibility against the SIDE a chunk is encoded for, the round trip, the row
   size cap and the chunk size cap. `KEC0014`, `KEC0022`, `KEC0026`, `KEC0027`, `KEC0038`.
5. **Remap rules.** The full ordered set. `KEC0015` to `KEC0019`.

**Three checks in that list are PUBLISH ONLY.** `KEC0003` (a key changed on a published row) and `KEC0029` (a
type id, type key or `chunk_slots` changed after its first publish) are both in pass 1, and both compare the
candidate against `previous`. When `previous` is null they are skipped and `KEC0000` names them. Every other
check in passes 1 to 5 runs on every call, so a boot still sweeps everything a boot can meaningfully sweep.

`KEC0039` is in no pass. It is emitted by `RollbackToAsync` while it builds the draft (section 6.13), before
the validator runs at all, and it carries a finding code because the rollback action returns findings the same
way a publish does.

`KEC0036` and `KEC0037` are in pass 1 because they are the property the id allocator's counter discipline
(section 4.7) is a mechanism for, and pass 1 is already walking every id of every type with the family block
list in hand. `KEC0037` is `(id & ~(size - 1)) == base` against each block of the row's type, which is
contracts 5.2's own membership test read the other way round.

Pass 3 needs pass 1 to have built the live-row index, and pass 5 needs pass 1 to know which ids ever existed.
Nothing else is ordered, and the passes are single threaded because the candidate at 1,000,000 rows is a few
hundred megabytes and the whole sweep is a linear walk (section 14, budget P5).

**Pass 6 is Scope B's, and it is INSIDE the sweep rather than in the game slot.** Scope B's types are engine
code in the engine's own 256 to 1023 band, so their checks are the engine's checks: they run after pass 5 and
BEFORE any game validator, they emit `KEC0100` to `KEC0199` directly, and a throw from one is a bug in the
engine rather than something to swallow. Putting them in the game slot would have done two wrong things at
once. An operator reading `KEC0040` could not tell an engine affix rule from a game rule, and the per-type
catch below, which exists because game validators are untrusted code, would have quietly turned a broken
engine validator into a finding. Pass 6 is skipped entirely when no Scope B type is registered, which is
every boot of a game that does not use instances. Its cost is inside P8 and 14.1 derives it.

Game validators (contracts 4.4, an ADDITIONAL constraint never a relaxation) run LAST, after pass 6, one per
registered type, each handed only its own type's rows plus a read-only lookup into the rest of the candidate.
Their findings come back as `KEC0040` with the game's message. A game validator that throws is caught and
reported as `KEC0040` with the exception message, so one bad game validator cannot take the publish down with
a stack trace instead of a finding.

### 5.4 The three callers

**Publish** (section 6.4) runs it on the candidate BEFORE any id is stamped into a durable row and before any
byte is written, and it is the ONE caller that passes a non-null `previous`: the snapshot of the version the
draft is based on, which the publish already holds because step 2 built the candidate from it. `IsValid ==
false` aborts the publish, nothing is written, the draft is untouched, and the findings come back to the
console as HTTP 400 with the full list. A publish of the FIRST version ever passes null, because there is no
previous, and takes the `KEC0000` informational finding like any other null run.

**Boot** (section 9.5) runs it on the snapshot decoded from the loaded pack, with `previous` null. A pack that fails validation at
boot fails the boot CLOSED, non-zero exit, findings on stderr (contracts 10.5). This is not redundant with the
publish run: the pack may have been written by an older engine build, may have been corrupted in transit, or
may have been produced by a publish whose validator had a bug now fixed. Running it again costs one linear
sweep at process start and is the last gate before a server serves content.

**Tests** build a `ContentSnapshot` in memory with no store, no file and no registry beyond the one they
construct, and assert on the exact finding codes. `previous` is null for almost all of them, and a test for
one of the three publish-only checks builds a SECOND in-memory snapshot and passes it, which costs the test
four more lines and no infrastructure at all. That is the property that makes the validator testable at all,
and it is why the signature takes the registry and the previous snapshot as arguments rather than reading an
ambient one or a store.

### 5.5 What the validator deliberately does NOT check

Named so nobody adds them later without a decision:

- **Whether a value is sensible.** A sword worth 0 coins and a tree with a 100 percent chance are legal. The
  validator enforces the SHAPE of content, and the owner owns the numbers. Grimhollow's AGENTS.md already
  states the corresponding rule for its own numbers, that they are content the owner owns
  (`b-grimhollow.md:1081-1105`).
- **Whether a client has the art.** `MinimumClientBuild` is the publisher's statement about that (contracts
  7.4), and the validator cannot see a client's asset bundle.
- **Whether a localization key resolves.** `IStringCatalog.Get` never throws for a missing key and returns the
  key itself as a visible placeholder (`KhaozEngine.App/IStringCatalog.cs:12-17`), so a missing string is a
  visible defect rather than a publish blocker. Section 15.7 adds a test-only coverage sweep for a game that
  wants the stronger guarantee.
- **Whether a remap rule is a GOOD idea.** `KEC0015` refuses a rule set that is not idempotent and nothing
  refuses a rule that moves every sword onto a stick. That is the owner's call and contracts 8.6 already says
  a retire is irreversible.

## 6. Publish

### 6.1 The whole algorithm, in order

Publish is eleven steps. Steps 1 to 8 write nothing durable. Step 9 writes files nothing references. Step 10
is the one commit. Step 11 is a sweep after the commit.

```
 1. Freeze the draft
 2. Build the candidate
 3. Allocate ids
 4. Validate
 5. Compute the temporal rows
 6. Select affected chunks
 7. Encode and compress the affected chunks, computing each chunk hash
 8. Build both manifests and compute both manifest hashes
 9. Write chunk files and manifest files to the pack store
10. COMMIT: one transaction writing the version row, the rows, the rules, the chunk rows,
    the audit rows, the draft deletion and the active pointer
11. Sweep: prune unreferenced chunk files written by a previous failed attempt
```

Crash safety is stated per step in section 6.11 and the invariant is one sentence: **a crash at any point
leaves either the old version or the new one, never a torn one.** That holds because nothing observable
changes until step 10 and step 10 is a single transaction.

### 6.2 Step 1, freeze the draft

The draft is marked frozen in memory for the duration of the publish and the store takes a row lock on
`catalog_draft`. A draft edit arriving while a publish is in flight is refused with HTTP 409 and
`publish-in-progress`. On SQL Server the lock is the `Serializable` transaction's own. On SQLite it is the
`SqliteStoreConnection` gate, which already serializes every command in the process
(`SqliteStoreConnection.cs:66-73`), plus a `BEGIN IMMEDIATE` so a second process cannot start one.

Two consoles publishing concurrently is section 11 row 4 and it resolves here: the second one either blocks
and then finds the draft empty, or aborts with a serialization failure. Neither produces a torn version.

The new version number is `MAX(version_number) + 1` read INSIDE the transaction at step 10, not here. Reading
it at freeze time and using it at commit time is exactly the race the lock is meant to close, so the number is
taken where the write happens.

### 6.3 Step 3, allocate ids

Every `Add` edit in the frozen change set needs an id, and so does every `Fork`, which allocates one for its
copy. Allocation runs before validation deliberately, because several checks (`KEC0006` reference resolution,
`KEC0010` family membership) need the ids the new rows will carry.

For each `Add`, in edit ordinal order:

1. If the edit already CARRIES an id, meaning its `definition_id` is non-zero, keep it and allocate nothing.
   Only `catalog-import` writes an `Add` that way, and only into an empty database (section 10.9).
2. Otherwise, if the edit names a family, call `AllocateInFamilyAsync(familyId)` (section 4.7).
3. Otherwise call `AllocateAsync(typeId, 1)`.

Both allocating branches go through the reserve-before-issue rule, so the reservation commits on its own
before the id appears anywhere. **An allocation that fails aborts the publish with nothing written**, because
no durable row carries the id yet. A reservation that COMMITTED and then aborted leaves a gap of
reserved-but-unissued ids, which is the safe direction and costs nothing (section 4.7).

**After every `Add` has an id, the high-water marks are SEEDED from the ids that were carried.** For each type
with at least one carried id, `reserved_through` and `issued_through` move to the greater of their current
value and the largest carried id of that type. Without it the first ordinary `Add` after an import allocates
id 1 straight onto an imported row. The step is a no-op for an ordinary publish, because nothing carries an id
there.

**So there is ONE path with two id sources, and which one runs is a property of the EDIT rather than of the
caller.** An `Add` with `definition_id = 0` is allocated one, an `Add` with a non-zero `definition_id` keeps
it, and a bundle may mix the two because the id is per row. Ids that ARE allocated come in edit ordinal order,
so two publishes of the same id-free bundle into two empty databases produce the same ids, which is what makes
an export and re-import reproducible (section 10.9) and what fixes Ruinborne's ids at import (section 17.3).
Ids that are CARRIED are kept exactly, which is what makes Grimhollow's import preserve ids 1 to 35 and leave
every stored container decoding unchanged (section 16.4). Ordering does not preserve an id, carrying it does.

**A `Fork` allocates through branch 3 and never carries an id.** Its copy takes the next free id of the type
and may not name one, because a fork is only ever authored against a live database and the carry path exists
for the empty-database import alone. It also may not name a family: the copy inherits the SOURCE row's family
if it has one, so the legacy copy sits in the same block as the row it came from and `KEC0037` stays quiet. A
family whose block is full reserves a second block the ordinary way (3.8).

### 6.4 Step 4, validate

Section 5, on the candidate built at step 2 plus the rule set as it will stand after step 5 appends this
publish's rules. Validating the rules BEFORE they are durable is the point: `KEC0015` refusing a
non-idempotent rule set is the check that makes contracts 8.3's crash safety hold, and a rule that got into
the table could not be taken back out.

**`previous` is the base version's snapshot and this step is the only place it is non-null.** Step 2 built the
candidate by applying the draft's edits to the version the draft is based on, so the previous snapshot is
already materialized and passing it costs nothing. It is what gives `KEC0003`, `KEC0029` and Scope B's
tier-ordinal check an input (section 5.1). The very first publish into an empty database passes null.

An invalid candidate aborts. The draft is NOT discarded and the console gets every finding, so an operator
fixes three problems in one round trip rather than three.

### 6.5 Step 5, compute the temporal rows

For each edit, with `V` the new version number:

| Edit | Writes |
|---|---|
| `Add` | One `catalog_row` with `valid_from_version = V`, `replaced_in_version = NULL`, `retired = 0`, plus one `catalog_row_field` per set field. |
| `Update` | Sets the current row's `replaced_in_version = V`, then inserts a successor with `valid_from_version = V` carrying the MERGED field set: the current row's fields with the edit's fields overlaid. |
| `Retire` | Sets the current row's `replaced_in_version = V`, then inserts a successor with `valid_from_version = V`, `retired = 1` and the SAME field set, plus one `catalog_remap_rule` with kind `2` and the policy payload. |
| `Fork` | Writes one `catalog_row` at the NEW id with `valid_from_version = V`, `retired = 0`, the source row's whole field set copied field by field, the new key, and the named flag field set true. Then sets the SOURCE row's `replaced_in_version = V` and inserts its successor with the edit's changed fields overlaid, exactly as an `Update` would. Then appends one `catalog_remap_rule` with kind `3`, `from_id` the source id and `to_id` the new id. |

A `Retire` whose policy is `0x02` replacement carries the destination id in payload bytes 1 to 4 (contracts
8.2) and the validator has already checked the destination is live (`KEC0017`).

**A `Fork` writes three things and the ORDER is the one that matters.** The copy is written FIRST, so the
kind 3 rule appended last names a `to_id` that is already live at `V`, which is what `KEC0017` checks and what
keeps the rule set applicable the moment it is published. The source row's successor is an ordinary `Update`
successor and carries no flag: the legacy marker is on the COPY, because the copy is what stored payloads are
moved onto and the original id keeps serving new rolls. Both rows land in the same publish and therefore the
same transaction (4.8), so there is no window in which the rule exists and the row it names does not.

A row NOT named by any edit is untouched. Nothing walks it, nothing rewrites it, and its
`valid_from_version` still names whichever old version it entered in. That is what makes step 6 cheap.

### 6.6 Step 6, select affected chunks

A chunk is `(typeId, chunkIndex)` where `chunkIndex = definitionId / chunkSlots` for that type (contracts
4.5, a slot count and not a row count, so a chunk's identity never moves when a row is added to it).

```
affected = { (type, id / chunkSlots[type])
             for every row whose valid_from_version = V
             or whose replaced_in_version = V }
```

Both halves matter. A row that entered at `V` changes its chunk. A row that was CLOSED at `V` also changes its
chunk, because it leaves the live set. An `Update` touches the same chunk twice, which the set collapses.

Every chunk NOT in `affected` keeps its previous version's hash, read from `catalog_chunk` at
`version_number = V - 1`. It is not encoded, not compressed, not hashed and not written. This is the mechanism
behind the owner's "download size after a one-item edit" budget (#882 body, item 12) and it is the reason
chunk identity had to be an id range rather than a row range.

**The carry-forward is PER SIDE, because `affected` is a set of `(typeId, chunkIndex)` and a chunk address is
`(typeId, chunkIndex, side)`.** For an unaffected chunk the publisher copies forward every
`catalog_chunk` row the previous version holds for that `(typeId, chunkIndex)`: one row for a chunk that is
single sided, two when the type is `Client` with a per-field `ServerOnly` override. For an affected chunk it
encodes every side the type produces, which is the same one or two. Nothing anywhere decides a side by
recomputing it from the type's default visibility, because the type's schema may have gained a `ServerOnly`
field at THIS version, and the row set is what says which sides the previous version actually wrote.

**A retire is an ordinary chunk rewrite.** The retired row keeps its slot and its bytes and gains the retired
bit, so exactly one chunk changes. Grimhollow's 18 retired ids sit across ids 1 to 35, which at the `item`
type's 1,024 slots per chunk is one chunk, so its whole retirement history is one chunk rewrite.

**Adding a content TYPE rewrites nothing.** A new type contributes its own chunks and every existing chunk
hash is unchanged. The MANIFEST hash changes, because the manifest enumerates every type sorted by type id
(contracts 7.3), and that is correct: the version identity moved even though no old byte did.

### 6.7 Step 7, encode, compress and hash

For each affected chunk, in `(typeId, chunkIndex)` order:

1. Take the live rows whose id falls in `[chunkIndex * slots, (chunkIndex + 1) * slots)`, sorted ASCENDING BY
   ID. Sorted, always, because contracts 4.3's registration-order-independence rule requires that two
   processes registering the same types in different orders produce byte-identical packs.
2. Encode each row through its type's `IContentRowCodec` into the row body.
3. Build the chunk's canonical uncompressed bytes (section 7.3).
4. `chunkHash = ContentHash.OfChunk(canonicalBytes)`, SHA-256 under sub-domain `kec/chunk/` with the scheme
   version folded in, rendered lower hex (contracts 7.3, 15).
5. Compress the body (section 7.5) and assemble the stored file.

**The hash is over the UNCOMPRESSED canonical bytes.** That is contracts 7.3's rule and it is what makes the
hash independent of the compressor: a later engine build that improves compression produces the same chunk
hash for the same content, so a client already holding the chunk fetches nothing and a republish of an
unchanged chunk is a no-op. A hash over the stored bytes would make a compressor change a full re-download of
the entire catalog.

**The client chunk is a DIFFERENT chunk with a DIFFERENT hash.** For a type whose visibility is `Client` but
which carries at least one `ServerOnly` field, the client chunk is encoded with those fields omitted, giving
its own canonical bytes and its own hash (contracts 11.3). A type whose visibility is `ServerOnly` produces no
client chunk at all. So a chunk address is `(hash)` and the client and server chunks for one id range are two
addresses, which is exactly right: they are different bytes.

**A `Client` type MAY carry a per-field `ServerOnly` override, and this is the case that path exists for**
(contracts 4.7 for the per-field level, contracts 11.3 for the client manifest omitting "every `ServerOnly`
field from the rows in the chunks it does carry"). Nothing refuses it and nothing needs to: the client chunk
omits those fields at encode time. `KEC0014` therefore fires on exactly one thing, the CLIENT-side encoded
bytes of a chunk still carrying a field the schema marks `ServerOnly`, which is an encoder defect and not an
authoring one. The publish-time refusal is the codec failing to honour the schema, never the schema itself.
The `catalog_chunk` row set carries both hashes (section 4.4) and section 6.6 copies both forward.

### 6.8 Step 8, build both manifests

Two manifests per version, the SERVER manifest over every chunk and the CLIENT manifest over the
client-visible chunks only (contracts 7.3, 11.3). Each is built by reading the version's `catalog_chunk` rows
and taking one side: the server manifest takes `visibility = 1` where it exists and `visibility = 0`
otherwise, the client manifest takes `visibility = 0` and names nothing for a type that has no such row.

Each gets its own hash under its own sub-domain, `kec/manifest/server/` and `kec/manifest/client/`, so a head
gating on one can never accidentally agree with a head gating on the other. That last property is
`TileWorldHash.OfWorldAndCatalogs`'s stated reason for existing (`a-engine.md:1206-1209`).

The canonical manifest text, in order (contracts 7.3):

```
<sub-domain><SchemeVersion>\n
<version number>\n
<format generation>\n
<minimum server build>\n
<minimum client build>\n
for each content type, SORTED BY TYPE ID:
    <type id> <len>:<type key>  <chunk count>\n
    for each chunk in ASCENDING INDEX order:
        <chunk index> <chunk hash>\n
<len>:<remap rule chunk hash> \n
for each language, SORTED ORDINAL BY TAG:
    <len>:<language tag>  <text chunk hash>\n
```

Every number goes through `CultureInfo.InvariantCulture`, because `StringBuilder.Append(int)` uses the CURRENT
culture and a negative number digests differently under a culture with its own minus sign
(`TileWorldHash.cs:171-176`). Every string is LENGTH PREFIXED as `"{len}:{value} "` with a bare `"- "` for
null, so a delimiter inside an authored key cannot make two different manifests digest the same
(`TileWorldHash.cs:178-185`).

**The minimum builds and the format generation are INPUTS to the manifest hash, not stamps beside it.** A
publisher who raises `MinimumClientBuild` without touching a row publishes a version with a different manifest
hash and identical chunk hashes, so a client re-reads one small manifest and downloads nothing. That is the
correct behaviour and it falls out of putting the three numbers inside the digest.

`MinimumServerBuild` and `MinimumClientBuild` are CONSUMER-SUPPLIED build ordinals carried on the publish
request (section 10.6). `FormatGeneration` is the ENGINE's own constant, `ContentPackFormat.Generation`, read
from the engine at publish time and never supplied by a caller (contracts 7.4).

### 6.9 Step 9, write the files

Every chunk file and both manifest files go to the pack store (section 8) BEFORE the database commit, at
content-addressed names nothing references yet.

- A chunk file is named by its chunk hash. Writing one twice is idempotent by construction.
- A chunk whose hash already exists in the store is NOT rewritten. `IPackStore.ExistsAsync(hash)` is checked
  first, which is what makes a republish of an unchanged chunk free.
- A manifest file is named by its manifest hash, so the same rule applies.
- The filesystem provider writes to `<hash>.tmp` and then `File.Move(temp, final, overwrite: true)`, the map
  document's idiom (`MapTiledFile.Save.cs:183-192`), with a `stream.Flush(flushToDisk: true)` before the move
  when the store is configured for power-fail durability.

**Step 9 also writes the version POINTER, and it is the one object in the store that is not named by its own
hash.** It goes at `versions/<n>`, it holds that version's two manifest hashes and nothing else, and it is the
only way a CONTENT-ADDRESSED store can answer "what does version 47 contain". The authoring database knows,
and the store has no access to it while the sweep runs against the store (section 6.12), so the fact has to
exist on both sides. `IPackStore.ListAsync(versionNumber)` reads the pointer and then the manifests it names
(sections 8.1 and 8.2).

Two consequences, stated so nobody treats the pointer as a second content address:

- **A client never reads it.** A client learns its manifest hash from the connect door (section 8.5), which is
  the authenticated channel the whole trust chain hangs on (section 13.2). A mutable name in the store is
  therefore never in the integrity path, and a pointer an attacker rewrote costs the publisher's own sweep and
  nothing else.
- **A crash between step 9 and step 10 leaves a pointer for a version that never committed.** That keeps a set
  of orphan files ALIVE rather than deleting live ones, which is the safe direction, and it self-repairs:
  version numbers are never skipped (contracts 7.1), so the retried publish takes the same number and
  overwrites the pointer with its own.

Nothing else points at any of these files until step 10, so a crash here leaves orphans and nothing else. Step
11 sweeps them.

### 6.10 Step 10, the commit

ONE transaction (section 4.8). In order inside it:

1. `newVersion = MAX(version_number) + 1` from `catalog_version`, or 1 when empty.
2. Insert the `catalog_version` row with both manifest hashes, both minimum builds, the format generation, the
   base version, the publisher, the note and the timestamp.
3. Apply every temporal row change computed at step 5.
4. Append every remap rule computed at step 5, at `MAX(sequence) + 1` upward.
5. Insert every `catalog_chunk` row for `newVersion`, ONE PER SIDE. Unaffected chunks get their rows too,
   copied from `newVersion - 1` side by side (section 6.6), so the table answers "which chunks does version N
   have, on which side" with one query and no recursion back through history.
6. Insert every audit row.
7. Delete `catalog_draft_edit_field`, `catalog_draft_edit` and `catalog_draft`.
8. `UPDATE catalog_metadata SET active_version = newVersion`.

Step 8 is the moment the version becomes live, and it is the last statement in the transaction. A reader that
sees `active_version = N` is guaranteed to see every row, rule, chunk and audit entry of version N, because
they committed together.

**The active pointer moves at publish, and the SERVER does not.** v1 applies a new version at server restart
(contracts 1.3 item 8), so moving the pointer makes the version ACTIVE FOR THE NEXT BOOT. A running server
keeps serving the version it loaded. Section 12.4 says what an operator sees and section 10.7 gives them the
pin action for holding a version back.

### 6.11 Idempotence and crash safety, step by step

| Crash point | State afterwards | Recovery |
|---|---|---|
| During step 1 to 8 | Nothing written. The draft is intact. | Republish. Nothing to clean. |
| Between step 3's reservation commit and step 4 | A gap of reserved-but-unissued ids. | None needed. The gap is invisible and bounded by 1,024 per type (section 4.7). |
| During step 9, part way through the chunk files | Some chunk files exist that nothing references. The old version is still active. | Republish writes them again idempotently, since a chunk file's name is its own hash. Step 11 of the NEXT successful publish sweeps any that are never referenced. |
| Between step 9 and step 10 | Every file of the new version exists, and so does its `versions/<n>` pointer. Nothing in the database references them. The old version is still active. | Republish. The `ExistsAsync` check at step 9 makes the rewrite free, so a retried publish after a crash here is fast, and it takes the same version number and overwrites the stale pointer (section 6.9). |
| During step 10 | The transaction rolls back. The old version is active. The orphan files remain. | Republish, then sweep. |
| Between step 10 and step 11 | The new version is fully live. Orphan files from a PREVIOUS failed attempt remain. | The next publish sweeps them. An operator may run the sweep alone (section 10.11). |

The property that makes all six rows safe is that **a chunk file's name is a hash of its own contents**, so
writing one is idempotent, writing one nothing references is inert, and two independent publishes that produce
identical bytes produce one file. That is the map document's argument, whose changed tiles are written "at
names nothing points at yet" with the manifest rename as the commit
(`MapTiledFile.Save.cs:81-102`), and the engine already has a step-hook enum to test exactly this shape
(`MapTiledSaveStep`, `KhaozEngine.MapDoc/MapDocumentForm.cs:58-74`). Section 15.6 copies it.

### 6.12 Step 11, the sweep

After the commit, and only after a SUCCESSFUL commit, the publisher lists the pack store and deletes any file
outside the keep set. **The keep set is the union, over EVERY version the store knows, of that version's
`versions/<n>` pointer, the two manifest hashes the pointer holds, and every hash named INSIDE either
manifest.** That is exactly `IPackStore.ListAsync(v)` for every `v`, which is why the interface has that
member at all (section 8.1). Every pointer is kept unconditionally, because a pointer is how the NEXT sweep
finds the version again. The keep set is defined against the manifests and not against `catalog_chunk`,
because the manifest is the complete enumeration and `catalog_chunk` is not: the
remap rule chunk sits at a reserved address outside any type's id space (section 7.7) and the text chunks are
per language (section 7.6), so neither has a `catalog_type` row to hang a `catalog_chunk` row on, while both
are named by both manifests (section 6.8). A keep set read from `catalog_chunk` would delete the rule chunk
and every text chunk at the first publish, and every later boot would fail closed on `chunk <hash> absent`,
for every version, forever. Reading the manifests also settles the per-side case for free, since the client
manifest names the client hash and the server manifest names the server hash (section 6.7).

The sweep is SKIPPED when the store listing fails for any reason, and a pointer that is absent or unreadable
for any version IS a listing failure. Deleting files on the authority of a listing that failed is how a bad
publish turns into a lost pack. That skip rule is the map document's, which skips its own sweep when the
previous manifest could not be read (`MapTiledFile.Save.cs:105-107`).

The sweep never deletes a file referenced by ANY version, not just the active one, because a pinned server
(section 10.7) and a rollback (section 12.3) both need older versions to stay fetchable.

### 6.13 Rollback is a publish

A rollback is a NEW version that restores old values and keeps every id introduced since (#882 body, item 8).
The version number keeps climbing throughout: a rollback is never a return to an old number, which is the same
property that lets the page stamp be an ordering comparison (contracts 7.2, 8.6).

`RollbackToAsync(targetVersion)` builds a draft rather than doing anything special:

1. Read the live row set at `targetVersion` and the live row set at the current version.
2. For every row live at BOTH whose field set differs, emit an `Update` restoring the target's field values.
3. For every row live at `targetVersion` and RETIRED since, REFUSE the rollback with `KEC0039`, naming the row
   and the rule that retired it.
4. For every row introduced AFTER `targetVersion`, do NOTHING. It keeps its id and its values. This is #882's
   "keeps every id introduced since" and it is the difference between a rollback and a restore.
5. Publish the draft in the ordinary way.

**Step 3 is a flat refusal because a retire is NEVER reversible by rollback** (contracts 8.6). There is no
un-retire branch and there never was a reachable one: step 5 of the publish appends exactly one kind `2`
`Retired` rule for every retire (section 6.5), so every retired row is named by a rule, so a branch conditioned
on "no rule names that id" could not run. Writing the branch and the condition beside each other made the rule
look like a special case when it is the whole case.

The way out is the contracts' own: **a rollback that wants a retired definition back MINTS A NEW ID carrying
the old values** (contracts 8.6), which is an ordinary `Add` with a new key, plus a `ReplacedBy` rule if the
owner wants existing references moved onto it. The API surfaces the refusal as a 409 listing every blocking
row and rule with the mint-new-id path named, so the operator is not left guessing.

`KEC0039` is its own code rather than a second meaning for `KEC0017`. `KEC0017` is "a remap rule's `ToId`
names a row that is not live at `IntroducedIn`", which is a different check about a different object, and
section 5.2's own rule is that codes are never reused.

Section 12 spends the operator-facing half of this.

## 7. Pack format, byte level

### 7.1 Rules that apply to every format here

From contracts 15, restated because every layout below depends on them:

- **Little endian**, written and read through `System.Buffers.Binary.BinaryPrimitives` with the endianness in
  the method name. `BitConverter` is FORBIDDEN because it is host endian.
- **Varint** is unsigned LEB128: seven value bits per byte, low group first, high bit set on every byte but
  the last, at most five bytes for a 32 bit value and ten for a 64 bit one. A field declared SIGNED is zig-zag
  encoded first. Content ids, chunk indices, kind ids, lengths, counts and row counts are all declared
  UNSIGNED and are never zig-zagged. Encodings must be MINIMAL.
- **Version field first**, a `public const ushort`, bumped and never reused. A mismatched version is a REFUSAL
  of the whole record with a reason, never a best-effort partial read.
- **Magic prefixes** for a format stored standalone: `KECC` chunk, `KECM` manifest, `KECR` remap rule chunk,
  `KECQ` quarantine wrapper (Scope B's). This spec adds `KECT` for a text chunk, which contracts 15 did not
  name because per-language text chunks are Scope A's own format. Section 20 records it as a note rather than
  a change request, because contracts 15 gives the RULE for magics and lists the ones it knew about.
- **Digests** are SHA-256, lower hex as text, raw 32 bytes as a field. Every digest is domain separated under
  `kec/` with its own sub-domain and the scheme version folded in.

```csharp
public static class ContentPackFormat
{
    public const ushort ChunkFormatVersion    = 1;
    public const ushort ManifestFormatVersion = 1;
    public const ushort RuleChunkFormatVersion = 1;
    public const ushort TextChunkFormatVersion = 1;
    public const int    Generation            = 1;   // contracts 7.4
    public const int    HashSchemeVersion     = 1;   // contracts 7.3
    public const int    MaxContentRowBytes    = 4096;   // absolute ceiling, no type may exceed it
    public const int    DefaultMaxRowBytes    = 1024;   // a type's cap when it declares none
    public const int    MaxChunkUncompressedBytes = 16 * 1024 * 1024;
}
```

`MaxContentRowBytes = 4096` is the ABSOLUTE row ceiling and no type may declare a cap above it. The
arithmetic: the largest realistic engine row is an item base with three asset references at 128 bytes each, a
20 tag list and a dozen ints, which is under 450 bytes. Its two localized fields contribute NOTHING, because a
localized text key is a derived marker with no bytes in the row (section 3.2).

**Each TYPE carries its own row cap, `DefaultMaxRowBytes = 1024` unless registration declares another, and
`KEC0026` checks a row against its type's cap rather than against the ceiling.** That is what makes the two
caps in this block relate to each other by a CHECK rather than by a hope, because they do not relate by
construction:

```
chunkSlots * (maxRowBytes + 8) + 36  <=  MaxChunkUncompressedBytes
```

Registration refuses a type that fails it, throwing `ContentRegistrationException` naming both numbers, before
the registry freezes. The `+ 8` is the row table entry (a varint id, a flags byte and a varint length, section
7.3) and the `+ 36` is the header.

**The old claim that the caps were "consistent by construction" was arithmetic backwards.** At 4,096 slots and
a 4,096-byte row cap the product is 16,777,216, which is EXACTLY `MaxChunkUncompressedBytes` before the row
table and the header, so a full chunk of maximum rows was already over. Worse, `loot_entry` registers at
16,384 slots (section 3.1), where the same product is 64 MiB, four times the cap, and nothing connected the
two: `KEC0026` bounds one row and `KEC0028` bounds only the slot count. A publish could therefore build a
chunk the reader's own cap refuses, which is a version no reader loads.

Under the bound the engine types fit with room: 4,096 slots at the 1,024 default is 4.2 MB, and `loot_entry`
and `base_socket` each declare `maxRowBytes: 512` for their 16,384 slots, giving 8.5 MB against rows that are
about 20 bytes in practice. A type that genuinely wants 4,096-byte rows may have at most 2,048 slots, which is the honest trade
and is visible at registration rather than at a failed boot.

**`KEC0038` is the check that the bound actually held.** It refuses a publish whose assembled chunk canonical
bytes exceed `MaxChunkUncompressedBytes`, measured after the encode at publish step 7. With the registration
bound in place it cannot fire, which is exactly the relationship `KEC0014` has to the encode-time omission
(section 6.7): the construction argument is the design and the finding is the proof it held.

### 7.2 The chunk file, `KECC`

```
offset  width  field
------  -----  ---------------------------------------------------------------
  0      4     magic, ASCII 'K','E','C','C'  = 4B 45 43 43
  4      2     formatVersion       uint16 LE
  6      2     typeId              uint16 LE
  8      4     chunkIndex          uint32 LE
 12      4     slotBase            uint32 LE     first definition id in range
 16      4     slotCount           uint32 LE     ids in range, a power of two
 20      4     rowCount            uint32 LE
 24      1     visibility          byte          0 = Client, 1 = ServerOnly
 25      1     compression         byte          0 = none, 1 = Brotli
 26      2     reserved            uint16 LE     written 0, refused non-zero
 28      4     uncompressedBytes   uint32 LE     body length before decompression
 32      4     storedBytes         uint32 LE     body length as stored
 36      N     body                storedBytes bytes
```

The header is 36 bytes and is NEVER compressed, so a reader learns the type, the id range, the row count and
the two lengths without touching the compressor. `reserved` is written 0 and a non-zero value is a REFUSAL
with reason `chunk-reserved-set`, which is the contracts' fail-closed rule for an extension a reader cannot
skip (contracts 8.5, 7.4).

`slotBase` and `slotCount` are carried explicitly rather than derived from `chunkIndex` and a registry lookup,
because a reader validating a chunk it just downloaded should not need the registry to decide the file is
malformed. The reader CHECKS them against the registry (`slotBase == chunkIndex * slotCount` and `slotCount`
matching the type's registration) and refuses a mismatch with `chunk-range-mismatch`.

**Two more header-level refusals run BEFORE any allocation, and they are the size gate the hash cannot be.**
The chunk hash is over the UNCOMPRESSED canonical bytes (section 7.3), deliberately, so a reader cannot verify
a stored file without first running the sender's bytes through the decompressor. The integrity check is sound
and it is LATE, so the resource check has to be early:

- `chunk-too-large` when the header's declared `uncompressedBytes` exceeds
  `ContentPackFormat.MaxChunkUncompressedBytes`. Taken from the 36 header bytes alone, before one body byte is
  read and before any buffer is sized.
- `chunk-stored-length` when `storedBytes` differs from the length of the body actually received. A declared
  length that disagrees with the delivered one is a malformed file whatever a hash would later say, and the
  comparison is free because the transport already knows the body length.

Without the first, a hostile proxy or a compromised CDN answers a chunk GET with a 40 KB Brotli stream whose
header declares `uncompressedBytes = 0xFFFFFFFF`, and the reader allocates and decompresses before it can
compute a hash to compare with. The hash check does discard every one of them, which is exactly the problem:
it discards them AFTER the allocation, across the bounded concurrency of 4 and across the retry-with-backoff
loop of section 13.4. Section 8.4 states the matching rule for the decompressor itself.

### 7.3 The chunk body, and the canonical bytes the hash is taken over

The body is a row table followed by the rows. Both are inside the compressed region.

```
offset  width  field
------  -----  ---------------------------------------------------------------
  0      ..    row table: rowCount entries, each
                 [definitionId : varint uint32]   strictly ascending
                 [flags        : byte]            bit 0 retired, bits 1-7 reserved 0
                 [rowLength    : varint uint32]   bytes of this row's body
  ..     ..    row bodies, in the SAME ORDER as the table, concatenated, each
                 [key    : varint uint32 length, then that many UTF-8 bytes]
                 [fields : the type's codec, positional in schema order]
```

**The key is the FIRST field of every row body**, which is what lets the runtime hold no separate `Keys`
array: an id-to-key read is the same offset lookup and span slice as a row read, one varint further in
(section 9.1). Section 7.9 works a two-row chunk through byte by byte.

Rows are STRICTLY ASCENDING by definition id and a row id appearing twice is a refusal with
`chunk-row-duplicate`. Disorder is `chunk-row-order`. Both mirror `ItemContainerCodec`, whose entries are
strictly ascending by slot and whose `Validate` rejects disorder
(`KhaozEngine.Items/ItemContainerCodec.cs:96`, `a-engine.md:103-124`).

A row's OFFSET is not stored. It is the running sum of the preceding `rowLength` values, which a reader
computes in one pass over the table. Storing an absolute offset per row would cost four bytes per row to save
an addition and would give a second representation of the same fact that could disagree with the first. The
table is walked once at load and the result is an `int[]` of offsets held beside the decompressed body, so a
lookup by id is a binary search over the ascending id array and then a slice.

`flags` bit 0 is the retired bit of section 3.9. A reader answers `IsRetired(id)` from the table with no row
decode at all. Bits 1 to 7 are written 0 and a non-zero value is `chunk-row-flags`, the same fail-closed rule
as the header's `reserved`.

**The canonical bytes the chunk hash is taken over are the UNCOMPRESSED bytes of the whole logical chunk**,
which is the 36 byte header with `compression` forced to 0 and `storedBytes` forced equal to
`uncompressedBytes`, followed by the uncompressed body. Written out, so there is no ambiguity:

```
canonical = header(36 bytes, with compression = 0 and storedBytes = uncompressedBytes)
          || uncompressedBody

chunkHash = lowerHex(SHA256( utf8("kec/chunk/" + Inv(HashSchemeVersion) + "\n") || canonical ))
```

The domain string is prepended as UTF-8 text and the canonical bytes follow raw. That is the one place this
spec mixes text and binary in a digest, and it is deliberate: `TileWorldHash` digests a text canonicalisation
(`TileWorldHash.cs:19-20`) because its input is authored JSON, while a chunk's input is already canonical
bytes and re-rendering them as text would double the digest cost for nothing. The domain prefix keeps
contracts 15's separation rule, and the scheme version is inside the prefix so a canonicalisation change
invalidates every digest exactly as intended.

Forcing `compression` and `storedBytes` to their uncompressed values in the canonical form is what makes the
hash independent of the compressor (section 6.7). A reader that verifies a downloaded chunk decompresses
first, rebuilds the canonical header, and hashes. That costs one header rewrite of 36 bytes per verification.

### 7.4 The manifest file, `KECM`

```
offset  width  field
------  -----  ---------------------------------------------------------------
  0      4     magic, ASCII 'K','E','C','M'  = 4B 45 43 4D
  4      2     formatVersion       uint16 LE
  6      1     side                byte          0 = client, 1 = server
  7      1     reserved            byte          written 0
  8      4     versionNumber       uint32 LE
 12      4     formatGeneration    uint32 LE
 16      4     minimumServerBuild  uint32 LE
 20      4     minimumClientBuild  uint32 LE
 24     32     remapRuleChunkHash  raw bytes
 56      ..    typeCount           varint uint32
 ..      ..    per type, ASCENDING BY TYPE ID:
                 [typeId      : uint16 LE]
                 [typeKeyLen  : byte]           1..64
                 [typeKey     : UTF-8 bytes]
                 [chunkSlots  : uint32 LE]
                 [visibility  : byte]
                 [chunkCount  : varint uint32]
                 per chunk, ASCENDING BY INDEX:
                   [chunkIndex        : varint uint32]
                   [uncompressedBytes : varint uint32]
                   [chunkHash         : 32 raw bytes]
 ..      ..    languageCount       varint uint32
 ..      ..    per language, ASCENDING ORDINAL BY TAG:
                 [tagLen     : byte]            1..35, a BCP-47 tag
                 [tag        : UTF-8 bytes]
                 [textHash   : 32 raw bytes]
```

Hashes are RAW 32 BYTES in the file and LOWER HEX only where a hash appears as text, which is contracts 15's
rule and which halves the manifest. A manifest for 1,000,000 item definitions at the `item` type's 1,024
slots is 977 chunks of about 39 bytes each for one type, so a five-type manifest is under 50 KB even at the
stress figure (section 14, budget P1).

**`uncompressedBytes` is per chunk and it is in the manifest deliberately.** Section 9.2 sizes the
concatenated `Bodies` buffer from the sum of it across a type's chunks, and it cannot get that from the CHUNK
headers: at the point it sizes the buffer the loader holds a manifest and has fetched nothing. Three bytes of
varint per chunk buys one allocation per type instead of a growing one.

**It is in the FILE and not in the canonical manifest TEXT**, because contracts 7.3 fixes that text down to
the field order and this spec refines the file rather than contradicting the digest. The field is therefore
outside the manifest hash, which would be a drift risk if nothing bound it, so something does: a chunk whose
own header declares a different `uncompressedBytes` than the manifest does is refused with
`chunk-range-mismatch`, the same treatment as a disagreeing slot range, and the chunk header IS inside the
chunk hash which IS inside the manifest digest. A wrong size in a manifest costs one refusal at the chunk that
disagrees with it, never a silent short buffer.

`side` is in the file AND in the hash sub-domain, so a client manifest and a server manifest of one version
can never be confused for each other in either direction. A reader handed the wrong side refuses with
`manifest-wrong-side`.

**The manifest carries `chunkSlots` per type.** A reader therefore validates a chunk's declared range against
the manifest rather than against its own registry, which matters for a client that loaded a pack produced by a
server whose registry it cannot see. A `chunkSlots` disagreeing with the local registration is
`manifest-chunk-slots` and is a refusal, because it means the two sides disagree about what a chunk index
means.

### 7.5 The compressor, weighed

Every candidate is in box in .NET through `System.IO.Compression`, so this is a cost and ratio question only.
The measurement to beat is the pack size and cold start budgets of section 14.

| Criterion | Brotli | Deflate | None |
|---|---|---|---|
| Ratio on small highly repetitive binary rows | 9 | 7 | 1 |
| Compress time at publish, a thousand chunks | 6 | 9 | 10 |
| Decompress time at boot and at client cold start | 8 | 9 | 10 |
| In box, no package reference | 10 | 10 | 10 |
| Deterministic output for a fixed input and level | 9 | 9 | 10 |
| Streaming and span friendly, no intermediate copy | 8 | 8 | 10 |
| Client cold start over a slow link (bytes dominate) | 10 | 7 | 1 |
| Total | 60 | 59 | 52 |

Recommendation: **Brotli at quality 5, window 22**, with the format carrying a `compression` byte so the
choice is per chunk and reversible. Brotli and Deflate score within one point, which is exactly why the
`compression` byte exists rather than a hard-coded algorithm, and the deciding row is the last one: a client
cold start is bytes over a link the engine does not control, and Brotli's advantage on small structured binary
is real.

Quality 5 rather than 11 because publish time is a budget (section 14, P5) and quality 11 costs roughly an
order of magnitude more CPU for a few percent of ratio on inputs this small. Window 22 because a chunk is at
most 16 MiB and a larger window buys nothing.

**A chunk whose compressed body is not SMALLER than its uncompressed body is stored uncompressed**, with
`compression = 0`. That covers a chunk of one row and a chunk of already-dense data, and it means the format
never pays a compression header to grow a file.

**Determinism is asserted, not assumed.** `BrotliEncoder` with a fixed quality and window is deterministic for
a given .NET version, and that is NOT a documented API guarantee across versions. This spec does not depend on
it: the chunk HASH is over the uncompressed bytes (section 7.3), so a runtime that compresses differently
produces a different FILE with the same content address, and the store's `ExistsAsync` check means the first
writer wins and the second does not rewrite. Section 15.3 pins the uncompressed canonical bytes in the golden
files and deliberately does NOT pin the compressed bytes, for exactly this reason.

### 7.6 The text chunks, `KECT`

Per-language text chunks, one chunk set per language, listed in the manifest with their own hashes so a client
downloads only the languages it wants (contracts 12.4, #882 body items 6 and 9). The engine ships chunks for
languages the game does not, which is gate 0 decision 9.

```
offset  width  field
------  -----  ---------------------------------------------------------------
  0      4     magic, ASCII 'K','E','C','T'  = 4B 45 43 54
  4      2     formatVersion       uint16 LE
  6      1     tagLen              byte
  7      N     languageTag         UTF-8, a BCP-47 tag, at most 35 bytes
 ..      1     compression         byte
 ..      1     reserved            byte          written 0, refused non-zero
 ..      4     uncompressedBytes   uint32 LE
 ..      4     storedBytes         uint32 LE
 ..      M     body                storedBytes bytes
```

The `reserved` byte is here for the same reason `KECC` and `KECR` carry one: a non-zero value is a refusal
with `chunk-reserved-set`, which is the fail-closed rule for an extension a reader cannot skip (contracts 8.5,
7.4). It was the one format in this spec without it, which would have made `KECT` the only one that could not
grow a flag.

Body, inside the compressed region:

```
[entryCount : varint uint32]
per entry, ASCENDING ORDINAL BY KEY:
  [keyLen   : byte]          1..192
  [key      : UTF-8]
  [valueLen : varint uint32] 0..8192
  [value    : UTF-8]
```

Keys are the derived keys of contracts 12.1, `<type key>.<content key>.<field>`, ordinal ascending so the
chunk is canonical and its hash is stable. The value cap of 8,192 bytes is generous for a UI string and
bounded, so a malformed length cannot make a reader allocate a gigabyte.

**The canonical bytes the text chunk hash is taken over are the whole logical chunk uncompressed**, the same
construction as section 7.3: the header exactly as written with `compression` forced to 0 and `storedBytes`
forced equal to `uncompressedBytes`, followed by the uncompressed body. The header is VARIABLE length here,
`17 + tagLen` bytes, and it is included WHOLE, language tag and all, so the same strings under two language
tags are two different chunks with two different hashes. Written out, so there is no ambiguity:

```
canonical = header(17 + tagLen bytes, with compression = 0 and storedBytes = uncompressedBytes)
          || uncompressedBody

textHash  = lowerHex(SHA256( utf8("kec/text/" + Inv(HashSchemeVersion) + "\n") || canonical ))
```

This was the one format whose canonical form section 7.8 named without anyone writing down, and the symptom of
two implementers guessing differently is a client that fetches a text chunk, verifies it, fails, and discards
it forever.

**`KECT` takes the same two header-level refusals as `KECC`**, `chunk-too-large` when the declared
`uncompressedBytes` exceeds `ContentPackFormat.MaxChunkUncompressedBytes` and `chunk-stored-length` when
`storedBytes` differs from the received body length (section 7.2). Both are taken from the variable-length
header before any allocation and before the compressor is touched. The 8,192 byte value cap bounds one entry
and these two bound the file, which is the pairing the entry cap alone was never going to give.

**One chunk per language, not one per language per type, up to a size at which it MUST shard.** The size is
computed once, in section 14.1, and this section does not recompute it: at 50,000 items a language chunk is
about 8.7 MB uncompressed, roughly 2.2 MB Brotli, and it holds 100,800 entries. That is every localized field
of every engine type, `item.name` and `item.examine` at 50,000 each, `tag.name` at 200 and `stat.name` and
`stat.display_format` at 300 each, not just the two item fields an earlier version of this paragraph counted,
and it includes the derived key bytes an earlier version left out.

**Sharding becomes MANDATORY before the stress figure, and that is not a preference.** At 86 bytes an entry,
`MaxChunkUncompressedBytes` of 16 MiB is about 195,000 entries, so one language chunk cannot hold more than
about 97,000 items with two localized fields each. At the 1,000,000 figure of contracts 1.3 one language is
2,000,000 entries and 172 MB, which is eleven shards at minimum. So the shard is a phase 3 mechanism by
schedule and a correctness requirement by arithmetic, and section 21 question Q4 sets the threshold against
these numbers rather than against the 8 MB one that was picked when the chunk was thought to be 6 MB.

The catalog is layered over the game's existing `.resx` catalog, content first (contracts 12.4):
`ContentStringCatalog` asks the pack, misses to the game's `IStringCatalog`, and misses there to the standard
behaviour of returning THE KEY ITSELF as a visible non-fatal placeholder
(`KhaozEngine.App/IStringCatalog.cs:12-17`). Content first, because content is the thing that ships without a
client release, so a content string must be able to override a stale shipped one.

`ContentStringCatalog.Format` routes through the `SafeFormat` behaviour contracts 12.3 adopts, so a malformed
translator-authored template falls back to the unformatted template rather than taking the frame loop down.

**A decoded language is the chunk body itself plus one index, and no decoded entry at all.** The decode
decompresses the body into a `byte[]` and KEEPS it, exactly as a type's `Bodies` keeps its rows (9.2), and
builds one open-addressed `int[]` over it holding each entry's byte offset plus one, hashed on that entry's
UTF-8 key and probed by an ordinal span compare. There is no `Dictionary<string, string>`, and nothing in the
chunk exists as UTF-16 until something asks for it. At 50,000 items and 100,800 entries that is the 8.5 MB
body plus 262,144 buckets of 4 bytes, about 9.6 MB, against the 44.2 MB the string dictionary measured at
stage 5 (14.3). A language that sharded holds one body and one index per shard, and because the entries are
ordinal ascending across the whole language (above), picking the shard is a binary search over each shard's
first key rather than a probe of every shard.

**`IStringCatalog.Get` returns `string`, so a materialisation happens somewhere, and the only choice is
whether the same one happens twice.** Materialising per call keeps the resident figure at exactly the body
plus the index and allocates on every call, which is right for a settings screen built once and wrong for
anything a frame loop touches. A small bounded cache of resolved strings costs a bounded number of live
strings and answers a repeat from the cache. **The cache wins and is what this spec ships**: a direct-mapped
table of 512 entries keyed on the entry offset, which at a 60-character value is about 80 KB of strings, three
ten-thousandths of P10's budget, and it holds the working set of a UI screen. It is bounded by construction
rather than by a policy, so it cannot grow into the thing the budget exists to prevent, and a miss is one
`Encoding.UTF8.GetString` over a slice the catalog is already holding.

### 7.7 The remap rule chunk, `KECR`

ONE rule chunk per manifest, at a reserved address outside any content type's id space, holding the FULL rule
list from sequence 1 rather than a delta (contracts 8.5). It is in BOTH manifests and its contents are
identical in both, because a rule is `(id, id, kind)` and never a value, so it carries no server-only
information by construction.

```
offset  width  field
------  -----  ---------------------------------------------------------------
  0      4     magic, ASCII 'K','E','C','R'  = 4B 45 43 52
  4      2     formatVersion       uint16 LE
  6      1     compression         byte
  7      1     reserved            byte          written 0
  8      4     ruleCount           uint32 LE
 12      4     uncompressedBytes   uint32 LE
 16      4     storedBytes         uint32 LE
 20      N     body                storedBytes bytes
```

The body is `ruleCount` rules in ASCENDING SEQUENCE order, each exactly contracts 8.4's encoding and not one
byte more:

```
[Sequence      : varint int32]
[IntroducedIn  : varint int32]
[TypeId        : uint16 LE]
[Kind          : byte]
[FromId        : varint int32]
[ToId          : varint int32]
[PayloadLength : byte, 0 to 64]
[Payload       : PayloadLength bytes]
```

A typical rule is 9 to 12 bytes and ten thousand rules is about 110 KB uncompressed, a rounding error against
a pack (contracts 8.4). A gap in the sequence, a non-ascending sequence or a `Kind` the reader does not know
are all REFUSALS rather than skips, with reasons `rule-sequence-gap`, `rule-sequence-order` and `rule-kind`.
An unknown kind failing closed is contracts 8.5's explicit rule, and it is what `FormatGeneration` exists to
announce in advance.

The rule chunk's hash is SHA-256 under sub-domain `kec/rules/` over its canonical uncompressed bytes, by the
same construction as section 7.3.

### 7.8 Every digest in this spec, in one table

| Digest | Sub-domain | Over |
|---|---|---|
| Chunk | `kec/chunk/` | The chunk's canonical uncompressed bytes (7.3). |
| Remap rule chunk | `kec/rules/` | The rule chunk's canonical uncompressed bytes (7.7). |
| Text chunk | `kec/text/` | The text chunk's canonical uncompressed bytes (7.6). |
| Server manifest | `kec/manifest/server/` | The canonical manifest text of 6.8 with `side = server`. |
| Client manifest | `kec/manifest/client/` | The same text with `side = client` and the server-only chunks omitted. |

No two share a sub-domain, so a head comparing one can never accidentally agree with a head comparing another
(contracts 15). `ContentHash.SchemeVersion` starts at 1 and is folded into every one of them, and bumping it
re-digests every version, which is precisely why it exists.

### 7.9 A worked hex example: a two-row chunk

The `tag` type, type id 1, chunk 0, slots 4,096, holding two rows. Tag 1 is `metal` with sort 10. Tag 2 is
`two_handed` with sort 20 and is retired. The `tag` schema of section 3.2 is two fields, `name` (localized
text key) and `sort` (int), and the `tag` codec writes them positionally in schema order, because a row
codec's field set is fixed by the schema it was checked against at registration (section 3.6).

**`name` is a derived marker, so it occupies no bytes here** (section 3.2). Its keys, `tag.metal.name` and
`tag.two_handed.name`, are derived from the type key and each row's content key, and the strings they name are
in the text chunk (section 7.6). Positional encoding is unaffected: a marker takes zero width in the sequence
the way a zero-length field would, and the decoder knows to skip it from the same schema the encoder used.

The row body for tag 1 therefore opens with the row's own content key, a varint length then the UTF-8 bytes
(section 7.3), and the one encoded field follows it:

```
05 6D 65 74 61 6C                                  key: varint length 5, then "metal"
0A                                                 sort: varint 10
```

Seven bytes. The row body for tag 2:

```
0A 74 77 6F 5F 68 61 6E 64 65 64                   key: varint length 10, then "two_handed"
14                                                 sort: varint 20
```

Twelve bytes. `0A` is 10 and `14` is 20, both single-byte varints because both are under 128, and tag 2's key
length prefix is that same `0A` byte for the same reason.

The row table, two entries:

```
01 00 07       id 1, flags 0 (live),    rowLength 7
02 01 0C       id 2, flags 1 (retired), rowLength 12
```

Six bytes. The uncompressed body is `6 + 7 + 12 = 25` bytes:

```
01 00 07  02 01 0C
05 6D 65 74 61 6C  0A
0A 74 77 6F 5F 68 61 6E 64 65 64  14
```

The 36 byte header, as it appears in the CANONICAL bytes the hash is taken over, so `compression = 0` and
`storedBytes = uncompressedBytes = 25`:

```
4B 45 43 43     magic  'K','E','C','C'
01 00           formatVersion 1
01 00           typeId 1
00 00 00 00     chunkIndex 0
00 00 00 00     slotBase 0
00 10 00 00     slotCount 4096
02 00 00 00     rowCount 2
00              visibility 0 (Client)
00              compression 0
00 00           reserved
19 00 00 00     uncompressedBytes 25
19 00 00 00     storedBytes 25
```

Note `00 10 00 00` is 4,096 little endian (`0x00001000`). The canonical bytes are 61 in total, 36 of header
plus 25 of body, and

```
chunkHash = lowerHex(SHA256( utf8("kec/chunk/1\n") || <those 61 bytes> ))
```

The STORED file differs from the canonical bytes in exactly two fields when the body compresses: byte 25 holds
`01` and bytes 32 to 35 hold the compressed length. At 25 bytes this body will not compress smaller, so this
particular chunk stores uncompressed and the stored file is byte identical to the canonical bytes. That is the
common case for a small chunk and it is why the `compression` byte exists per chunk rather than per pack.

## 8. Pack store and transport

### 8.1 The pack store abstraction

```csharp
public interface IPackStore
{
    Task<bool>          ExistsAsync(string hash, CancellationToken ct = default);
    Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken ct = default);
    Task                PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken ct = default);
}
```

Content addressed, so `hash` is the 64 character lower hex of whatever the object's own digest rule is
(section 7.8). Four members and no more, deliberately:

- `GetAsync` returns null for absent rather than throwing, matching
  `TileWorldCatalogs.Archetype(string? id)` returning null rather than throwing so a validation pass is never
  taken down by one lookup (`a-engine.md:321-322`).
- `PutAsync` is idempotent. Putting a hash that exists is a no-op, and a provider MAY verify rather than
  rewrite.
- `ListAsync` takes a version number rather than listing everything, because a cloud container may hold every
  version ever published and the only caller that needs a list is the publish sweep (section 6.12). It answers
  from the VERSION POINTER the publisher writes at `versions/<n>` (section 6.9), which holds that version's two
  manifest hashes: a content-addressed store has no version index and cannot derive one, so the pointer is the
  index and this is the only member that reads it. A provider that cannot enumerate returns an empty sequence
  and the sweep is skipped, which is section 6.12's own skip rule, and so does a provider whose pointer for
  that version is absent or unreadable.
- There is no delete. The sweep calls a provider-specific `IPackStorePruning.DeleteAsync` when the provider
  implements it, so a read-only provider cannot be asked to prune and a misconfigured one cannot delete a
  production pack through the common interface.

**`PutAsync` VERIFIES the hash before writing.** Every provider recomputes the digest of the bytes it was
handed and throws `ContentPackException` on a mismatch. That is one SHA-256 over bytes already in memory at
publish time, and it is the check that makes every later "verify on read" meaningful, because a store that
will write anything under any name is not content addressed.

### 8.2 `FileSystemPackStore`

One file per hash under a two-level shard, `<root>/<hash[0..2]>/<hash[2..4]>/<hash>.kec`. Two levels of 256
because a flat directory of a thousand chunks times 50 versions is fine and a flat directory of a million is not, and
the shard is derived from the hash so it needs no index.

Writes go to `<hash>.tmp` in the same shard directory and then `File.Move(temp, final, overwrite: true)`, the
map document's idiom (`MapTiledFile.Save.cs:183-192`). `overwrite: true` is correct here precisely because
the name is the content: rewriting a hash with its own bytes is a no-op by definition.

`ListAsync(version)` reads `<root>/versions/<n>`, the pointer written at publish step 9, takes the two
manifest hashes it holds, reads both manifests out of the shard tree and yields every hash they name plus the
two manifest hashes themselves. It does NOT walk the directory. A directory walk would find orphans, and a
store's job is to answer what a version contains, not what happens to be on disk. The publish sweep needs
BOTH, and it gets the orphan half from a separate provider-specific enumeration behind `IPackStorePruning`.

The pointer lives OUTSIDE the two-level shard tree, under a `versions/` directory, because the shards are
named from a hash and a version number is not one. It is written with the same temp-then-move idiom as every
other file, so a reader never sees a half-written pointer. `HttpPackStore` reads `<base>/versions/<n>` by the
same rule, and still throws `NotSupportedException` from `ListAsync` because it is a fetch path and the sweep
is the publisher's.

An `fsync` before the move happens only when the store is constructed with `PackDurability.PowerFail`,
matching `MapSaveDurability` (`MapTiledFile.Save.cs:190`). The default is the cheaper mode, because a pack
file lost to a power cut is refetchable from its hash and a lost world file is not.

### 8.3 `HttpPackStore`, the cloud provider

Read only, over one injected `HttpClient`, with the base address being any HTTP-addressable container: an
Azure Blob container with public read or a SAS, an S3 bucket, or a CDN in front of either. `GetAsync` issues
`GET <base>/<hash[0..2]>/<hash[2..4]>/<hash>.kec`, the same shard layout as the filesystem provider, so the
same tree serves both.

**No cloud SDK, and that is a deliberate package decision.** Taking `Azure.Storage.Blobs` would put a
third-party dependency into `KhaozEngine.Catalog`, which is `Foundation` and is Pure .NET (section 2.1), and
would force every client of every game to carry it. Every blob service worth using serves an HTTP GET, and
the WRITE side is the publisher's, which runs on a server that can implement `IPackStore` over whatever SDK
the game already has. So the engine ships the two providers that need no dependency and the seam for the rest.

`PutAsync` and `ListAsync` throw `NotSupportedException` on `HttpPackStore`, which is what makes it obviously
a fetch path rather than a half-working publish target.

### 8.4 `CachingPackStore`

A decorator taking a LOCAL store and a REMOTE store. `GetAsync` asks local, and on a miss asks remote,
VERIFIES the hash, writes through to local, and returns. `ExistsAsync` asks local then remote. `PutAsync` goes
to local only.

The verification is the whole value of the decorator and it is not optional:

```
bytes = await remote.GetAsync(hash)
actual = ContentHash.OfBytesForKind(kind, bytes)
if (actual != hash) -> discard, do NOT cache, report reason "hash-mismatch", try the next source
```

`OfBytesForKind` dispatches on the magic in the first four bytes, so a chunk is hashed under `kec/chunk/` and
a manifest under its own sub-domain, and a file whose magic does not match any known kind is rejected before
any length field is read.

**The decompression that feeds it is BOUNDED, and that ordering is load bearing.** Verifying a hash taken over
uncompressed bytes means decompressing bytes that are not yet trusted, so the steps are fixed: check
`storedBytes` against the received body length (`chunk-stored-length`), check the declared
`uncompressedBytes` against `ContentPackFormat.MaxChunkUncompressedBytes` (`chunk-too-large`), allocate
exactly the declared length, decompress INTO that buffer and refuse with `chunk-too-large` on the first byte
that would overrun it, THEN rebuild the canonical form and hash. The overrun refusal is not redundant with the
declared-length check, because a Brotli stream can expand past whatever its container claims and the declared
length is the sender's number too. Only bytes that survive all of that are hashed, and only bytes whose hash
matches are cached.

**A mismatching object is never cached and never used.** That is the entire defence against a poisoned CDN or
a corrupted proxy, and it is why the cache is keyed by hash rather than by version: a cache entry that does
not hash to its own key is self-evidently wrong and can be discarded without any other information.

### 8.5 How a client learns the version at the connect door

Contracts 7.5 fixes this and `KhaozEngine.Catalog.Netcode` implements it. A new labelled `HandshakeToken`
layer carries `<versionNumber>|<clientManifestHash>`, gated by `ContentIdentityGateAuthenticator` modelled
directly on `WorldIdentityGateAuthenticator` (`KhaozEngine.Netcode/ConnectionGate.cs:53-96`): unwrap one
layer, compare ORDINAL, refuse with a stable wire token carrying both sides, otherwise delegate inward.

Layer order in the nest, outermost first: protocol version, world, CONTENT, the game's token auth, the ban
check (contracts 7.5).

```
ke:content-mismatch:<serverVersion>|<serverHash>|<clientVersion>|<clientHash>
ke:content-client-too-old:<minimumClientBuild>
```

A client presenting no layer at all unwraps to the empty label and is refused with empty client fields, which
is what `GrimhollowConfigGate` already does (`b-grimhollow.md:449-456`).

**A client that is behind is REFUSED, not admitted read-only while it fetches** (gate 0 decision 6). So the
client loop is: connect, get refused with the server's version and hash, fetch the manifest by that hash, fetch
the chunks the manifest names that the local cache lacks, verify each, then reconnect. The refusal token
carries everything the fetch needs, which is why it carries the hash and not just the number.

**Where the client gets the base URL.** Not from the refusal token. A URL in a refusal token is a redirect an
unauthenticated party controls, and section 13.4 spends why that is unacceptable. The client is configured
with its pack base address the same way it is configured with its server address, which for the two consumers
is already how the Azure Blob client update feed works (AGENTS.md, Consumers).

### 8.6 Which endpoint serves the chunks, weighed

This is genuinely contested, because the obvious answer (the game server already has an admin HTTP endpoint)
is wrong for reasons that only show up at scale.

| Criterion | Game server's admin HTTP | Static host or CDN | Game-owned game endpoint |
|---|---|---|---|
| Bytes served do not compete with the tick loop | 1 | 10 | 3 |
| Works for a client that has not authenticated yet | 2 | 10 | 5 |
| Survives a thundering herd after a world restart | 1 | 10 | 3 |
| Costs the engine no new transport | 8 | 10 | 5 |
| Reuses an existing consumer deployment shape | 4 | 10 (both consumers already ship an Azure Blob client update feed) | 6 |
| Operator effort to stand up | 9 | 6 | 5 |
| Can be rate limited independently of gameplay | 3 | 10 | 6 |
| Total | 28 | 66 | 33 |

Recommendation: **a static host or CDN**, addressed by `HttpPackStore`, with the game server's admin HTTP
serving nothing but the manifest for an operator diagnostic. The admin endpoint is bearer gated behind one
token (`AdminHttpServer.cs:61-71`) and binds to loopback by default (`AdminEndpointOptions.cs:29`), so it is
not a thing a player's client can reach at all, and its pre-auth connection cap defaults to 64
(`AdminEndpointOptions.cs:39`). Serving a megabyte pack to every reconnecting player through it would put
client bandwidth on the same process as the simulation, which is the exact failure the owner's original
chunked-push-on-join proposal was revised away from (#882 comment 1: "That is thousands of 1024-byte messages
per join, multiplied by every player reconnecting after a world restart").

The thundering-herd row is the decisive one. v1 applies a new version at server restart, so every connected
player reconnects at once and every one of them needs the same set of chunks. A CDN serves that from cache.

### 8.7 The client fetch loop

```
1. Refused at the door with ke:content-mismatch:<v>|<serverHash>|...
2. manifest = cache.Get(serverHash)
   if absent: manifest = remote.Get(serverHash), verify, cache
3. if manifest.formatGeneration > reader.Generation           -> fail, ask the player to update
   if localBuild < manifest.minimumClientBuild                -> fail, ask the player to update
4. missing = [ every chunk hash in manifest not in local cache ]
5. for each hash in missing, bounded concurrency 4:
       bytes = remote.Get(hash)
       verify hash; on mismatch retry once against the same source, then fail this chunk
       cache.Put(hash, bytes)
6. if every chunk arrived: reconnect
   else: report progress and retry the missing set with backoff
```

Bounded concurrency 4, because a cold start at 1,000,000 definitions is 977 item chunks and unbounded
parallelism against a CDN buys nothing over a link a player's home connection saturates at two or three
streams.

**Decode is LAZY.** Step 5 stores bytes. Nothing is decompressed or decoded until a lookup asks for a row in
that chunk (section 9.3). A client that walks its inventory touches a handful of item chunks and never opens
the rest, which is what makes the cold start budget (section 14, P4) a download budget rather than a decode
budget.

### 8.8 Partial download, corrupt chunk, hash mismatch

| Situation | What the client does |
|---|---|
| Fetch interrupted part way through the missing set | The chunks that arrived are cached and verified. The next attempt recomputes `missing` and fetches only the rest. There is no resume state beyond the cache itself. |
| A chunk arrives truncated | Its hash does not match. Discarded, not cached, retried once, then the whole fetch reports `chunk-fetch-failed` with the hash and the client stays at the door. |
| A chunk arrives with a valid hash but a malformed body | Impossible for a whole-file corruption, since the hash covers the whole canonical form. A body that decodes badly under a matching hash means the PUBLISHER wrote a bad chunk, which `KEC0027` should have caught, and the client refuses with the decode reason and does not cache it. |
| A cached chunk goes bad on disk | Detected on first use (section 9.6 and section 11 row 1), the cache entry is deleted, and the chunk is refetched. |
| The manifest hash does not match | The manifest is discarded and refetched once. A second mismatch is `manifest-hash-mismatch` and the client stays at the door with the server version in the notice, because it cannot distinguish a bad CDN from a bad configuration and guessing is worse than stopping. |
| The server's version moves mid fetch | The client finishes the fetch it started and then reconnects. The door refuses again with the NEW hash, and the second fetch downloads only the chunks that differ. No special casing, because chunk addresses are content addresses. |

Every one of these is a reason token, never an exception across the boundary, following the whole-tree rule
that a decoder handed remote bytes is total (`a-engine.md:814-817`).

**A partial download never becomes a partial catalog.** The client does not reconnect until every chunk in
the manifest verifies, so there is no state in which a client holds half a version. That is what makes the
door comparison a simple hash equality rather than a negotiation.

## 9. Server runtime

### 9.1 The loaded shape

The active version loads into `ContentRuntime`, which holds, per registered content type:

```csharp
internal sealed class ContentTypeTable
{
    public readonly int SlotBase;          // always 0 for a type's table 0
    public readonly int[] Offsets;         // index by (id - SlotBase), -1 for absent
    public readonly byte[] Bodies;         // every row body of every chunk, concatenated
    public readonly int[] Lengths;         // parallel to Offsets
    public readonly ulong[] RetiredBits;   // one bit per slot
    public readonly int[] KeyIds;          // open addressed id table, hashed on the UTF-8 key slice
}
```

A lookup is `offsets[id]`, one array read, then a `ReadOnlySpan<byte>` slice of `Bodies`. No dictionary, no
lock, no allocation. That is #882's requirement stated directly: "load the active version into arrays indexed
by id, immutable and swapped atomically, with no lock per lookup".

`Offsets` is sized to the highest live id plus one, not to the sum of chunk slots. A type with ids 1 to 35 and
a chunk size of 1,024 allocates a 36-element array, not a 1,024-element one. Chunk slots are a TRANSPORT unit
(contracts 4.5) and the runtime does not inherit their sparseness.

**There is no `Keys` array, because every row body already opens with its own key, length prefixed UTF-8
(section 7.3).** The per-type key blob is `Bodies`, the offset array is `Offsets`, and a second blob would be
a second copy of bytes the loader already holds. So an id-to-key read is the same array read and span slice as
a row read, one varint further in, and what it hands back is a `ContentKey` over that slice:

```csharp
public readonly struct ContentKey            // (blob, start, length) over the loaded bytes
{
    public ReadOnlySpan<byte> Utf8 { get; }  // a slice of ContentTypeTable.Bodies, no copy
    public bool Equals(ContentKey other);    // ordinal, SequenceEqual over the two slices
    public override string ToString();       // materialises, on demand, for a log line or a console
}
```

A `ContentKey` built from a string instead, which is what an admin lookup, a world boot reference (3.10) and
an import row hand in, encodes to UTF-8 once at construction and compares by bytes after that. Both sides are
ordinal, which is contracts 5.3's rule, and no loaded key exists as UTF-16 anywhere in the runtime.

**`KeyIds` is the key-to-id index of 9.4, and it is the only structure a key costs.** An open-addressed
`int[]` of a power-of-two capacity at least twice the live row count, holding an id in each occupied bucket
and 0 in an empty one, which is unambiguous because 0 is not a legal id (contracts 5.1). A probe hashes the
key's UTF-8 bytes, masks to a bucket, and compares the candidate id's key slice ordinally, walking linearly on
a collision. The build is one pass over the live ids and a lookup is O(1) expected at one cache line a probe.

**The alternative was a sorted-key binary search over the offsets, and it lost on build and on lookup.** Its
whole case is memory: 4 bytes a row rather than 8. Against that, building it sorts the live ids with an
ordinal span comparison inside every comparison, which is n log n with a random memory access per compare
against one linear pass, at a stress figure of 1,833,833 rows. A lookup is about 17 of those random probes
rather than one. What the sorted form buys that the hash does not is ordered iteration, and nothing in this
spec asks for one: a console listing is by id (10.4) and so is a diff. So the hash table is the shape, and the
sorted form is written down here in case a prefix query ever turns up.

**Every array in `ContentTypeTable` except `Bodies` and `KeyIds` is parallel to `Offsets`, so the sparse cost
is three arrays and not one.** A type with a family block at base 65,536 and 40 members sizes `Offsets` to
65,577 entries, and `Lengths` and `RetiredBits` follow:

| Array | At 65,577 slots |
|---|---|
| `Offsets`, `int[]` | 262 KB |
| `Lengths`, `int[]` | 262 KB |
| `RetiredBits`, 1,025 `ulong` | 8 KB |
| `KeyIds`, 128 buckets for 40 rows | 512 bytes |
| Total | about 533 KB, for 40 rows |

That is twice the 262 KB this paragraph once quoted, which counted `Offsets` alone, and half the 1.06 MB the
string-keyed layout cost, and it is the number question Q5's sparse-table threshold is argued against. The
recommended default there is to switch a type to a sorted-id binary search when live density falls below one
in sixteen.

**`ItemRow` is the typed view over the `item` type, and it exists because four of its fields are read per
operation.** The generic read side is `ContentRow`, an ordered field-value list, which is right for an editor,
a diff and a game type the engine has never heard of. It is wrong for the stacking rule: Scope B consults
`stackable` and `max_stack` on every merge test and caches neither, reads `durability_max` at every
generation, and reads `socket_max` in its container validator. A field-by-name walk over a value list on that
path is not the sub-5 ns operation P7 budgets, and the alternative, each consumer caching its own decoded
copy, is four caches that can disagree with the runtime after a version swap.

```csharp
public readonly ref struct ItemRow
{
    public bool Stackable    { get; }
    public int  MaxStack     { get; }
    public int  DurabilityMax{ get; }
    public int  SocketMax    { get; }
    public ContentKey Key    { get; }
    public bool IsRetired    { get; }
}

// on ContentRuntime
public bool TryGetItem(int id, out ItemRow row);
```

A `ref struct` over the body span, with the four hot fields decoded at construction from known offsets rather
than searched by name. It is the ENGINE `item` type only, which is what makes fixed offsets legal: the schema
is this spec's, it is in section 3.3, and a game cannot add a field to it. The budget is P7's, under 5 ns and
zero allocation, and it is the same array index plus span slice as a generic lookup with four varint reads
after it. Nothing else gets a typed view: `ContentRow` stays the answer for every other type, because a typed
view over a schema the engine does not own could not be written.

### 9.2 Memory layout and the arithmetic

One `Bodies` array per type holding every row body concatenated in ascending id order, rather than one array
per chunk. The reason is allocation count and locality: at 1,000,000 item definitions and 1,024 slots that is
977 chunks, so one byte array per chunk would be 977 large-object-heap allocations that a walk over ids
touches in 977 discontiguous regions. One array is one allocation and a sequential walk is sequential.

The concatenation happens ONCE at load, chunk by chunk, into a buffer sized from the per-chunk
`uncompressedBytes` the manifest carries (section 7.4), so there is no growth and no copy beyond the
decompress.

Per-type memory at the two owner figures, with the item base's realistic row of about 124 bytes encoded
(section 14, P1 arithmetic). That row figure INCLUDES the row's own key, because the key is the first thing
the encoder writes:

| Definitions | `Bodies` | `Offsets` + `Lengths` | `KeyIds` | `RetiredBits` | Total |
|---|---|---|---|---|---|
| 50,000 | 6.2 MB | 0.4 MB | 0.5 MB | 6 KB | about 7.1 MB |
| 1,000,000 | 124 MB | 8 MB | 8.4 MB | 125 KB | about 140 MB |

**A loaded key costs the `KeyIds` bucket and nothing else, which is the line this table has now got wrong
twice in opposite directions.** It first counted an array of references and object headers, 1.6 MB and 32 MB,
and left the characters out. It then counted the UTF-16 characters too, 3.6 MB and 72 MB, and was right about
the layout it was describing. What changed is the layout: 9.1 reads a key out of `Bodies` where the encoder
already put it, so the only structure left is the open-addressed id table, at 4 bytes a bucket and a
power-of-two capacity of at least twice the row count. At 50,000 rows that is 131,072 buckets and 524 KB, and
at 1,000,000 it is 2,097,152 buckets and 8.4 MB.

**That is 65 MB off the stress figure, and it also deletes the separate key-to-id dictionary**, because
`KeyIds` IS that index (9.4). The string-keyed layout carried both, an array of `ContentKey` at 72 MB and a
`Dictionary<ContentKey, int>` at roughly 32 MB on top of it, and the two are one 8.4 MB array now. What the
saving is NOT is free: a key comparison became a byte-span compare against a scattered row body rather than a
UTF-16 compare against a string the index had already in hand, and the keys stay around for the same three
reasons as before, logging, an admin lookup, and the quarantine reason of contracts 10.2 which has to name
what failed. Budget P3 in 14.1 is where the trade is measured rather than argued.

### 9.3 Decode is lazy on the client and eager on the server

**The server decodes eagerly at boot**, because the validator runs on the full snapshot (contracts 10.5) and
because a server that decodes lazily pays a first-touch cost inside a tick. The decode is one pass over
`Bodies` and it is what builds any per-type derived index the runtime holds (section 9.4).

**The client decodes lazily**, per chunk, on first lookup into that chunk's id range. It holds the compressed
bytes from the cache and decompresses a chunk the first time a row in it is asked for. A client that walks a
30-slot inventory touches at most a handful of chunks, which is the whole reason the cold start budget is a
download budget (section 14, P4).

The two paths share one reader and differ only in when they call it, so there is one decoder and one set of
reason tokens.

### 9.4 Derived indexes, built once at load

Four are built by the ENGINE at load and none is built lazily, because each is walked inside gameplay and a
lazy build inside a tick is a latency spike:

| Index | Shape | Who reads it |
|---|---|---|
| Key to id, per type | The open-addressed `int[]` of 9.1, hashed on the UTF-8 key slice | Admin lookup, world boot resolution (3.10), bulk import. |
| Tag to ids | Per tag id, a sorted `int[]` per content type | Drop tables with a `required_tags` filter (3.5), store rates by item class. |
| Family membership | The block list per family, cached | The two-comparison membership test of contracts 5.2. |
| Loot candidate arrays | Per `loot_table`, the resolved entry list with weights prefixed-summed | The roll, so a weighted draw is one binary search over an `int[]`. |

The prefix-summed weight array is the one that matters for the tick: a weighted draw over `n` entries becomes
`NextInt(0, total)` plus a binary search, with no allocation and no per-roll summation. The random source is
`IRandomSource` handed in by constructor (contracts 14.4, `KhaozEngine.Primitives`), never an ambient static
and never a default.

**Those four are the engine's, and they are not the only indexes a boot builds.** Scope B derives mod
candidate tables at load, on the order of 25 MB and low hundreds of milliseconds, and a game with a large
custom type will want the same. With no hook they would be built lazily inside the first roll, which is the
latency spike this whole section exists to refuse, or by a second pass in the host that nobody specified and
that runs after the validator. So a type registers an index BESIDE its codec:

```csharp
public interface IContentLoadIndex
{
    ContentTypeId Type { get; }                 // the type whose rows it is derived from
    void Build(IContentSnapshot snapshot);      // called once, at boot step 7b
}
```

Registered through `RegisterContentType(..., loadIndex: ...)`, one per type at most, held by the runtime and
handed back through a typed accessor the registering code owns. The rules that make it safe are the ones the
engine's own four already follow:

- **It runs at step 7b, AFTER the engine's four and BEFORE the validator.** After the four, because an index
  over rows will want the key lookup and the tag lists. Before the validator, because a type whose index
  cannot be built has content the validator should be given the chance to explain, and because a boot that
  builds indexes after validating would have the validator pass and the boot fail afterwards.
- **In type id order**, so the order is deterministic and an index over engine rows is built before one over
  Scope B rows. An index MAY read another type's rows through the snapshot. It may NOT read another index,
  which would make the order load bearing in a way a registration list cannot express.
- **It throws to fail the boot closed**, with the exit path of 9.6, rather than returning a partial index.
- **It is built once and is immutable after**, like everything else in the runtime, and it is rebuilt wholesale
  beside the old one when a version changes, which is the same `Volatile.Write` swap as 9.7.

**Its cost is not inside P3.** Section 14 says so on P3's own row and P11 is the composed number.

### 9.5 Boot order

```
1. Register every content type.                                  (registry not yet frozen)
2. Resolve the version: config pin, else catalog_metadata.pinned_version, else active_version (10.7).
3. Fetch that version's server manifest, verify its hash AND its embedded versionNumber.
4. Refuse if manifest.formatGeneration > ContentPackFormat.Generation.
5. Refuse if localServerBuild < manifest.minimumServerBuild.
6. Fetch and verify every chunk the manifest names. Freeze the registry.
7. Decode into ContentRuntime. Build the four engine derived indexes.
7b. Build every registered IContentLoadIndex, in type id order.   (section 9.4)
8. Run the validator on the decoded snapshot plus the rule set.   (section 5.4)
9. Publish the runtime: Volatile.Write of the new instance into the single field.
10. Load the world document.
11. Resolve every world-to-content key against the runtime.        (section 3.10)
12. Build the connect door with the content layer.                (section 8.5)
13. Start accepting connections.
```

**Step 2 takes the version from exactly one place and step 3 cross-checks it.** The precedence is section
10.7's and is not restated here. The cross-check is contracts 7.4's `versionNumber` at manifest offset 8: a
manifest whose embedded number differs from the version the boot resolved means the pointer and the pack
disagree, which is a store misconfiguration or a hand-copied file, and it is refused rather than served
(section 9.6). Without it the server would announce one version number at the door while serving another
version's chunks, and every client would compare hashes correctly against the wrong number.

**Content loads BEFORE the world and both load before the door opens.** Content first, because step 11 needs
it and because a world that references content the pack does not carry is a boot failure that should name the
missing key rather than a null reference inside a tick. The door last, because contracts 7.5 puts the content
layer inside the world layer, and a door built before the runtime exists would have nothing to compare.

### 9.6 Fail closed, and the exact exit path

A missing or invalid active content version FAILS THE BOOT. There is no runtime fallback to code defaults
(#882 body item 8, contracts 10.5).

| Failure at step | Exit code | stderr |
|---|---|---|
| 2, no active version | 3 | `content: no active version. Publish one or set the pinned version.` |
| 3, manifest absent or hash mismatch | 3 | `content: manifest <hash> <absent or hash mismatch> from <store>.` |
| 3, manifest names a different version | 3 | `content: manifest <hash> declares version <n>, expected <v>.` |
| 4, generation too new | 3 | `content: pack generation <n> needs a newer server. This build reads <m>.` |
| 5, server build too old | 3 | `content: version <v> requires server build <n>. This build is <m>.` |
| 6, a chunk absent or mismatched | 3 | `content: chunk <hash> <reason>.` |
| 6, the manifest names an unregistered type | 3 | `content: version <v> names type <id> which this build does not register.` |
| 6, a registered type is absent from the manifest | 3 | `content: type <key> (<id>) is registered and absent from version <v>.` |
| 7, a chunk decode failure | 3 | `content: chunk <hash> <reason token>.` |
| 7b, a registered load index threw | 3 | `content: load index for type <key> (<id>) failed: <message>.` |
| 8, validator findings | 3 | one line per finding, `content: <code> <type>/<id> <message>`, then `content: <n> findings, refusing to serve.` |
| 11, unresolved world key | 3 | `content: world <source> references <typeKey>.<contentKey> which is not live in version <v>.` |

Exit code 3 throughout, distinct from the 2 both Grimhollow heads already return for a bad skilling config
(`b-grimhollow.md:389-391`), so an operator or a supervisor script can tell a content failure from a config
failure without parsing text.

**The two type-registration rows are both refusals, in both directions, and the mirror case is the one worth
arguing.** A manifest naming a type this build does not register is the ordinary shape of a server rolled back
past a game type, or a type behind a feature flag that is off: there is no codec to decode its rows with, and
carrying the chunks undecoded would mean a runtime that cannot answer a reference into them. A REGISTERED type
absent from the manifest is the same failure seen from the other side, a build ahead of the pack rather than
behind it, and the tempting answer, an empty runtime table, is worse than a refusal: every reference into that
type then resolves to nothing, `KEC0006` fires at boot for each one, and the operator reads a page of
reference findings instead of one line naming the type that is missing. Both refuse at step 6, before a single
chunk is fetched, because the manifest's type list is enough to decide it.

This is stated as a hard rule because the consumer precedent is the opposite. Ruinborne's loader falls back to
five hardcoded code defaults on any failure, announces it with a `Console.WriteLine`, and serves a different
catalog than the database holds with no metric, no exit code and no refusal to admit joins
(`c-ruinborne.md:176-196`, Ruinborne [#512](https://github.com/APKiwiOrg/Ruinborne/issues/512)). A silent
fallback catalog is worse than an outage, because an outage is noticed.

### 9.7 The atomic swap, and why there is no lock

The runtime is one field:

```csharp
private ContentRuntime? current;
public ContentRuntime Current => Volatile.Read(ref current)
    ?? throw new InvalidOperationException("content runtime not loaded");
```

A reader takes the reference ONCE at the top of whatever it is doing and uses that instance throughout, so a
swap mid-operation cannot give it a half-old half-new answer. `ContentRuntime` and everything reachable from
it is immutable after construction: the arrays are never written after the load, so there is no torn read and
no memory barrier needed beyond the `Volatile.Write` that publishes the new instance.

v1 never swaps at runtime, because a new version applies at server restart (contracts 1.3 item 8). The field
and the `Volatile` pair exist anyway, for two reasons: a test swaps a runtime to exercise a fixture, and a
later live-apply phase needs exactly this shape and nothing else. The cost of building it now is two lines.

## 10. Authoring API for game consoles

### 10.1 It is registered actions, and there is no new transport

`ServerAdmin.RegisterAction` is the whole extension mechanism and it already exists
(`KhaozEngine.NetWorld/ServerAdmin.cs:101` async, `:114` sync). `AdminHttpServer` dispatches `GET /actions`,
`GET /actions/{name}` with a null payload and `POST /actions/{name}` with an optional JSON body
(`KhaozEngine.Server.Admin/AdminHttpServer.cs:136-152`). A content authoring API is registered actions on
that surface, with no new package and no new transport, which is the engine survey's own conclusion
(`a-engine.md:1027-1034`).

An engine-side helper, `CatalogAdminActions.Register(ServerAdmin admin, IContentAuthoringStore store,
ContentTypeRegistry registry)`, registers all SIXTEEN. A game calls it once beside its own registrations, the
way Grimhollow registers its inspection actions today
(`Grimhollow.Server/Admin/AdminInspectionActions.cs:63-65`, `b-grimhollow.md:878-899`).

**The threading contract is load bearing and this API honours it.** A handler runs on the HTTP REQUEST THREAD
and "must never touch simulation state directly: enqueue mutations to the host thread and return published
snapshots for reads" (`ServerAdmin.cs:92-95`). Every action here touches the AUTHORING STORE and never the
simulation, so it is compliant by construction: the authoring store is a database the tick loop does not read,
and the runtime the tick loop does read is immutable and only replaced at boot.

Action names match `^[a-z0-9][a-z0-9-]{0,63}$`, enforced at `ServerAdmin.cs:127-130`, and a duplicate
registration throws.

**One engine change IS required, and it is not free.** "No new transport" is true of the listener, the TLS,
the auth and the routing, and false of the RESULT type. `AdminActionStatus` today is exactly
`{ Ok, Accepted, BadRequest }` (`KhaozEngine.NetWorld/AdminActionResult.cs:10-20`),
`AdminActionResult.BadRequest(string error)` carries a bare string, and `DispatchActionAsync` maps
`BadRequest` to `Results.BadRequest(new { error = result.Error })` with everything else falling to 500
(`KhaozEngine.Server.Admin/AdminHttpServer.cs:156-172`). Section 10.2 assigns 409 to five actions and section
10.6 returns a 400 body carrying a `findings` ARRAY and a 409 body carrying `expectedBaseVersion`,
`blockedByRules` and `remedy`. Neither is expressible today: every 409 in this spec would be a 500 and every
structured body would collapse to one string. So phase 1 milestone 1.4 ships three small additions to two
existing packages:

- `AdminActionStatus.Conflict` in `KhaozEngine.NetWorld`, and an `AdminActionResult.Conflict(object payload)`
  factory beside `BadRequest`.
- An object-carrying error payload on `AdminActionResult`, so `BadRequest` can return a document rather than a
  message. The string overload stays and keeps its shape, because every existing caller uses it.
- The matching arm in `AdminHttpServer.DispatchActionAsync`, mapping `Conflict` to
  `Results.Json(payload, statusCode: 409)` and the object-carrying `BadRequest` to the same at 400.

This is additive and breaks no caller, which is why it is a milestone item rather than a CCR: contracts 10.4
and 10.5 say nothing about the admin result type. It is named here because the spec's entire answer to two
named failure modes rides on it. `expectedBaseVersion` optimistic concurrency (section 10.6) is what section
11 row 4 resolves with, and the empty-database 409 (section 10.9) is what the Ruinborne seeding class resolves
with. An unnamed engine change is an unbudgeted one.

### 10.2 The sixteen actions

| Action | Verb | Status codes | Audit action |
|---|---|---|---|
| `catalog-schema` | GET | 200 | none |
| `catalog-list` | POST | 200, 400 | none |
| `catalog-get` | POST | 200, 400 | none |
| `catalog-edit` | POST | 200, 400, 409 | `draft-edit` |
| `catalog-draft` | GET | 200 | none |
| `catalog-discard` | POST | 200, 409 | `draft-discard` |
| `catalog-validate` | POST | 200, 400 | none |
| `catalog-diff` | POST | 200, 400 | none |
| `catalog-publish` | POST | 200, 400, 409 | `publish` |
| `catalog-versions` | GET | 200 | none |
| `catalog-pin` | POST | 200, 400 | `pin` |
| `catalog-rollback` | POST | 200, 400, 409 | `rollback` |
| `catalog-import` | POST | 200, 400, 409 | `bulk-import` |
| `catalog-export` | POST | 200, 400 | none |

**There is one action count in this spec and it is SIXTEEN**, the number of names `ServerAdmin` holds after
`CatalogAdminActions.Register` returns. Fourteen are in the table above and two more are the operational pair
of section 10.11, `catalog-sweep` and `catalog-verify`. Earlier drafts counted eleven in one place, fourteen
in another and sixteen nowhere, by collapsing pairs that a caller still has to know the names of, so the
pairing is now a reading aid rather than a count: `catalog-draft` with `catalog-discard`, `catalog-pin` with
`catalog-rollback`, `catalog-import` with `catalog-export` and `catalog-sweep` with `catalog-verify`.

The status conventions are `AdminHttpServer`'s own: reads return `Results.Json` and a bad request body or a
bad id returns 400 with `new { error = ... }`, both in the action dispatch at `AdminHttpServer.cs:156-172`.
The 501 arms are NOT on that path: they are on the four BUILT-IN routes, `/accounts`, `/bans`, `/ban` and
`/unban`, each gated on an `admin.*Supported` capability flag (`AdminHttpServer.cs:100`, `:105`, `:111`,
`:128`). A registered action that does not exist is a 404 from `TryGetAction`, not a 501, so no content action
returns 501 and none of them is listed as returning one. The 409 column is the engine change named in section
10.1.

**None of these returns 202 Accepted**, which is a deliberate departure from the built-in mutation routes. A
202 means the command was enqueued to the host thread and will complete
(`AdminHttpServer.cs:107-108`). A content edit completes INSIDE the request against the database, so the
operator gets the real answer rather than an optimistic one. Ruinborne's console reporting success on a row
the server then rejects at boot is exactly the failure a 202 invites here
(`c-ruinborne.md:364-369`).

### 10.3 `catalog-schema`, the action the generic editor is built on

`GET /admin/actions/catalog-schema` returns the full registered schema, which is what makes ONE editor render
every type including one the console has never heard of (contracts 4.7, first consumer).

```json
{
  "generation": 1,
  "types": [
    { "typeId": 2, "typeKey": "item", "visibility": "Client", "chunkSlots": 1024,
      "fields": [
        { "name": "name",      "kind": "LocalizedTextKey", "target": null,  "visibility": "Client", "required": true, "derived": true },
        { "name": "tags",      "kind": "TagList",          "target": "tag", "visibility": "Client", "required": false },
        { "name": "stackable", "kind": "Bool",             "target": null,  "visibility": "Client", "required": true  },
        { "name": "value",     "kind": "ScaledInt", "scale": 1, "target": null, "visibility": "Client", "required": true }
      ] } ] }
}
```

A console renders a READ-ONLY display for `LocalizedTextKey`, showing the derived key and, when the console
has the language loaded, the string it currently resolves to. There is no text box, because the value is not
the console's to set: the key is derived from the row (section 3.2) and the string is the localization
catalog's. `"derived": true` on the field is what tells a generic editor that, without it having to special
case a kind name. A console that wants to offer translation editing edits the TEXT side, which is a separate
surface this spec does not define in v1. It renders a checkbox for `Bool`, a numeric with a scale hint for
`ScaledInt`, a typeahead over the target type's keys for `KeyReference`, and a multi-select over the `tag`
type for `TagList`. It also validates CLIENT SIDE against the same schema, so an obviously bad value never
reaches the server, and the server validates again because a client-side check is a convenience and never a
gate.

This is what closes Ruinborne #510, `item_stat` and `item_ability_modifier` having no admin page at all so
per-item stats are hand SQL only (`c-ruinborne.md:334-339`,
[#510](https://github.com/APKiwiOrg/Ruinborne/issues/510)). Under this design a type gets its editor by
registering, so there is no such thing as a content type with no page.

### 10.4 `catalog-list` and `catalog-get`

```json
// request  POST /admin/actions/catalog-list
{ "typeKey": "item", "version": 0, "keyPrefix": "stone", "includeRetired": false,
  "skip": 0, "take": 200 }

// response 200
{ "version": 47, "total": 35, "rows": [
    { "id": 13, "key": "stone_sword", "retired": false, "validFrom": 41,
      "fields": { "name": "item.stone_sword.name", "stackable": false, "value": 42 } } ] }
```

The `name` in that response is DERIVED at read time and is not a stored value (section 3.2). It is returned
because a console lists rows by display name, and it is returned read only.

`version: 0` means the current live set, which is the active version when one exists and the DRAFT-APPLIED set
when a draft is open. `take` is capped at 500 server side and the response carries `total`, which is what
makes a console page rather than fetch a million rows. Ruinborne's bag silently showing only the first 30
stacks with no indication ([#233](https://github.com/APKiwiOrg/Ruinborne/issues/233), closed) is the shape
this cap exists to avoid repeating.

`catalog-get` takes `{ "typeKey", "id" }` or `{ "typeKey", "key" }` and returns one row plus its full version
HISTORY, which is the temporal model's payoff: an operator asking "when did this price change and what was it
before" gets an answer from the row table rather than from an audit reconstruction.

**The int id is shown to authors, read-only beside the key.** That is gate 0 decision 3, and the reason is
that an operator reading a quarantine reason or a log line needs to look the id up.

### 10.5 `catalog-edit`, `catalog-draft`, `catalog-discard`

```json
// request  POST /admin/actions/catalog-edit
{ "operator": "oid:8f2c...", "note": "autumn price pass",
  "edits": [
    { "op": "update", "typeKey": "item", "id": 13, "fields": { "value": 45 } },
    { "op": "add",    "typeKey": "item", "key": "iron_sword",
      "family": "swords",
      "fields": { "stackable": false, "max_stack": 1,
                  "tradable": true, "value": 120, "tags": ["metal", "two_handed"] } },
    { "op": "retire", "typeKey": "item", "id": 25, "policy": "replacement", "replacementKey": "oak_shield_v2" },
    { "op": "fork",   "typeKey": "mod", "id": 412, "forkKey": "added_fire_damage_legacy",
      "flagField": "legacy",
      "fields": { "tier_1_max": 60 } } ] }

// response 200
{ "draft": { "baseVersion": 47, "editCount": 12, "openedBy": "oid:8f2c...", "openedAtUtc": "..." },
  "applied": 3 }

// response 400
{ "error": "content edit refused", "findings": [
    { "code": "KEC0004", "type": "item", "id": 13, "message": "no field 'valeu' on type 'item'" } ] }
```

Every edit in one request is applied in ONE database transaction or none of them is, so a batch save from a
grid is atomic. The edits are checked against the schema AT THE BOUNDARY (section 3.7) and the response
carries every finding rather than the first, matching the validator's own accumulate-to-the-end rule.

**No edit ever carries a localized text key**, which is why the `add` above sets no `name` even though the
schema marks it required. The key is derived from the type key, the row key and the field name (section 3.2),
so an edit that named one could only either repeat the derivation or contradict it. A payload carrying a
marker field is refused with `KEC0004`, the same finding as any other field the schema does not accept a value
for, and the message names the derived key so an operator sees what they were trying to set.

A `retire` names its policy as `placeholder` or `replacement`, matching contracts 8.2's payload byte, and a
`replacement` policy without a resolvable `replacementKey` is a 400 with `KEC0017`.

**`fork` is an `op` value rather than a seventeenth action**, because it is an edit against the open draft
like the other three and it is saved, validated, diffed and published through the same path. Adding an action
would have split the draft's own vocabulary across two endpoints for no gain, and the action count in 10.2 is
a number this spec states once and means.

Its `fields` are the changes to the ORIGINAL row, which is the direction that reads correctly at the console:
the author is editing the mod they have open, and the fork is how they say "keep what players already rolled".
`forkKey` is the copy's key and is required, because a key is immutable once published (5.3) and the engine
will not invent one. `flagField` names the `Bool` field the copy gets set to true. A fork whose `forkKey` is
taken, whose `flagField` is absent from the type's schema or is not `Bool`, or whose `id` names a row that is
already retired, is a 400 with `KEC0041`. The response is the ordinary draft response: the copy's allocated id
does not appear in it, because ids are allocated at publish (6.3) and reporting one from an edit would be
reporting a number that does not exist yet.

`catalog-draft` returns the open draft with its edits expanded, so a console can show a pending-changes panel.
`catalog-discard` deletes the draft and writes one `draft-discard` audit row carrying the edit count, so a
discarded draft leaves a trace. It is a 409 while a publish is in flight.

### 10.6 `catalog-validate`, `catalog-diff`, `catalog-publish`

```json
// request  POST /admin/actions/catalog-validate   (no body needed)
// response 200
{ "valid": false, "findingCount": 2, "findings": [
    { "code": "KEC0005", "type": "item", "id": 61, "message": "required field 'tradable' is absent" },
    { "code": "KEC0006", "type": "loot_entry", "id": 418, "message": "field 'item' references item id 25, retired in version 46" } ] }
```

`catalog-validate` builds the candidate and runs the full section 5 sweep WITHOUT allocating ids and without
writing anything. That is the dry run an operator runs before a publish, and it is the same validator, so a
green validate followed by a red publish can only mean the draft changed in between.

```json
// request  POST /admin/actions/catalog-diff
{ "from": 46, "to": 0 }            // 0 means the draft-applied candidate

// response 200
{ "from": 46, "to": null, "changes": [
    { "type": "item", "id": 13, "key": "stone_sword", "op": "update",
      "fields": [ { "field": "value", "before": "42", "after": "45" } ] },
    { "type": "item", "id": 61, "key": "iron_sword", "op": "add", "fields": [ ... ] } ],
  "chunkSummary": [ { "type": "item", "changedChunks": 1, "totalChunks": 13 } ] }
```

The diff is FIELD LEVEL, computed over `catalog_row_field` (contracts 4.7, third consumer), so "what changed
between version 46 and 47" is a field-level answer rather than a chunk hash inequality. `chunkSummary` is what
lets an operator see the download cost of their edit before they publish it, which is the operator-facing half
of the one-item-edit budget (section 14, P6).

```json
// request  POST /admin/actions/catalog-publish
{ "operator": "oid:8f2c...", "note": "autumn price pass",
  "minimumServerBuild": 4120, "minimumClientBuild": 4118,
  "expectedBaseVersion": 47 }

// response 200
{ "version": 48, "serverManifestHash": "9f3c...", "clientManifestHash": "1a7d...",
  "formatGeneration": 1, "chunksWritten": 2, "chunksReused": 63,
  "bytesWritten": 41207, "rulesAppended": 1, "elapsedMs": 830 }

// response 409
{ "error": "base version moved", "expectedBaseVersion": 47, "actualBaseVersion": 48 }
```

`expectedBaseVersion` is optimistic concurrency and it is REQUIRED. Two consoles cannot both publish the same
draft, because the second one's expectation is stale and it gets a 409 naming both numbers. That is the same
shape as `JournalStreamMutation.ExpectedVersion` (`a-engine.md:424-427`), and it turns section 11 row 4 from a
race into an error message.

`minimumServerBuild` and `minimumClientBuild` are CONSUMER-SUPPLIED and the engine never interprets them
beyond comparing (contracts 7.4). The default when omitted is to carry FORWARD the previous version's values,
so a publisher who has nothing to say about builds says nothing rather than accidentally resetting them to 0.

### 10.7 `catalog-versions` and `catalog-pin`

```json
// response  GET /admin/actions/catalog-versions
{ "activeVersion": 48, "pinnedVersion": null, "versions": [
    { "version": 48, "serverManifestHash": "9f3c...", "clientManifestHash": "1a7d...",
      "minimumServerBuild": 4120, "minimumClientBuild": 4118, "formatGeneration": 1,
      "publishedBy": "oid:8f2c...", "operator": "ana", "note": "autumn price pass",
      "publishedAtUtc": "...", "baseVersion": 47 } ] }
```

`catalog-pin` takes `{ "version": 47 }` or `{ "version": null }` and writes `catalog_metadata.pinned_version`
(section 4.4). That is the only lever v1 gives an operator between publishing and restarting, and it exists
because there is no staging environment (contracts 1.3 item 9): pinning is how a publish is staged for a later
restart, and unpinning is how a server catches up.

**Which version a server boots has ONE order of precedence, and it is written here once.** Boot step 2
(section 9.5) resolves it in this order and takes the first that answers:

1. A version pinned in the SERVER'S OWN CONFIG. Config wins, always.
2. Otherwise `catalog_metadata.pinned_version`, when it is not null.
3. Otherwise `catalog_metadata.active_version`.

**When both pins are present, config wins and the ACTION SAYS SO.** A `catalog-pin` against a server whose
config already pins a version returns 200 carrying `"configPinnedVersion": 52` and a warning naming it. The
write happened and it takes effect the moment the config pin is removed, so the response is not a refusal, but
an operator who gets a bare 200 for a call with no effect on the next restart is precisely the failure this
section exists to prevent. Rule 1 is also why a server configured with a pinned version and a pack store needs
no authoring database at boot at all (section 11 row 5): it never reaches rules 2 or 3.

A pin naming a version that does not exist is a 400. A pin naming a version whose `minimumServerBuild` exceeds
the running build is ACCEPTED with a warning in the response, because the operator may be pinning ahead of a
server upgrade on purpose, and the boot-time check (section 9.6) is the real gate.

### 10.8 `catalog-rollback`

```json
// request  POST /admin/actions/catalog-rollback
{ "operator": "oid:8f2c...", "toVersion": 46, "note": "revert the autumn pass" }

// response 200
{ "draftCreated": true, "editCount": 18, "blockedByRules": [] }

// response 409
{ "error": "rollback blocked by an irreversible retire", "code": "KEC0039",
  "blockedByRules": [ { "sequence": 31, "introducedIn": 47, "type": "item", "fromId": 25, "kind": "Retired" } ],
  "remedy": "mint a new definition carrying the old values and add a ReplacedBy rule" }
```

Rollback BUILDS A DRAFT rather than publishing directly (section 6.13). The operator then reviews the diff and
publishes it, which is what makes a rollback reviewable rather than a second uncontrolled change. The 409 case
is contracts 8.6's irreversibility surfaced as an operator message with the way out named.

### 10.9 `catalog-import` and `catalog-export`, and the empty-database rule

A `ContentBundle` is the whole catalog as one JSON document: a format version, the registered type list with
their schemas, every live row with its id, key and fields, every family with its blocks, and the full remap
rule list. It is the seeding format and the LOSSLESS EXPORT format and there is only one of them. It is not a
database backup, and section 10.9's last paragraph says what is.

**`catalog-import` works into an EMPTY database ONLY, and is refused otherwise** (#882 body item 8, "Seeding
imports a whole bundle into an empty database only"). Empty means `catalog_version` has no rows. A non-empty
database gets a 409 with `{ "error": "catalog is not empty", "activeVersion": 48 }` and no partial write.

That single rule is the answer to Ruinborne's entire seeding class of defects, and it is worth naming them
because the rule looks unhelpfully strict until they are on the page:

- Insert-if-absent seeding never reaches an existing row, so PostDeploy carries a growing set of guarded
  `UPDATE ... WHERE column = <old literal>` corrections that knowingly revert an operator value. The file says
  so in its own comments: an operator who retuned a number "would see it reverted on the next deploy, the same
  limit every other numeric correction in this file already accepts" (`c-ruinborne.md:197-220`, Ruinborne
  [#111](https://github.com/APKiwiOrg/Ruinborne/issues/111)).
- Grimhollow's economy has the same shape from the other side: a stored row WINS over a changed default
  forever, so a `GrimhollowEconomyMigration` one-time reset had to exist to push the owner's approved values
  onto a live database once (`b-grimhollow.md:304-319`).

Both are the same defect: a seed that runs repeatedly against live data. An import that runs ONCE into an
empty database cannot have it. A deployed database's values change through `catalog-edit` and `catalog-publish`
and through nothing else, ever.

**A bundle row's id is OPTIONAL, and that is the whole of the import id rule.** It is stated here and in
section 6.3 and nowhere else, because two mechanisms described in two places is how this became contradictory
in the first place:

- A row that NAMES an id is imported with it. `catalog-import` writes an `Add` edit carrying the
  `definition_id`, step 3 of the publish skips allocation for it, and the type's high-water marks are seeded
  from the largest carried id afterwards. This is the case that makes an adoption a no-op for stored data, it
  is contracts 5.1's narrow exception (CCR-2), and it is why the exception is licensed only into an empty
  database: nothing is there to collide with.
- A row that names NO id is allocated one in edit ordinal order, so the bundle's row order determines the ids.

Both run through one path, `catalog-import` producing a draft of `Add` edits and `catalog-publish` publishing
it, and a bundle may mix the two because the id is per row. There is no second import mechanism anywhere.

`catalog-export` takes `{ "version": 48 }` and returns the bundle for that version, ids included. Export at
version `N` then import into an empty database reproduces the same rows with the same keys and the SAME IDS,
because the export carries them and the import keeps them, which is what makes the bundle **a lossless export
rather than an approximation**.

**A lossless export is not a backup, and the difference is the version LINE.** An import runs through
`catalog-publish` and publishes version 1, so the new database's history starts there: the version numbers,
the temporal row generations and every rule's `IntroducedIn` are the new line's, not the old one's. For a
fresh database that is exactly right, and it is the only case `catalog-import` accepts. For a LIVE world it is
not a restore at all, because a durable page carries the version number it was stamped with (contracts 7.2)
and a rule applies to a stamp strictly older than its `IntroducedIn` (contracts 8.3), so a page stamped 46
against a database whose newest version is 1 is newer than every rule there is. **When the version line must
be preserved, the path is an ordinary database restore of the authoring store**, which is the provider's own
tooling, a SQLite file copy or a SQL Server restore, and it is outside this spec because it is outside this
engine. The bundle is for seeding a new database and for reading a version out in one document. The database
backup is for putting a database back.

### 10.10 Operator identity

**The bearer token is ONE token and it is not an identity.** `AdminEndpointOptions.BearerToken` is a single
required string compared constant time as the first middleware (`AdminHttpServer.cs:61-71`,
`AdminEndpointOptions.cs:23`). There is no per-operator layer at the engine endpoint and this spec does not
add one, because adding an identity provider to `KhaozEngine.Server.Admin` would put an authentication stack
into the engine for a problem both consumers have already solved in their own consoles.

So the CONSOLE forwards an operator identity, as an `operator` field on every mutating request body, and the
engine records it in `catalog_audit.operator` beside its own `actor`. The engine does not verify it, and the
audit row says so by keeping both columns: `actor` is what the engine authenticated (the bearer token's
holder) and `operator` is what the console asserted.

Two consumer lessons say why this is the right shape and why the field is required rather than optional:

- **[Ruinborne #371](https://github.com/APKiwiOrg/Ruinborne/issues/371)**, open: its console's owned-item
  verbs hardcode `AdminActors.AdminConsole` on all ten including `grant_item`, `delete_item` and `move_item`,
  so an audit row cannot say which operator did it. Its CONTENT edits do carry a name, and they carry the
  operator's DISPLAY NAME rather than the stable object id, even though a stable `oid:` identity exists in the
  same codebase and the content pages simply do not use it (`c-ruinborne.md:432-437`). So the engine's field
  is documented as taking a STABLE identity, and a console passing a display name gets an audit trail that
  breaks when someone changes their name.
- **[Grimhollow #226](https://github.com/APKiwiOrg/Grimhollow/issues/226)**, open: its console derives the
  item Name column from the config key because `Grimhollow.Shared` cannot reference the client's
  `ItemStrings`, so an operator is not seeing the name a player sees. The survey's matching finding is that
  the only identity available to a write today is the bearer token, so an audit row would name
  `AdminActors.AdminEndpoint` rather than an operator unless the console forwards its Entra identity
  (`b-grimhollow.md:955-960`). Both halves of that issue are things the console cannot fix alone and the
  catalog fixes for free: names become a catalog lookup and identity becomes a forwarded field.

A mutating request with no `operator` field is ACCEPTED and audited with an empty operator, because refusing
it would break a scripted maintenance call that has no human behind it. A request whose `operator` exceeds 128
characters is a 400.

### 10.11 Two operational actions, the fifteenth and sixteenth

`catalog-sweep` runs step 11 of the publish alone (section 6.12), for an operator cleaning up after a crashed
publish. It returns the count deleted and the count skipped and it obeys the same skip-on-listing-failure
rule.

`catalog-verify` walks the active version's manifest, fetches every chunk and rehashes it, and returns the
list of chunks that do not match. It is the detection half of section 11 row 1 and it is what an operator runs
when a runtime decode reason appears in a log. It is read only and it never repairs, because a repair means
deciding which copy is right and only a republish can know that.

## 11. Failure modes and recovery

| # | Failure | Detection | Effect | Recovery |
|---|---|---|---|---|
| 1 | A chunk file on the server's disk is corrupt | Hash mismatch at boot step 6, or `catalog-verify` (10.11) on demand | Boot fails closed, exit 3, the hash and reason on stderr (9.6). A running server is unaffected, its bytes are already decoded and in memory. | Delete the file and refetch from the remote store, or republish the version. The chunk is content addressed, so any copy that hashes correctly is the right one. |
| 2 | A chunk is missing on the client | `missing` is non-empty after the fetch loop (8.7) | The client stays at the connect door showing a fetch-progress notice. It never joins with a partial catalog. | Retry with backoff. If the remote genuinely lacks it, the pack store is broken and an operator runs `catalog-verify` server side. |
| 3 | The manifest hash does not match | The `CachingPackStore` verify on read (8.4) | The manifest is discarded, refetched once, and a second mismatch leaves the client at the door with `manifest-hash-mismatch`. | Operator side. Either a store wrote bytes under the wrong name, which `PutAsync`'s verify (8.1) makes impossible for the engine's own providers, or a cache or proxy is serving stale bytes for a content address, which is a deployment defect. |
| 4 | Two consoles publish concurrently | `expectedBaseVersion` optimistic concurrency (10.6), plus the draft row lock (6.2) | The second publish gets 409 naming both version numbers. On SQL Server a `Serializable` transaction may instead abort with a serialization failure, reported as the same 409. | The second operator refreshes, sees the first publish's diff, and decides. No torn version is possible, because the whole commit is one transaction (4.8). |
| 5 | The authoring database is unreachable at boot | The provider throws on connect at boot step 2 | Boot fails closed, exit 3. | The database is only needed to READ the active version number when the server is configured to take it from the store. A server configured with a pinned version and a pack store needs no database at boot at all, which is the deployment this design recommends for a game server: the authoring database is a TOOLING dependency, not a runtime one. |
| 6 | A client download is interrupted part way | The next `missing` recomputation (8.7) | Nothing. The chunks that arrived are cached and verified. | Resume by recomputing `missing`. There is no resume state beyond the cache, because a chunk is atomic. |
| 7 | Client cache poisoning: a local file is replaced with attacker bytes | Hash verify on every read from the cache (8.4) | The entry is discarded and refetched. | Automatic. This is the reason the cache verifies on READ and not only on write. Section 13.5 spends what a local attacker can and cannot achieve. |
| 8 | A rollback publishes while clients hold the newer pack | The door's ordinal hash comparison (8.5) | Every client on the newer version is refused with `ke:content-mismatch` carrying both versions. | The client fetches the ROLLBACK version's pack, which is a new version number with mostly-reused chunk hashes, so the download is small, and reconnects. A client holding a newer pack is not special: it is simply on the wrong version. |
| 9 | An id block is exhausted mid-publish | `AllocateInFamilyAsync` finds every block full (4.7) | Nothing torn. A new block is reserved and the publish continues. If the type's id space itself were exhausted, which needs 2.1 billion ids, the allocator throws and the publish aborts at step 3 with nothing written (6.3). | None needed for the common case. The block reservation commits on its own before any id is issued, so a crash between the two leaves a gap and never a duplicate. |
| 10 | A validator bug rejects everything | Every publish returns 400 with the same finding code across unrelated edits | No publish is possible. Nothing durable is damaged, because the validator runs before any write (6.4) and has no side effects (5.1). | The validator is PURE and takes its whole world as an argument, so a failing candidate is exported through `catalog-export` and replayed in a unit test against a fixed build. There is deliberately NO override flag: a publish that bypasses validation is how a bad row reaches a pack, and the repair path is an engine patch, not an operator escape hatch. |
| 11 | A game validator throws | The per-type catch in the sweep (5.3) | One `KEC0040` finding naming the type and the exception message. The other types still validate. | The game fixes its validator. One bad game validator cannot take the publish down with a stack trace instead of a finding. |
| 12 | The pack store is full or read only at publish step 9 | `PutAsync` throws | The publish aborts with nothing committed. Orphan chunk files may exist. | Fix the store, republish. The `ExistsAsync` check makes the retry skip everything already written (6.11). |

Row 10 is the one worth reading twice. The absence of an override is a deliberate cost: it means a validator
bug blocks content authoring until the engine ships a fix. The alternative, a force flag, converts a
correctness gate into a habit, and the whole reason the validator is shared by publish, boot and tests
(contracts 10.4) is so that the answer is the same in all three. A force flag would make boot the only real
gate, and boot fails closed, so the operator would have published a version that cannot be served.

## 12. Versioning and rollback

### 12.1 The four numbers

| Number | Width | Owner | Moves when |
|---|---|---|---|
| Version number | `int`, from 1, plus exactly 1 per publish, never reused, never skipped | The engine | Every publish, including a rollback. |
| Manifest hash | SHA-256 lower hex, 64 characters, one per side | The engine | Any chunk changes, any of the three numbers below changes, or the scheme version bumps. |
| `MinimumServerBuild` and `MinimumClientBuild` | `int`, monotonic | The GAME, supplied per publish | The publisher says so. The engine only compares. |
| `FormatGeneration` | `int`, from 1 | The ENGINE | An engine-owned row codec or the pack format gains a field an older reader cannot skip. |

The version number is the ORDERING and the manifest hash is the IDENTITY, and neither substitutes for the
other (contracts 7.1). Both travel together everywhere a version is named: in the door layer, in the refusal
token, in `catalog-versions` and in the `catalog_version` row.

A durable container page stamps the version NUMBER, never the hash (contracts 7.2, gate 0 decision 5), because
a remap rule applies to any page whose stamp is OLDER than the rule's version and a digest has no order.
That is Scope B's field to carry and this spec only supplies the number.

**`ContentPackFormat.Generation` is bumped by ANY engine change to a pack-carried format, and a new remap rule
KIND is one.** The constant lives in this package and this spec owns it, which makes it this spec's job to say
what moves it rather than leaving each change to judge itself:

- A row codec for an ENGINE type gains a field an older reader cannot skip. Bump.
- The `KECC`, `KECM`, `KECT` or `KECR` layout changes in a way an older reader misreads. Bump.
- A new `RemapRuleKind` is defined, by this spec or by Scope B. **Bump**, and this is the case that is easy to
  get wrong. A rule is carried in the pack, it must fail closed rather than be skipped (contracts 8.5), and the
  generation is the only thing that makes an older server refuse the pack at boot step 4 instead of ignoring a
  rule it does not understand and serving items it has not migrated.
- A durable format that is NOT in the pack does not move it. Scope B's container page version and its
  quarantine wrapper version are its own numbers, on its own rows, and carry their own forward compatibility.

So the rule in one line: if an older server reading this pack would be WRONG rather than merely older, the
generation moves. Scope B does not own the constant and does not bump it on its own initiative, it asks for
the bump in the same change that defines the rule kind, which is the same shape as a contracts amendment.

### 12.2 What a rollback does to ids

**Every id introduced since the target version KEEPS its id and its values** (#882 body item 8). A rollback
does not renumber, does not free ids and does not delete rows. Concretely, rolling version 48 back to 46:

- A row edited in 47 or 48 gets an `Update` restoring the version 46 field values. Its id does not move.
- A row ADDED in 47 or 48 is untouched. It stays live with its id, its key and its values.
- A row RETIRED in 47 or 48 BLOCKS the rollback, with `KEC0039` naming it (section 6.13 step 3). There is no
  un-retire: a retire is irreversible (contracts 8.6) and the way back is a new id carrying the old values.
- The result publishes as version 49.

The reason a rollback does not remove a newly added row is that the id has already been handed out: a player
may own one, a ground stack may carry one, and a journal projection may name one. Contracts 5.1 puts it
directly, an id is never reused and never deleted, and taking one back is deleting it.

### 12.3 What a rollback does to remap rules

Nothing is removed, because rules are append only and the list is a permanent part of every published version
(contracts 8.1). A rollback APPENDS rules only when it needs to move references, which is the
`replacementKey` case, and a plain value rollback appends none.

The consequence worth stating: **a rollback cannot un-retire a definition that pages have already migrated
past** (contracts 8.6). Once a page is brought forward past a `Retired` or `ReplacedBy` rule the old id is
gone from that page, because a remap is a rewrite rather than a log, and the idempotence check `KEC0015`
forbids the rule that would point back. So the way out is to MINT A NEW ID carrying the old values, which is
an ordinary `Add` with a new key under 5.3's immutability rule, plus a `ReplacedBy` rule moving whatever the
owner wants moved onto it.

Pages that were never brought forward need nothing special. They still hold the old id, the full ordered rule
set still applies to them, and they arrive where every other page arrives.

### 12.4 What an operator sees

At publish: the new version number, both hashes, how many chunks were written and how many reused, the bytes
written, the rules appended and the elapsed time (section 10.6).

At rollback: a DRAFT with its edit count and the diff, or a 409 naming every blocking rule with the
mint-new-id remedy spelled out (section 10.8). A rollback is never a single irreversible button.

After publish, before restart: `catalog-versions` shows `activeVersion` ahead of what the running server
loaded. The engine logs one line at Information on the publish, naming the new version and both hashes, so a
log reader can see the gap. There is no pressure to restart, because the running server keeps serving what it
loaded (section 6.10).

At restart: one line at Information naming the version loaded, its hash, its chunk count and its decode time,
which is the shape both consumers' skilling and economy boot lines already take
(`b-grimhollow.md:322-327`, `b-grimhollow.md:386-388`).

On a refusal: the player's client shows a localized notice derived from the stable wire token, which is what
`NoticeStrings.ForRefusal` already does for the four existing refusal kinds
(`b-grimhollow.md:472-484`). The token carries BOTH sides' version numbers, so an operator reading a support
report sees "server 48, client 46" rather than two digests to diff. That is the operator-legibility property
Grimhollow's hyphen-joined game data hash was built for, improved (contracts 7.6).

## 13. Security and exploit analysis

### 13.1 The client half is public data

Everything in a client manifest is data the client is meant to have, so **nothing secret reaches a client
chunk**. That is not a hope, it is two mechanisms in order. A field marked `ServerOnly` on a `Client` type is
OMITTED from the client-side encode by construction, which is contracts 11.3's own wording and section 6.7's
two-hash chunk. `KEC0014` then refuses, as a publish ERROR rather than a warning, any publish whose
client-side bytes still carry such a field. The omission is the design and the finding is the proof it
happened, so a codec that forgets a field's visibility fails the publish instead of shipping the value to
every player. What `KEC0014` does NOT refuse is the per-field override itself: a `Client` type carrying a
`ServerOnly` field is legal and is how a public row holds a private number.

The two families that are `ServerOnly` at the TYPE level, so no client ever sees a row of them at all, are
`loot_table` and `loot_entry` (section 3.5). Drop rates are the motivating case and the owner put drop tables
in the same versioned content as items on purpose (#882 comment 2).

A game registering its own type sets its own default visibility and the engine does not second guess it.
Grimhollow's `store` type is `ServerOnly` for its RATES and its shelf list is `Client`, which is why they are
two types (section 3.6): visibility is per type and per field, and the cleanest way to hold a public list
beside a private rate is to split them.

### 13.2 Integrity is by hash, end to end

Every object in the pack is addressed by the digest of its own canonical bytes, and every read verifies
(sections 7.8, 8.4). A tampered chunk does not hash to its own name, so it is not the chunk. There is no
signature and no key, deliberately: a content address IS the integrity check as long as the NAME arrives over
a channel the attacker does not control, and the name arrives inside the connect handshake from the
authenticated server (section 8.5).

That is the whole trust chain, stated once: **the client trusts the server's manifest hash because it came
over the game connection, and it trusts every byte after that because every byte hashes to a name the
manifest gave it.** A CDN in the middle is untrusted by construction and needs no credential.

This is why the refusal token must never carry a URL (section 8.5). A URL in a token is a redirect the peer
controls, and a client that fetched from it would be fetching attacker-chosen bytes under attacker-chosen
names. The hash chain would still refuse them, but the client would have made a request to an attacker-chosen
host, which is a server-side request forgery primitive handed out for free.

### 13.3 Authoring is behind TLS, a bearer token and an operator identity

`AdminHttpServer` makes TLS mandatory (`AdminHttpServer.cs:57`, `Certificate` is `required`) and compares the
bearer token constant time as the FIRST middleware before any route (`:61-71`). It binds to
`IPAddress.Loopback` by default (`AdminEndpointOptions.cs:29`) and carries pre-auth limits because the TLS
handshake completes before the bearer check: `MaxConcurrentConnections` 64, `RequestHeadersTimeout` 10 s,
`KeepAliveTimeout` 30 s (`AdminEndpointOptions.cs:39-51`). The content actions inherit all of it and add
nothing, which is the point of registering rather than opening a second listener.

Three Ruinborne admin lessons are ADDRESSED by the design rather than by a control, and they are worth naming
because each is an OPEN issue whose fix is structural. Two are resolved and the third is improved, which the
bullets say individually:

- [Ruinborne #506](https://github.com/APKiwiOrg/Ruinborne/issues/506), `UpsertItemDefAsync` has no domain gate
  so the console can save one bad row that reverts the whole item catalog at boot. Answered by validating at
  the API boundary (section 3.7) and again at publish (section 6.4) and again at boot (section 9.5), and by
  boot failing closed rather than falling back (section 9.6).
- [Ruinborne #509](https://github.com/APKiwiOrg/Ruinborne/issues/509), content-edit audit rows record no
  field, no old value and no new value. IMPROVED rather than resolved. The field-level audit through the schema
  (section 4.6) fixes the half the issue is titled for, one row per changed field with a before and an after,
  and it is the contracts' own second consumer for the field schema existing at all (contracts 4.7). The WHO
  half is only improved, for the reason below.
- [Ruinborne #512](https://github.com/APKiwiOrg/Ruinborne/issues/512), a catalog load failure is a
  `Console.WriteLine` with no metric and no refusal to boot, so players see a stale catalog. Answered by exit
  code 3 with findings on stderr (section 9.6) and by there being no code-default catalog to fall back TO.

**The `operator` field is an audit AID among cooperating operators, not an authentication control.** The engine
does not verify it, a request that omits it is accepted and audited with an empty operator, and any holder of
the single bearer token can put any 128-character string in it including somebody else's object id (section
10.10). So `catalog_audit.operator` is caller-asserted data, and a forged or absent value is indistinguishable
from a true one in the table. That is a stated design limit rather than a defect, because the alternative is
an identity provider inside `KhaozEngine.Server.Admin`, and it does constrain what this design may CLAIM: the
audit is accountable to the TOKEN HOLDER, and it distinguishes operators only as far as the operators
cooperate. Ruinborne [#371](https://github.com/APKiwiOrg/Ruinborne/issues/371) asks for exactly the
accountability a shared token cannot give, so this design improves it, by giving the console a stable place to
put a stable identity and by auditing what it sent beside what the engine authenticated, and does not close
it. Closing it needs per-operator credentials at the endpoint, which is a separate piece of work in a
different package.

### 13.4 Rate limits on fetch

Fetch is served by a static host or CDN (section 8.6), so the rate limit is that host's and the engine
specifies only what the CLIENT does: bounded concurrency 4 (section 8.7), one retry per chunk on a hash
mismatch, and exponential backoff on a failed set starting at 1 s and capped at 60 s with full jitter. The
jitter is what stops a thundering herd after a world restart from becoming a synchronized retry storm.

The engine's own `HttpPackStore` sets no credential and follows no redirect. `HttpClientHandler.AllowAutoRedirect`
is set false, because a redirect from a content-addressed store is either a misconfiguration or a
redirection attack, and the hash check would catch the latter only after the request was made.

The admin endpoint serves no chunks (section 8.6), so the engine's only bandwidth-heavy path is the one the
operator's static host owns, and the tick loop is never in it.

### 13.5 What a malicious client can and cannot do

**Cannot:**

- Read a `ServerOnly` chunk. It is not in the client manifest and the client manifest is the only thing it is
  told the hash of. Guessing the hash of a chunk whose contents it does not have is guessing SHA-256.
- Forge a pack. Every byte must hash to a name the server-supplied manifest gave, and the manifest hash comes
  over the authenticated game connection.
- Join with a catalog the server did not publish. The door compares ORDINAL on
  `<versionNumber>|<clientManifestHash>` and refuses on any difference (section 8.5).
- Change what the server believes. The server reads content from its own loaded runtime and never from
  anything a client sends. A client's catalog is a rendering copy, exactly as Grimhollow's economy copy is
  display only (`b-grimhollow.md:336-339`).
- Make the server fetch anything. Fetch is a client-side path and the server loads from its configured store.

**Can:**

- Read every `Client` chunk, including the ones for content its character will never see. Item stats, values
  and names are public by construction, and that is the same position Grimhollow's economy already takes by
  pushing the whole table to every client on join (`b-grimhollow.md:320-339`).
- Modify its own cached pack, and see the result immediately: the cache verifies on read (section 8.4), so a
  modified chunk is discarded and refetched. Deleting the cache costs it a redownload. Neither reaches the
  server.
- Compute a chunk hash and check whether the CDN holds it. That is a membership oracle over content the
  client already has the manifest for, so it leaks nothing.
- Spend the CDN's bandwidth by refetching. That is the CDN's rate limit to enforce and the reason section 8.6
  puts fetch off the game process.

**The residual risk, named:** a client can enumerate every `Client` chunk hash from the manifest and therefore
knows the exact size of every chunk it does not need. That leaks the ROUGH SHAPE of the catalog, for example
that the item type has 977 chunks. It leaks nothing about `ServerOnly` content, because those chunks are
absent from the client manifest entirely rather than present with their hashes withheld.

## 14. Performance budgets

Each row is a TARGET, the method it is measured by, and a `Measured` column filled at stage 5 by the proof
spike #882 item 12 requires. Every target is justified by arithmetic rather than by a feeling.

| # | Budget | Target | How it is measured | Measured |
|---|---|---|---|---|
| P1 | Pack size at 50,000 definitions | Server pack under 12 MB stored, client pack under 9 MB stored | `KhaozEngine.Benchmarks --catalog --definitions 50000`, summing `storedBytes` across the manifest | **2.96 MB** server, **2.66 MB** client. MEETS |
| P2 | Pack size at 1,000,000 definitions | Server pack under 240 MB stored, client pack under 180 MB stored | the same run at `--definitions 1000000` | **58.9 MB** server, **52.9 MB** client. MEETS |
| P3 | Server load time and memory at 50,000 | Under 400 ms wall clock from manifest to validated runtime, under 20 MB of managed heap for the runtime | `Stopwatch` around boot steps 3 to 8, `GC.GetTotalAllocatedBytes` delta and `GC.GetTotalMemory(true)` after | **334 ms**, **12.3 MB** heap. MEETS |
| P4 | Client cold start at 50,000 | Under 6 s on a 20 Mbit link to first joinable, of which under 300 ms is local work | the fetch loop against a local HTTP server with a token-bucket shaper, timed from refusal to reconnect | **1.05 s**, **57 ms** local. MEETS |
| P4b | Client COLD start at 1,000,000 | Under 90 s on a 20 Mbit link to first joinable. Stated rather than targeted: it is the transfer, and the design cannot beat it | the same run at `--definitions 1000000` | **22.1 s**. MEETS |
| P5 | Publish time, one item edited, 50,000 definitions | Under 1.5 s wall clock end to end | `catalog-publish` elapsed, reported in its own response | **93 ms**. MEETS |
| P6 | Download size after a one-item edit | Under 80 KB, including the manifest | the publish response's `bytesWritten` plus the manifest size | **34.9 KB** at 1,024 slots. MEETS |
| P7 | Lookup by id, server runtime | Under 5 ns, zero allocation | a tight loop over random live ids, `GC.GetAllocatedBytesForCurrentThread` delta asserted 0 | **1.76 ns**, 0 B. MEETS |
| P8 | Validator sweep at 1,000,000 | Under 20 s | `ContentValidator.Validate` timed over a synthetic snapshot | **609 ms**. MEETS |
| P9 | Weighted loot draw | Under 100 ns, zero allocation | a loop over a 200-entry table through the prefix-summed array (9.4) | **33.8 ns**, 0 B. MEETS |
| P10 | Text chunk decode, one language at 50,000 | Under 250 ms, under 32 MB resident | decode timed, `GC.GetTotalMemory(true)` after | **15.8 ms**, **9.2 MB** resident. MEETS |
| P11 | **Total cold boot to accepting connections** | Under 1.5 s wall clock, under 80 MB of managed heap, at 50,000 definitions with Scope B's types registered | `KhaozEngine.Benchmarks --catalog --compose`, timed from process start to the listener opening, `GC.GetTotalMemory(true)` after | **395 ms**, **21.5 MB** heap. MEETS |

**P3 EXCLUDES the per-type load indexes, and P11 is the number an operator actually restarts against.** P3 is
scoped to boot steps 3 to 8, which is the engine's own work: decompress, decode, validate and build the four
derived indexes of 9.4. It does NOT include the text decode of P10, and since 9.5 step 7b it does not include
any index a Scope B or game type registers through `IContentLoadIndex` either. Those are real seconds on a
real restart, so a spec that only ever published P3 would be telling an operator 400 ms for a boot that takes
three times that. P11 is the composed row and it is measured in the SAME harness rather than added up on
paper, because the three terms share a heap and a file cache and summing them on paper gets both numbers
wrong in opposite directions.

### 14.1 Where the numbers come from

**P1 and P2, pack size.** The item base row of section 3.3 encodes to about 120 bytes: three asset references
averaging 28, a tag list of 4 tags at 5 bytes, and eleven ints averaging 2 bytes as varints, plus the 6-byte
row table entry. Its `name` and `examine` contribute NOTHING, because a localized text key is a derived marker
with no bytes in the row (section 3.2). Rounded to 130 for the fields a game adds, that is 6.5 MB uncompressed
for the item type at 50,000.

The second largest type is `loot_entry`, at maybe 4 entries per item averaging 20 bytes, which is 4 MB. That
is 60 percent of the item type and it is NOT "small by comparison", so it is named separately here: the two
small types are `tag` at 200 rows and `stat` at a few hundred, which together are under 50 KB and round to
nothing.

`base_socket` contributes nothing to these figures and that is not an oversight. A game that does not use
instances authors no socket rows at all, which is both adopting consumers today, and a game that does authors
them only on the bases that take sockets: at a socketed tenth of 50,000 bases averaging two sockets each, 10,000
rows of under 10 bytes is under 100 KB. It joins `tag` and `stat` in the round-to-nothing group unless a game
sockets nearly everything, which would be a decision visible in its own authoring long before it showed up
here.

**Text is now the largest single term, and it is computed once in this paragraph.** Every localized field of
every engine type is an entry in the language chunk: `item.name` and `item.examine` at 50,000 rows each,
`tag.name` at 200, `stat.name` and `stat.display_format` at 300 each, which is 100,800 entries. Each entry is
`[keyLen][key][valueLen][value]` (section 7.6), so at a 24-byte derived key and a 60-byte string that is 86
bytes, and the chunk is **about 8.7 MB uncompressed per language.** Section 7.6 and question Q4 use this
number and do not recompute it.

So an uncompressed one-language server pack is about 19 MB, and Brotli at quality 5 on this kind of structured
repetitive text and varint data reliably lands between 2:1 and 4:1. At the conservative 2:1 that is 9.5 MB
stored, so the 12 MB target carries about a quarter of headroom over the pessimistic end on purpose: a miss
against it means something is actually wrong rather than that the compressor had a bad day.

The client pack is smaller by exactly the `ServerOnly` families, which is `loot_table` and `loot_entry`, so
about 15 MB uncompressed and comfortably inside the 9 MB stored target at the same ratio. P2 scales P1 by
twenty, which is linear because every term above is per definition, except that the text chunk cannot scale
linearly as ONE chunk: 2,000,000 entries at 86 bytes is 172 MB against a `MaxChunkUncompressedBytes` of 16
MiB, so sharding is mandatory rather than optional at the stress figure. Section 7.6 states that rule.

**P3, server load time and memory.** The memory table is section 9.2's, about 7.1 MB for the item type at
50,000, and the whole runtime is that plus the five smaller engine types and the derived indexes of 9.4,
which measured 10.7 MB by the runtime's own accounting. The time is
dominated by three linear passes: decompress about 19 MB, decode 50,000 rows, and validate. Brotli
decompresses at well over 100 MB/s, a row decode is a handful of varint reads, and the validator is five
linear passes with two dictionary builds. Four hundred milliseconds gives each pass a generous share and it
is the number that matters operationally, because it is added to every server restart and v1 applies a new
version at restart.

**P4, client cold start.** Nine megabytes over 20 Mbit is 3.6 s of pure transfer. Bounded concurrency 4 and
connection setup put a realistic floor near 4.5 s. Six seconds leaves headroom for the door round trip and
the reconnect, and the 300 ms local-work budget is the manifest parse plus the hash verification of every
chunk, which is 9 MB of SHA-256 at typical throughput of over 1 GB/s on any machine that can run the client.
This is a ONE TIME cost per version per client, because the cache is by hash and a subsequent version reuses
every unchanged chunk.

**P4b, client cold start at 1,000,000, which is the number this spec cannot argue away.** The client pack at
the stress figure is 180 MB stored (P2). At 20 Mbit, which is 2.5 MB/s, that is **72 seconds of pure
transfer** before a player can join, and gate 0 decision 6 forbids admitting the client read only while it
fetches. Ninety seconds is the honest budget: 72 of transfer, the rest for connection setup at bounded
concurrency 4, 180 MB of SHA-256 and the reconnect. There is no design lever inside this spec that moves it,
because it IS the bytes, and the levers that exist are outside it: a smaller catalog, a faster link, or a
client that ships the pack with its installer.

**What P4b is NOT is a restart herd.** Section 8.6's thundering-herd row is about a VERSION CHANGE, and a
version change is not a cold start: the cache is content addressed (section 8.4), so a client already holding
version N fetches only the chunks that differ in version N+1, which is P6 sized and measured in tens of
kilobytes. The 72 seconds is paid once per client per machine, by a player who has never held this catalog,
and a thousand of those arriving together is a CDN sizing question rather than a client one. The two cases
were conflated in an earlier reading of this section and they have nothing in common but the word cold.

**P5, publish time.** One item edited touches ONE chunk of 1,024 slots holding at most 1,024 rows, so the
encode and compress is at most 133 KB of work. Everything else is fixed cost: the candidate build is a query
over the live set, the validator is P8 scaled down by twenty, and the commit is one transaction. The dominant
term at 50,000 is the validator sweep at roughly 1 s by P8's ratio, which is why the target is 1.5 s rather
than 200 ms. If the measurement comes in far under, the validator can stop rebuilding indexes it could
incrementally maintain, and that optimization is deliberately not designed now.

**P6, download size after a one-item edit, and the arithmetic that set `item` to 1,024 slots.** A one-item
edit rewrites exactly one chunk (section 6.6) and the client refetches that chunk plus the manifest, so P6 is
those two numbers and nothing else.

At the 4,096 slots this spec first gave `item`, the chunk is 4,096 rows at about 130 bytes, which is 533 KB
uncompressed and roughly 180 KB stored at a conservative 3:1. Against an 80 KB target that is more than twice
over, and the budget table was publishing a number the section under it refuted.

At 1,024 slots the same chunk is 1,024 rows, 133 KB uncompressed and about 44 KB stored. The manifest is the
other object, at 65 chunk entries of 39 bytes plus the type and language rows, so under 4 KB. **So P6 is about
48 KB and its 80 KB target holds**, with the margin covering a chunk that compresses worse than 3:1.

The cost is manifest entries, four times as many for the item type, which is 49 at 50,000 definitions and 977
at 1,000,000. That is why section 3.1 now registers `item` at 1,024 and every other engine type at 4,096 or
above: `item` is the type edited one row at a time, and the others are not. Question Q3 is answered by this
paragraph rather than deferred to the spike, because contracts 4.5 and section 19 both put `chunkSlots` in the
expensive-after-data column and the spike lands after the decision is needed.

**P7, lookup.** One array index into `Offsets`, one bounds check, one span slice. Five nanoseconds is a
generous ceiling for that on any current hardware and the real value should be under 2. The budget exists to
catch a regression that introduces a dictionary or a lock, not to celebrate the number.

**P8, validator sweep.** Five linear passes over 1,000,000 rows with two dictionary builds at 1,000,000
entries each. A dictionary build at that size is a few hundred milliseconds, a linear pass with a few
comparisons per row is a few tens of milliseconds, and the reference pass does a lookup per reference field.
Twenty seconds is roughly ten times the arithmetic, which is the right margin for a publish-time check that
runs once.

**P8 also carries the `KEC0100` band, and one of its checks is not linear.** Scope B's validators run inside
this sweep (5.3), so their cost is inside this budget, and six of the seven are linear passes over types
whose row counts are in the tens of thousands rather than the millions, which is noise against the figures
above. The seventh is not: its affix-availability check is a cross product over rarities, mod positions and
tags, which at Scope B's own declared ceilings is 255 rarities by 2 positions by 200 tags, so 102,000 cells,
each resolving a candidate list that the same pass has already built once. That is a fraction of a second at
the sizes either spec permits, and it is bounded by the ceilings rather than by the definition count, which
is why P8 does not move for it. It is written down here because a cross product hidden inside a budget
derived from linear passes is exactly the term that surprises someone at ten times the scale.

**P9 and P10** are Scope B adjacent but belong here because the arrays they read are built by this spec. P9's
prefix-summed binary search over 200 entries is 8 comparisons. P10 is the 8.7 MB language chunk computed
above, held the way section 7.6 says a decoded language is held: the decompressed body itself plus an
open-addressed index of entry offsets over it. Its RESIDENT number follows from that directly. The body is
the 8.7 MB, and the index is 4 bytes a bucket at a power-of-two capacity of at least twice the entry count,
so 100,800 entries take 262,144 buckets and about 1 MB, and the bounded cache in front of `Get` adds at most
512 materialised values, about 80 KB. Call it 9.7 MB, and the time is one linear pass plus a hash and a probe
an entry rather than a dictionary build.

**The 32 MB budget was set against the layout this one replaced, and it stays where it is.** The frozen
dictionary of 100,800 UTF-16 pairs computed to about 216 bytes an entry, roughly 25 MB, and the budget was
put at 32 so that the measured value would decide whether to hold the blob instead. It decided: the string
layout measured 460 bytes an entry and 44.2 MB, the blob measures 96 and 9.2 MB (14.3), and the target does
not move because a budget is a ceiling on what may be spent rather than a record of what was.

**P11, the composed cold boot.** The terms are P3 at 400 ms and 20 MB, P10 at 250 ms and 32 MB for one
language, and the registered per-type indexes of step 7b, of which Scope B's generator tables are the only
one anybody has sized: 500 ms and 40 MB at 2,000 mods. Added on paper that is 1.15 s and 92 MB, and both
numbers are wrong. The time is pessimistic, because the three passes share a warm file cache and a warm JIT
and step 7b runs after the rows are already decoded and resident. The heap is pessimistic in the other
direction only if a game registers more indexes, which it may. So P11 is set at 1.5 s and 80 MB and MEASURED
in one process rather than summed, and a miss is diagnosed by attributing it to a term rather than by
arguing about the addition. Three facts make the number matter rather than being a curiosity: v1 applies a
version at a restart (contracts 1.3 item 8), every player reconnects at once when it does (8.6), and the
door refuses until the runtime is live (9.6). P11 is the length of that window.

### 14.2 The benchmark it runs in

`KhaozEngine.Benchmarks` gets a `--catalog` mode following the journal's shape exactly
(`a-engine.md:1322-1349`): a `CatalogBenchmarkConfig` with a static `Parse(args)`, a
`CatalogBenchmarkRunner.RunAsync(config, ct)`, a `CatalogBenchmarkResult` with `ToJson()`, a
`CatalogBenchmarkOutput.WriteAsync(result, path)` for `--output`, and a checked-in JSON baseline under
`Baselines/`. New flags are `--definitions`, `--types`, `--chunk-slots`, `--languages`, `--edit-count` and
`--compose`.

`--compose` is P11's flag. It boots once with every registered type's load index built, times from process
start to the listener opening, and reports the composed figure ALONGSIDE its attribution, so the JSON carries
`p3Ms`, `p10Ms`, a per-index `loadIndexMs` map and the total. A consumer runs it with its own types
registered, which is the only way the number means anything: the engine alone cannot know what a game builds
at step 7b.

The benchmark is `IsPackable=false`, is not on the engine version line, and CI's `dotnet test` never invokes
its timing loop. Its STRUCTURAL behaviour is tested in CI, as a `CatalogBenchmarkTests` class in
`KhaozEngine.Server.Tests`, mirroring `MutationJournalBenchmarkTests`. Always `-c Release`, because Debug
numbers are not representative (`KhaozEngine.Benchmarks/README.md:43`).

### 14.3 Stage 5 measurements

Measured on an Apple Silicon Mac, macOS 26.6.2 arm64, .NET 10.0.12, 12 logical processors, always
`-c Release`, seed 835, one language, `item` at 1,024 chunk slots and fetch concurrency 4 over a 20 Mbit
token bucket. Every figure below is one run of `KhaozEngine.Benchmarks --catalog`, EXCEPT P3, P10 and P11,
which were re-measured after the layout revision of 9.1 and 7.6, for the reason the last block of this
section gives: P3 and P10 are the median run of five at 50,000, taken by P3's own total, and P11 is the
median of five compose runs. That re-measurement rewrote all three baselines whole, the 1,000,000 one as the
median of three, so their stored-byte fields are byte identical to stage 5's, which is the check that the
publish path did not move, and their timing fields are the later session's. The baselines are checked in as
`KhaozEngine.Benchmarks/Baselines/catalog-v1-seed835-50000.json`,
`catalog-v1-seed835-50000-compose.json` and `catalog-v1-seed835-1000000.json`. The command lines are in
`KhaozEngine.Benchmarks/README.md` and are reproduced here in the order they must run:

```bash
dotnet run --project KhaozEngine.Benchmarks -c Release -- --catalog --definitions 50000 --seed 835 --pack-root /tmp/kecat-50k --output "$PWD/KhaozEngine.Benchmarks/Baselines/catalog-v1-seed835-50000.json"

dotnet run --project KhaozEngine.Benchmarks -c Release -- --catalog --definitions 50000 --seed 835 --compose --pack-root /tmp/kecat-50k --output "$PWD/KhaozEngine.Benchmarks/Baselines/catalog-v1-seed835-50000-compose.json"

dotnet run --project KhaozEngine.Benchmarks -c Release -- --catalog --definitions 1000000 --seed 835 --pack-root /tmp/kecat-1m --output "$PWD/KhaozEngine.Benchmarks/Baselines/catalog-v1-seed835-1000000.json"
```

**Per budget, the method and what is approximated.**

- **P1** sums the stored bytes of every chunk, every text shard, the rule chunk and the manifest, per side,
  off one publish of 92,166 rows into 61 chunks. Uncompressed the server pack is 14.8 MB against the 19 MB
  section 14.1 predicted, and Brotli at quality 5 returned 5:1 rather than the conservative 2:1 that target
  was set at, which is where the four times headroom comes from.
- **P2** is the same sum at 1,000,000 definitions, 1,833,833 rows into 1,054 chunks. The text chunk sharded
  into 12 at 2,000,800 entries, so section 7.6's sharding rule is exercised rather than assumed.
- **P3** is `ContentRuntime.Load` with chunk hash verification on, plus the validator, timed by `Stopwatch`
  and bracketed by `GC.GetTotalMemory(true)`. The heap figure is that delta. The runtime's own accounting of
  what it retains is 10.7 MB, so both readings are inside the 20 MB target rather than only the optimistic
  one. The widest term in the time is the chunk fetch at 156 ms of the 334, which is a local file read and
  swings with the file cache, and the validator is the next at 81 ms.
- **P4** runs the real fetch loop against a local HTTP server behind a token bucket. It is a loopback
  transfer, so link latency and TCP slow start are absent and the measured 1.05 s for 2.66 MB sits at the
  1.12 s floor the shaper alone imposes on those bytes. Treat it as that floor plus local work rather than
  as a field number. Local work was 57 ms of manifest parse and SHA-256 over 58 objects.
- **P4b** is the same loop at 1,000,000, fetching 1,016 objects and 52.9 MB, and it is transfer bound
  exactly as the section predicted: 22.15 s wall against a 22.19 s shaper floor.
- **P5** publishes a one-item edit through the benchmark's own publisher rather than through a
  `catalog-publish` service call, so it carries no transport or transaction commit. Its dominant term is the
  validator at 81 ms of the 93 ms, which is the shape section 14.1 predicted at a twentieth of the cost.
- **P6** is the rewritten chunk plus the client manifest and nothing else. At 1,000,000 the same edit is
  69.3 KB, because the manifest grows to 36.7 KB at 981 item chunks, and that figure is reported for
  information rather than against this target, which is stated at 50,000.
- **P7** is 50,000,000 iterations over a power-of-two ring of live ids scattered by a fixed stride, with a
  `GC.GetAllocatedBytesForCurrentThread` delta of 0. The typed `ItemRowView` accessor over the same ring is
  72.3 ns and also allocation free, which is the row decode rather than the lookup.
- **P8** is the validator over the 1,000,000 definition snapshot, 1,833,833 rows and 0 findings, at 609 ms
  against a 20 s target set at roughly ten times the arithmetic. The `KEC0100` band is NOT in this number,
  because Scope B's validators do not exist yet.
- **P9** is 50,000,000 draws through the prefix-summed array over a 200-entry table, allocation free.
- **P10** decodes the one language chunk of 100,800 entries into the body-plus-index form of section 7.6 and
  measures the `GC.GetTotalMemory(true)` delta as resident. The catalog's own accounting is 9.16 MB of the
  9.18 MB measured, which is the 8.16 MB body plus 262,144 buckets. The same run also times `Get` over a
  64-key working set, which is the evidence for the bounded cache: 28.1 ns and 18 bytes a call through it
  against 55.7 ns and 144 bytes materialising on every call, and the 18 is the direct-mapped collisions
  inside those 64 keys rather than a leak.
- **P11** boots a second process against the pack the first one published, so the timer from process start
  is a real cold boot rather than a publish. It builds the text chunk and a STAND-IN per-type load index of
  25 MB, which is the flattering approximation in this table: the stand-in built in 29 ms where section 14.1
  sizes Scope B's real generator tables at 500 ms, so the composed figure understates the third term by
  roughly half a second. Even carrying that half second the budget holds. Five runs put process start to
  listener between 385 and 511 ms, with one outlier at 1,008 ms on the first start after a rebuild, which is
  the file cache rather than the boot.

**The two misses stage 5 recorded, and what the revision did to them.** Both were UTF-16 inflation of bytes
the pack already carries as UTF-8, which is what sections 9.1 and 7.6 now hold as blobs.

- **P3 heap: 12.3 MB against a 20 MB target, from 26.3 MB.** The stage 5 reading was over by a third on a
  `Keys` array holding a .NET string per row. A key is now read out of `Bodies` where the encoder already
  wrote it, and the key-to-id index is an open-addressed `int[]` rather than a dictionary keyed on those
  strings, so the two structures the keys cost became one. The runtime's own accounting went from 20.4 MB to
  10.7 MB with it.
- **P10 resident: 9.2 MB against a 32 MB target, from 44.2 MB.** 460 bytes an entry became 96, which is the
  body plus 4 bytes a bucket, and the decode got faster with it, 36.6 ms to 15.8 ms, because it no longer
  builds 201,600 strings and a dictionary to hold them.

**The re-measurement session was noisier than stage 5's, so it carries its own control.** The same machine
was under other load, and budgets the revision cannot touch came back 20 to 40 percent slower, which is
enough to read as a regression if nobody checks. So the pre-change tree was rebuilt from `355b79d8` and
measured in the SAME session, same pack, same seed, same commands, warm through `--phases load`:

| Back to back, warm | Before | After |
|---|---|---|
| At 50,000: load steps 3 to 8 | 217 to 225 ms | 206 to 236 ms |
| At 50,000: validator sweep | 104 to 108 ms | 104 to 107 ms |
| At 50,000: decode | 37 ms | 31 to 33 ms |
| At 50,000: runtime heap | 25.1 MB | 12.3 MB |
| At 1,000,000: validator sweep | 694 to 697 ms | 652 to 821 ms |
| At 1,000,000: decode | 572 to 590 ms | 387 to 453 ms |
| At 1,000,000: runtime heap | 424.8 MB | 194.9 MB |
| At 1,000,000: one decoded language | 485.8 MB | 184.9 MB |

Every row is warm against warm, which matters because a full run measures the heap at a different point and
reads lower: the checked-in 1,000,000 baseline says 179.5 MB where the warm row above says 194.9. So the
revision buys about half the runtime heap and a fifth of a decoded language, pays nothing for it in load
time, and the one term worth watching is the validator at the stress figure, whose spread straddles the
before figure rather than clearing it. The 1,000,000 figures are informational: P3 and P10 are budgeted at
50,000 and nothing in section 14 targets the runtime heap at the stress figure.

Nothing else misses. **Question Q3 is confirmed by measurement rather than by arithmetic alone:** the same
one-item edit at 4,096 `item` chunk slots downloads 129.7 KB against the 80 KB target, so the 1,024
registration of section 3.1 is load bearing and the earlier 4,096 would have shipped a budget its own
section refuted.

## 15. Test plan

### 15.1 Golden format files, checked in

Under `KhaozEngine.Catalog.Tests/Goldens/v1/`, one set per format version, added to and never edited:

| File | Contents |
|---|---|
| `chunk-tag-0.kecc` | The exact two-row chunk of section 7.9, stored uncompressed. |
| `chunk-item-0.kecc` | A three-row item chunk exercising every value kind including an empty tag list, a null optional field and a retired row, stored Brotli compressed. |
| `manifest-server.kecm` | A two-type, four-chunk, two-language server manifest. |
| `manifest-client.kecm` | The same version's client manifest, with the `ServerOnly` type omitted. |
| `rules.kecr` | Six rules covering all four kinds of contracts 8.2, including a 5-byte replacement payload and a zero-length payload. |
| `text-en-us.kect` | Twelve entries including an empty value and a 192-character key. |
| `goldens.json` | Every file's expected chunk hash, uncompressed length and decoded field values. |

Three assertions per golden. **Decode**: the reader produces exactly the values in `goldens.json`.
**Hash**: `ContentHash` over the canonical bytes equals the recorded hash, which is what pins the digest
domain, the scheme version and the canonical form all at once. **Re-encode**: encoding the decoded values
reproduces the UNCOMPRESSED canonical bytes byte for byte.

The compressed bytes are deliberately NOT pinned (section 7.5). `chunk-item-0.kecc` is checked in compressed
so the decompression path has a golden, and the test asserts on the decompressed result rather than on the
stored bytes, so a .NET upgrade that changes Brotli's output does not turn into a red test with no defect
behind it.

### 15.2 Decoder fuzzing

A mutation fuzzer over the goldens, in `KhaozEngine.Catalog.Tests/Fuzz/`:

- Seeded with `SeededRandomSource` wrapping `DeterministicRng` (contracts 14.2), so a failure reproduces from
  the seed printed in the assertion message.
- Mutations: flip a random bit, truncate at a random offset, zero a random run, splice two goldens, set a
  random varint byte's continuation bit, and set a reserved field non-zero.
- 10,000 mutants per golden per run in CI, which is a few seconds, and a `--soak` mode for more.

Two invariants, and they are the whole test. **It never throws.** Every decode entry point returns
`false` plus a reason, never an exception, which is the whole-tree rule for bytes from a remote peer
(`a-engine.md:814-817`) and `ItemContainerCodec.TryDecode`'s own shape
(`KhaozEngine.Items/ItemContainerCodec.cs:49-104`). **Reasons are stable.** Every rejection carries a reason
from the fixed token list, and the test asserts the token is in the list rather than asserting a specific
token per mutant, because a bit flip can legitimately turn one failure into another.

The fixed list, in two halves. A DECODE entry point may return only:

```
chunk-format-version   chunk-reserved-set     chunk-range-mismatch   chunk-too-large
chunk-stored-length    chunk-row-duplicate    chunk-row-order        chunk-row-flags
manifest-wrong-side    manifest-chunk-slots   rule-kind              rule-sequence-gap
rule-sequence-order
```

A FETCH may additionally report `hash-mismatch`, `manifest-hash-mismatch` and `chunk-fetch-failed`, which are
outcomes of a transfer rather than of a decode and which the fuzzer never produces, because it mutates bytes
the test already holds. The boot refusals of section 9.6 are a third set and are not decode reasons at all.
`chunk-too-large` and `chunk-stored-length` are in the decode half deliberately: the fuzzer's truncate
mutation and its varint-continuation mutation both reach them, so they are exercised on every run rather than
only by a hand-written hostile-CDN test.

A third invariant catches the dangerous case: **a mutant that DECODES must round trip.** If a mutated chunk
decodes successfully, re-encoding it must reproduce the mutated bytes. That is what catches a decoder that
silently normalizes away a difference, which is how a canonical format stops being canonical and how byte
equality stops being property equality (contracts 9.3).

### 15.3 Cross-version round trips

A version 1 pack read by a version 2 reader, which is the test that only pays off later and is impossible to
add retroactively:

- The v1 goldens stay checked in unchanged forever. When `ChunkFormatVersion` becomes 2, the v1 goldens are
  still read by the current reader and every assertion still holds.
- A v2 reader handed a v1 chunk reads it. A v1 reader handed a v2 chunk REFUSES with `chunk-format-version`,
  because a mismatched version is a refusal of the whole record and never a best-effort partial read
  (contracts 15).
- A manifest whose `formatGeneration` exceeds the reader's `ContentPackFormat.Generation` is refused on both
  sides, server at boot and client at the door, with no consumer involvement (contracts 7.4).

The test that makes this real is a `GoldenFormatVersionsTests` class that ENUMERATES the `Goldens/` directory
and asserts every version subdirectory present is readable by the current reader. Adding a format version
means adding a directory, and forgetting to keep reading the old one goes red immediately.

### 15.4 Scale tests

In `KhaozEngine.Benchmarks --catalog` (section 14.2), at 50,000 and 1,000,000 synthetic definitions, producing
the eleven budget numbers of section 14 as a JSON result with a checked-in baseline. Synthetic generation is
deterministic from a seed, so two runs at the same seed and size produce byte-identical packs, which is itself
an assertion: **the same input publishes the same bytes.** That is contracts 4.3's registration-order
independence made measurable.

The structural half runs in CI as `CatalogBenchmarkTests` in `KhaozEngine.Server.Tests`: the generator is
deterministic, the config parses, the result serializes, and a tiny run completes. The timing half runs only
under `dotnet run -c Release`.

### 15.5 Provider conformance

`KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.cs`, an abstract class with
`protected abstract IContentAuthoringStore NewStore()`, subclassed by `SqliteContentAuthoringStoreTests` and
`SqlServerContentAuthoringStoreTests` (section 2.6). That is the pattern
(`KhaozEngine.Server.Tests/Commerce/WalletStoreContract.cs:9-11`, and the journal's 22-fact
`MutationJournalStoreConformance` at larger scale).

The facts, each asserting OBSERVABLE behaviour and never a mechanism:

| # | Fact |
|---|---|
| 1 | `AutoCreate` on an empty database creates the schema and reports version 1. |
| 2 | `ValidateOnly` on an empty database throws naming the required migration. |
| 3 | `ValidateOnly` on a correct schema succeeds. |
| 4 | A key differing only in case is a DIFFERENT key, which is the binary collation assertion. |
| 5 | Two `Add` edits for the same key in one draft collide on the unique index. |
| 6 | An `Update` merges fields rather than replacing the row's field set. |
| 7 | Publish assigns version 1 then 2, never skipping. |
| 8 | Publish with a stale `expectedBaseVersion` is refused and nothing is written. |
| 9 | A row untouched by a publish keeps its `valid_from_version`. |
| 10 | The live set at an old version excludes a row added later. |
| 11 | A retire writes a successor row plus exactly one remap rule. |
| 12 | A remap rule cannot be updated or deleted through the API surface. |
| 13 | Allocation reserves before issuing, asserted by reading `reserved_through` after a single allocate. |
| 14 | Two allocations never return the same id, across 10,000 in a loop. |
| 15 | A family allocation stays inside its aligned block and reserves a second block when full. |
| 16 | Every audit row carries a before and an after for a field change. |
| 17 | An audit insert failure rolls back the edit. |
| 18 | Import into an empty database succeeds and reproduces the source ids. |
| 19 | Import into a non-empty database is refused with nothing written. |
| 20 | Export at version N then import into an empty store gives identical rows, keys and ids. |
| 21 | A publish that fails at the validator leaves the draft intact. |
| 22 | The active pointer and the version row commit together, asserted by a reader seeing both or neither. |
| 23 | A plain allocation taken after a family block is reserved never returns an id inside that block, asserted by draining the whole gap under the block and then some. |

### 15.6 Publish crash safety

A `ContentPublishStep` internal enum and an `OnStep` hook on the publisher, copied from
`MapTiledSaveStep` and `MapTiledSaveOptions.OnStep` (`KhaozEngine.MapDoc/MapDocumentForm.cs:58-74`,
`MapTiledFile.Save.cs:94-101`):

```csharp
internal enum ContentPublishStep
{
    BeforeIdAllocation, AfterIdAllocation,
    BeforeChunkWrite,   AfterChunkWrite,
    BeforeManifestWrite, AfterManifestWrite,
    BeforeCommit,       AfterCommit,
    DuringSweep,
}
```

One test per step: the hook throws, and the test then asserts the store is either entirely at the old version
or entirely at the new one, that the pack store holds no file any version references but cannot serve, and
that a REPUBLISH after the kill succeeds and produces the same manifest hash it would have produced without
the kill. That last clause is the idempotence assertion and it is the one that matters, because a publish that
merely fails safely but cannot be retried is not recoverable.

The out-of-process version runs in `KhaozEngine.Benchmarks --catalog-crash-probe`, killing a child process at
each step against a real SQLite file, mirroring `JournalCrashProbe.cs` and the `--journal-crash-probe` mode
(`KhaozEngine.Benchmarks/Journal/JournalCrashProbe.cs`). In-process hooks prove the ordering and a real kill
proves the durability.

### 15.7 The rest

**Validator tests.** One per ISSUED finding code, 41 of them, each building the smallest `ContentSnapshot`
that triggers exactly that code and asserting the code, the type and the id. Forty-one and not forty-two: the
codes run `KEC0001` to `KEC0042`, `KEC0013` is WITHDRAWN and never reissued (section 5.2), and `KEC0032` to
`KEC0035` are four codes on one table row, which is where the count went wrong before. `KEC0000` gets a test
too and it is the one that asserts a finding does NOT set `IsValid` to false. Two groups are not
ordinary sweep tests. The four inheritance codes are unreachable in phase 1, so their tests assert they do NOT
fire on a candidate whose `parent_id` is 0 throughout, and `KEC0039` is asserted through `RollbackToAsync`
because it is emitted there rather than by the sweep (section 5.3). Plus three sweep-level facts: findings
accumulate rather than stopping at the first, the validator never throws for content reasons, and a game
validator that throws becomes one `KEC0040` rather than an escaping exception.

**Remap idempotence.** Apply the full ordered rule set to a byte array twice and assert the result equals
applying it once, over a generated corpus of rule sets and pages. Plus the negative: a rule set where a rule's
`ToId` is an earlier rule's `FromId` for the same type is refused by `KEC0015` AND, applied twice, would
genuinely differ, so the test proves the check is guarding a real failure rather than a hypothetical one.

**Manifest hash stability across registration order.** Register the same five types in a shuffled order,
publish the same content, and assert the manifest hashes are byte identical. Ten shuffles from a seeded
source. This is the direct test for contracts 4.3's registration-order independence and it is the test
Ruinborne's wire index would have failed, since the same item has a different byte index depending on whether
the catalog loaded from SQL or from code defaults (`c-ruinborne.md:517-536`).

**Chunk reuse.** Publish, edit one row, publish again, and assert exactly one chunk hash changed and every
other is identical to the previous version's. Then edit a row in a different chunk and assert two changed.
This is P6's correctness half and it is what would catch a chunk boundary accidentally becoming row-relative.

**Visibility.** Publish a `Client` type with one `ServerOnly` field, then assert the client chunk decodes
without that field, that its hash differs from the server chunk's, that BOTH hashes are in `catalog_chunk`
under the one `(version, type, chunk index)` at two sides, that the next publish of an unrelated chunk carries
both forward, that the client manifest omits every `ServerOnly` TYPE, and that a stub encoder which leaves the
field in the client-side bytes is refused by `KEC0014`.

**Boot fail-closed.** Twelve facts, one per row of section 9.6's exit table, each asserting exit code 3 and the
exact stderr prefix. Run in process against a test host that captures the exit rather than calling
`Environment.Exit`.

**Fork.** Publish a row, fork it, and assert all five properties in one test: the copy carries a new id and
the source row's whole field set, the copy's flag field is true, the ORIGINAL id is live with the edit's new
values and no flag, exactly one kind 3 rule was appended naming both ids in that direction, and a page stamped
before the fork resolves onto the copy while a page stamped after it does not. Plus the four `KEC0041`
negatives of 10.5. This is the test that would catch the rule being appended without the row, which is the one
failure that cannot be repaired afterwards (3.7).

**The door.** In `KhaozEngine.TileWorld.Netcode.Tests`: a matching layer admits, a version mismatch refuses
with both sides in the token, a hash mismatch with matching versions refuses, an absent layer refuses with
empty client fields, and a client below `minimumClientBuild` gets `ke:content-client-too-old` rather than the
generic mismatch.

**Localization coverage, opt in.** A test helper a GAME can call, asserting every derived key for every live
row resolves in its shipped catalog. Not an engine test, because contracts 12.4 makes a miss a visible
placeholder rather than a failure, and not every game wants the stronger guarantee. Grimhollow does: its
`EveryDeclaredKeyResolvesAgainstTheShippedCatalog` already walks every declared key constant
(`b-grimhollow.md:773-780`), and this helper is what that test becomes after adoption, walking the CATALOG
rather than a reflected constant list, which closes the gap the survey names (the current test cannot catch an
item with no name field at all).

## 16. Grimhollow adoption plan

This is written, not executed. It is phase 1's acceptance (#882, "Grimhollow adoption (phase 1 acceptance)")
and it is tracked in [Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208).

### 16.1 The premise: `feature/item-drop` lands first

Gate 0 decision 12 puts Grimhollow's `feature/item-drop` branch BEFORE these contracts, so this plan is
written against the branch as shipped rather than around it. What it brings is a third authored content
mechanism, `assets/config/items.jsonc`, with a `tradable` flag per live item, a fail-closed every-live-item
needs-a-row rule, a `GrimhollowItemProperties` ambient shaped like `GrimhollowSkilling`, and
`GrimhollowGameDataHash.Current` as a HYPHEN-JOINED pair of the skilling hash and the item-properties hash
carried in the existing fourth door layer (`b-grimhollow.md:1332-1370`, item-drop design sections 1 and 5).

Landing it first is what keeps `items.jsonc` a migration absorbed AT adoption rather than a third mechanism
appearing after the contracts and needing migration the moment it lands (contracts 7.6).

### 16.2 Every source mapped

| Source today | Becomes | Notes |
|---|---|---|
| `GrimhollowItems.ItemId` consts, lines 13-68 | The `item` type's definition ids | 35 ids preserved exactly, section 16.4. |
| `GrimhollowItems.ConfigKeys`, lines 191-227 | The `item` type's content keys | Already snake case and already the key table. |
| `GrimhollowItems.Stackable`, lines 131-132 | `item.stackable` | Four ids, three of them retired, so one live stackable. |
| `GrimhollowItems.RetiredIds` 134-143, `IsRetired` 151 | The `retired` row flag plus 18 `Retired` remap rules | Section 16.4. |
| `GrimhollowItems.CanBeAHatchet` 164-165, `CanBeAPickaxe` 169-170 | Two `tag` rows, `hatchet` and `pickaxe`, on `item.tags` | Contracts 4.6's first named replacement. |
| `GrimhollowItems.ValueOf` 159 and the `item.<key>.value` economy rows | `item.value` | 35 rows. |
| `GrimhollowItems.FoodFor` 180, `FoodValueOf` 186, `item.<key>.heals`, `.attackDelayTicks` | A `food` game type, one row per food, key-referencing `item` | One row today, bread. |
| `EconomyTable`, `EconomyCodec`, `EconomyRows`, `GrimhollowEconomy` (692 lines across five files) | DELETED. Values move to `item.value`, rates to the `store` type, drop numbers to `monster_drop`. | Section 16.3. |
| The `economy` database table and `EconomySchema` | DELETED after a one-time export into the bundle | Section 16.4. |
| `GrimhollowEconomyMigration` (67 lines) | DELETED | Its whole job was the one-time reset a code-owned `Defaults` made necessary (`b-grimhollow.md:304-319`). With the database as the only source there is no `Defaults` and no reset. |
| `GrimhollowEconomySync` and message kind 27 | DELETED | The economy no longer arrives after the door as a game message. It is in the client pack, gated at the door. |
| `GrimhollowEquipmentRoster.For`'s 9-arm switch, lines 132-155, the SLOT and archetype half | An `equip_profile` game type with `slot` and `weapon_archetype`, referenced from `item.equip_profile` | The `EquipSlot` enum VALUES stay durable, because they are container indices (`b-grimhollow.md:80-85`). |
| The same switch, the `accuracy`, `strength` and `defence` half | Three `stat` rows plus one `equip_stat_line` row per stat per profile, `Flat`, folded at source kind 1 | Section 3.3 reconciles this with Scope B, which maps the same switch to stat lines. Both halves land in the same adoption step, because a profile with no lines equips a weapon that does nothing. |
| `GrimhollowShop.GeneralStore` 46-56 | A `store` row plus eight `store_shelf` rows carrying the draw order | The draw order becomes a `sort` field, not a list position. |
| `GrimhollowShop.RatesFor` 96-100 and `store.general.sellRateBp` / `buyRateBp` | `store.sell_rate_bp`, `store.buy_rate_bp`, `ServerOnly` | AGENTS.md already anticipates "different rates per CLASS of item" (`b-grimhollow.md:1097-1099`), which is a `store_rate` row keyed by tag once the tag vocabulary exists. |
| `GrimhollowDrops.Roll`'s one-armed switch 27-52, `drop.goblin.coins`, `drop.goblin.breadOneIn` | A `monster_drop` game type keyed by monster kind, referencing a `loot_table` | The STRUCTURE moves out of code, which is the five-edit growth path (`b-grimhollow.md:269-284`) collapsing to one edit. |
| `skilling.jsonc` `trees` and `rocks` (lines 101-143) | `gathering_node` rows | Item references stay keys. |
| `skilling.jsonc` `processing` (152-174) | `recipe`, `recipe_input`, `recipe_output` rows | Recipe ids are append only because they key per-character quantity memory (file comment 147-149), so they import as definition ids preserved exactly. |
| `skilling.jsonc` `hatchets` and `pickaxes` (58-68) | `tool_tier` rows | The family validation becomes a tag reference, so `SkillingConfig.cs:455-457`'s double check collapses into `KEC0008`. |
| `skilling.jsonc` `maxLevel`, `xp`, `gathering`, `parents`, `stamina`, `skills` | `skill_curve` rows | The stamina hundredths trick (`SkillingConfig.cs:410-443`) becomes a `ScaledInt` with scale 100, which is what it already was in spirit. |
| `items.jsonc` `tradable` | `item.tradable` | Contracts 7.6. |
| `items.jsonc` `drop.despawnSeconds` | A `skill_curve` style singleton row, or a game config outside content | It is a global knob rather than per-definition tunable content. Recommended: a one-row `game_tuning` type, so it is versioned with everything else. |
| `GrimhollowGameDataHash` and `GrimhollowConfigGate` | DELETED, replaced by the engine's content layer | Contracts 7.6 is explicit: deleted rather than re-pointed. |
| `GrimhollowCatalogGate` and `GrimhollowCatalogHash` | KEPT, unchanged | They gate the WORLD catalogs (archetype collision, size, tags), which section 3.10 keeps out of content. |
| `ItemStrings` 35 name fields plus two 35-arm switches | The `item.name` and `item.examine` localized keys, resolved through `ContentStringCatalog` | Section 16.5. |
| `ItemIcons.RosterIcons` 88-125 | `item.icon` | The icon id is already `item.<config_key>` and already the source png filename. |
| `GroundItemMeshes.Roster` 21-58 AND its duplicated 35-arm `MeshRefFor` switch 77-115 | `item.mesh`, once | The duplication is deleted by construction: there is one field. |
| `GroundItemMeshes.GroundTransformFor`'s 12-id lie-flat list 147-169 | `item.ground_pose` | 0 upright, 1 lie flat. The minimum-Y walk stays code. |
| `GrimhollowHeldMeshes.For`'s 12-arm switch 20-35 | `item.held_mesh` | Its null-means-empty-hand rule stays: an absent field is an empty hand, never a greybox. |
| `GrimhollowHeldMeshes.ActionSourceItems` 42-47 | A `held_in_off_hand` tag | Eight ids become eight tag references. |
| `IconShots.TiltFor` 70-78 and `SpinFor` 88-95 | `item.icon_tilt`, `item.icon_spin` | Two of the three per-item switches in the snapshot tool. |
| `IconShots.MeshRefFor` 108-143, the THIRD copy of 32 mesh refs | DELETED, reads `item.held_mesh` then `item.mesh` | The tool joins the catalog like everything else. |
| `ItemInspection` and `ItemCatalogInspector` (`Grimhollow.Shared/Admin/ItemInspection.cs`) | DELETED | Its 15 fields gathered from five sources are what a `catalog-list` response is. |
| `Items.razor`, the read-only 14-column table | An EDITOR over the authoring API | Section 16.6. |

### 16.3 The five-edit growth path, collapsed

Adding one property to Grimhollow's economy today touches five places: the `EconomyTable` constructor, the
`EconomyCodec` version plus its encode and decode arms, `EconomyRows.From`, `EconomyRows.Parse` plus a key
constant, and the key table in `docs/DEVELOPMENT.md` (`b-grimhollow.md:269-284`, issue 208 defect 1). A second
monster's drops is a schema change across all five, because the goblin numbers are NAMED FIELDS rather than a
table.

After adoption it is ONE edit: add the field to the type's `ContentFieldSchema` and to its codec, which are
two lines in one file and are checked against each other at registration (section 3.6). There is no wire
format to version, because the field travels inside a chunk whose row codec declares it. There is no key
table to update, because `catalog-schema` IS the key table and it is generated. There is no `Parse` to extend,
because the editor is generated from the schema.

A second monster's drops is ZERO code edits: a `monster_drop` row and a `loot_table` with its entries, through
the console.

### 16.4 The import that preserves ids 1 to 35 and the 18 retired flags

A one-time `ContentBundle` built by a throwaway tool in the Grimhollow repo, imported into an empty catalog
database through `catalog-import` (section 10.9), which is the only write path that accepts a whole bundle
and only into an empty database.

The bundle is generated by reading, in this order:

1. `typeof(ItemId)`'s public static int literals, exactly as `ItemCatalogInspector.Capture()` already reflects
   them (`b-grimhollow.md:900-915`), filtered by nothing, so RETIRED ids are included.
2. `GrimhollowItems.ConfigKeys` for the key of each.
3. `GrimhollowItems.RetiredIds` for the retired flag of each.
4. The live `economy` table through `IGrimhollowEconomyStore.LoadAsync`, merged over `EconomyTable.Defaults`
   exactly as the boot does today (`b-grimhollow.md:320-327`), so the OPERATOR's tuned values win and are
   what gets imported. Not `Defaults`. This is the one step where getting it backwards silently discards
   every number the owner has tuned on the live server.
5. `GrimhollowEquipmentRoster.For(id)` for each, giving the `equip_profile` rows.
6. `GrimhollowShop.GeneralStore` for the store and its shelves.
7. `GrimhollowItemProperties.Current` for `tradable`.
8. `GrimhollowSkilling.Current` for the nodes, recipes, tool tiers and curves.
9. `ItemIcons.RosterIcons`, `GroundItemMeshes.Roster`, `GrimhollowHeldMeshes.For` and `IconShots`'s two
   switches for the presentation fields. These live in `Grimhollow.Core`, which the server does not reference,
   so the tool is a CLIENT-side generator writing a JSON fragment the bundle builder merges. That layering
   cost is real and it is one tool run, once.

**Ids are carried in the bundle, not reallocated.** This is the carried-id case of section 6.3 step 1 and
section 10.9's first bullet: `ContentBundle` names each row's id explicitly, `catalog-import` writes `Add`
edits carrying them, publish skips allocation for every one, and the commit seeds
`catalog_id_high_water.reserved_through` and
`issued_through` to the maximum imported id per type afterwards. So item 13 is `stone_sword` before and after,
every stored container decodes unchanged, and no player's bank moves. Contracts 6.5 states the outcome
directly: Grimhollow's adoption is a no-op for stored data.

**The bundle declares no families, so the block rule of section 4.7 does not bear on this import.** Grimhollow
has no id block structure to carry across: its ids are a flat 1 to 35 and its equipment profiles are a
satellite table keyed by item id rather than a family. Seeding the high-water marks is the whole of the
allocator work here, and the first family this database ever sees will be created by a later ordinary edit,
against a counter that is already past every imported id.

**The 18 retired ids import as rows with `retired = 1` plus 18 `Retired` remap rules with policy `0x01`
placeholder.** They are not replacements, because Grimhollow's retired items have no destination: the
comment's own rule is that retired ids are never removed so a stored stack still decodes and the player
upgrade can find it (`GrimhollowItems.cs:133-151`). A placeholder policy preserves exactly that, and
`GrimhollowPlayerUpgrade`'s use of `IsRetired` becomes a runtime `IsRetired(id)` off the retired bit (section
7.3) with no behavioural change.

### 16.5 The localization key rename

Two keys break the derivation of contracts 12.1: `ItemPineLogs = "item.pinelogs.name"` and
`ItemOakLogs = "item.oaklogs.name"` drop the underscore every other key keeps
(`Grimhollow.Core/Localization/GrimhollowStrings.cs:583, 586`, `b-grimhollow.md:765-772`). They are RENAMED to
`item.pine_logs.name` and `item.oak_logs.name` at adoption. Not special cased, not aliased, not exempted
(contracts 12.5).

It is one line in the `.resx` and one constant in `GrimhollowStrings.cs`, and the existing reflection test
walks every declared key constant against the shipped catalog so a half-done rename goes red immediately
(`b-grimhollow.md:773-780`). Tracked in
[Grimhollow #222](https://github.com/APKiwiOrg/Grimhollow/issues/222).

The examine keys rename with them, since they share the stem.

### 16.6 The connect door, and the admin console

**`GrimhollowConfigGate` is DELETED, not re-pointed** (contracts 7.6). The door after adoption, outermost
first: protocol version, world hash, CONTENT (the engine's layer), `GrimhollowCatalogGate`, the game's token
auth, the ban check. Note the content layer sits OUTSIDE the game's catalog gate, matching contracts 7.5's
ordering rule that content sits inside world and outside auth.

`GrimhollowCatalogGate` and `GrimhollowCatalogHash` are KEPT, because they gate the WORLD catalogs (archetype
collision kind, size, interactive flag and tags), which section 3.10 deliberately leaves outside content.
`NoticeStrings.ForRefusal` gains one arm for `ke:content-mismatch` and one for
`ke:content-client-too-old` and loses the `GrimhollowConfigGate` arm
(`Grimhollow.Core/Client/NoticeStrings.cs:104-111`).

**The admin console's Items page becomes an editor.** Today it is a single read-only table with 14 columns
served from an immutable snapshot captured at server start, and its subtitle says so
(`b-grimhollow.md:866-899`). After adoption:

- `AdminApiClient` gains the sixteen `actions/catalog-*` calls beside its existing `actions/item-catalog`
  (`Grimhollow.Admin/Services/AdminApiClient.cs:139`), and `actions/item-catalog` and
  `actions/skilling-config` are deleted.
- `Items.razor` renders its grid and its edit drawer FROM `catalog-schema` rather than from a hand-written
  column list, so a new field appears with no console change. That is the generic editor of section 10.3 and
  it is what makes the same page serve `store`, `recipe` and every Scope B type.
- The name column reads the catalog's localized name instead of title-casing the config key, which closes
  [Grimhollow #226](https://github.com/APKiwiOrg/Grimhollow/issues/226) for free, exactly as that issue
  predicts.
- The console forwards its Entra identity as the `operator` field on every mutating call (section 10.10). It
  already resolves one: `Program.cs` narrows authorization to a single `AllowedObjectId`
  (`b-grimhollow.md:927-945`), so the stable oid is in hand and needs only to be passed.
- The write path is bearer-token plus pinned-certificate-thumbprint as today
  (`Grimhollow.Admin/Program.cs:127-135`), unchanged.

### 16.7 The tests that pin it

There is NO test today pinning the catalog against a previous release's serialized form. What exists pins it
against hand-written literals in the same commit: `NewItemIdsAppendWithoutMovingTheOldRoster` asserting six
ids, `TheTenMiningAndMasonryItemsAppendInSpecOrder`, `EveryDurableItemHasAConfigKeyAndIsKnown`,
`TheEighteenRetiredItemsAreKnownAndNothingCanMintThem` and four more
(`Grimhollow.Tests/Shared/GrimhollowItemsTests.cs`, `b-grimhollow.md:1158-1184`). So #208's proposal of a test
against the previous release's catalog is genuinely new work, and it is where the risk of this migration
lives.

Four tests, in order of how much they buy:

1. **`TheImportedBundleMatchesTheShippedRoster`.** Build the bundle from the sources of section 16.4, import
   into an empty in-memory store, publish version 1, and assert row by row against the SAME literals
   `GrimhollowItemsTests` already asserts. The existing eight tests keep passing unchanged, against the
   catalog instead of against the constants, which is what makes this a migration rather than a rewrite.
2. **`EveryStoredContainerStillDecodes`.** A corpus of real encoded `ItemContainer` blobs checked in from a
   production export, decoded before and after the import, asserting that every blob **decodes to the same
   slots**: the same item id and the same count in the same slot index, blob by blob and slot by slot. This is
   the test that would catch an id moving, and an id moving is a player who cannot log in
   (`b-grimhollow.md:545-556`, [Grimhollow #224](https://github.com/APKiwiOrg/Grimhollow/issues/224)).

   **The assertion is about the SLOTS and not about which validator runs, deliberately.** Written against
   `GrimhollowJournalContracts.ValidateContainer` throwing on an unknown id, this test would assert on a
   mechanism Scope B replaces: its Grimhollow row 4 swaps that hard throw for the engine validator plus
   quarantine, on purpose, so that a bad id stops being a crash. A test pinned to the throw would go red the
   day that lands and would have to be rewritten under time pressure, and the thing it was protecting, the
   ids, would be unguarded in between. Pinned to the slots, it passes unchanged through the replacement: the
   whole point of preserving ids 1 to 35 is that a stored blob still names the same things, and that is true
   whether an unknown id throws, quarantines or is refused at the door. The substitution is a strictly better
   failure mode for the same property, and this test is indifferent to it by construction.
3. **`TheEconomyNumbersSurviveTheImport`.** Load a fixture `economy` table holding OPERATOR-tuned values that
   differ from `EconomyTable.Defaults`, run the bundle build, and assert the tuned values are what landed.
   This is the test for step 4 of section 16.4 and it is the one that catches the backwards merge.
4. **`ThePublishedManifestIsStable`.** Publish the imported bundle twice from two shuffled registration orders
   and assert identical manifest hashes, which is section 15.7's engine test applied to the real Grimhollow
   type set.

`Grimhollow.Tests/Shared/GrimhollowItemsTests.cs` line 225's known gap, that
`OnlyCoinsArrowShaftsAndJoineryPegsStackInTheBag` loops ids 1 to 25 so a new stackable would be pinned by
nothing ([Grimhollow #225](https://github.com/APKiwiOrg/Grimhollow/issues/225)), is closed as a side effect:
the catalog-driven version loops every live row.

### 16.8 The order of steps

1. `feature/item-drop` merges (gate 0 decision 12). Nothing in this plan starts before it.
2. Engine phase 1 ships (section 18) and Grimhollow pins it.
3. Register the four engine types and the ten game types. No behaviour change, nothing reads them yet.
4. Build the bundle tool and land test 3 of section 16.7. Run it against a production export and eyeball the
   diff. This is the review gate and it is a human one.
5. Import the bundle into an empty catalog database and publish version 1 through the API.
6. Land tests 1, 2 and 4. All four green before anything reads the catalog.
7. Switch the SERVER's readers over, one subsystem at a time, each with the old source deleted in the same
   commit: items, then economy values, then equipment, then shop, then drops, then skilling. Deleting in the
   same commit is what stops two sources of truth existing for a release.
8. Switch the CLIENT's readers over: strings, icons, meshes, poses. Delete `GrimhollowEconomySync` and message
   kind 27.
9. Replace the door's fourth layer with the content layer. Delete `GrimhollowConfigGate`,
   `GrimhollowGameDataHash`, `ItemPropertiesConfig` and `items.jsonc`.
10. Delete `skilling.jsonc`, `SkillingConfig`'s parser, `EconomyTable`, `EconomyCodec`, `EconomyRows`,
    `GrimhollowEconomy`, `GrimhollowEconomyMigration`, `EconomySchema` and the `economy` table.
11. Rewire the admin console (section 16.6) and do the localization rename (section 16.5).

Steps 7 and 8 are where the release boundary sits: everything up to step 6 is additive and shippable, and
step 9 is the one that requires every client to update at once, because it changes the door.

**One coordination point with Scope B, and it is a scheduling constraint rather than a dependency.** Scope B's
phase 1 changes `ItemStack` to a three-component record struct and takes `ItemContainerCodec` to version 2. It
does not wait on this plan and this plan does not wait on it, but a three-component record struct generates a
three-out-parameter `Deconstruct`, so every `var (id, count) = stack` in the fleet stops compiling and stack
equality starts including the instance id. **That bump lands BEFORE step 7 or AFTER step 11, never inside the
window.** Inside it, Grimhollow is half migrated: some subsystems read the catalog and some still read the old
source, every one of them touches stacks, and a deconstruction break lands on both halves at once with a codec
version bump underneath. It would also put `EveryStoredContainerStillDecodes` (16.7) either side of a codec
version change it never mentions, which turns the plan's sharpest regression fence into a test comparing two
different formats. Neither ordering costs anything to choose. Choosing neither costs a half-migrated game.

## 17. Ruinborne adoption plan

Written, not executed. Tracked in [Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465).

### 17.1 The precondition

[Ruinborne #299](https://github.com/APKiwiOrg/Ruinborne/issues/299) is a PRECONDITION on the owned-item half,
not on this half. Its PostDeploy duplicate-row collapse partitions by `(character_id, item_id)` with no
`instance_json` term and runs on every redeploy, so the moment two differently rolled copies of one
non-stackable item exist they collapse into one (`c-ruinborne.md:939-948`). Scope A can land without it,
because Scope A does not create instances. Scope B cannot.

### 17.2 `item_def` and its satellites, mapped

| Source today | Becomes |
|---|---|
| `item_def.item_id NVARCHAR(64)`, the clustered PK | The `item` type's content KEY. The int32 definition id is NEW and allocated at import (contracts 5.5). |
| `item_def.display_name` (already a localization key) | `item.name` |
| `item_def.item_type` and `.slot`, bare varchars with no reference table | `item.tags` plus `item.equip_profile`. This is [Ruinborne #199](https://github.com/APKiwiOrg/Ruinborne/issues/199) resolved: the complaint is that rarity gets a proper reference table and these do not, and a tag row IS the reference table. |
| `item_def.stackable`, `.max_stack` | `item.stackable`, `item.max_stack` |
| `item_def.base_stats_json`, the dead column with no reader | DELETED at import. It is [Ruinborne #511](https://github.com/APKiwiOrg/Ruinborne/issues/511), a dead column that is still editable, still encoded and still carried, and it stops being any of the three. |
| `item_def.rarity_id` and the `item_rarity` table | A Scope B `rarity` content type, id range 256 to 1023. Scope A imports the rows and Scope B owns the schema. |
| `item_def.icon_id` | `item.icon` |
| `item_stat` (composite PK, `flat REAL`, `percent REAL`) | An `item_stat` game type key-referencing `item` and `stat`, with `flat` and `percent` as `ScaledInt`. The REAL columns become integers, which is contracts 13.4's determinism rule and is a real behaviour change: a value of 0.1 becomes 10 at scale 100. |
| `weapon_def` (1:1-optional, `damage`, `range REAL`, `half_arc_deg REAL`, `cooldown_seconds REAL`) | A `weapon_profile` game type, the same `ScaledInt` conversion. |
| `item_ability_modifier`, whose `required_tags` and `excluded_tags` are COMMA-JOINED STRINGS in one column | Two `TagList` fields on an `ability_modifier` game type. A set membership test stops being a substring search (contracts 4.6). |
| `stat_def` (whose own comment says the C# `StatChannel` enum stays the source of truth for the index) | The engine `stat` type. The enum stops being the source of truth for anything, which is what the comment asks for. |
| `loot_table` and `loot_table_entry` | The engine `loot_table` and `loot_entry` types, one to one. Section 3.5 chose the child-type shape partly because Ruinborne already has it. |
| `ability_def`, `npc_archetype`, `npc_spawn` | Game types, `1024` upward. |
| The five `RuinborneItems` code defaults | DELETED. There is no code-default catalog under contracts 1.4 ("a definition exists in the authoring store and nowhere else"), and deleting them is what closes [#512](https://github.com/APKiwiOrg/Ruinborne/issues/512). |

### 17.3 The string id becomes the key, and the int id is new

Contracts 5.5 fixes this and names it as the single most likely place the two adoption specs would diverge, so
it is restated here in the concrete: `item_def.item_id NVARCHAR(64)` becomes the engine content KEY, and the
int32 definition id is allocated by the authoring store at import and did not exist before.

Ruinborne's five current ids are already legal keys under contracts 5.3, which is worth checking rather than
assuming: `health_potion`, `sword`, `staff`, `swiftstride_boots` and `rime_band` are `a-z0-9_`, no leading
digit, no double underscore, well under 64 characters (`c-ruinborne.md:221-227`). So the import renames
nothing, unlike Grimhollow's two localization keys (section 16.5).

**Nothing durable is rewritten by Scope A.** The seven tables carrying `item_id` as a foreign key keep working
unchanged, because the key survives: `character_inventory`, `item_stat`, `weapon_def`,
`item_ability_modifier`, `loot_table_entry`, `world_entity` and `economy_ledger`
(`c-ruinborne.md:859-867`). The int id is added beside the key, not in place of it. Migrating
`character_inventory.item_id` to the int definition id is Scope B's work, because it is the owned-item half,
and it is not a precondition for anything in this section.

**Ruinborne's bundle carries NO ids, so it is the allocated case, and the order is what pins them.**
Grimhollow's bundle carries ids because it has ids to keep (section 16.4). Ruinborne has none: its catalog is
keyed by a string and the int32 definition id is new here, so every row goes in with `definition_id = 0` and
comes out with an allocated one. The two are the two bullets of section 10.9, one import path, chosen per row
by whether the row names an id. Ids that are allocated come in edit ordinal order (section 6.3), so the
bundle's row order determines them. The bundle builder orders every type's rows by KEY ascending, ordinal,
which is the order `SqlRuinborneStore.cs:433` already reads them in (`ORDER BY [item_id]`). Two consequences:
an export at version N re-imported into an empty database reproduces
the same ids (section 10.9), and the ordering that was a hazard when it was a WIRE index becomes harmless the
moment it is only an allocation order, because the id it produces is then stored rather than derived.

**No families at import.** Nothing in Ruinborne's catalog is grouped contiguously today and contracts 5.2's
block size is expensive to change once allocated, so the import declares no family and every id comes from the
type's plain high-water mark. A family is declared later, for a new group, and costs nothing to add then. The
opposite mistake, declaring a family at import to be tidy and sizing its block wrong, is in section 19's table.

### 17.4 The wire index, `ItemRosterPush` and message kinds 16 and 17 are deleted

This is the largest deletion in the plan and the one that removes a whole class of hazard rather than a
defect.

Today the server pushes the catalog to each client at join: one `ReliableOrdered` message per rarity then one
per item, each carrying `[totalCount:byte][index:byte]` plus the def's fields, where the index is the def's
POSITION in a list (`ItemRosterPush.cs:49-62`, `ItemDefCodec.cs:23-53`). The bag then names items by that
byte (`InventoryStateCodec.cs:40`). The position is built by a cast, `map[items[i].ItemId] = (byte)i`
(`ItemCatalogContentLoader.cs:27`), enforced by a 255-item and a 255-rarity ceiling that reject the WHOLE
catalog when breached (`:137-148`), and it disagrees between the two sources it can be built from
(`c-ruinborne.md:517-536`).

After adoption the client has the catalog BEFORE it connects. The door refuses a client whose content version
does not match, with the server's version and manifest hash in the refusal token, and the client fetches what
it lacks from the pack store and reconnects (section 8.5, gate 0 decision 6). So:

| Deleted | Replaced by |
|---|---|
| `ItemRosterPush` and its one call site at `PlayerLifecycleService.cs:491` | Nothing. The catalog is not pushed. |
| Message kinds 16 `RarityDefsMessageKind` and 17 `ItemDefsMessageKind` (`RuinborneItemProtocol.cs:31, :36`) | Retired, never reused, for the same reason a definition id is never reused. |
| `ItemDefCodec` and `RarityDefCodec` | The registered row codecs of section 3.6, one per type. |
| `ItemClientState` and `RarityClientState`, their `totalCount` sizing and their out-of-order fill (`ItemClientState.cs:24-80`) | `ContentRuntime` on the client half, loaded from the cached pack at startup. |
| The 255-item and 255-rarity ceilings | `int32` ids. |
| `InventoryStack.ItemIndex`, a `byte` list position | The int32 definition id as a varint. **Scope A's change**, not durable, so it is a message version bump rather than a migration. |

**That last row is claimed by THIS spec, and the claim is the fix for a scheduling contradiction.** It was
labelled Scope B's message change, and it cannot be: Scope A's phase 2 IS Ruinborne's adoption, step 8 of
17.10 performs the substitution, and step 8 and step 9 ship together or the bag names ids a client cannot
resolve. Scope B's own Ruinborne adoption runs after its phase 3, several phases later, so leaving the row
with Scope B would have made Scope A's phase 2 block on a phase no table draws an edge to, silently, until
someone tried to sequence it.

It is also genuinely this spec's work by content. The change substitutes one number for another in a message
body: a byte holding a list position becomes a varint holding a definition id, with no payload, no instance,
no roll and no property anywhere in it. Everything Scope B would contribute to that message arrives LATER,
when a stack gains its third component, and that is a separate change in a separate phase (18.1). So the
wire-index substitution lands in Scope A's phase 2, with the rest of Ruinborne's adoption, and Scope B's
Ruinborne row 8 records the outcome without scheduling anything.

**Two documented holes close by construction rather than by a fix.** The first is the ordering hole at
`ItemRosterPush.cs:43-48`: a killing blow attributed to the slot, landing after the character binding exists
but before the roster push runs, can deliver a bag push naming indices the client has not learned yet. It is
called "practically unreachable, self-correcting, and real" and left open because closing it needs a per-slot
roster-sent latch. There is nothing to latch once the client has the catalog before the door admits it. The
second is `DefsReady`, the client's rule that the bag simply does not render until every def has arrived
(`ItemClientState.cs:19-21`). A client that is admitted is by definition content-current, so there is no
not-ready window to render around.

### 17.5 The five content loaders collapse to one load

Ruinborne has five content loaders, each with its own SQL read, its own retry wrapper, its own validation
placement and its own fallback. Four of them converged on `ContentRowMapping` when
[#200](https://github.com/APKiwiOrg/Ruinborne/issues/200) was fixed, itself a repeat of #24 in
`NpcContentLoader`, and the item loader is the fifth and was never swept
([#325](https://github.com/APKiwiOrg/Ruinborne/issues/325), open).

After adoption there are ZERO content loaders. There is one load, the engine's, at boot: read the active
version, fetch and verify every chunk, decode, validate, build the runtime arrays, resolve the world keys
(section 9.5). It is the same code for every content type in both games, which is what makes a sixth type
cost no loader at all.

What that deletes, and what each deletion buys:

- `ItemCatalogContentLoader` in full, including `CodeDefaults`, `TryBuildFromSql` and its three
  whole-catalog rejections, and `AttachModifiers`. The modifier fold becomes a key reference from the
  `ability_modifier` game type to `item`, resolved by `KEC0006` at publish rather than by a per-row skip at
  load (`ItemCatalogContentLoader.cs:199-209`).
- `RuinborneItems.All` and `RuinborneRarities.DefaultRarities()`. Contracts 1.4 is explicit that a definition
  exists in the authoring store and nowhere else, so there is no code-default catalog to fall back to. This is
  what closes [#512](https://github.com/APKiwiOrg/Ruinborne/issues/512), and it closes it by removing the
  destination rather than by improving the announcement.
- The hand-built `RarityDef` construction the item loader does instead of calling
  `ContentRowMapping.ToItemRarity`, which is [#313](https://github.com/APKiwiOrg/Ruinborne/issues/313) exactly.
  It is resolved by there being one decode path per type, the registered codec, with no second hand-rolled
  mapper left to drift from.
- The positional `SqlDataReader` ordinal hazard that shipped as
  [#494](https://github.com/APKiwiOrg/Ruinborne/issues/494) in `AbilityContentLoader`, where a duplicate column
  in a SELECT shifted every later ordinal. A pack row is a field list the schema names, so there is no ordinal
  to shift.

**#325's distinction is preserved, deliberately, and it is worth spelling out because the issue argues for
keeping the behaviour that looks like the defect.** It says the wholesale rejection is DEFENSIBLE and should
stay, and that the real defect is the DIAGNOSTIC: an operator reading `Item catalog: SQL read failed (Item
'xyz' has no IconId.)` goes looking at the network. This design keeps the rejection, because a pack is atomic
by construction and half a version is not a thing that can exist, and it fixes the diagnostic completely: a
content failure exits 3 with one line per finding naming the code, the type, the id and the message, and a
transport failure exits 3 with a chunk hash and a store name (section 9.6). The two are never confusable
because they are different rows of the same table.

### 17.6 PostDeploy's insert-if-absent seeding becomes a one-time bundle import

Every catalog row in Ruinborne is seeded by `Scripts/PostDeploy.sql` as `IF NOT EXISTS ... INSERT`, eleven
blocks across `item_def`, `item_rarity`, `weapon_def`, `item_stat`, `stat_def`, `item_ability_modifier` and
`loot_table` (`c-ruinborne.md:197-209`). Because insert-if-absent never reaches an existing row, the script
carries a growing set of guarded `UPDATE ... WHERE column = <old literal>` corrections, and the file states in
its own comments that these knowingly revert an operator value: "an operator who retuned it back down would
see it reverted to 40 on the next deploy, the same limit every other numeric correction in this file already
accepts" (`PostDeploy.sql:382-385`).

Section 10.9's empty-database rule is the whole answer, and section 10.9 already names this as the defect
class it exists for. The replacement is one `catalog-import` of one `ContentBundle` into one empty catalog
database, once, ever. After that a value changes through `catalog-edit` and `catalog-publish` and through
nothing else.

**The bundle is built from the LIVE database, not from PostDeploy and not from `RuinborneItems`.** This is the
same step that Grimhollow's section 16.4 gets wrong if it is done backwards, and Ruinborne's version of it is
sharper, because its corrections mean the live rows and the seed text genuinely differ. The sequencing that
makes it safe is: deploy normally so every pending correction has been applied, THEN export, so the
corrections are already in the live rows and the export captures the operator's state including them.

**The REAL columns become scaled integers, and the import reports every value that does not convert exactly.**
Contracts 13.4 makes integers the determinism rule and this is a real behaviour change rather than a
representation change, so the scales are chosen once, written down here, and put in section 19's table:

| Source column | Type today | Becomes | Scale | Note |
|---|---|---|---|---|
| `item_stat.flat`, `.percent` | `REAL` | `ScaledInt` | 100 | Two decimal places. `0.1` becomes `10`. |
| `weapon_def.range`, `.half_arc_deg` | `REAL` | `ScaledInt` | 1000 | Three places, which is well under a pixel and under a degree. |
| `weapon_def.cooldown_seconds` | `REAL` | `ScaledInt` | 1000 | Milliseconds. The seeded `0.45` and `2.0` are exact. |
| `item_ability_modifier.value` | `REAL` | `ScaledInt` | 100 | Matches `item_stat`, since both feed the same evaluation. |
| `loot_table_entry.drop_chance` | `REAL` in `[0,1]` | `loot_entry.chance_bp` int | basis points | Times 10,000, the one percent representation in the system (section 3.5). |

Rounding is half away from zero, and the import writes a line per value whose round trip back through the
scale does not reproduce the source `REAL` bit for bit. Nothing is silently moved. That report is the review
gate for this step and it is a human one, the same shape as Grimhollow's diff eyeball in section 16.8 step 4.

**What STAYS in PostDeploy.** The owned-item repairs: the duplicate-row collapse at `:523-536` and the
bag-slot repair at `:538-602`. Those operate on `character_inventory`, which is owned data rather than
content, and section 3.10's boundary rule applies in the same spirit. The content blocks and every content
correction leave the script, and the duplicate-row collapse keeps its own open defect,
[#299](https://github.com/APKiwiOrg/Ruinborne/issues/299), which section 17.1 already names as Scope B's
precondition.

### 17.7 The seven content pages become one generic editor

`Ruinborne.Admin` is a Blazor Server app with seven content editor pages, one per table, at `/content/items`,
`/content/rarities`, `/content/weapons`, `/content/loot`, `/content/abilities`, `/content/npcs` and
`/content/spawns` (`c-ruinborne.md:321-333`). Each has its own drawer, its own view model, its own
hand-written validation subset and its own upsert on the `ContentStore` facade. Two tables that are real
content, `item_stat` and `item_ability_modifier`, have no page at all and are editable only by hand SQL or a
PostDeploy edit ([#510](https://github.com/APKiwiOrg/Ruinborne/issues/510)).

After adoption there is ONE page, routed `/content/{typeKey}`, rendering its grid and its drawer from
`catalog-schema` (section 10.3). A type gets its editor by registering, so `item_stat` and
`item_ability_modifier` get theirs for free and so does every Scope B type that has not been designed yet.

**The console stops being a second writer to the same database, and this is the structural half of the
change.** Today `ContentStore` holds a `relational` handle and writes SQL directly
(`ContentStore.cs:91-96` calling `SqlRuinborneStore.UpsertItemDefAsync`, a bare `MERGE ... WITH (HOLDLOCK)`
at `SqlRuinborneStore.cs:403-422`). The authoring store's one open draft, its id allocator and its single
publish transaction cannot be enforced against two independent writers, so after adoption the console calls
the admin endpoint's sixteen actions (section 10.2) and the server owns every write. The bearer token plus the
pinned certificate is the transport, and the console forwards its stable Entra `oid` as the `operator` field
(section 10.10).

What that fixes, item by item:

- **[#506](https://github.com/APKiwiOrg/Ruinborne/issues/506)**, `UpsertItemDefAsync` is the only upsert in
  the facade with no `Require(...)` gate, so an operator can save `stackable = 1, max_stack = 1` and the
  console reports success while the server rejects the whole catalog at the next boot. All nine `Require`
  call sites are deleted along with the facade: a draft edit naming an undeclared field is a 400 at the API
  boundary (section 3.7), and `KEC0025` refuses `max_stack` of 1 on a stackable row at publish (section 5.2).
  Two gates instead of a per-method habit that one method did not have.
- **[#509](https://github.com/APKiwiOrg/Ruinborne/issues/509)**, the entire durable record of an item edit is
  "someone edited item_def:sword at time T", because `AuditAsync` passes `detail` as literal null and the
  table has no value columns. `catalog_audit` is one row per FIELD change with who, when, note, type, id,
  field, before and after (section 4.6), which is the contracts' own second consumer for the field schema
  existing at all.
- **[#511](https://github.com/APKiwiOrg/Ruinborne/issues/511)**, `base_stats_json` is a dead
  `NVARCHAR(MAX)` with no reader, still a free-text box in the drawer at `Items.razor:99-100` and still
  written by the wire codec. It is not in the `item` schema of section 3.3, so after the import there is no
  field, no box and no codec arm.
- The dangling-rarity hole at `Items.razor:76-82`, where the drawer deliberately offers an unlisted
  `RarityId` as a "(current)" option so opening it cannot silently rewrite the row, and saving it leaves a
  reference that rejects the whole catalog at boot. A `KeyReference` field renders a typeahead over the
  target type's live keys, and `KEC0006` refuses a reference that is not live at the version being published.
- The dropped-field-on-save class, [#201](https://github.com/APKiwiOrg/Ruinborne/issues/201) on the item
  editor and [#250](https://github.com/APKiwiOrg/Ruinborne/issues/250) on the NPC editor, both closed and
  both the same shape: a view model round trip that silently resets a field the drawer does not carry. A
  `ContentEdit` carries the CHANGED FIELDS ONLY (section 3.7), so a field the editor never touched is not in
  the edit and cannot be reset by one.

**What this does NOT fix**, named so nobody reads the list above as complete.
[#425](https://github.com/APKiwiOrg/Ruinborne/issues/425), a stranded `max_stack` of 64 on a row whose
`stackable` is false, survives: the engine has no rule that a non-stackable row's `max_stack` must be 1, and
`KEC0025` only refuses the reverse. It stays a Ruinborne-side data-hygiene call, which is what the issue
itself calls it, and the game can add it as a registered per-type validator (contracts 4.4) in two lines if it
wants it.

### 17.8 The tests that pin it

Four, in the same order-of-value shape as section 16.7, and the second is the one that matters most because
Ruinborne's durable rows reference the catalog by a key that is about to gain an id beside it.

1. **`TheImportedBundleMatchesTheLiveDatabase`.** Build the bundle from a restored copy of the live database,
   import into an empty in-memory store, publish version 1, and assert every row's field set against the
   source rows read directly. Includes the scale conversions of section 17.6 as explicit expected integers,
   so `0.45` seconds asserting `450` is a written fact rather than a derived one.
2. **`EveryForeignKeyStillResolves`.** For each of the seven tables carrying `item_id`, assert every distinct
   value present in the live database resolves to a live content key in the published version. This is the
   test that catches a key dropped or renamed in the import, and it matters because
   `character_inventory.item_id` is a `FOREIGN KEY` the database itself enforces, so a miss here is a failed
   deploy rather than a wrong number.
3. **`TheRealToScaledIntConversionIsExactOrReported`.** A property test over the four REAL columns: for every
   value in the live database, converting to the declared scale and back reproduces the source, or the import
   reported it. Nothing converts silently and nothing converts wrongly without a line.
4. **`ThePublishedManifestIsStable`.** The same shuffled-registration-order test as Grimhollow's fourth,
   against Ruinborne's type set, which is section 15.7's engine test applied to a second real consumer.

### 17.9 The Ruinborne issues this resolves

Each row names the issue, what in this design resolves it, and where. A resolution is written against this
spec's sections rather than against a promise, so a reviewer can check the claim.

| Issue | State today | What resolves it | Where |
|---|---|---|---|
| [#506](https://github.com/APKiwiOrg/Ruinborne/issues/506) | open | The console cannot write a row the server will reject: an undeclared field is a 400 at the API boundary, `KEC0025` refuses `stackable` with `max_stack` 1 at publish, and boot fails closed rather than falling back. The ungated facade method is deleted with the facade. | 3.7, 5.2, 6.4, 9.6, 17.7 |
| [#509](https://github.com/APKiwiOrg/Ruinborne/issues/509) | open | IMPROVED, not resolved. A field-level audit through the schema gives it one row per changed field with a before and an after, which is what it is titled for. The WHO half is improved only: the forwarded `operator` is caller-asserted and unverified, so it is an audit aid among cooperating operators (section 13.3). | 4.6, 10.10, 13.3, 17.7 |
| [#510](https://github.com/APKiwiOrg/Ruinborne/issues/510) | open | A type gets its editor by registering, so `item_stat` and `item_ability_modifier` stop being hand-SQL-only content. There is no such thing as a registered type with no page. | 10.3, 17.7 |
| [#511](https://github.com/APKiwiOrg/Ruinborne/issues/511) | open | `base_stats_json` is not in the `item` schema, so after the import it is not a column, not a drawer field and not a codec arm. | 3.3, 17.2, 17.7 |
| [#512](https://github.com/APKiwiOrg/Ruinborne/issues/512) | open | There is no code-default catalog to fall back to, and a load failure is exit code 3 with findings on stderr rather than a `Console.WriteLine`. Resolved by removing the destination, not by improving the message. | 9.6, 17.5 |
| [#325](https://github.com/APKiwiOrg/Ruinborne/issues/325) | open | The wholesale rejection the issue argues to KEEP is kept, because a pack is atomic. The diagnostic the issue names as the defect is fixed: a content finding and a transport failure are different rows of section 9.6's table and are never confusable. | 9.5, 9.6, 17.5 |
| [#199](https://github.com/APKiwiOrg/Ruinborne/issues/199) | open | `item_type` and `slot` stop being bare varchars. `item_type` becomes `item.tags`, whose vocabulary is the `tag` content type with a real id and a real reference, and `slot` becomes `item.equip_profile`, a key reference to a game type. A tag row IS the reference table the issue asks for. | 3.2, 3.3, 17.2 |
| [#313](https://github.com/APKiwiOrg/Ruinborne/issues/313) | open | There is one decode path per type, the registered codec, so the hand-built `RarityDef` the item loader constructs instead of calling the shared mapper has nothing to drift from. The loader is deleted. | 3.6, 17.5 |

Three more are touched and none is claimed as resolved.
[#371](https://github.com/APKiwiOrg/Ruinborne/issues/371), whose console hardcodes one actor on all ten
owned-item verbs, is IMPROVED by the forwarded operator identity of section 10.10 and not closed by it: the
engine does not verify the field and the endpoint still holds one bearer token, so the audit is accountable to
the token holder (section 13.3).
[#279](https://github.com/APKiwiOrg/Ruinborne/issues/279), asking that `ItemDef.Validate` forbid equippable
plus stackable, is GENERALIZED by `KEC0022` (a definition declaring durability or sockets is stackable) rather
than answered: a row with an `equip_profile` and no durability is still publishable, and a game that wants the
stricter rule registers it as a per-type validator. [#299](https://github.com/APKiwiOrg/Ruinborne/issues/299)
stays open and stays Scope B's precondition, as section 17.1 says.

### 17.10 The order of steps

1. Engine phase 1 ships (section 18) and Ruinborne pins it. Unlike Grimhollow there is no branch to land
   first, so this is the first step.
2. Register the five engine types and the game types of section 17.2. No behaviour change, nothing reads them.
3. Deploy normally, so every pending PostDeploy correction has been applied to the live rows.
4. Build the bundle tool, run it against a restored copy of the live database, and read the conversion report
   (section 17.6). This is the human review gate and it is the step that cannot be automated away, because
   only the owner knows whether a value that moved by a rounding step is acceptable.
5. Import into an empty catalog database and publish version 1 through `catalog-import` then
   `catalog-publish`.
6. Land the four tests of section 17.8. All four green before anything reads the catalog.
7. Switch the SERVER's readers over, one type at a time, each with its loader deleted in the same commit:
   items and rarities, then weapons, then abilities and modifiers, then loot, then NPCs and spawns.
8. Switch the CLIENT over: read the cached pack instead of `ItemClientState`, delete `ItemRosterPush`, retire
   message kinds 16 and 17, and change the bag message's `ItemIndex` from a byte position to an int32 id.
   **All four are Scope A's, in this phase** (17.4). The index substitution in particular waits on nothing
   from Scope B: it is one number replacing another in a message body with no payload attached.
9. Add the content layer to the connect door. This is the step that requires every client to update at once,
   the same boundary Grimhollow's step 9 has, and for the same reason.
10. Delete the five loaders, `RuinborneItems`, `RuinborneRarities.DefaultRarities`, and every content block
    and content correction in `PostDeploy.sql`. The owned-item repairs stay.
11. Rewire the console onto the authoring API and delete `ContentStore` and the seven pages (section 17.7).
12. Scope B, separately: migrate `character_inventory.item_id` to the int32 definition id, after
    [#299](https://github.com/APKiwiOrg/Ruinborne/issues/299) is closed.

Steps 1 to 7 are additive and shippable one at a time. Step 8 and step 9 ship together or the bag names ids a
client cannot resolve, and step 9 is the release boundary.

## 18. Phased delivery

Four phases plus a measurement spike ahead of them. One rule runs through all of them and is stated first
because it is what the phase boundaries are chosen to protect:

**Every BYTE FORMAT ships complete in phase 1. Phases divide behaviour, never format.** The chunk, the
manifest, the text chunk, the remap rule chunk, the varint rules and every digest are section 19's expensive
column, so a phase that ships half a format is a phase that plans a migration. A phase may ship a format that
nothing reads yet, and phase 1 does: it writes per-language text chunks that no client consumes until phase 1
milestone 5, because a manifest that gains a section later is a manifest hash that changes for every
already-published version.

### 18.0 Phase 0, the measurement spike

**Ships:** a throwaway spike, not merged, measuring P1, P3, P6 and P7 of section 14 against synthetic data at
50,000 definitions through a minimal encoder. It is #882 item 12's proof spike and its output is numbers, not
code.

**Acceptance:** section 14's `Measured` column filled for those four rows, and question Q3's 1,024 slots for
`item` either confirmed or moved by the measurement. Section 14.1 argues P6 to its target at 1,024 rather than
leaving it three times over, so the spike is CHECKING an answer rather than supplying one, and the whole point
of making `chunkSlots` a per-type registration parameter is that moving it is a one-line change while no data
exists.

**Consumer:** none. This gates the OWNER's approval of the spec, not a release.

### 18.1 Phase 1, Scope A complete, Grimhollow adopts

**Ships:** all five packages of section 2.1, the complete pack format of section 7, the validator of section 5,
the publish pipeline of section 6, both authoring providers, the server runtime of section 9, all sixteen
actions of section 10.2 and 10.11, and the connect door layer of section 8.5. Grimhollow's backend is env-selected
between SQLite and SQL Server (`GrimhollowEconomyDatabase.cs:26-40`), so BOTH providers are phase 1 and
neither is deferred.

It is a large phase because #882 names Grimhollow's adoption as phase 1's acceptance, and that adoption runs
all eleven steps of section 16.8 including the client and the door. It is built in five ordered milestones,
each with its own gate, so it is not one undivided landing:

| Milestone | Ships | Gate |
|---|---|---|
| 1.1 | `KhaozEngine.Catalog`: registry, field schema, codecs, varint, hashes, the four pack formats, remap rules, `FileSystemPackStore`, `ContentPackReader`, plus `IContentSnapshot` and `ItemRow` (2.2) | The golden files of 15.1, the decoder fuzzing of 15.2, the cross-version round trips of 15.3 |
| 1.2 | `Catalog.Authoring`, `Catalog.Sqlite`, `Catalog.SqlServer`: temporal rows, draft, change set, field audit, id allocator, publish | The provider conformance suite of 15.5 on both backends, the crash-safety cases of 15.6 |
| 1.3 | `ContentRuntime`, the boot sequence, fail-closed exit 3, the derived indexes, `IContentLoadIndex` and boot step 7b, `LootRoller`, the `--catalog` benchmark mode | The twelve boot facts of 15.7, plus P3, P7, P9 and P11 measured at 50,000 |
| 1.4 | The sixteen actions, the bundle, the empty-database rule, operator identity, and the admin-result change of section 10.1: `AdminActionStatus.Conflict`, an object-carrying error payload on `AdminActionResult`, and the matching arm in `AdminHttpServer.DispatchActionAsync` | The action tests, including one asserting a real 409 body with `expectedBaseVersion`, plus P5 and P6 measured |
| 1.5 | `Catalog.Netcode`, `HttpPackStore`, `CachingPackStore`, the client fetch loop, `ContentStringCatalog` | The door tests of 15.7, plus P4 and P10 measured |

**Acceptance:** the four Grimhollow tests of section 16.7 green, and section 16.8's eleven steps complete.
Concretely, `TheImportedBundleMatchesTheShippedRoster` asserts row by row against the same literals
`GrimhollowItemsTests` asserts today, and `EveryStoredContainerStillDecodes` decodes a checked-in corpus of
real production container blobs to the SAME SLOTS before and after, an assertion that survives Scope B
replacing the validator underneath it (16.7). Those two are the acceptance. The other two are the regression
fence around it.

**Coordination with Scope B, named here because a phase table that names no edge implies there is none.**
Scope A phase 1 and Scope B phase 1 do not depend on each other and may land in either order, which is what
`IContentSnapshot` shipping in milestone 1.1 (2.2) is for. One thing between them IS ordered: Scope B phase 1
takes `ItemStack` to three components and `ItemContainerCodec` to version 2, a fleet-wide compile break plus a
durable codec bump, and it must land OUTSIDE the window of Grimhollow's adoption steps 7 to 11 (16.8). Before
step 7 or after step 11, either is fine. Inside is not, and nothing except these two sentences would have
stopped it, because each spec's phase 1 is written as though the other's is not happening.

**Consumer:** Grimhollow, [#208](https://github.com/APKiwiOrg/Grimhollow/issues/208). Its
`feature/item-drop` branch lands before any of this (gate 0 decision 12, section 16.1).

### 18.2 Phase 2, the second consumer

**Ships:** by default, nothing new in the engine. Phase 2 is Ruinborne's adoption, section 17, and its job is
to prove the engine is not shaped around one game.

That is not a licence for it to ship nothing. Three things are EXPECTED and are budgeted for:

- A `ContentBundle` conversion report helper, for section 17.6's REAL-to-scaled-int step. Ruinborne needs it
  and any consumer migrating a float column will.
- The opt-in localization coverage helper of section 15.7, which Grimhollow's existing reflection test becomes
  and which Ruinborne needs against a second string catalog.
- Whatever the SQL Server provider's first real production exercise turns up. Phase 1 gates it on an env-gated
  conformance suite that CI does not run (section 2.6), so phase 2 is where it meets a live schema.

**The rule that makes this phase a test rather than a second design round:** a change phase 2 forces to a BYTE
FORMAT is a FAILURE of phase 1 and goes back through the contracts first (contracts 18), amending them and
re-reading both specs. A change to a non-format surface, a new finding code, a new action, a schema helper, is
expected and is exactly what a second adoption is for. Writing that boundary down now is what stops phase 2
quietly widening a varint because it is easier than filing an amendment.

**Acceptance:** section 17.10's twelve steps, gated on the four tests of section 17.8. The sharpest of them is
`EveryForeignKeyStillResolves`, because `character_inventory.item_id` is a database-enforced foreign key, so a
key dropped in the import is a failed deploy rather than a wrong number.

**Consumer:** Ruinborne, [#465](https://github.com/APKiwiOrg/Ruinborne/issues/465).

### 18.3 Phase 3, scale and operations

**Ships:** the 1,000,000-definition runs, the operational actions exercised under load, and whatever the
measurements force. Candidates already named in this spec and deliberately not designed yet: the incremental
validator index of section 14.1's P5 note, and the sparse-table threshold of section 9.1, whose recommended
default (switch a type to a sorted-id binary search below one-in-sixteen live density) is question Q5. One
item is NOT a candidate but a requirement at this size: the text chunk shard of question Q4, because one
language chunk cannot exceed `MaxChunkUncompressedBytes` and 1,000,000 items is eleven shards of it.

**Acceptance:** P1, P2, P4b and P8 measured at 1,000,000, the scale tests of section 15.4 green, a language
chunk shard exercised at the size that forces one, and `catalog-sweep` and `catalog-verify` run against a
version with a deliberately corrupted chunk and a deliberately orphaned one.

**Consumer:** neither game at its current size. This phase is the claim that the design scales, made honestly
rather than assumed, and it is where the design either survives a number or gets a note in section 14 saying
which target moved and why.

### 18.4 Phase 4 and later, the named deferrals

Each is a non-goal from section 1.2 with the hook phase 1 already built for it, so none of them is a rewrite.

| Deferral | What phase 1 already built | What it still needs |
|---|---|---|
| Live apply | The `Volatile` swap field and the immutable runtime (9.7) | A quiesce point in the tick loop and a policy for a mid-tick version change. |
| Definition inheritance | `parent_id` on every row, `KEC0031` to `KEC0035`, and resolution placed at publish (1.3, 3.8) | The resolver. No pack format, chunk hash, runtime or client change, by construction. |
| Staging promotion | Version pinning and the pack store abstraction (10.7, 8.1) | A second active pointer and the page-stamp hash of gate 0 decision 5, which is a page format change and must be decided when staging arrives. |
| Multi-world activation | The active pointer as a single row (4.9) | One column. Section 4.9 says so in full. |
| Family generators | Families, aligned blocks and the ordered block list (3.8) | A template expansion at authoring time. Nothing durable. |
| Market index | Tag ids as the query surface (3.2, contracts 4.6) | An index build, outside the pack. |

## 19. Decisions that are expensive to change once data exists

This table adds ONLY what this spec introduces. Contracts section 16 is the parent table and is not repeated
here: the int32 definition id, the never-reuse rule, family block sizes, key immutability, the page stamp
being the number, the manifest hash algorithm, chunk size being a power-of-two slot count, remap rule
append-only ordering and encoding, tag ids as the tag representation, a published field being retired rather
than removed, the stat scale and rounding rules, little endian, the varint definition and SHA-256 are all
decided THERE and are binding here unchanged.

"Expensive" keeps the contracts' meaning: rewriting durable bytes that already exist in a consumer's
production database, or in a player's cached pack, rather than a recompile or a republish.

| Decision | Section | Cost if changed later |
|---|---|---|
| The six engine type ids and their keys: 1 `tag`, 2 `item`, 3 `stat`, 4 `loot_table`, 5 `loot_entry`, 6 `base_socket` | 3.1 | Every chunk in every published pack is addressed by type id, and `KEC0029` refuses the change outright. A renumber is a new catalog. |
| Engine ids stop at 255, Scope B takes 256 to 1023, games start at 1024 | 3.1, contracts 4.3 | A game type sitting in a range the engine later claims collides silently at registration in a future engine release. |
| Loot entries are their OWN content type, not a repeated group in an opaque field | 3.1 | Every entry has an id and a key. Folding them into the table later retires every entry id and rewrites every table row. |
| The `item` type's field set as shipped | 3.3 | A published field is retired, never removed (contracts 4.7). Adding is cheap, so the cost here is only in what shipped wrong. |
| Asset references are `opaque bytes` holding a varint length plus UTF-8, capped at 128 bytes, character set `a-z0-9_./-` | 3.3 | Changing the encoding restates every row carrying an icon, mesh or held mesh. Raising the cap is safe. Lowering it strands rows already over it, the same shape as contracts' `MaxInstancePayloadBytes`. |
| `MaxContentRowBytes = 4096` | 7.3 | Raising is safe. Lowering strands every row already over it and makes a published version unrepublishable. |
| The default `chunkSlots` per engine type: 1,024 for `item`, 4,096 for `tag`, `stat` and `loot_table`, 16,384 for `loot_entry` and `base_socket` | 3.1, 4.5 | Renumbers every chunk of that type and invalidates every cached client pack for it. `KEC0029` refuses a change after the type's first publish. Question Q3 is why `item` is the odd one out, and it is settled before the first publish rather than after. |
| The chunk body's canonical byte layout, which is what the chunk hash is taken over | 7.3 | Every chunk hash in every manifest of every published version changes, so it needs a `SchemeVersion` bump and a re-digest. |
| The hash domain prefix `kec/` and its five sub-domains | 7.8 | The same re-digest, plus a gate that compared one manifest side could start agreeing with the other. |
| The version number is monotonic from 1, plus exactly one per publish, never reused and never skipped | 12.1 | A durable container page stamps it and a remap rule applies to any page stamped OLDER than the rule. A gap or a reuse makes "older" ambiguous. |
| A retire's POLICY for a specific definition, placeholder versus replacement | 3.9, 16.4 | Contracts 8.6: a retire is irreversible for pages already migrated past it. Grimhollow's 18 retired ids import as placeholder, and choosing replacement later cannot reach the pages already migrated. |
| The `ContentBundle` format version | 10.9 | A bundle is the lossless export format as well as the seeding format, so an old bundle needs a reader for as long as anyone might import one. |
| The authoring store's temporal row shape: `valid_from_version` and `replaced_in_version` per row version | 3.7, 4.3 | Rewrites the operator's whole authoring database. Cheaper than the rows above because the store HAS a migration path by design (4.2's `CurrentVersion` and `RequiredMigration`), which is exactly what a pack format does not have. |
| `catalog_row_field`, one row per field, rather than one encoded blob per row | 4.3 | The same migration, plus every audit row before the migration loses its field-level meaning. |
| Grimhollow: definition ids 1 to 35 preserved exactly at import | 16.4 | Every stored `ItemContainer` blob names them, so a moved id is a player whose items are not the items they had. Today `ValidateContainer` THROWS on an unknown id and they cannot log in at all. After Scope B replaces it with the engine validator plus quarantine the failure is gentler, and the id is no less load bearing. |
| Ruinborne: ids allocated in key-ascending order at import | 17.3 | The ids exist after the import. A different order is a different catalog, and `character_inventory` will reference them after Scope B. |
| Ruinborne: the four REAL-to-`ScaledInt` scales, 100 for stats and modifier values, 1000 for range, arc and cooldown, basis points for drop chance | 17.6 | Restates every number those columns hold, in both directions, and a rescale after publish is a balance edit applied silently (gate 0 decision 4). |

Everything else this spec introduces is cheap by comparison and is named so the table is not read as
exhaustive in the other direction: the finding CODES (contracts 16 already lists the validator's findings as
cheap, and this spec's rule that codes are never renumbered is a convenience for runbooks rather than a
durability constraint), the exit code 3 and its stderr text, the `FileSystemPackStore` directory sharding, the
action names and their status codes, the per-field visibility assignments, the package names, the benchmark
flags, and every localization key including Grimhollow's two renames in 16.5.

## 20. Contract change requests

Contracts 18 sets the rule: a spec may REFINE anything in the contracts and may not CONTRADICT it, and when
a spec finds it NEEDS a contradiction, the change comes back to the contracts first, is amended there, and
both specs are re-read against the new text before either is approved. Three requests follow. All are narrow,
all are stated with the change written out, and the rest of this spec is designed on the contracts AS
WRITTEN so that a refusal costs an editor feature and a paragraph rather than a redesign.

### CCR-1: an `AssetReference` value kind in contracts 4.7

**Contract section:** 4.7, the value kind list.

**What needs to change:** add `asset reference` to the list of value kinds, defined as a varint length
followed by UTF-8 bytes, character set `a-z0-9_./-`, at most 128 bytes.

**Why.** Three of the `item` type's fields are asset references: `icon`, `mesh` and `held_mesh`, replacing
Grimhollow's `ItemIcons.RosterIcons`, `GroundItemMeshes.Roster` and `GrimhollowHeldMeshes.For` and
Ruinborne's `item_def.icon_id` (section 3.3). Their values look like `kit/unknown_item.glb`
(`b-grimhollow.md:709`), which cannot be a content key because keys are `a-z0-9_` only with no dot and no
slash (contracts 5.3), and contracts 4.7's kind list has no plain-string kind. So on the contracts as
written the only home for them is `opaque bytes`, which is what section 3.3 specifies and what the design
assumes throughout.

**What the absence costs, exactly.** Nothing in the pack format, nothing in the runtime and nothing in
validation: the codec constrains the character set and the length either way, and `KEC0026` still caps the
row. It costs ONE thing, and it is the thing contracts 4.7 lists first among its three reasons for existing:
a generic editor cannot know these bytes are text, so it renders a hex box where an author wants a file path
with a typeahead. Every consumer console would then special-case three field names by hand, which is the
bespoke-screen-per-type outcome the field schema exists to prevent.

**If refused:** section 3.3 stands unchanged, the three fields stay `opaque bytes`, and each console adds a
hand-maintained list of which opaque fields are really paths. Section 21 does not carry this as a question,
because it is a contracts decision rather than an owner preference.

### CCR-2: contracts 5.1 needs a narrow exception for a one-time bundle import

**Contract sections:** 5.1 (allocation), against 6.5 (how the two consumers' existing identities map).

**What needs to change:** contracts 5.1 says "Ids are allocated by the AUTHORING STORE, never by an
importer, a code constant or a file order." Add the exception: **`catalog-import` into an EMPTY database MAY
carry explicit ids, and the store adopts them and sets its high-water mark above the highest imported id per
type. No other write path may name an id, ever.**

**Why.** Contracts 6.5 already requires the outcome that 5.1 forbids the mechanism for. It says of
Grimhollow: "its definition ids map one to one and no stored container changes value", and grades the
migration "no" in its own expensive column because it is a no-op. That is only true if the import PRESERVES
ids 1 to 35 rather than allocating fresh ones. Section 16.4 specifies exactly that, and it is the only way
the outcome contracts 6.5 promises can be reached, because the ids already exist in every stored
`ItemContainer` blob and `GrimhollowJournalContracts.ValidateContainer` THROWS on an unknown item id, so a
moved id is a player who cannot log in (`b-grimhollow.md:545-556`).

So this is a contradiction INTERNAL to the contracts, surfaced by writing the adoption plan, rather than
this spec wanting something new. Two sections cannot both be right.

**Why the exception is safe as scoped.** The empty-database rule (section 10.9) already makes import a
once-ever operation per database, refused with a 409 otherwise, so there is no second import to disagree
with the first. The store sets `reserved_through` and `issued_through` to the maximum imported id per type
immediately afterwards (section 16.4), so every subsequent id comes from the allocator under the ordinary
reserve-before-issue rule and the never-reuse guarantee of contracts 5.1 is untouched. The spirit of 5.1 is
that no ONGOING path mints an id outside the store, and that spirit survives intact.

**If refused:** Grimhollow cannot adopt without renumbering its 35 item ids, which rewrites every stored
container in production and contradicts contracts 6.5's own grading. There is no third option, so this
request is a genuine blocker on phase 1 rather than a preference.

### CCR-3: contracts 4.7 should say the `localized text key` kind is a MARKER

**Contract sections:** 4.7 (the value kind list), against 12.1 (key derivation).

**What needs to change:** contracts 4.7 lists `localized text key` beside `int`, `bool` and the rest, which
reads as a kind whose field HOLDS a value. Say instead: **a `localized text key` field carries no value. Its
key is derived as `<type key>.<content key>.<field>` per 12.1, the authoring store holds nothing for it, the
row codec writes no bytes for it, and the string it names lives in the language text chunk.**

**Why.** 12.1 already requires the derivation and gives the reason, "derivation rather than authoring, because
an authored key rots independently of the row it names". 4.7 read alone does not carry that through to
storage, and this spec originally stored the derived string in `catalog_row_field.text_value`, wrote it into
every row body, showed it in an editable box and accepted it on an edit payload. A stored copy of a derived
value is a field that can disagree with its own derivation: `stone_sword.name` holding
`item.iron_sword.name` shows another row's display name, passes every check that does not re-derive the
answer, and costs 14 to 25 bytes per localized field per row in every chunk and every client download for a
value the reader can compute. Section 3.2 now states the marker rule and the rest of the spec follows it.

**What the absence costs, exactly.** Nothing, as long as every spec reads 4.7 through 12.1 the way this one
now does. The request is for the contracts to say it once so the next spec does not have to find it the way
this one did, through a review.

**If refused:** section 3.2 stands as a Scope A refinement of 4.7 rather than a restatement of it, because
"carries no value" is narrower than "carries a value of this kind" and a refinement is licensed. Nothing in
this spec changes.

### A note that is NOT a change request

`KECT`, the per-language text chunk magic, is added by this spec (section 7.1) and contracts 15 does not
list it. That is not a contradiction: contracts 15 gives the RULE for magics, a four-character ASCII prefix
on a format stored standalone, and lists the four it knew about. It also grades the case explicitly,
"expensive to change once data exists: yes for endianness, the varint definition and the digest algorithm.
**No for adding a magic** or bumping a version." So `KECT` is a refinement under the rule and is recorded
here only so a reader diffing the two magic lists knows it was deliberate.

### What was re-read, and found consistent

Sections 1 to 15 of this spec were re-read against the contracts after the adoption plans were written,
because an adoption plan is where a drift shows up. CCR-2 is what that pass found. These are the places the
pass checked hardest and cleared, named so a reviewer knows where to look rather than re-deriving the list:

| This spec | Contract | Verdict |
|---|---|---|
| The five packages, 2.1 | 3.2 | Identical set. `KhaozEngine.Content` untouched, as 3.1 requires. |
| Engine type ids 1 to 5, 3.1 | 4.3 | Inside `1` to `255`. Scope B's `256` to `1023` and the games' `1024` upward left alone. |
| `chunkSlots` 1,024, 4,096 and 16,384, 3.1 | 4.5 | All three are powers of two inside `256` to `65,536`. 4.5 explicitly licences per-type tuning with spike measurements. |
| Loot entries as their own type, 3.1 | 4.7 | A refinement. 4.7's kind list has no repeated group, and this avoids needing one, which is why no CCR asks for one. |
| Family blocks, 3.8 | 5.2 | Same bounds, same alignment rule, same second-block behaviour, same never-deleted rule. |
| Definition ids allocated reserve-before-issue, 4.7 | 5.1, 6.2 | A refinement. 6.2 states the ORDER rule for instance ids and 5.1 leaves definition-id mechanics open, so applying the same order is narrowing, not contradicting. |
| `KEC0014`, client-side bytes still carrying a `ServerOnly` field, 5.2 | 11.3 | 11.3's second bullet is the OMISSION, which section 6.7 does at encode time. `KEC0014` is the publish-time check that it happened, rather than a warning. |
| `chance_bp` basis points out of 10,000, 3.5 | 13.2 | The same percent representation and the same round-half-up shape, so there is one convention rather than two. |
| The connect door layer order, 8.5 | 7.5 | Content sits inside world and outside auth, and a client that is behind is refused rather than admitted read-only, which is gate 0 decision 6. |
| The page stamp is the number, 12.1 | 7.2, gate 0 decision 5 | This spec supplies the number and never asks a page to carry a hash. |
| Version number monotonic and never skipped, 12.1 | 7.1, 8.3 | A refinement. 7.1 makes the number the ordering, and "never skipped" is what makes "older than the rule" decidable. |
| Retired rows stay in the pack forever, 3.9 | 5.1, 8.6 | Same rule, same irreversibility, and the retire policy is on the remap rule rather than the row, as 8.2 has it. |

## 21. Open questions for the owner

Seven, each with a recommended default so a non-answer is still a decision rather than a stall. Nothing here
is a contracts question: those are section 20's three change requests. Q3, Q4 and Q5 are referenced from the
sections that raised them and are repeated here in full so this section reads standalone. Q3 and Q4 are
ANSWERED in the spec rather than left open, because both are expensive after the first publish, and Q5's key
half is answered by the stage 5 measurement that missed two budgets against it. All three stay in this section
so the owner can overrule them.

### Q1. Where do the chunks get served from in production

Section 8.6 weighs three endpoints and recommends a static host or CDN by a wide margin, 66 to 33 to 28, with
the thundering-herd row decisive: v1 applies a version at server restart, so every connected player
reconnects at once and every one of them wants the same chunks. The engine ships `HttpPackStore` and cares
about nothing else, so this is a provisioning decision rather than a design one, and it is here because it
costs the owner real money and real setup and phase 1 milestone 5 cannot be accepted without it.

**Recommended default: the same Azure Blob feed each game already uses for its client updater.** Both
consumers already ship one, for the reason AGENTS.md records (a private repo cannot serve Release assets to
an unauthenticated client), so the deployment shape, the auth model and the operator's familiarity are all
already paid for. A separate container per game, addressed by hash, with no listing enabled.

### Q2. Who sets `MinimumClientBuild` and `MinimumServerBuild` on each publish

Section 12.1 makes these the GAME's numbers, supplied per publish, with the engine only comparing them. What
it does not settle is the console's behaviour: does the operator type them every publish, or do they carry
forward. Typing them every time invites a typo that locks every client out of a version, which is the worst
operational failure in this spec that is not a data failure. Carrying them forward invites a publish that
needed to raise one and did not, which refuses nobody and lets an old client decode a row it cannot
understand.

**Recommended default: carry forward from the previous version, and require an explicit tick plus the new
number to raise either.** The asymmetry is deliberate. A forgotten raise is caught by the format generation
check (section 9.6 row 4), because an engine-owned codec change bumps `FormatGeneration` and that is the case
a stale minimum build actually matters for. A typo'd raise is caught by nothing.

### Q3. The `item` type's default `chunkSlots`, taken at 1,024 and open to being overruled

**This question is ANSWERED IN THE SPEC rather than left open, and it is here so the owner can overrule it.**
Section 3.1 registers `item` at 1,024 slots and every other engine type at 4,096 or above. At the 4,096 this
spec first carried, a one-item edit rebuilds a 533 KB chunk that stores at roughly 180 KB, against budget P6's
80 KB target. At 1,024 it is 133 KB storing at about 44 KB, and P6 holds with margin. The cost is four times
the manifest entries for that one type, 49 at 50,000 definitions and 977 at 1,000,000, which is a few tens of
kilobytes of manifest.

It is settled rather than deferred because section 19 puts `chunkSlots` in the expensive-after-data column:
changing it renumbers every chunk of the type and invalidates every cached client pack for it, so it is a
decision to take before the first publish, and phase 0's spike lands after the first publish would otherwise
have happened. Phase 0 still MEASURES it, and if the measurement says 2,048 is the better point the change is
one line taken before any data exists.

**The decision to overrule, if the owner wants it: accept about 180 KB per edit and keep 4,096 everywhere.**
That is a coherent choice if manifest size matters more than edit size, and nothing else in the design
depends on which one is picked.

### Q4. When a language's text chunk should shard

Section 14.1 computes the chunk once, with the derived keys and every localized field of every engine type:
**8.7 MB uncompressed at 50,000 items, 100,800 entries, about 2.2 MB stored.** The earlier figures in this
spec were 6 MB in one section and 9 MB in another, one counting no key bytes and one counting only the item
type's two fields, which is why the number is now computed in exactly one place.

The 8 MB threshold this question used to recommend was picked against the 6 MB figure, and the corrected 8.7
MB is ALREADY PAST IT. So the threshold has to move or sharding ships in phase 1, and there is a third
constraint that settles it: a language chunk cannot exceed `MaxChunkUncompressedBytes`, which at 86 bytes an
entry is about 195,000 entries, so the 1,000,000 figure needs eleven shards whatever anyone prefers.

**Recommended default: one chunk per language up to 12 MB uncompressed, sharding by id range above it.**
Twelve leaves the 50,000 case comfortably inside one chunk at 8.7 MB, sits well under the 16 MiB hard cap so
the shard boundary is a choice rather than a cliff, and keeps the sharding code out of phase 1 exactly as the
old answer intended. Above the threshold a language shards by content id range, matching the chunking of the
types it names, so a one-string change re-downloads one shard. Section 18.3 carries it with the other two
deferred optimizations, and unlike those two it is a correctness requirement above about 97,000 items rather
than a performance one.

### Q5. How hard to work at runtime memory before it is measured

**The key half is ANSWERED BY MEASUREMENT, and the answer was "harder than this question's default said".**
The default was to ship the string-keyed layout and measure. The measurement missed two budgets: P3's heap at
26.3 MB against 20, and P10's resident at 44.2 MB against 32, both of them UTF-16 inflation of bytes the pack
already carries as UTF-8. So the layout changed before the spec moved on, which is what 9.1, 9.2 and 7.6 now
describe, and the numbers are on the record in 14.3 rather than in a recommendation here.

At 50,000 definitions the runtime heap went from 26.3 MB to 12.3 MB and the runtime's own accounting from
20.4 MB to 10.7 MB, and one decoded language went from 44.2 MB to 9.2 MB, which is 460 bytes an entry to 96.
At 1,000,000, where nothing is budgeted and the figures are informational, the runtime went from about 425 MB
to about 180 MB and one language from 486 MB to 185 MB. Load time did not pay for it: the decode pass stops
building a string per row and got faster, 37 ms to 32 at 50,000 and about 580 ms to about 390 at 1,000,000.
The prize the old recommendation quoted was 48 MB at the stress figure, and taking the key-to-id dictionary
with it made the measured saving five times that.

**The sparse table is the half that stays open, and it is the same recommendation as before.** `Offsets` and
`Lengths` are parallel to the id space rather than to the row count, so a type with a family block based at
65,536 and 40 members costs about 533 KB for 40 rows (section 9.1, halved by the key change and still the
shape of the problem). Switch a type to a sorted-id binary search when live density falls below one in
sixteen, in phase 3 with a number rather than in phase 1 with a guess, because no engine type is sparse
today. Worth noting that the two halves did NOT have the same answer: the key half was worth doing before the
first line of the implementation, and this one is still worth deferring.

### Q6. How many superseded versions the pack store keeps

Section 6.12's sweep deletes only files referenced by NO version, deliberately, because a pinned server and a
rollback both need older versions fetchable. So the store grows by the changed chunks of every publish,
forever, and nothing in this spec ever prunes a version that was once published. At one item edited per
publish and 250 KB per edit that is slow growth, and at a large content drop per publish it is not.

**Recommended default: retain the last 20 versions plus every pinned one, and prune below that with an
explicit operator action rather than automatically.** The action is a new one, not designed here, because a
prune is the one operation in this spec that destroys something a rollback might want and it should not be a
side effect of an ordinary publish. Until it exists, an operator deletes by hand against `catalog-versions`,
and `catalog-verify` is what confirms the active version survived.

### Q7. Grimhollow's global tuning knobs

Section 16.2 maps `items.jsonc`'s `drop.despawnSeconds` to "a `skill_curve` style singleton row, or a game
config outside content" and recommends the former. It is a global knob rather than per-definition tunable
content, so it is the one row in the Grimhollow mapping that does not obviously belong.

**Recommended default: a one-row `game_tuning` game type, id `1035`.** The argument is that it is versioned,
audited, rolled back and diffed with everything else for free, and the alternative leaves one number in a
jsonc file that section 16.8 step 9 is otherwise deleting, which reopens the two-sources-of-truth problem the
adoption exists to close. The cost is one content type holding one row, which is cheap, and a type with one
row is still rendered by the generic editor with no extra work.

## 22. Appendix A: review log

Stage 4 was an adversarial read of this spec against `CONTENT-CONTRACTS-DESIGN-2026-09-14.md` and against the
engine tree, at `1c5a51f4`. It raised 29 findings. Each was then verified independently against the cited
lines, and the verified verdict is what decided the disposition: three findings were REFUTED as stated and
carry only the wording fix their verification suggested, three were PARTIAL where the gap was real and the
consequence was not, and one was a LEAD that named no mechanism.

Every row is here, including the refuted ones, because the reason a finding was wrong is worth as much to the
next reader as the reason another was right.

| ID | Claim | Reviewer severity | Verified verdict | Disposition | Commit |
|---|---|---|---|---|---|
| F1 | `AllocateAsync` can issue a plain id already inside a reserved family block | high | Confirmed, high | Fixed. Family reservation advances `issued_through` past the block top, with the alternative weighed, plus `KEC0036` and `KEC0037` in pass 1. 4.7, 5.2, 5.3, 15.5, 16.4 | `6cb5dfdb` |
| F2 | The publish sweep deletes the remap rule chunk and every text chunk | high | REFUTED, low, editorial | Rejected as a finding: `ListAsync(version)` reads the manifest, which names both hashes, so the sweep never had them out of its keep set. The wording fix was applied anyway, since the keep set was described against `catalog_chunk`. 6.12 | `6cb5dfdb` |
| F3 | `catalog_chunk`'s key cannot hold both hashes of a mixed-visibility chunk | high | Confirmed, high | Fixed. `visibility` joins the primary key, carry-forward and commit are per side, a `Client` type may carry a per-field `ServerOnly` override, `KEC0014` fires only on client-side bytes. 4.3, 4.4, 6.6, 6.7, 6.8, 6.10, 13.1 | `6cb5dfdb` |
| F4 | 409 and structured error bodies are not expressible through `RegisterAction` | high | Confirmed, medium | Fixed. The engine change is named and budgeted: `AdminActionStatus.Conflict`, an object error payload, the dispatch arm. 2.1, 10.1, 10.2, 18.1 milestone 1.4 | `7f241ca3` |
| F5 | The `ContentBundle` round trip loses the version history, so it is not a backup | high | REFUTED, low | Rejected as a finding: an import runs through `catalog-publish`, which restamps at version 1, and re-seeding a live world is not a path this spec offers. The wording fix was applied: a lossless export, with an ordinary database restore named for the version line. 10.9, 19 | `5f60805b` |
| F6 | A chunk is decompressed before its hash can be checked, with no size refusal | high | Confirmed, medium | Fixed. `chunk-too-large` and `chunk-stored-length` are header-level refusals before any allocation, decompression is bounded, both tokens are in the fixed list. 7.2, 7.6, 8.4, 15.2 | `7f241ca3` |
| F7 | `chunk_slots` is mutable after a publish with no guard | medium to high | PARTIAL, low. The guard is missing. The consequence is a version no reader loads, not silent wrong rows | Fixed minimally. `KEC0029` covers `chunk_slots` after the first publish, and 3.6 states the fail-closed consequence correctly. 3.6, 5.2 | `5f60805b` |
| F8 | A draft cannot hold an `Update` and a `Retire` for one row, and one is discarded | unstated, reads medium | REFUTED, low | Rejected as a finding: the stated observable is a unique-index refusal, and the draft-edit audit records the change. One sentence added making the collision explicit for a second edit with a different operation. 4.4 | `5f60805b` |
| F9 | Audit value columns cap at 512 while a field caps at 4,096, so an audit failure fails the edit | medium | Confirmed, medium | Fixed. The caps widen to 4,096 and a rendering that still overflows is abbreviated visibly. 4.4, 4.6 | `b63cd535` |
| F10 | `catalog-pin` has no column, and `sealed_flag` and `store_epoch` are unexplained | medium | Confirmed, medium | Fixed. `pinned_version` added, `store_epoch` given its stated job, `sealed_flag` deleted. 4.3, 4.4 | `b63cd535` |
| F11 | `ListAsync(version)` cannot learn a version's manifest hash from a content-addressed store | medium | Confirmed, medium | Fixed. The publisher writes a `versions/<n>` pointer at step 9 and `ListAsync` reads it. 6.9, 6.11, 6.12, 8.1, 8.2 | `b63cd535` |
| F12 | The import path has two id mechanisms and the schema expresses one | medium | Confirmed, medium | Fixed. A bundle row's id is optional, stated once in 6.3 and once in 10.9, with 4.4, 16.4 and 17.3 reading as two cases of one path | `7f241ca3` |
| F13 | The text chunk's canonical form is never defined, so its hash is not reproducible | medium | PARTIAL, low. The prose gap is real. The lockout is not reachable, since one `OfBytesForKind` serves both sides | Fixed minimally. 7.6 writes out the construction and the header gains a `reserved` byte | `5f60805b` |
| F14 | No boot refusal for a manifest type with no registration, or a registered type absent from the manifest | medium | Confirmed, medium | Fixed. Two exit rows at step 6, both exit 3, with the mirror case argued. 9.6, 15.7, 18.1 | `b63cd535` |
| F15 | `MaxChunkUncompressedBytes` and `MaxContentRowBytes` are not consistent by construction | medium | Confirmed, medium | Fixed. A per-type `maxRowBytes`, a registration bound, and `KEC0038` as the proof the bound held. 3.1, 3.6, 5.2, 5.3, 7.1 | `b63cd535` |
| F16 | The `Keys` memory line omits the string payload, and the sparse example counts one array | medium | Confirmed by arithmetic, medium | Fixed. Both recomputed and Q5 re-argued against the corrected numbers. 9.1, 9.2, 14.1, 21 Q5 | `b63cd535` |
| F17 | Localized text keys are authored row values, which contracts 12.1 forbids | medium | Confirmed, medium | Fixed. A localized text key is a derived marker carrying no value and no row bytes, stated once in 3.2. 3.2, 3.3, 3.4, 4.4, 5.2, 7.1, 7.9, 10.3, 10.4, 10.5, 14.1, CCR-3 | `6f41bf93` |
| F18 | No client cold start budget at 1,000,000, and the number is bad | medium | PARTIAL, low. The missing row is real. The restart herd is wrong, because the cache is content addressed | Fixed. Budget P4b with the 72 s arithmetic, on 18.3's acceptance, with the warm case named as P6 sized. 14, 14.1, 18.3 | `05f9bda9` |
| F19 | The rollback un-retire branch is unreachable and reuses `KEC0017` | medium | Confirmed, medium | Fixed. The branch is deleted, a retire is never reversible by rollback, and the blocker is `KEC0039`. 5.2, 5.3, 6.13, 10.8, 12.2 | `b63cd535` |
| F20 | The pin has two sources and the action writes only one | medium | Confirmed, medium | Fixed. One precedence order, config first, plus the manifest `versionNumber` cross-check at boot. 9.5, 9.6, 10.7 | `b63cd535` |
| F21 | `KEC0023` refuses the `required_tags` draw shape 3.5 describes | medium | Confirmed, medium | Fixed. Exactly one of `item`, `nested_table` and a non-empty `required_tags`, with more than one refused rather than ranked. 3.5, 5.2 | `b63cd535` |
| F22 | The manifest carries no per-chunk size, so 9.2's load buffer cannot be sized as described | low | Confirmed, low | Fixed. `uncompressedBytes` per chunk in the file, outside the canonical text, cross-checked against the chunk header. 7.4, 9.2 | `5f60805b` |
| F23 | P6's 80 KB target is refuted by 14.1's own 250 KB arithmetic | low to medium | Confirmed, low | Fixed. `item` registers at 1,024 chunk slots per Q3's own recommendation and every dependent number follows. 3.1, 7.4, 8.2, 8.7, 9.1, 9.2, 10.3, 13.5, 14.1, 18.0, 19, 21 Q3 | `05f9bda9` |
| F24 | The text chunk size is computed two ways and both are wrong | low to medium | Confirmed, low | Fixed. Computed once in 14.1 with the keys and every localized field, read there by 7.6 and Q4, and sharding is mandatory above about 97,000 items | `6f41bf93` (14.1), `05f9bda9` (7.6, Q4, 18.3) |
| F25 | The action count is eleven, fourteen or sixteen by section | low | Confirmed, low | Fixed. Sixteen, everywhere. 10.1, 10.2, 10.11, 16.6, 17.7, 18.1 | `05f9bda9` |
| F26 | "40 finding codes" is 36 | low | Confirmed, low | Fixed. Thirty-nine issued across `KEC0001` to `KEC0040`, with `KEC0013` withdrawn. 5.2, 15.7 | `05f9bda9` |
| F27 | `KEC0013` as worded fires on the legal per-field override | low | Confirmed by quote, low | Fixed with F3. `KEC0013` is withdrawn and never reissued, and its real content is `KEC0014`. 5.2, 5.3 | `6cb5dfdb` |
| F28 | Operator identity is unverified and optional, so it cannot do the accountability job it is given | low | Confirmed by quote, low | Fixed. 13.3 and 17.9 now say audit aid among cooperating operators, and Ruinborne 371 and 509 are improved rather than resolved | `05f9bda9` |
| F29 | The `equip_profile` reference target is a game type the engine schema cannot name | lead | LEAD | Fixed by naming the mechanism: the engine schema names the type KEY, a game registers under it in its own range, and an unregistered target means the field must be 0, refused otherwise by `KEC0007`. 3.3 | `5f60805b` |

Three findings were rejected AS FINDINGS and still changed the text, which is worth separating: F2, F5 and F8.
In each case the mechanism the finding attacked was sound and the SENTENCE describing it was not, so the fix
is wording and the design did not move. A reader who reaches for one of those three sections later should know
the argument was tested and survived.

One procedural note, because the appendix is the place for it: F17's verdict is carried by the stage 4
disposition rather than by a verifier line of its own, unlike the other 28.

### The cross-spec consistency round

A second adversarial read followed, this one across THREE documents at once: the contracts, this spec and the
Scope B instances spec, looking for the failures a single-document review structurally cannot see. Two specs
that each read correctly on their own can still disagree about who owns a type, which package a seam lives in,
what a shared signature takes and which phase a fleet-wide break lands in. It raised 30 findings across both
scopes. The rows below are the ones this spec owns, and the disposition on each is the verified one.

Three of them are the same finding seen from two sides, so they appear here and again in the Scope B spec with
the half each owns: C18, C26 and C30.

| ID | Claim | Reviewer severity | Verified verdict | Disposition | Commit |
|---|---|---|---|---|---|
| C1 | The `IRandomSource` seam sits in Scope B's package, which depends on this one, so the edge this spec needs would close a cycle | high | Confirmed, high | Fixed by the contracts amendment. The seam and both implementations live in `KhaozEngine.Primitives`, below both, and this spec takes that edge. 2.1, 2.5, 2.6, 3.5, 9.4 | `17b05e5f` |
| C3 | Nothing can produce the `MovedToLegacy` rule Scope B's keep-legacy flow depends on: three edit operations and none emits kind 3 | high | Confirmed, high | Fixed. A fourth operation, `Fork`, with the alternative of a keep-legacy `Retire` policy weighed and rejected. It allocates the id, copies the field set, sets the caller-named flag field and appends the kind 3 rule as one atomic edit. 2.3, 3.7, 4.4, 6.3, 6.5, 10.5, 5.2 `KEC0041`, 15.7 | `ec3bd641` |
| C4 | Nobody owns the loot roll: this spec indexes and budgets it, Scope B disclaims it | high | Confirmed, high | Fixed. `LootRoller` is a ninth deliverable, the one implementation of 3.5's composition rule. It returns item ids and counts, creates nothing, and the journal event a game records stays the game's. 1.1, 2.2, 3.5, 18.1 milestone 1.3 | `ec3bd641` |
| C5 | Scope B's phase 1 is designed behind an `IContentSnapshot` that appears nowhere here, and "the one shared type is `ContentVarint`" is not true of the coupling either spec describes | high | Confirmed, high | Fixed. `IContentSnapshot` with seven members ships in milestone 1.1, and 2.5 now tables the real shared surface instead of naming one type. 2.2, 2.5, 18.1 | `17b05e5f` |
| C7 | Grimhollow's `EquipStats` has two destinations, an `equip_profile` row here and stat lines in Scope B, and neither plan cites the other | medium | Confirmed, medium | Fixed by splitting rather than choosing. The row holds the slot and the archetype, a child game type holds the numbers as `Flat` lines the evaluator folds at source kind 1. Both plans now say so. 3.3, 3.6, 16.2 | `e28dea2d` |
| C11 | `socket_max` is an int, and Scope B's generator needs to know which socket TYPES a base is authored with, and in what order | medium | Confirmed, medium | Fixed. `base_socket`, a sixth engine content type, the same child shape as `loot_entry`. `socket_max` stays as the cap the AddSocket primitive checks. 3.1, 3.3, 7.1, 14.1, 19 | `e28dea2d` |
| C12 | A rarity id must fit a byte and nothing stops the allocator issuing a 256th | medium | Confirmed, medium | Fixed. An optional `maxDefinitionId` at registration, refused by the allocator and by `KEC0042`, with retired rows counting toward it and Scope B declaring 255 on `rarity_rule`. 3.6, 4.4, 4.7, 5.2 | `ec3bd641` |
| C13 | Three required checks are statements about a CHANGE and the validator cannot see the previous version | medium | Confirmed, medium | Fixed by the contracts amendment. `Validate` takes `ContentSnapshot? previous`, null at boot and in tests, populated at publish. `KEC0003`, `KEC0029` and Scope B's tier-order check are publish-only and `KEC0000` names them when they are skipped. 5.1, 5.2, 5.3, 5.4, 6.4 | `17b05e5f` |
| C14 | The boot order is closed and has no hook for a Scope B or game load-time derived table | medium | Confirmed, medium | Fixed. `IContentLoadIndex` registered beside a codec, boot step 7b after the engine's four indexes and before the validator, with P3 stated to exclude them. 3.6, 9.4, 9.5, 9.6, 14, 18.1 | `17b05e5f` |
| C16 | Ruinborne's wire-index change is scheduled by this spec's phase 2 and labelled Scope B's work, whose adoption is three phases later | medium | Confirmed, medium | Fixed by claiming it. A definition-id substitution with no payload is this spec's, in phase 2, and Scope B's row records the outcome without scheduling anything. 17.4, 17.10 step 8 | `e28dea2d` |
| C18 | Scope B phase 1 takes `ItemStack` to three components, a fleet-wide compile break neither phase table sequences against Grimhollow's adoption | medium | Confirmed, medium, both | Fixed, this spec's half. The bump lands before step 7 or after step 11 of 16.8, never inside the window, named in both the adoption plan and the phase table. 16.8, 18.1 | `e28dea2d` |
| C19 | Scope B's seven validators have no home in the finding codes or the sweep, so they would report as game findings | medium | Confirmed, medium | Fixed. `KEC0100` to `KEC0199` reserved for Scope B, run as pass 6 inside the sweep and before any game validator, with the cross-product check added to P8's derivation. 5.2, 5.3, 14.1 | `17b05e5f` |
| C20 | P3 is a 400 ms restart that is really about 1.15 s, because the text decode and the per-type index builds are outside it | medium | Confirmed, medium | Fixed. P11, total cold boot to accepting connections, measured in one harness through a `--compose` flag rather than summed on paper. 14, 14.1, 14.2, 18.1 | `17b05e5f` |
| C21 | `EveryStoredContainerStillDecodes` asserts through a `ValidateContainer` that Scope B deletes | medium | Confirmed, medium | Fixed. The assertion is "decodes to the same slots", independent of which validator runs, and 16.7 says why the replacement does not weaken it. 16.7, 18.1, 19 | `e28dea2d` |
| C23 | The derived localization key is stored as a field value, which the contracts derive | low | Confirmed, medium, as F17 | Already fixed in the first round as F17, which is the same finding reached from the contracts side. A localized text key is a derived marker carrying no value and no row bytes. 3.2 | `6f41bf93` |
| C26 | Contracts 4.4 forbids a game registering into the engine or Scope B ranges and nothing enforces it | low | Confirmed, low, both | Fixed, this spec's half. `RegisterContentType` takes a `ContentRegistrationBand` and throws on a type id outside it, the shape `ReplicationRegistry.FirstExtensionTypeId` already uses for components. 3.6 | `17b05e5f` |
| C29 | Scope B reads four `item` fields per operation out of an untyped `ContentRow` | lead | LEAD, confirmed in the direction it named | Fixed. `ItemRow`, a typed `ref struct` view over the engine `item` type's four hot fields at P7's budget. Nothing else gets one, because a typed view over a schema the engine does not own cannot be written. 2.2, 9.1 | `e28dea2d` |
| C30 | Neither spec says whether a Scope B format change bumps `FormatGeneration` | lead | LEAD, confirmed for one case | Fixed, this spec's half. 12.1 states what moves `ContentPackFormat.Generation`, including a new remap rule kind, and states that a durable format outside the pack does not. 12.1 | `17b05e5f` |
| Pre-existing | 6.10 pointed at a section 12.5 that does not exist in this spec | not raised | Found while applying the above | Fixed. The reference is 12.4, "What an operator sees". The `(contracts 12.5)` citation in 16.5 is correct and untouched, since the contracts do have one. 6.10 | `e28dea2d` |

**What this round says about the first one.** Every finding above was invisible to a review of this document
alone, and most of them were invisible to a review of the contracts too. C4 is the sharpest: this spec defines
the loot composition rule, builds its arrays, budgets its draw and ships no roller, and nothing inside it is
wrong. The gap only exists in the space between two specs that each believed the other owned the code. A
third document is what made it visible, and that is the argument for reading specs in groups rather than one
at a time.
