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

## 3. Data model

### 3.1 The engine content types

Five types register in the ENGINE range `1` to `255` of contracts 4.3. The range holds 255 ids and five are
spent, which is deliberate headroom: an engine release adding a type can never collide with a game's, which is
the property `ReplicationRegistry.FirstExtensionTypeId` already gives components
(`TileProtocol.Components.cs:17-30`, `a-engine.md:880-916`).

| Type id | Type key | What it is | Default visibility | Default chunk slots |
|---|---|---|---|---|
| `1` | `tag` | The tag vocabulary of contracts 4.6. | `Client` | 4,096 |
| `2` | `item` | The item base. | `Client` | 4,096 |
| `3` | `stat` | The stat definition of contracts 13.1. | `Client` | 4,096 |
| `4` | `loot_table` | A named drop or reward table. | `ServerOnly` | 4,096 |
| `5` | `loot_entry` | One weighted row of one loot table. | `ServerOnly` | 16,384 |
| `6` to `255` | reserved | Future engine types. Never assigned by a game. | | |

`loot_entry` takes a larger chunk than its parents because entries outnumber tables by roughly the branching
factor, and a chunk is a transport unit sized for download economics rather than an authoring unit (contracts
4.5). At 4,096 slots a table of 50 entries would straddle chunks for no reason.

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
| `name` | localized text key | | `Client` | yes |
| `sort` | int | | `Client` | no |

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
| `name` | localized text key | | `Client` | yes | Grimhollow `ItemStrings.NameFor` 35-arm switch (`b-grimhollow.md:673-685`), Ruinborne `item_def.display_name` (`c-ruinborne.md:49-60`). |
| `examine` | localized text key | | `Client` | no | Grimhollow `ItemStrings.ExamineFor` (`b-grimhollow.md:679-681`). |
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
| `socket_max` | int | | `Client` | no | New. 0 means the base takes no sockets. Scope B reads it. |
| `equip_profile` | key reference | game type | `Client` | no | Grimhollow `GrimhollowEquipmentRoster.For` 9-arm switch (`b-grimhollow.md:78-104`), Ruinborne `weapon_def` and `item_stat` (`c-ruinborne.md:78-95`). |

**`equip_profile` points at a GAME type and the engine does not own its shape.** An equip slot vocabulary is
game specific: Grimhollow has eleven slots whose numbers are durable because they shipped in that order
(`b-grimhollow.md:80-85`), Ruinborne has a nullable `slot NVARCHAR(32)` with no reference table. The engine
declares the FIELD and the game registers the type it points at, which is exactly the `IProductCatalog` seam's
stated shape: "The engine defines the shape, the game supplies entries"
(`KhaozEngine.Commerce/IProductCatalog.cs:5`, `a-engine.md:668-676`).

The consequence for contracts 4.7 is that a reference target may name a type the ENGINE does not register, so
the validator resolves the target at REGISTRY FREEZE rather than at compile time, and a game that leaves
`equip_profile` unwired registers no target and every row leaves the field at 0. Finding `KEC0007` covers a
non-zero reference whose target type was never registered.

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
| `name` | localized text key | | `Client` | yes |
| `scale` | int | | `Client` | yes |
| `min` | int | | `Client` | yes |
| `max` | int | | `Client` | yes |
| `tags` | tag list | `tag` | `Client` | no |
| `display_format` | localized text key | | `Client` | yes |

`scale` is a fixed power of ten and the stored integer is the value times `scale` (contracts 13.1). `min` and
`max` are in scaled units and are the inclusive clamp of the evaluation formula (contracts 13.2). The
validator refuses a `scale` that is not a power of ten and refuses `min > max` (`KEC0020`, `KEC0021`).

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
Exactly one of `item` and `nested_table` is set, checked by the validator (`KEC0023`). A cycle through
`nested_table` is refused (`KEC0024`) by a depth-first walk with a visited set, because a cyclic loot table is
an infinite roll at runtime and the validator is the only place that can see the whole graph.

