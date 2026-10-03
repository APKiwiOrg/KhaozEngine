# Catalog Text Authoring Implementation Plan

> **For implementation owners:** Execute one bounded task at a time. The coordinator owns assignment,
> independent review and integration. Steps use checkboxes for tracking.

**Goal:** Author, publish and recover per-language catalog text without losing translations through old callers.

**Architecture:** An opt-in authoring companion applies and commits complete row/text changes atomically.
Exact-version snapshots supply values and recorded language/hash mappings to publishing, bundles and recovery.
The read codecs, dependency direction and row-only binary signatures stay intact.

**Tech Stack:** .NET 10, xUnit, existing KECT/Brotli/hash codecs, existing SQLite and SQL Server providers.

**Spec:** [Catalog text authoring design](../../design/CATALOG-TEXT-AUTHORING-DESIGN-2026-10-03.md).
**Status:** Implemented in 20.20.0 for [#1000](https://github.com/APKiwiOrg/KhaozEngine/issues/1000). All seven
tasks are complete. The names below were frozen at task 1 review, and the package READMEs document the shipped API.

## Global constraints

- Keep code on a dedicated `feature/catalog-text-authoring` integration branch until every task and safety gate
  is complete. The coordinator creates scoped worktrees and pushes the integration branch. No partial merge to
  main, closure of the parent issue, release/tag, consumer launch or worktree cleanup by an implementation owner.
- Existing row-only constructors, interfaces, record deconstruction and binary signatures remain available.
  Missing additive text data is unrepresented, never proof that a backend holds no text.
- All new collections and reachable mutable payloads are owned/protected. Provider state, not a caller flag,
  establishes complete freeze, commit, discard and import proofs.
- Language identity is ASCII letters/digits/hyphen, 1..35 bytes, no empty segments, first segment starts with a
  letter, lowercase invariant. No installed-culture or full BCP-47 registry validation.
- Set introduces a pending language independently. Set then Remove retains it through publication. Remove in
  an undeclared language refuses, declared absent removal is idempotent and no public language-removal verb exists.
- Sparse translations and caller-selected present default. CLIENT-visible type AND CLIENT-visible marker field.
  Retired values remain, and registered retired rows may receive Set/Remove without changing retirement.
- Strict UTF-8 limits: key 192 bytes, value 8192 bytes, tag 35 bytes, uncompressed language body 16 MiB.
  Checked producer preflight precedes encoding/allocation. KECT 1, existing manifest format and pack generation 2.
- Preserve historical wire spelling/hashes. New declarations use canonical spelling. Keep declared empty languages.
  No sharding, tag aliases, silently dropped values or server-only shared-manifest text.
- Schema 4, bundle 2 with declared languages plus values, explicit bundle 1 empty-text conversion and future refusal.
  Preserve existing rows, identities, timestamps and version history. No invented text provenance.
- Per-version completeness is nullable. New atomic commits set complete even with no languages. Legacy NULL is
  unknown until both recorded manifest hashes or verified stored manifests prove empty, without a read-side write.
  Unknown/nonempty history gates publish/rebuild/export/rollback and legacy wrappers with a typed refusal.
- Audit remains 4096 with visible valid-text abbreviation. Add nullable language identity without replacing the
  old record constructor/deconstruction. Full published values live in temporal text history.
- New behavior goes in cohesive helpers/types, not arbitrary file splits. No dependency, warning suppression,
  file-size baseline growth or exemption. Each owner updates only its package README when that API is implemented.
- Request the single local dotnet lease before every RED/GREEN/build. Run each focused command once synchronously,
  retain actual exit/output and release promptly. No stress/load/retry loops or windows. Missing-API compile RED
  is recorded as compilation evidence with zero executed cases, followed by behavioral coverage as the seam lands.
- The coordinator runs the complete Release solution suite once at batch finish and reconciles main/version/tags.
  Stage commits use explicit paths and `Refs #1000`, never a parent-closing body.

## Review focus

1. An old wrapper reconstructs a draft/plan/bundle and erases additive text. Actual backend guards must refuse it.
2. A rival adds text or a declaration between ownership proof and discard. Atomic expected-draft comparison retains it.
3. Set then Remove replaces the only Set. Its independent declaration still yields an empty default-language chunk.
4. Historical wire spelling and language lists differ from the active version. Recovery reproduces the requested hashes.
5. An 8192-byte Unicode value exceeds audit presentation or a language body exceeds 16 MiB. Store full valid values,
   abbreviate safely and refuse over-cap publication before reachable writes.

Tests for these conditions belong to tasks 1, 2 and 5 below.

## File ownership and integration

| Owner | Exclusive area |
|---|---|
| Domain/reference owner | New Authoring DTO/seam/identity/compatibility helpers, additive common DTO hooks, memory text/lifecycle state and core contract fixtures. |
| Publish/recovery owner | New Publish text builders/writer, existing publisher/commit dispatch and rebuild verification, deterministic Catalog.Tests cases. |
| SQLite owner | All SQLite text, schema/inventory/migration and provider Draft/Freeze/Publish/Bundle/Audit/reset hooks through tasks 3 and 5. |
| SQL Server owner | All SQL Server text, DDL/expectations/migration and provider Draft/Freeze/Publish/Bundle/Audit/reset hooks through tasks 4 and 5. |
| Bundle/upgrade owner | Common bundle JSON/model compatibility and Upgrade planning/matching/orchestration, with provider hooks requested from their owners. |
| Admin owner | Server.Admin catalog parsing/dispatch/read/diff payloads and Server.Tests real action cases. |
| Coordinator | Common conformance routing, shared docs/metadata, branch reconciliation, final verification and main integration. |

Provider Bundle/Audit files are never concurrently owned by the bundle owner. Domain safety hooks land before
provider fan-out. Later bundle integration is a coordinated provider-owned follow-up. Run no stage on main.
Unfinished text operations must refuse, never succeed while ignoring data. Record intermediate pending-provider
failures honestly and never call the whole integration branch ready before the final gate.

### Task 1: Freeze domain, atomic companion and reference safety

**Create:** `KhaozEngine.Catalog.Authoring/IContentTextAuthoringStore.cs`, `ContentTextTarget.cs`, `ContentTextEdit.cs`,
`ContentAuthoringChanges.cs`, `ContentDraftTextState.cs`, `ContentTextLanguageDeclaration.cs`, `ContentTextLanguage.cs`,
`ContentTextRevision.cs`, `ContentVersionTextSnapshot.cs`, `Publish/ContentTextPublishSnapshot.cs`,
`Publish/ContentTextPublishPlan.cs`, `Publish/ContentTextCandidate.cs`, `Publish/ContentTextChunkRecord.cs`,
`ContentTextLanguageTag.cs`, `ContentTextCompatibility.cs`,
`ContentBundleTextState.cs`, `ContentTextAuditRendering.cs`, `InMemoryContentAuthoringStore.Text.cs`,
`InMemoryContentAuthoringStore.TextPublish.cs`.
**Modify:** `ContentDraft.cs`, `ContentAuditEntry.cs`, `ContentBundle.cs`, `Publish/ContentPublishPlan.cs` and memory
`Freeze.cs`, `Publish.cs`, `Bundle.cs` hooks. Stage minimal legacy guards in both providers' owned files before
fan-out, coordinated with those owners. Guard legacy `ContentUpgradeDraftMatch` proofs before any text state can
exist. Do not bump the bundle writer before task 5 provides compatibility.
**Test:** new `KhaozEngine.Catalog.Tests/Authoring/TextAuthoringContractTests.cs`, `InMemoryTextAuthoringTests.cs`,
`TextLegacyCompatibilityTests.cs`, `TextAuthoringFixtures.cs`.

**Interfaces:** Produce every proposed companion signature and protected DTO in design section 3. Add protected
nullable text state to old DTOs through overloads, with null meaning unrepresented. Keep old row counts and add
complete row/text/declaration counts. Actual backend gates confirm full lists, base version and epoch.

- [ ] Write tests for ASCII normalization (`EN-us` -> `en-us`), invalid segments/Unicode/underscores, duplicate
  canonical targets in one batch, cross-call Set/Remove replacement and protected list/byte-payload copies.
  Pin Set then Remove -> one pending declaration, Remove of declared absent -> success and no duplicate intent,
  Remove of never-declared -> typed refusal. Verify old constructor/default/deconstruction metadata explicitly.
- [ ] Add memory tests for atomic mixed apply/commit, independent declaration freeze, clock/audit rollback,
  retired-row Set/Remove, full 8192-byte value storage and audit length <=4096 ending `[cut]` without invalid UTF-16.
  Test an old wrapper stripping text through commit/import/discard and an actual held text-only draft. No success
  may erase text. A rival declaration makes TryDiscardChangesAsync return false without delete/audit. Pin the
  authoritative complete/unknown version distinction and no caller-supplied empty proof.
- [ ] Request RED, run the exact command below and retain the actual missing-seam or behavioral failure. Add a
  direct behavioral RED against the partially available route if the first run only fails compilation.

```sh
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter 'FullyQualifiedName~TextAuthoringContractTests|FullyQualifiedName~InMemoryTextAuthoringTests|FullyQualifiedName~TextLegacyCompatibilityTests' --nologo -v minimal
```

- [ ] Implement the companion's apply/freeze/read/commit/atomic-discard reference behavior and fail-closed legacy
  gates. Complete snapshot read uses a positive committed version. Bundle/rollback handlers not yet complete stay
  explicitly unavailable. Do not advertise successful text production on main. Add LanguageTag as an additive audit
  property, leaving `MaxValueLength` at 4096 and old row renderers unchanged.
- [ ] Request GREEN for the same filter, require zero failures/skips, run relevant guards and commit explicit paths.
  Independent review freezes the complete mutation, commit, snapshot and declaration contracts before tasks 2-4.

### Task 2: Deterministic publication and exact-version recovery

**Create:** `KhaozEngine.Catalog.Authoring/Publish/ContentTextCandidateBuilder.cs`, `ContentTextChunkBuilder.cs`,
`ContentTextPublisher.cs`, `ContentTextPackWriter.cs`
and Catalog.Tests `Publish/TextPublishTests.cs`,
`TextRebuildTests.cs`, `TextCrashSafetyTests.cs`.
Consume the `ContentTextCandidate` and `ContentTextChunkRecord` DTO contracts frozen by task 1.
**Modify:** existing `ContentPublisher.cs`, `ContentPublishCommit.cs`, `ContentPackRebuild.cs`,
`ContentRebuildSnapshot.cs`, `ContentRebuildVerification.cs` and manifest-based sweep integration.

**Interfaces:** Consume task 1's frozen snapshot/companion. Produce these proposed internal/helper signatures:

```csharp
ContentTextCandidate ContentTextCandidateBuilder.Build(
    ContentTypeRegistry registry, ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan);
IReadOnlyList<ContentTextChunkRecord> ContentTextChunkBuilder.Build(
    ContentTextCandidate candidate, ContentVersionTextSnapshot baseline);
Task<ContentTextPublishPlan> ContentTextPublisher.PrepareAsync(
    ContentPublishRequest request, ContentTextPublishSnapshot snapshot, CancellationToken cancellationToken);
Task<ContentPublishPlan> ContentPublisher.PrepareFrozenAsync(
    ContentPublishRequest request, ContentPublishBaseline baseline, ContentDraft draft,
    CancellationToken cancellationToken);
```

The scoped row-preparation entry consumes the already frozen row snapshot, without freezing twice.
ContentPublishCommit selects the explicit companion commit, and legacy pack writes refuse unrepresented languages.
The text writer uses a scoped internal row/rule/manifest write entry after text puts, rather than bypassing the
legacy public writer's guard.

Preparation runs in two internal phases so row ids are allocated exactly once. Phase one allocates ids and builds
the row and rule chunks. The candidate and chunk builders then run against the phase-one row ids. Phase two encodes
both manifests once with the complete output language list. Public signatures stay unchanged, and the task 1
invariant that the row plan's languages equal the output chunks holds by construction rather than by a rebuild.
Text-only work prepares with zero row edits. Held text targets are rechecked against the final row plan, because
row-only edits never recheck them, and an inherited value whose marker is no longer eligible refuses.

- [ ] Write real publish tests for text-only changes, unchanged row/rule bytes, changed-language-only reuse,
  deterministic hashes despite text input order and both manifest language lists. Compare against literal keys and
  existing codec bytes, not the new builder as both expectation and result. Set empty resolves empty, Remove of
  the last value retains a decoded empty index and a constructible caller default. Pending introductions survive.
- [ ] Add Fork baseline-copy tests using the final approved row-plan ids, retired translation/removal tests and CLIENT eligibility at type and field
  levels. Test strict surrogate rejection, multibyte byte bounds and checked 16 MiB preflight without huge load
  loops. A retained value with unrepresentable visibility/provenance refuses rather than leaks or disappears.
- [ ] Add version N rebuild after later edits/removal, historical `en-US` spelling, mismatch/missing provenance,
  complete empty-language mapping and failure before pointer. Pin reachable text sweep retention and existing
  orphan-pointer semantics. Legacy NULL is accepted as empty only when both regenerated no-language manifest
  hashes match or both verified stored manifests prove empty. Hash mismatch/nonempty/missing proof refuses, and
  reads never update provenance. Request RED with:

```sh
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter 'FullyQualifiedName~TextPublishTests|FullyQualifiedName~TextRebuildTests|FullyQualifiedName~TextCrashSafetyTests|FullyQualifiedName~PackRebuildTextTests' --nologo -v minimal
```

- [ ] Implement strict producer preflight, ordinal derived-key sorting and actual wire-tag manifest ordering.
  Copy Fork text before original edits, encode one chunk per declared language through the existing codec and put
  text before manifests. Rebuild compares recorded text and both manifest hashes before any pointer.
  Bind fork copies to the final row-plan ids. [#908](https://github.com/APKiwiOrg/KhaozEngine/issues/908) is
  settled on main, family copies allocate inside their family's blocks, and text never compensates for allocation.
- [ ] Request GREEN with the same filter, review failure atomicity/legacy dispatch and commit explicit owned paths.
  Existing unsupported-text recovery tests must become meaningful supported-version or missing-capability cases,
  without weakening their before-write refusal assertions.

### Task 3: SQLite persistence and migration

**Create:** `KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.Text.cs`, `SqliteContentAuthoringStore.TextPublish.cs`,
`SqliteCatalogSchema.VersionFour.cs`, `SqliteCatalogSchemaValidation.VersionFour.cs` and focused schema/text tests.
**Modify:** provider schema/inventory/initialization, Draft/Freeze/Publish and its owned Bundle/Audit hooks.
**Test:** `KhaozEngine.Catalog.Tests/Sqlite/SqliteTextAuthoringTests.cs`, `SqliteTextSchemaMigrationTests.cs`, plus
new `KhaozEngine.Server.Tests/Catalog/ContentAuthoringTextStoreConformance.cs` and
`InMemoryContentAuthoringTextConformanceTests.cs`, `SqliteContentAuthoringTextConformanceTests.cs`.
Coordinator owns common schema-version conformance assertions.

**Interfaces:** Implement the frozen task 1 companion using the existing connection lease and transactions.
Persist four text tables and nullable audit language. Bundle/Audit hook ownership remains here through task 5.

- [ ] Write reopen tests for full values, historical wire tags/hash mappings, empty declared languages, pending
  introduction replacement, mixed-batch/audit failure rollback and frozen writes. Old DTO reconstruction must
  fail at backend confirmation. Test the complete expected-draft discard race with controlled callbacks, no timing.
- [ ] Add verified v1/v2/v3-to-v4 migration tests preserving old rows, timestamps, marks, versions, pins, epoch,
  rules and draft state. New tables stay empty. ValidateOnly names the migration and performs no mutation.
  Legacy version completeness remains NULL and every new commit writes complete in its transaction, including
  zero-language versions. New creation/change timestamps use one injected operation clock. Request RED:

```sh
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter 'FullyQualifiedName~SqliteTextAuthoringTests|FullyQualifiedName~SqliteTextSchemaMigrationTests|FullyQualifiedName~SqliteCatalogSchema' --nologo -v minimal
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~SqliteContentAuthoringTextConformanceTests' --nologo -v minimal
```

- [ ] Implement schema 4, verified migration/inventory and full companion transactions. Enforce UTF-8 limits in
  application code, not an asserted equivalence with SQLite/SQL character length. Stage all row/text/audit/ledger
  changes under one commit and confirm the complete frozen draft before deleting any category.
- [ ] Request sequential GREEN under one lease, require zero failures/skips for SQLite, review and commit. Leave
  unfinished text bundle/upgrade paths guarded until task 5's owner-coordinated follow-up completes them.

### Task 4: SQL Server persistence and migration

**Create:** `KhaozEngine.Catalog.SqlServer/SqlServerContentAuthoringStore.Text.cs`, `SqlServerContentAuthoringStore.TextPublish.cs`,
`CatalogSchemaV4.sql`, `SqlServerCatalogSchema.VersionFour.cs` and targeted text/schema tests.
**Modify:** provider schema resources/expectations/initialization, Draft/Freeze/Publish and owned Bundle/Audit hooks.
**Test:** `KhaozEngine.Server.Tests/Catalog/SqlServer/SqlServerTextSchemaMigrationTests.cs`,
`SqlServerTextAuthoringTests.cs`, `SqlServerContentAuthoringTextConformanceTests.cs`.

**Interfaces:** Implement the same frozen companion, exact snapshot and four tables under existing Serializable
scopes. This owner may work independently of task 3 after tasks 1-2 freeze contracts. It retains Bundle/Audit hooks.

- [ ] Port the shared conformance facts with explicit environment-gated overrides and the existing serialized SQL
  Server collection. Write fresh/migrated schema parity tests for columns, indexes, collations and named checks.
  Pin full 8192-byte values, 4096 abbreviated audits, historical spellings and atomic mixed/clock/audit failures.
- [ ] Pin legacy NULL completeness, no fabricated empty snapshot and complete new zero-language commits. Snapshot
  validation gates all mutations/recovery/export without treating missing mapping rows as proof.
- [ ] Test migrations from verified older shapes and validation-only refusal without type/schema writes. Do not
  assert UTF-16 DATALENGTH or LEN equals UTF-8 value bytes. Request RED on an authorized isolated database:

```sh
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~SqlServerTextAuthoringTests|FullyQualifiedName~SqlServerTextSchemaMigrationTests|FullyQualifiedName~SqlServerContentAuthoringTextConformanceTests' --nologo -v minimal
```

- [ ] Implement persistent companion dispatch, parameters/readers, schema 4 migration and complete-state guards.
  Preserve connection/scope ownership, cancellation, pointer order, existing audit cap and null old audit language.
- [ ] Request GREEN with the same command. Report actual gate availability and skipped counts. Skipped SQL facts
  are not a provider pass or proof borrowed from SQLite. Coordinate real provider proof before the final gate.
- [ ] Independently review and commit explicit paths. Task 5's common owner requests follow-up provider hooks from
  this owner, rather than editing its Bundle/Audit files concurrently.

### Task 5: Complete bundle, rollback and upgrade safety

**Create:** Authoring `ContentBundleTextJson.cs`, `ContentBundleTextCompatibility.cs`, scoped Upgrade text matching
helpers, Catalog.Tests `Authoring/TextBundleTests.cs`, `Upgrade/TextUpgradeTests.cs` and Server.Tests conformance
bundle/rollback/disposal facts.
**Modify:** task 1's ContentBundleTextState and common ContentBundle/ContentBundleJson compatibility,
ContentDraftCandidate and Upgrade plan/context/
runner/match paths. Memory Bundle/Freeze/Audit hooks belong to the reference owner. SQLite and SQL Server hooks
remain with their provider owners, including reset/prior-state participation and explicit companion import.

**Interfaces:** Bundle 2 contains declared canonical/wire tags plus full targeted values. Bundle 1 converts
explicitly to empty text, retaining its old fixture. Add
`ContentUpgradePlan.Changes(ContentAuthoringChanges changes, IReadOnlyList<string> changeLines)` and matching
complete-state overloads while preserving old signatures. Use TryDiscardChangesAsync for text-aware disposal.

- [ ] Write bundle 2 round-trip/import tests with retired rows, historical wire spelling and empty languages.
  Reject colliding aliases, missing sections, invalid text and future versions before reset. Reconstruct the old
  constructor from a text-bearing bundle and prove refusal, not a successful row-only import.
- [ ] Add text-only upgrade preview/apply, later planners seeing earlier text, sticky declaration matching,
  backend-held rival text invisible to an old wrapper and atomic discard-race tests. Rollback retains all currently
  declared languages and old row-retirement blockers. Unknown snapshot status refuses export/rollback/upgrade,
  including through reconstructed DTOs. No generic empty republish or unretire is introduced.
- [ ] Request sequential RED:

```sh
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter 'FullyQualifiedName~TextBundleTests|FullyQualifiedName~TextUpgradeTests|FullyQualifiedName~BundleJsonFormatVersionTests' --nologo -v minimal
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~InMemoryContentAuthoringTextConformanceTests|FullyQualifiedName~SqliteContentAuthoringTextConformanceTests' --nologo -v minimal
```

- [ ] Complete explicit companion import/rollback and exact export through coordinated provider follow-ups.
  Provider confirmation covers rows, text and declarations before draft consumption/reset. Keep caller actor/operator
  and upgrade ledger atomic. Preserve empty-destination bundle import, do not invent a live historical repair verb.
  Legacy proof overloads cannot claim incomplete or text-bearing drafts are known-empty.
- [ ] Request GREEN, plus the task 4 SQL conformance filter after its bundle hooks land. Independently review all
  three provider follow-ups before lifting transitional refusals. Commit each owner's explicit paths.

### Task 6: Real admin-to-reader behavior

**Create:** `KhaozEngine.Server.Admin/Catalog/CatalogTextEditParser.cs` and text payload helpers, Server.Tests
`Catalog/CatalogTextAdminActionTests.cs`, `CatalogTextEndToEndTests.cs`.
**Modify:** CatalogEditActions, CatalogReadActions, CatalogBundleActions, discard/rollback dispatch and additive
payloads. Preserve old action names, row-field marker refusal and captured actor/operator behavior.

**Interfaces:** catalog-edit accepts textEdits with type key, content key, field, language, set/remove and value.
Use the complete ApplyChangesAsync once. Supported import/commit/discard/rollback dispatch is explicit, and legacy
wrappers cannot return false success. Use the existing CatalogActionHarness for real registered action execution.

- [ ] Add malformed/multiple-finding tests, canonical duplicate/undeclared-language refusal, CLIENT eligibility
  and mixed add plus text atomicity. A failed second target leaves rows, draft categories and audit unchanged.
- [ ] Add real admin add/name -> publish -> actual manifest/KECT/index -> ContentStringCatalog resolution, then a
  text-only update with the same reader/client code. Pin literal derived key/value, fallback unchanged, empty Set
  resolving empty and Remove of the last value leaving a constructible empty default-language layer.
- [ ] Request RED:

```sh
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~CatalogTextAdminActionTests|FullyQualifiedName~CatalogTextEndToEndTests|FullyQualifiedName~CatalogEditActionTests|FullyQualifiedName~CatalogBundleActionTests' --nologo -v minimal
```

- [ ] Implement cohesive parsing and complete-state read/diff/draft/history/discard paths. Gather all findings
  before one atomic apply. Typed unsupported capability and frozen/stale conflicts remain distinct from malformed
  JSON. Do not read raw input, launch a game or add authentication/transport.
- [ ] Request GREEN, review and commit explicit paths. Update the owning package READMEs now that APIs exist.

### Task 7: Coordinator completion gate

- [ ] Review the whole integration branch for old wrapper/DTO data loss, full provider commit dispatch,
  exact-version provenance, sticky/default languages, safe audit abbreviation and no server-only leakage.
  Every proposed companion operation must now be implemented and validated, not left guarded as unfinished.
- [ ] Re-read current main/version/tags. Reconcile conflicts and one valid package-bearing stage on the integration
  branch. Complete the full Markdown sweep and update shared USING/changelog/version declarations. Keep the parent
  issue open until all required behavior is achieved, and do not automatically release or tag.
- [ ] Run each whole-tree guard and retain separate actual logs/exit codes:

```sh
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

- [ ] Under the exclusive lease, create the gitignored feed then complete the final Release verification once:

```sh
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
```

- [ ] Record failures, skips and actual SQL provider evidence. Fix/review any newly discovered gap before repeating
  only justified checks. The coordinator integrates/pushes the complete validated result and owns guarded packing
  from current pushed main. Consumer adoption remains a separate handoff. No release/tag or stress permission is
  implied by this plan.
