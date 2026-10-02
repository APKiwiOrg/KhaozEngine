# Catalog text authoring

Status: contracts settled for [#1000](https://github.com/APKiwiOrg/KhaozEngine/issues/1000), implementation pending.
The [implementation plan](../superpowers/plans/2026-10-03-catalog-text-authoring.md) stages the complete change.
All new names and signatures below are proposed until implemented and reviewed.

## 1. Problem and source boundary

An operator can add an item row but cannot author its display text into a catalog pack. `LocalizedTextKey` is a
derived marker, and the shipped row edit parser correctly refuses a value for it. Drafts, bundles and persisted
authoring state carry no per-language text values. The publisher carries `baseline.Languages`, and pack commit
writes no KECT objects. A fresh store therefore publishes no content strings.

The reader already has canonical KECT encoding/decoding, text indexes and content-first fallback. This program
adds the producer without changing those wire formats or the catalog package's dependency direction.

| Existing source | Decision it constrains |
|---|---|
| `KhaozEngine.Catalog/ContentTextKey.cs`, `ContentFieldSchema.cs` | Derive keys from type key, content key and declared marker field. Never author a key override. |
| `KhaozEngine.Catalog.Authoring/IContentAuthoringStore.cs` | Keep old row-only signatures, provider transactions and external-pointer failure semantics. |
| `Publish/ContentPublisher.cs`, `ContentPublishCommit.cs` | Add a complete text plan and explicit commit dispatch, rather than a second write after row commit. |
| `Publish/ContentPackRebuild.cs`, `ContentRebuildSnapshot.cs` | Recovery requires exact-version values and recorded language/hash mappings. |
| `ContentDraft.cs`, provider `Freeze.cs` files | Frozen copies, deletion and survival must include text and language introductions. |
| `Upgrade/ContentUpgradeDraftMatch.cs`, `ContentUpgradeRun.Obstruction.cs` | Row-only proofs cannot establish ownership of translations or make check-then-discard atomic. |
| `ContentBundle.cs`, `ContentBundleJson.cs` | Bundle format 2 needs an explicit format 1 conversion and future-version refusal. |
| `KhaozEngine.Server.Admin/Catalog/CatalogEditParser.cs` | Keep marker-value refusal and parse text intents through a separate helper. |
| `KhaozEngine.Catalog/ContentTextChunkCodec.cs`, `ContentManifestCodec.cs` | Strict writer preflight, ordinal key order and one distinct tag per manifest language entry. |
| `KhaozEngine.Catalog.Authoring/ContentAuditEntry.cs` | Keep the 4096 audit cap and visible abbreviation. |

Source was checked at `ba247429`. Authoring stays above Catalog, both SQL providers stay opt-in and admin stays
in Server.Admin. No dependency, renderer, player-store or read-format change is needed.

## 2. Settled policy

- Text targets require a CLIENT-visible registered type and a CLIENT-visible `LocalizedTextKey` field. Check both
  levels. Retired rows remain eligible. Text never changes retirement state or rewrites an old version.
- Translations are sparse. The caller chooses a present default language. There is no mandatory English,
  required-marker coverage rule or installed-culture dependency.
- `Set("")` is a present empty value. `Remove` is absence and permits fallback. Values are not trimmed,
  case-folded, interpolated or Unicode-normalized.
- New language identity is ASCII letters, digits and hyphens, 1 through 35 bytes, with no empty segments and a
  first segment starting with a letter. Normalize letters to lowercase invariant. This is not full BCP-47
  registry validation. Underscores and Unicode aliases are not silently converted.
- Reject duplicate canonical text targets within one submitted batch. Across calls the last text intent wins,
  retaining its first ordinal. Existing row-intent collision rules remain unchanged.
- A Set introduces its language into the pending draft independently of the surviving text intent. Set followed
  by Remove before first publication still declares an empty language. Remove alone introduces no language.
  Removing an absent value is idempotent in a baseline or pending declared language. A never-declared language
  produces a typed refusal. There is no public DeclareLanguage or RemoveLanguage operation.
- Published languages stay declared. Removing the last value writes a deterministic empty KECT chunk, so a caller
  default remains constructible. Discarding a draft drops only its unpublished introductions.
- Preserve each published language's historical wire spelling. New languages use canonical spelling. Imports
  reject aliases sharing a canonical identity, even if their values or hashes agree.
- Strict UTF-8 bounds are 192 bytes per derived key, 8192 per value and 35 per wire tag. Validate strings without
  replacement of invalid surrogate input. One uncompressed text body per language is bounded at 16 MiB.
- KECT stays format 1, manifests stay at their existing format and pack generation stays 2. No sharding,
  invented shard tags, read-side tag grammar change or side-specific language format is included.

## 3. Proposed domain and companion seam

New types live in `KhaozEngine.Catalog.Authoring`, with one cohesive type per responsibility.

| Proposed type | Complete data |
|---|---|
| `ContentTextTarget` | Type id, immutable content key, declared field name and canonical language identity. Pending adds do not yet need a definition id. |
| `ContentTextEdit` | Set or Remove plus target, with a non-null value only for Set. |
| `ContentAuthoringChanges` | Submitted row and text intents. Public callers do not submit language-declaration commands. |
| `ContentTextLanguageDeclaration` | Canonical identity and wire spelling, without an unpublished hash. |
| `ContentDraftTextState` | Ordered text intents and independent pending language introductions. |
| `ContentTextRevision` | Type/id/field/language identity, complete value and temporal valid-from/replaced-in versions. |
| `ContentTextLanguage` | Canonical identity, exact wire spelling and recorded KECT hash. |
| `ContentVersionTextSnapshot` | Store epoch, requested committed version, complete visible revisions and all recorded languages, including empty ones. |
| `ContentTextPublishSnapshot` | Consistently read baseline rows/text plus the complete frozen draft and its identity. |
| `ContentTextCandidate` | Merged complete values/declarations and the temporal closes/inserts needed by commit. |
| `ContentTextChunkRecord` | Wire tag, content hash, owned stored bytes and reuse status. |
| `ContentTextPublishPlan` | Ordinary row/rule/version plan, frozen text/declarations, revision closes/inserts and every output language/chunk mapping. |

Copy and protect new collections and reachable mutable payloads. A frozen snapshot cannot alias submitted lists,
row byte payloads or mutable change sets. Preserve every old constructor and its binary signature. Add explicit
text-aware overloads or composed types, rather than replacing constructors with extra optional parameters.
An old DTO constructor means text is unrepresented, not that the provider has proved text absent.

The proposed `IContentTextAuthoringStore : IContentAuthoringStore` has these core operations:

```csharp
Task<ContentDraft> ApplyChangesAsync(
    ContentAuthoringChanges changes, string actor, string operatorId, string note,
    CancellationToken cancellationToken = default);
Task<ContentTextPublishSnapshot> FreezeChangesAsync(
    int expectedBaseVersion, CancellationToken cancellationToken = default);
Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(
    int versionNumber, CancellationToken cancellationToken = default);
Task<ContentVersionRecord> CommitTextPublishAsync(
    ContentTextPublishPlan plan, ContentPublishRequest request,
    IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default);
Task<bool> TryDiscardChangesAsync(
    ContentDraft expected, string actor, string operatorId,
    CancellationToken cancellationToken = default);
Task<ContentPublishResult> ImportTextBundleAsync(
    ContentBundle bundle, string actor, string operatorId, string note,
    CancellationToken cancellationToken = default);
Task<ContentDraft> RollbackTextToAsync(
    int targetVersion, string actor, string operatorId, string note,
    CancellationToken cancellationToken = default);
```

`ReadTextSnapshotAsync` reads an exact positive committed version, without a zero-means-active shortcut. Empty
baseline version 0 is created only by the publish/import path. Capability discovery alone is insufficient:
text-bearing publication dispatches through `CommitTextPublishAsync`, and text-bearing bundle import dispatches
through `ImportTextBundleAsync`. Neither calls the old commit/import and appends text afterward.

## 4. Legacy safety and draft lifecycle

The backend is authoritative. A caller's completeness flag, empty list or reconstructed old DTO is never proof
that held translations are absent. Within the provider gate/transaction, compare the expected base/epoch,
frozen rows, text payloads and declaration state with actual persisted state before deletion or commit.

| Legacy route | Required behavior |
|---|---|
| Row-only ApplyEditsAsync | Preserve held text/declarations while adding row intents. Never rebuild the draft from row intents alone. |
| Freeze or CommitPublishAsync | Succeed only when the actual held/base state is representable by the row-only path. Otherwise refuse before text could be omitted or reachable files written. |
| DiscardDraftAsync | Refuse when actual pending text or declarations would be deleted without a complete proof. |
| Bundle import/export | Refuse unrepresented format 2 text and incomplete provenance. Never serialize or import only the row half of a text version. |
| Upgrade matching/disposal | Row-only overloads cannot prove text-bearing or incomplete drafts are planned/known-empty. Use complete comparisons and atomic expected-draft discard. |

Engine-owned forwarding paths preserve representation and format version. A reconstructed format 2 DTO missing
its text section is refused. Format 1 is explicitly text-free by contract. The engine cannot detect an external
caller deliberately fabricating a new format 1 document from selected rows, and does not invent a provenance
framework to claim that it can.

Pending introductions survive copy, freeze, rebase, recovery and last-intent replacement. Frozen total work includes
real language introductions as well as row/text intents. A truly empty publish remains refused. Keep the old row
count available and expose text/declaration counts separately, so old row-count consumers are not redefined.

`TryDiscardChangesAsync` compares the complete expected draft and deletes it in the same gate/transaction. A
rival translation, introduction or freeze returns false without deletion or discard audit. The existing row-only
maintenance-window check/discard behavior is not retroactively described as atomic.

## 5. Candidate, encoding and atomic publication

1. Validate batch identities against the registry and effective pending rows, including add plus text in one batch.
   Apply CLIENT eligibility, strict encoding, key/tag/value bounds and canonical duplicate refusal before writes.
2. Freeze one complete draft and baseline through the companion. Text-only work is valid. Allocate new row ids
   through the existing allocator and preserve its reservation/abort behavior.
3. Begin text from the exact baseline. Bind pending targets to the final approved row-plan ids. A Fork copies
   original baseline values to the new legacy keys before explicit text edits apply to the original key. Retain
   retired-row values. Do not assume a plain allocator branch for family forks or compensate for their ids here.
4. Merge declared baseline languages with pending introductions. Reuse each established wire spelling. Set
   updates a complete value, Remove closes it, and a language without values remains in the output list.
5. Derive keys through `ContentTextKey`, sort ordinally and reject duplicate derived identities. Preflight body
   size using checked arithmetic, including entry-count and value-length varints, before Canonical/Encode allocates.
   Refuse above 16 MiB. Do not rely on the codec to enforce producer preflight or strict input encoding.
6. Use the existing codec and hash subdomain. Reuse unchanged hashes/files, including established empty chunks.
   Sort manifest entries ordinally by actual wire tag, not by a different canonical spelling.
7. Write new text objects before manifests. Dispatch the complete plan through the companion commit. Validate
   provider state again, then commit row/text revisions, all language mappings, audit, version, upgrade ledger and
   draft consumption together. The active database pointer moves last.

A failed provider commit cannot expose a partial committed active version. Existing cross-store semantics remain:
an external version pointer can have been written before a database failure and retain orphan objects. Recovery
and retries follow the existing pointer protocol. Do not claim a distributed transaction or change transport.

Inherited values that no longer have a representable registered CLIENT-visible marker must cause a typed refusal,
not disappear or leak into a shared manifest. This does not relax existing type-absence or schema guards.

## 6. Exact-version recovery and rollback

The snapshot includes values and recorded language/hash mappings from the same committed version. It includes
retired rows and languages with zero entries. Do not read version N values beside the active version's languages.
Derive keys using version N rows and reproduce each header with its original wire tag spelling.

Persist nullable `catalog_version.text_snapshot_complete`. Every new schema-4 atomic commit sets it complete,
including row-only versions with zero languages. Migration leaves old records NULL, meaning unknown. Missing
mapping rows or new tables do not prove old text absent, and a caller DTO cannot supply that proof.

An unknown legacy version may yield an empty snapshot only after read-only proof: regenerated exact-version
server AND client manifests with no languages match both recorded hashes, or verified stored manifest bytes prove
both empty lists at that version. Do not write a provenance marker from a read. Unknown/nonempty history refuses
until explicit complete value import/proof is available. A complete marker also does not excuse invalid values,
missing mappings or recorded hash disagreement.

Publish requires a complete/proved baseline. Rebuild and export require it for the exact requested version.
Rollback requires it for current and target versions. Backend legacy commit/import/discard/upgrade guards enforce
these status gates even when a wrapper reconstructs old DTOs. Existing type/format guards remain intact, including
refusal when they prevent a trustworthy legacy proof. No #1065 relaxation is introduced to make proof succeed.

Rebuild preflights and regenerates every text chunk, compares each hash and the complete language list with the
recorded mapping, then verifies both manifest hashes. Only after all comparisons pass may it write the rebuilt
objects and pointer. Missing capability, values, mappings, provenance or a hash match is a typed refusal.
The existing manifest-based sweep must retain reachable text hashes and respect its incomplete-list rules.

Rollback stages complete value changes against the selected historical version. It retains all currently declared
languages, including languages absent at the target, with empty chunks where needed. Existing irreversible row
retirement blockers stay in force. It changes no old version and does not unretire through text operations.

Read-only codecs keep their existing tag grammar. A historical tag that cannot be mapped to the new authoring
identity is not silently repaired or dropped. Mutation/import refuses unsupported identity provenance, while
existing read-side behavior remains unchanged. Exact historical recovery requires the provider's complete data.

## 7. Provider schema and audit

Both durable providers require schema 4, with a named migration such as `catalog-v4-text-authoring`.

| Proposed table/change | Responsibility |
|---|---|
| `catalog_draft_text_edit` | Ordered canonical type/key/field/language intents, Set/Remove and complete pending value. |
| `catalog_draft_text_language` | Independent pending introductions with canonical identity and wire spelling. |
| `catalog_text` | Temporal type/id/field/language values with valid-from/replaced-in versions. Translation-only publication need not rewrite the content row. |
| `catalog_text_chunk` | Complete `(version, canonical identity, wire spelling, hash)` list, including empty languages. |
| `catalog_version.text_snapshot_complete` | Nullable authoritative completeness, set by new atomic commits, unknown on legacy rows until a read-only empty proof. |
| `catalog_audit.language_tag` | Nullable structured language identity, separate from the bounded field name. |

Use unique canonical target/live-revision/version-language constraints and existing ordinal comparison rules.
SQLite values use TEXT under its binary comparison rules. SQL Server values use `nvarchar(max)` under the existing
`Latin1_General_100_BIN2` collation, with bounded ASCII identity columns. Neither storage representation replaces
strict application-side UTF-8 measurement.
Preserve identities, families, row history, remap rules, version hashes, pins, epoch and timestamps. AutoCreate may
migrate verified older shapes through the existing chain. ValidateOnly modes refuse older shapes and name the
migration. Validate before migration and re-read objects afterward. New tables start empty, with no invented
historic text, tags, hashes or timestamps.

Every inserted mutable row has creation and change times from the operation clock. Re-editing retains creation
time. Published revision closure updates its change time. SQLite leases/transactions and SQL Server scopes retain
their current ownership. Memory builds failing clock/audit/validation work into locals before its commit tail.

App validation enforces exact UTF-8 byte limits. SQL `LEN`, UTF-16 `DATALENGTH` and C# string.Length do not stand
in for UTF-8 measurement. Provider constraints may add structural bounds, but must not claim equivalent byte rules.

Keep `ContentAuditEntry.MaxValueLength` at 4096 and the existing visible `[cut]` abbreviation. Add nullable
LanguageTag as an additive property, retaining the old record constructor and deconstruction signatures and null
for old row audits. Text abbreviation must preserve valid surrogate pairs and leave room for the marker. Full
published values remain recoverable from versioned text history. Drafts hold full values while pending, and audit
does not promise full unpublished values after intentional discard. Clock/audit failure rolls back the whole batch.

## 8. Bundles and upgrades

Bundle format 2 adds a protected text section containing declared languages and complete values. A declaration
contains canonical identity and wire spelling. Values target type/key/field/canonical language, not a raw derived
key. Empty declarations survive round-trip even when values are absent. Export the exact requested version.

Read/import format 1 through an explicit empty-text conversion. Preserve its old fixture and reader contract.
Refuse future formats, missing format 2 sections, duplicate aliases and malformed targets before reset/staging.
The complete checked companion import owns rows, ids, families, rules, declarations, values and prior-state
restoration together. No public language-only command is added by exposing a bundle snapshot.
The existing empty-destination bundle import rule remains. This does not invent an in-place historical text
repair API. Unknown/nonempty legacy history stays refused until an explicit complete proof or separately reviewed
value-repair operation exists, with no repair hidden inside a read or migration.

Upgrade baseline bundles include text. Add a complete-changes plan overload while retaining old constructors and
row-only plan signatures. Text-only work is real authored work, not #1065's generic empty republish. Preview,
exact plan matching, known-work proof and disposal include payloads and introduction state. Another operator's
translation is neither adopted nor discarded. The upgrade ledger commits with the published text version.

## 9. Admin and final outcome

Extend catalog-edit with `textEdits` beside existing `edits`, supporting text-only and mixed batches. Each text
entry names type key, content key, field, language, operation and value for Set. A cohesive text parser collects
all findings. Preserve malformed-body, validation, unsupported-capability, frozen and stale refusal distinctions.
Keep the old row-field marker rejection. Apply the parsed batch once, with captured actor/operator metadata.

Draft, diff, history, discard, rollback and bundle actions must expose or safely refuse complete text state. The
end-to-end proof uses the real admin action harness, publishes an item plus name, reads actual manifest/KECT bytes
and resolves the derived key through ContentStringCatalog without changing the shipped fallback or client code.

## 10. Delivery and completion gate

Keep the whole code program on a dedicated feature integration branch. The coordinator pushes that branch and
owns bounded worktree assignments. Never merge a partial producer or DTO change into main, which another session
may release. Unfinished operations must fail closed, and partial providers must not advertise successful text
publication/import/recovery. The parent issue remains open until the complete safety program is reviewed.

Stages are domain/reference contracts, deterministic publisher/recovery, independent SQLite and SQL Server
providers after contract freeze, coordinated bundle/upgrade hooks, admin end-to-end and final docs/validation.
Provider owners retain Bundle/Audit/Freeze integration ownership through the coordinated stages.

Each stage gets meaningful headless RED/GREEN and independent review. All local dotnet runs use the coordinator's
single lease. No load/stress loops, window or consumer launch. The coordinator runs the final Release solution
build and full required suite, re-reads main/version/tags and reconciles metadata before integration. No release
or tag is inferred. SQL Server gate skips remain explicit evidence gaps, not provider success.

Acceptance requires atomic mixed changes, sticky declarations, empty-language fallback, alias refusal, strict
UTF-8/cap preflight, protected copies, full historic hashes/spelling, safe fork/retirement/rollback, provider
reopening/migration, legacy wrapper/reconstructed DTO refusal, unknown draft retention and real admin-to-reader
resolution. The plan names the exact tests and file owners.

This does not fix #919's pin reason or duplicated audit renderer, #1065's type-adoption republish, sharding, empty
read-layer construction, hot reload or game adoption. Existing catalog and item-instance designs remain history.
[#908](https://github.com/APKiwiOrg/KhaozEngine/issues/908) settled family-fork allocation separately, so a family
copy now allocates inside its family's blocks. Text binds copies only after the final approved fork ids and never
selects an allocation branch.
