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
| `KhaozEngine.Catalog` | `Foundation` | Pure .NET | The registry, the codecs, the hashes, the pack format, the validator, the pack store seam, the read-side runtime. |
| `KhaozEngine.Catalog.Authoring` | `Server` | `KhaozEngine.Catalog` | The authoring store contract, drafts, change sets, audit, publish, diff, bulk import and export. No SQL. |
| `KhaozEngine.Catalog.Sqlite` | none, opt-in sibling | `Catalog.Authoring`, `KhaozEngine.Sqlite`, `Microsoft.Data.Sqlite` | The SQLite authoring provider. |
| `KhaozEngine.Catalog.SqlServer` | none, opt-in sibling | `Catalog.Authoring`, `Microsoft.Data.SqlClient` | The SQL Server authoring provider. |
| `KhaozEngine.Catalog.Netcode` | `Server` | `Catalog`, `KhaozEngine.Netcode` | The content handshake layer and its gate authenticator. |

The layering rules are the README's, surveyed at `a-engine.md:1373-1407`. A pure catalog with no SQL belongs in
`Foundation` beside `Items` and `Stats`. Anything with a SQL provider is an opt-in SIBLING pair and is never
bundled in an umbrella, stated twice in the README (lines 101 and 104) and followed by both `WorldStore` and
`Commerce`. `Foundation` cannot reference `Server`-side packages, which is why the handshake gate is its own
small package rather than a type inside `KhaozEngine.Catalog`.

**`KhaozEngine.Catalog` is Pure .NET and that is load bearing.** A game CLIENT needs the read side and must
never pull a database dependency, so the client half of a pack has to decode with no authoring type in the
graph (contracts 3.2, rationale). It uses `System.IO.Compression` and `System.Security.Cryptography`, both in
box, and no third-party package at all. In particular it does NOT take `JsonSchema.Net`, which is contained to
`KhaozEngine.Content` by an architecture test (`KhaozEngine.Tests/ArchitectureTests.cs:121`).

### 2.2 `KhaozEngine.Catalog` public types

| Type | One line |
|---|---|
| `ContentTypeId` | A `readonly record struct` wrapping the `ushort` of contracts 4.3, with the three range predicates. |
| `ContentKey` | A `readonly record struct` wrapping the string key of contracts 5.3, ordinal, validated at construction. |
| `ContentVisibility` | `Client` or `ServerOnly`, contracts 11.1's content vocabulary. |
| `ContentFieldKind` | `Int`, `ScaledInt`, `Bool`, `KeyReference`, `TagList`, `LocalizedTextKey`, `OpaqueBytes`, contracts 4.7. |
| `ContentFieldSchema` | One type's ordered field list: name, kind, scale, reference target, visibility, required. |
| `IContentRowCodec` | `Encode(row, IBufferWriter<byte>)` and `TryDecode(ReadOnlySpan<byte>, out row, out reason)`, never throwing. |
| `IContentValidator` | One type's own checks, run after the engine's, contracts 4.4. |
| `ContentTypeRegistry` | Registration, the freeze at first pack load, and lookup by type id or type key. |
| `ContentRow` | The generic row a codec-free consumer sees: id, key, parent id, an ordered field-value list. |
| `ContentSnapshot` | A complete candidate or loaded version: every type's rows plus the version header. |
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
| `ContentVersionIdentity` | `(int Number, string ManifestHash)`, the pair that travels together, contracts 7.1. |
| `ContentStringCatalog` | The layered `IStringCatalog` of contracts 12.4, content first then the game's resx. |
| `ContentVarint` | The LEB128 and zig-zag primitives of contracts 15, shared with Scope B. |

### 2.3 `KhaozEngine.Catalog.Authoring` public types

| Type | One line |
|---|---|
| `IContentAuthoringStore` | The provider seam: draft edits, publish, version listing, audit, id allocation, bulk import and export. |
| `ContentAuthoringSchemaMode` | `AutoCreate` or `ValidateOnly`, the journal's shape (`SqliteJournalSchema.cs:9-13`). |
| `ContentDraft` | The one open draft: its base version, its edit list and its opened-by and opened-at stamps. |
| `ContentEdit` | One edit: type, id or key, operation (`Add`, `Update`, `Retire`), and the field values it sets. |
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
KhaozEngine.Catalog            (Foundation, pure .NET, no third-party)
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
Scope B exists. The one shared type is `ContentVarint`, which Scope B's payload codec uses, so the varint
definition of contracts 15 has exactly one implementation in the tree.

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
| Registry, codecs, hashes, pack format, validator, runtime, remap rules, golden files, decoder fuzzing | **`KhaozEngine.Catalog.Tests`** (new) | `KhaozEngine.Catalog` only. |
| Draft, change set, publish pipeline, diff, id allocator, bundle import and export, all against an in-memory store | `KhaozEngine.Catalog.Tests` | plus `KhaozEngine.Catalog.Authoring`. |
| Provider conformance: `ContentAuthoringStoreConformance` abstract class, `SqliteContentAuthoringStoreTests` and `SqlServerContentAuthoringStoreTests` subclasses | `KhaozEngine.Server.Tests/Catalog/` | The existing home of every Commerce and WorldStore provider test (`a-engine.md:781-788`). |
| The connect-door layer and its refusal tokens | `KhaozEngine.TileWorld.Netcode.Tests` | Where the existing gate tests are. |
| Scale runs at 50,000 and 1,000,000 definitions | `KhaozEngine.Benchmarks` plus a structural test in `KhaozEngine.Server.Tests` | The journal's shape (`a-engine.md:1322-1349`). |

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