`required_tags` is the tag filter a drop table uses to name a CLASS of item rather than enumerating ids
(contracts 4.6, first bullet). When it is non-empty and `item` is unset, the entry draws uniformly from live
items carrying every listed tag, resolved at pack load into a precomputed candidate array (section 9.4).

### 3.6 How Scope B and a game register their own

Registration is contracts 4.2's call, once, at process start, before any pack loads:

```csharp
registry.RegisterContentType(
    typeId: 1024,
    typeKey: "store",
    codec: new GrimhollowStoreCodec(),
    validator: new GrimhollowStoreValidator(),
    schema: GrimhollowStoreSchema.Create(),
    defaultVisibility: ContentVisibility.ServerOnly,
    chunkSlots: 256);
```

The registry freezes when the first pack loads and a later registration throws (contracts 4.2). A game MAY
register a type in the game range, supply its own codec and validator, set its default visibility, set its
chunk size, and register an ADDITIONAL validator for an engine type that runs after the engine's own. A game
MAY NOT register into the engine or Scope B ranges, replace an engine type's codec, weaken an engine
validator, change a type's id or key after the first publish, or register the same type id twice (contracts
4.4).

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
| `1026` | `equip_profile` | `GrimhollowEquipmentRoster.For`'s 9-arm switch (`b-grimhollow.md:78-104`). |
| `1027` | `food` | The `item.<key>.heals` and `.attackDelayTicks` economy rows (`b-grimhollow.md:208-234`). |
| `1028` | `gathering_node` | `skilling.jsonc` `trees` and `rocks` blocks (`b-grimhollow.md:347-368`). |
| `1029` | `recipe`, `1030` `recipe_input`, `1031` `recipe_output` | `skilling.jsonc` `processing` block. |
| `1032` | `tool_tier` | `skilling.jsonc` `hatchets` and `pickaxes` blocks. |
| `1033` | `skill_curve` | `skilling.jsonc` `xp`, `gathering`, `parents`, `stamina` and `skills` blocks. |
| `1034` | `monster_drop` | The `GrimhollowDrops.Roll` switch, whose STRUCTURE is code today (`b-grimhollow.md:123-136`). |

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
| `catalog_metadata` | exactly 1 | Schema version, store epoch, the ACTIVE version pointer. |
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
| `catalog_chunk` | one per (version, type, chunk index) | The chunk hash, so a republish knows which chunks changed. |

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
    updated_at_utc INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS catalog_type (
    type_id INTEGER NOT NULL PRIMARY KEY CHECK (type_id BETWEEN 1 AND 65535),
    type_key TEXT COLLATE BINARY NOT NULL CHECK (length(type_key) BETWEEN 1 AND 64),
    chunk_slots INTEGER NOT NULL CHECK (chunk_slots BETWEEN 256 AND 65536),
    default_visibility INTEGER NOT NULL CHECK (default_visibility IN (0, 1)),
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
    published_at_utc INTEGER NOT NULL,
    sealed_flag INTEGER NOT NULL DEFAULT 0 CHECK (sealed_flag IN (0, 1)));

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
`ScaledInt`, `Bool` and `KeyReference` use `int_value`, `LocalizedTextKey` uses `text_value`, and `TagList`
and `OpaqueBytes` use `blob_value`. A tag list is stored as a `blob_value` of varint tag ids in AUTHORED
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
    operation INTEGER NOT NULL CHECK (operation IN (1, 2, 3)),
    retire_policy INTEGER NOT NULL DEFAULT 0 CHECK (retire_policy IN (0, 1, 2)),
    replacement_id INTEGER NOT NULL DEFAULT 0 CHECK (replacement_id >= 0),
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
console that saves the same row twice updates the one edit rather than queueing two. An `Add` carries
`definition_id = 0` because no id exists yet, so its uniqueness comes from the key half of the index, which is
also what refuses two `Add` edits for the same key in one draft.

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
    before_value TEXT COLLATE BINARY NULL CHECK (before_value IS NULL OR length(before_value) <= 512),
    after_value TEXT COLLATE BINARY NULL CHECK (after_value IS NULL OR length(after_value) <= 512),
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
    PRIMARY KEY (version_number, type_id, chunk_index),
    FOREIGN KEY (version_number) REFERENCES catalog_version(version_number),
    FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
CREATE INDEX IF NOT EXISTS ix_catalog_chunk_hash ON catalog_chunk(chunk_hash);

INSERT OR IGNORE INTO catalog_metadata(metadata_key, schema_version, store_epoch, active_version, updated_at_utc)
VALUES (1, 1, lower(hex(randomblob(16))), 0, CAST(strftime('%s', 'now') AS INTEGER) * 1000);
```

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
new block by the step-2 rule above, inserts the `catalog_family_block` row, and only then issues. The rounding
up is what wastes ids and what makes `(id & ~(size - 1)) == base` a legal membership test (contracts 5.2), and
the waste is bounded by the block size.

An id block exhausted MID-PUBLISH cannot happen, because allocation runs as step 2 of the publish (section
6.3) before anything is written, and a failure there aborts the publish with nothing changed. Section 11 row 9
spends this case.

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
    IReadOnlyList<RemapRule> rules,
    ContentTypeRegistry registry);
```

- INPUT is a complete candidate snapshot plus the full ordered rule set. Nothing is read from a database, a
  file or an ambient static inside it.
- OUTPUT is `(bool IsValid, IReadOnlyList<ContentFinding> Findings)`, accumulating rather than stopping at the
  first, following `JsonSchemaValidator.ValidationReport` and its run-to-the-end sweep
  (`KhaozEngine.Content/JsonSchemaValidator.cs:11-101`, `a-engine.md:275-292`).
- NO SIDE EFFECTS. It does not log, does not mutate the candidate, does not touch a counter and does not throw
  for content reasons. A throw from it is a bug in the validator.

Because it is pure and takes its whole world as an argument, a test builds a snapshot in memory and asserts on
findings, publish runs it before writing anything, and boot runs it against the loaded pack. Ruinborne's
catalog loader is the shape this avoids: validation lives INSIDE the SQL read delegate, so an
`ArgumentException` from a bad row is routed to the connectivity failure handler and reported as
`Item catalog: SQL read failed`, which sends an operator looking at the network
(`c-ruinborne.md:176-196`, Ruinborne [#325](https://github.com/APKiwiOrg/Ruinborne/issues/325)).

### 5.2 The findings

A finding is `(ContentTypeId Type, int Id, string Code, string Message)`. The CODE is a stable token so a
counter, a test and an operator runbook can all key on it, which is the same rule contracts 9.7 applies to
decode reasons. Codes are never reused and never renumbered.

| Code | Check | Contract |
|---|---|---|
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
| `KEC0013` | A `ServerOnly` field appears on a type whose visibility is `Client`, with no per-field override. | 11.3 |
| `KEC0014` | A `Client` chunk would carry a `ServerOnly` field. | 11.3, 10.4 |
| `KEC0015` | The remap rule set is not idempotent: a rule's `ToId` is an earlier rule's `FromId` for the same type. | 8.3, 10.4 |
| `KEC0016` | A remap rule's `FromId` names a row that never existed. | 8.1 |
| `KEC0017` | A remap rule's `ToId` names a row that is not live at `IntroducedIn`. | 8.2 |
| `KEC0018` | Remap rule sequences are not contiguous from 1, or are not strictly ascending. | 8.1 |
| `KEC0019` | A remap rule payload is longer than 64 bytes, or is malformed for its kind. | 8.4 |
| `KEC0020` | A stat `scale` is not a power of ten, or is below 1. | 13.1 |
| `KEC0021` | A stat `min` exceeds its `max`. | 13.1 |
| `KEC0022` | A definition declaring durability or sockets is stackable. | 10.4 |
| `KEC0023` | A loot entry sets both `item` and `nested_table`, or neither. | 3.5 here |
| `KEC0024` | A loot table graph contains a cycle through `nested_table`. | 3.5 here |
| `KEC0025` | `max_stack` is below 1, or is 1 on a stackable row. | 3.3 here |
| `KEC0026` | A row's encoded bytes exceed `MaxContentRowBytes`. | 7.3 here |
| `KEC0027` | A row's codec round trip is not byte identical. | 7.3 here |
| `KEC0028` | A `chunk_slots` value is not a power of two between 256 and 65,536. | 4.5 |
| `KEC0029` | A type id or type key changed after its first publish. | 4.4 |
| `KEC0030` | A localized text key exceeds 192 characters or breaks 12.2's character rules. | 12.2 |
| `KEC0031` | A `parent_id` is non-zero while inheritance is unimplemented. | 3.8 here |
| `KEC0032` to `KEC0035` | The four inheritance checks of section 3.8, unreachable in phase 1. | 3.8 here |
| `KEC0040` | A game validator returned a finding. The message is the game's, the code is prefixed with its type key. | 4.4 |

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
   to `KEC0012`, `KEC0028`, `KEC0029`.
2. **Schema.** Every field against its type's declared schema, required fields, value ranges, localized key
   shape, the inheritance guard. `KEC0004`, `KEC0005`, `KEC0020`, `KEC0021`, `KEC0025`, `KEC0030` to
   `KEC0035`.
3. **References.** Key references, tag lists, loot graphs. `KEC0006` to `KEC0008`, `KEC0023`, `KEC0024`.
4. **Visibility and codec.** Field visibility against chunk visibility, the round trip, the row size cap.
   `KEC0013`, `KEC0014`, `KEC0022`, `KEC0026`, `KEC0027`.
5. **Remap rules.** The full ordered set. `KEC0015` to `KEC0019`.

Pass 3 needs pass 1 to have built the live-row index, and pass 5 needs pass 1 to know which ids ever existed.
Nothing else is ordered, and the passes are single threaded because the candidate at 1,000,000 rows is a few
hundred megabytes and the whole sweep is a linear walk (section 14, budget P5).

Game validators (contracts 4.4, an ADDITIONAL constraint never a relaxation) run LAST, after pass 5, one per
registered type, each handed only its own type's rows plus a read-only lookup into the rest of the candidate.
Their findings come back as `KEC0040` with the game's message. A game validator that throws is caught and
reported as `KEC0040` with the exception message, so one bad game validator cannot take the publish down with
a stack trace instead of a finding.

### 5.4 The three callers

**Publish** (section 6.4) runs it on the candidate BEFORE any id is stamped into a durable row and before any
byte is written. `IsValid == false` aborts the publish, nothing is written, the draft is untouched, and the
findings come back to the console as HTTP 400 with the full list.

**Boot** (section 9.5) runs it on the snapshot decoded from the loaded pack. A pack that fails validation at
boot fails the boot CLOSED, non-zero exit, findings on stderr (contracts 10.5). This is not redundant with the
publish run: the pack may have been written by an older engine build, may have been corrupted in transit, or
may have been produced by a publish whose validator had a bug now fixed. Running it again costs one linear
sweep at process start and is the last gate before a server serves content.

**Tests** build a `ContentSnapshot` in memory with no store, no file and no registry beyond the one they
construct, and assert on the exact finding codes. That is the property that makes the validator testable at
all, and it is why the signature takes the registry as an argument rather than reading an ambient one.

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

Every `Add` edit in the frozen change set needs an id. Allocation runs before validation deliberately, because
several checks (`KEC0006` reference resolution, `KEC0010` family membership) need the ids the new rows will
carry.

For each `Add`, in edit ordinal order:

1. If the edit names a family, call `AllocateInFamilyAsync(familyId)` (section 4.7).
2. Otherwise call `AllocateAsync(typeId, 1)`.

Both go through the reserve-before-issue rule, so the reservation commits on its own before the id appears
anywhere. **An allocation that fails aborts the publish with nothing written**, because no durable row carries
the id yet. A reservation that COMMITTED and then aborted leaves a gap of reserved-but-unissued ids, which is
the safe direction and costs nothing (section 4.7).

Ids are allocated in edit ordinal order, so two publishes of the same bundle into two empty databases produce
the same ids. That is what makes a bundle export and re-import reproducible (section 10.9) and what makes the
Grimhollow import preserve ids 1 to 35 (section 16.4).

### 6.4 Step 4, validate

Section 5, on the candidate built at step 2 plus the rule set as it will stand after step 5 appends this
publish's rules. Validating the rules BEFORE they are durable is the point: `KEC0015` refusing a
non-idempotent rule set is the check that makes contracts 8.3's crash safety hold, and a rule that got into
the table could not be taken back out.

An invalid candidate aborts. The draft is NOT discarded and the console gets every finding, so an operator
fixes three problems in one round trip rather than three.

### 6.5 Step 5, compute the temporal rows

For each edit, with `V` the new version number:

| Edit | Writes |
|---|---|
| `Add` | One `catalog_row` with `valid_from_version = V`, `replaced_in_version = NULL`, `retired = 0`, plus one `catalog_row_field` per set field. |
| `Update` | Sets the current row's `replaced_in_version = V`, then inserts a successor with `valid_from_version = V` carrying the MERGED field set: the current row's fields with the edit's fields overlaid. |
| `Retire` | Sets the current row's `replaced_in_version = V`, then inserts a successor with `valid_from_version = V`, `retired = 1` and the SAME field set, plus one `catalog_remap_rule` with kind `2` and the policy payload. |

A `Retire` whose policy is `0x02` replacement carries the destination id in payload bytes 1 to 4 (contracts
8.2) and the validator has already checked the destination is live (`KEC0017`).

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

**A retire is an ordinary chunk rewrite.** The retired row keeps its slot and its bytes and gains the retired
bit, so exactly one chunk changes. Grimhollow's 18 retired ids sit across ids 1 to 35, which at 4,096 slots
per chunk is one chunk, so its whole retirement history is one chunk rewrite.

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

### 6.8 Step 8, build both manifests

Two manifests per version, the SERVER manifest over every chunk and the CLIENT manifest over the
client-visible chunks only (contracts 7.3, 11.3). Each gets its own hash under its own sub-domain,
`kec/manifest/server/` and `kec/manifest/client/`, so a head gating on one can never accidentally agree with a
head gating on the other. That last property is `TileWorldHash.OfWorldAndCatalogs`'s stated reason for
existing (`a-engine.md:1206-1209`).

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

Nothing points at any of these files until step 10, so a crash here leaves orphans and nothing else. Step 11
sweeps them.

### 6.10 Step 10, the commit

ONE transaction (section 4.8). In order inside it:

1. `newVersion = MAX(version_number) + 1` from `catalog_version`, or 1 when empty.
2. Insert the `catalog_version` row with both manifest hashes, both minimum builds, the format generation, the
   base version, the publisher, the note and the timestamp.
3. Apply every temporal row change computed at step 5.
4. Append every remap rule computed at step 5, at `MAX(sequence) + 1` upward.
5. Insert every `catalog_chunk` row for `newVersion`. Unaffected chunks get a row too, carrying the hash
   copied from `newVersion - 1`, so the table answers "which chunks does version N have" with one query and no
   recursion back through history.
6. Insert every audit row.
7. Delete `catalog_draft_edit_field`, `catalog_draft_edit` and `catalog_draft`.
8. `UPDATE catalog_metadata SET active_version = newVersion`.

Step 8 is the moment the version becomes live, and it is the last statement in the transaction. A reader that
sees `active_version = N` is guaranteed to see every row, rule, chunk and audit entry of version N, because
they committed together.

**The active pointer moves at publish, and the SERVER does not.** v1 applies a new version at server restart
(contracts 1.3 item 8), so moving the pointer makes the version ACTIVE FOR THE NEXT BOOT. A running server
keeps serving the version it loaded. Section 12.5 says what an operator sees and section 10.7 gives them the
pin action for holding a version back.

### 6.11 Idempotence and crash safety, step by step

| Crash point | State afterwards | Recovery |
|---|---|---|
| During step 1 to 8 | Nothing written. The draft is intact. | Republish. Nothing to clean. |
| Between step 3's reservation commit and step 4 | A gap of reserved-but-unissued ids. | None needed. The gap is invisible and bounded by 1,024 per type (section 4.7). |
| During step 9, part way through the chunk files | Some chunk files exist that nothing references. The old version is still active. | Republish writes them again idempotently, since a chunk file's name is its own hash. Step 11 of the NEXT successful publish sweeps any that are never referenced. |
| Between step 9 and step 10 | Every file of the new version exists. Nothing references them. The old version is still active. | Republish. The `ExistsAsync` check at step 9 makes the rewrite free, so a retried publish after a crash here is fast. |
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
whose hash appears in no `catalog_chunk` row and is neither manifest of any version. The sweep is SKIPPED when
the store listing fails for any reason, because deleting files on the authority of a listing that failed is
how a bad publish turns into a lost pack. That skip rule is the map document's, which skips its own sweep when
the previous manifest could not be read (`MapTiledFile.Save.cs:105-107`).

The sweep never deletes a file referenced by ANY version, not just the active one, because a pinned server
(section 10.7) and a rollback (section 12.3) both need older versions to stay fetchable.

### 6.13 Rollback is a publish

A rollback is a NEW version that restores old values and keeps every id introduced since (#882 body, item 8).
The version number keeps climbing throughout: a rollback is never a return to an old number, which is the same
property that lets the page stamp be an ordering comparison (contracts 7.2, 8.6).

`RollbackToAsync(targetVersion)` builds a draft rather than doing anything special:

1. Read the live row set at `targetVersion` and the live row set at the current version.
2. For every row live at BOTH whose field set differs, emit an `Update` restoring the target's field values.
3. For every row live at `targetVersion` and retired since, emit an `Update` clearing `retired` AND refuse if
   any remap rule of kind 1, 2 or 3 names that id, because contracts 8.6 makes a retire irreversible for
   migrated pages.
4. For every row introduced AFTER `targetVersion`, do NOTHING. It keeps its id and its values. This is #882's
   "keeps every id introduced since" and it is the difference between a rollback and a restore.
5. Publish the draft in the ordinary way.

Step 3 is the one place a rollback can fail, and it fails EARLY with finding `KEC0017` plus a message naming
the rule sequence, rather than silently producing a version whose remap rules contradict its rows. The way out
is the contracts' own: **a rollback that wants a retired definition back MINTS A NEW ID carrying the old
values** (contracts 8.6), which is an ordinary `Add` with a new key, plus a `ReplacedBy` rule if the owner
wants existing references moved onto it. The API surfaces this as a 409 with the blocking rules listed and the
mint-new-id path named, so the operator is not left guessing.

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
    public const int    MaxContentRowBytes    = 4096;
    public const int    MaxChunkUncompressedBytes = 16 * 1024 * 1024;
}
```

`MaxContentRowBytes = 4096` is the row-level guard rail behind `KEC0026`. The arithmetic: the largest realistic
engine row is an item base with three asset references at 128 bytes each, two localized keys at 192, a 20 tag
list and a dozen ints, which is under 900 bytes. Four kilobytes is four times that and it is what stops one
pathological row from dominating a chunk. `MaxChunkUncompressedBytes = 16 MiB` is the matching chunk-level
guard: at 4,096 slots a chunk would have to average 4 KB per row to reach it, which `MaxContentRowBytes`
makes the absolute worst case, so the two caps are consistent by construction.

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
