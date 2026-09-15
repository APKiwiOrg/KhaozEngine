# Content Catalog Phase 1 Implementation Plan (Scope A, engine half)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Ship Scope A of the content foundation: five new packages that author tunable game content in a
database, publish it as immutable content-addressed packs, and load it into a server runtime of arrays
indexed by id with a fail-closed boot.

**Architecture:** `KhaozEngine.Catalog` (Foundation, no third-party) owns the registry, the byte formats,
the hashes, the validator, the pack store seam, the read-side runtime and the loot roller.
`KhaozEngine.Catalog.Authoring` (Server, pure .NET) owns the temporal store seam, drafts, the id
allocator, publish, diff and the bundle. `KhaozEngine.Catalog.Sqlite` and `KhaozEngine.Catalog.SqlServer`
are opt-in sibling providers in no umbrella. `KhaozEngine.Catalog.Netcode` (Server) owns the connect-door
layer. Three existing packages gain additive members: `KhaozEngine.Primitives` gains the random seam,
`KhaozEngine.NetWorld` gains a conflict status and an object error payload, and
`KhaozEngine.Server.Admin` gains the dispatch arm and the registered actions.

**Tech Stack:** .NET 10, xUnit, `System.IO.Compression` Brotli, `System.Security.Cryptography` SHA-256,
`Microsoft.Data.Sqlite` (provider only), `Microsoft.Data.SqlClient` (provider only). No third-party
package enters `KhaozEngine.Catalog`.

**Spec:** docs/design/CONTENT-CATALOG-DESIGN-2026-09-15.md, phase 1 is section 18.1 milestones 1.1 to 1.5
plus the package-change table of 2.1.

**Contracts:** docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md. BINDING. A spec section may refine a
contract and may never contradict one. If a task looks like it needs a contradiction, stop and report
rather than diverging.

**Out of scope for this plan:** Grimhollow's own adoption steps (spec section 16.8) are a separate plan in
that repo, named as the follow-on in the last task. Scope B (`KhaozEngine.ItemInstances`, #884) is a
parallel program and nothing here depends on it.

---

## Global Constraints

These bind every task. Breaking one is not a local decision.

**Format and encoding, from contracts 15.**

- Little endian everywhere, through `System.Buffers.Binary.BinaryPrimitives` with the endianness in the
  method name. `BitConverter` is FORBIDDEN because it is host endian.
- Varints are unsigned LEB128, seven value bits per byte, low group first, high bit set on every byte but
  the last, at most five bytes for 32 bit and ten for 64 bit. Encodings MUST be minimal. A field declared
  signed is zig-zag transformed first. Content ids, chunk indices, kind ids, lengths, counts and row
  counts are all declared UNSIGNED and are never zig-zagged.
- Every standalone format opens with a four-character ASCII magic then a `public const ushort` version.
  `KECC` chunk, `KECM` manifest, `KECR` rule chunk, `KECT` text chunk. A version mismatch is a REFUSAL of
  the whole record with a reason token, never a best-effort partial read.
- Every digest is SHA-256, domain separated under `kec/` with its own sub-domain and
  `ContentHash.SchemeVersion` folded into the prefix. Lower hex as text, raw 32 bytes as a field. No two
  digests share a sub-domain.
- **No floating point on any path a client and a server must agree** (contracts 13.4). Every stat value,
  every roll, every threshold and every scaled number is an integer. `float`, `double` and `MathF` are out.
  Rounding is round half up through floor division, never `Math.Round`.

**Behaviour.**

- **Decoders never throw.** Every decode entry point returns `false` plus a stable reason token from the
  fixed list in spec 15.2. Bytes arrive from a remote peer, so a decoder is total.
- **No ambient statics.** The registry is per `ContentTypeRegistry` instance. The random source arrives by
  constructor parameter (contracts 14.4), never a service locator and never a default. This is what keeps
  every new test class out of a `DisableParallelization` collection.
- **The registry freezes at first pack load** and a later registration throws (contracts 4.2).
- **Fail closed at boot.** A missing or invalid active content version exits non-zero with findings on
  stderr. There is no runtime fallback to code defaults (contracts 10.5).
- **The validator is pure.** No side effects, no logging, no counters, no ambient reads, no throwing for
  content reasons. It takes its whole world as arguments and accumulates findings rather than stopping at
  the first (contracts 10.4, spec 5.1).
- **A published version is immutable.** Remap rules are append only with no update and no delete path
  anywhere in either provider (contracts 8.1, spec 4.4).
- **Reserve before issue.** The id allocator commits a reservation on its own transaction BEFORE handing
  out any id from below it (contracts 6.2, spec 4.7). Inverting that order is the one failure that section
  exists to prevent.

**Repository rules, from AGENTS.md.**

- **Warnings are errors** in every configuration. Keep the tree at zero warnings and fix at the source,
  never with `NoWarn`, `#pragma warning disable` or `TreatWarningsAsErrors=false`.
- **Every file stays under 800 lines.** The KESIZE ratchet is compile time. When it fires, the fix is a new
  TYPE, never an arbitrary split at a line number. Do NOT edit `.filesize-baseline`, and do not pass
  `FILESIZE_OK`. If a file genuinely cannot be split by responsibility, stop and report.
- **CI tests Release.** Run each new test project once with `-c Release` before handing the task back. A
  test that asserts on `Debug.Assert`, a `[Conditional("DEBUG")]` member or an `#if DEBUG` block passes
  locally and goes red on the first push.
- **A new test project references ONLY what its tests use**, sets `<IsPackable>false</IsPackable>` and
  pins `<RootNamespace>KhaozEngine.Tests</RootNamespace>`, so its declared namespaces are
  `KhaozEngine.Tests.Catalog.*`. Push CI selects test projects by the reference graph, so an over-broad
  reference silently degrades selection.
- **A new packable package ships its README in the SAME commit that creates its csproj**, with a
  `<PackageReadmeFile>README.md</PackageReadmeFile>`, plus a `**KhaozEngine.<Name>**` catalog row in the
  root `README.md`. `scripts/check-doc-versions.sh` runs on every push and fails otherwise, so deferring
  either to the last task turns every intermediate push red.
- **Commit early, with explicit paths.** Never `git add -A`, never `git commit -a`, never `git stash`.
- **No em dashes, no en dashes, no semicolons in prose.** Run `scripts/check-dashes.sh --tree` and
  `scripts/check-prose.sh --tree` before handing a task back. Semicolons in code are fine.
- **One version bump for the whole batch**, in the last task only. No task before it touches
  `Directory.Build.props`. No tag: the owner starts a release.

---

## Milestone 1.1: `KhaozEngine.Catalog`, the formats and the registry

**Group acceptance (spec 18.1's gate for 1.1):** the golden format files of spec 15.1 decode, hash and
re-encode byte for byte, the decoder fuzzing of 15.2 runs 10,000 mutants per golden with no throw and only
listed reason tokens, and the cross-version round trips of 15.3 hold. Tasks 1 to 12.

### Task 1: The random source seam in `KhaozEngine.Primitives` (small)

Contracts 14.1, 14.2 and 14.4. Spec 2.1's third package-change row.

**Files:**

- Create: `KhaozEngine.Primitives/IRandomSource.cs`
- Create: `KhaozEngine.Primitives/SeededRandomSource.cs`
- Create: `KhaozEngine.Primitives/CryptographicRandomSource.cs`
- Create: `KhaozEngine.Foundation.Tests/Primitives/RandomSourceTests.cs`
- Modify: `KhaozEngine.Primitives/README.md`

**Interfaces:**

- Consumes: the existing `KhaozEngine.Primitives/DeterministicRng.cs`
- Produces: `IRandomSource` and its two implementations, reachable from every package in both scopes with
  no new dependency edge

**Why here and not in a new package:** `Primitives` already owns `DeterministicRng`, which
`SeededRandomSource` wraps, and it sits below `KhaozEngine.Catalog`. Declaring the seam in Scope B's
package would close a cycle, because `ItemInstances` depends on `Catalog` and `Catalog` needs the seam for
the loot draw and for the fuzzer (contracts 14.1).

- [ ] **Step 1: Write failing tests.**

Cover exactly these: `NextInt` throws `ArgumentOutOfRangeException` when `maxExclusive <= minInclusive`,
`NextInt` over a one-wide range returns the single value without drawing, `SeededRandomSource` with the
same seed produces an identical sequence across two instances, two different seeds diverge,
`NextRollPosition` covers 0 and 65535 over a large seeded run and never exceeds `ushort.MaxValue`,
`NextBytes` fills the whole destination span, and `CryptographicRandomSource` produces a uniform
distribution over a narrow range with no modulo bias (chi-square over 10,000 draws into 3 buckets, a loose
bound, this is a bias smoke test rather than a statistics suite). Assert that neither type exposes a
`Seed`, a `State` or a `CreateDerived` member, by reflection over public members.

- [ ] **Step 2: Run the tests and verify the missing types fail the build.**

~~~bash
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~RandomSourceTests
~~~

- [ ] **Step 3: Implement the seam exactly as contracts 14.1 writes it.**

~~~csharp
public interface IRandomSource
{
    int    NextInt(int minInclusive, int maxExclusive);
    ulong  NextULong();
    ushort NextRollPosition();          // uniform over 0..65535, contracts 6.4
    void   NextBytes(Span<byte> destination);
}
~~~

There is NO `Seed` property, no `State` property, no `CreateDerived` and no way to ask an instance what it
will do next. A seam whose seed is readable is a seam a crafting system can leak. Nothing returns a float.

- [ ] **Step 4: Implement both sources.**

`SeededRandomSource(ulong seed)` WRAPS `DeterministicRng` rather than reimplementing a generator, so the
engine keeps one seeded stream definition and the vectors already pinning it keep working. The seed is not
readable back off the instance.

`CryptographicRandomSource()` seeds from `System.Security.Cryptography.RandomNumberGenerator`. Uniform
integer draws use REJECTION SAMPLING rather than modulo, because modulo bias on a crafting roll is an edge
a player can farm. Both compute `NextInt` over `(uint)(maxExclusive - minInclusive)` so a full-range draw
does not overflow.

- [ ] **Step 5: Add the two types to `KhaozEngine.Primitives/README.md`, run the tests green, run the guards.**

~~~bash
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~RandomSourceTests
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
~~~

- [ ] **Step 6: Commit.**

~~~bash
git add KhaozEngine.Primitives/IRandomSource.cs KhaozEngine.Primitives/SeededRandomSource.cs KhaozEngine.Primitives/CryptographicRandomSource.cs KhaozEngine.Primitives/README.md KhaozEngine.Foundation.Tests/Primitives/RandomSourceTests.cs
git commit -m "primitives(random): add the IRandomSource seam and its two sources"
~~~

---

### Task 2: The `KhaozEngine.Catalog` package and the format primitives (medium)

Spec 2.1, 2.6, 7.1, 7.8, 9.1. Contracts 5.3, 15.

**Files:**

- Create: `KhaozEngine.Catalog/KhaozEngine.Catalog.csproj`
- Create: `KhaozEngine.Catalog/README.md`
- Create: `KhaozEngine.Catalog/ContentPackFormat.cs`
- Create: `KhaozEngine.Catalog/ContentVarint.cs`
- Create: `KhaozEngine.Catalog/ContentHash.cs`
- Create: `KhaozEngine.Catalog/ContentKey.cs`
- Create: `KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj`
- Create: `KhaozEngine.Catalog.Tests/Format/ContentVarintTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/ContentHashTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/ContentKeyTests.cs`
- Modify: `KhaozEngine.slnx`, `README.md` (catalog row)

**Interfaces:**

- Consumes: `KhaozEngine.Primitives`
- Produces: `ContentPackFormat`, `ContentVarint`, `ContentHash`, `ContentKey`

**Lift from the spike, with changes.** `KhaozEngine.Benchmarks/Catalog/ContentVarint.cs`,
`ContentHash.cs`, `ContentPackFormat.cs` and `ContentKey.cs` are clean and were the code that measured the
budgets. Copy them into the package and change three things: the namespace becomes `KhaozEngine.Catalog`,
`ContentPackFormat` drops the spike's measurement-only comment and gains XML doc on every const, and
`ContentKey` gains `GetHashCode` over the UTF-8 bytes so it can key a dictionary in an authoring test. The
spike stays where it is and keeps building. Do not delete it and do not re-point the benchmark at the new
package in this plan.

- [ ] **Step 1: Write failing tests before the copy.**

`ContentVarintTests`: round trip every boundary (0, 1, 127, 128, 16383, 16384, `uint.MaxValue`), a
non-minimal encoding (`0x81 0x00` for 1) is rejected with `varint-not-minimal`, a non-terminating varint is
rejected with `varint-overflow` after five bytes for 32 bit and ten for 64 bit, zig-zag maps 0 to 0, -1 to
1 and 1 to 2, and a read that would run past the end of the span returns false rather than throwing.

`ContentHashTests`: each of the five sub-domains produces a different digest over identical bytes, a
`SchemeVersion` change would change every digest (assert by computing with a local copy of the prefix
construction), the hex is 64 lower-case characters, and the manifest text digest formats numbers through
`CultureInfo.InvariantCulture` with strings length prefixed `"{len}:{value} "` and a bare `"- "` for null.

`ContentKeyTests`: a key built from a string and the same key built from a blob slice compare equal,
comparison is ORDINAL so `Stone` and `stone` differ, `ToString` materialises on demand and the type holds
no string field, and a 64-byte key is legal while a 65-byte one is not rejected here (the character and
length rules are the validator's, `KEC0001`, not the value type's).

- [ ] **Step 2: Create the package and the test project.**

The csproj follows `KhaozEngine.Items/KhaozEngine.Items.csproj`: a `PackageId`,
`<Version>$(KhaozEngineVersion)</Version>`, a `<Description>`, `<PackageReadmeFile>README.md</PackageReadmeFile>`
and the `<None Include="README.md" Pack="true" PackagePath="\" />` item. Its only `ProjectReference` is
`KhaozEngine.Primitives`. It takes NO third-party package, and in particular not `JsonSchema.Net`, which an
architecture test already contains to `KhaozEngine.Content`.

`KhaozEngine.Catalog.Tests` sets `<IsPackable>false</IsPackable>` and
`<RootNamespace>KhaozEngine.Tests</RootNamespace>`, references `KhaozEngine.Catalog` ONLY, and is added to
`KhaozEngine.slnx` beside `KhaozEngine.Foundation.Tests`. Declared namespaces are `KhaozEngine.Tests.Catalog.*`.

Write `KhaozEngine.Catalog/README.md` now, not in the last task: `check-doc-versions.sh` fails a packable
package with no README or no catalog row on the very next push. Add the `**KhaozEngine.Catalog**` row to
the root `README.md` package table and the directory to its repo-layout block in the same commit.

- [ ] **Step 3: Implement the four types.**

~~~csharp
public static class ContentPackFormat
{
    public const ushort ChunkFormatVersion     = 1;
    public const ushort ManifestFormatVersion  = 1;
    public const ushort RuleChunkFormatVersion = 1;
    public const ushort TextChunkFormatVersion = 1;
    public const int    Generation             = 1;    // contracts 7.4
    public const int    HashSchemeVersion      = 1;    // contracts 7.3
    public const int    MaxContentRowBytes     = 4096; // absolute ceiling, no type may exceed it
    public const int    DefaultMaxRowBytes     = 1024; // a type's cap when it declares none
    public const int    MaxChunkUncompressedBytes = 16 * 1024 * 1024;
}
~~~

`ContentHash` exposes `OfChunk`, `OfRuleChunk`, `OfTextChunk`, `OfServerManifest`, `OfClientManifest` and
`OfBytesForKind(ReadOnlySpan<byte>)`, which dispatches on the four-byte magic so a caller verifying a
downloaded file hashes it under the right sub-domain and rejects an unknown magic before reading a length.
The chunk digests take a UTF-8 domain prefix `"kec/<sub>/" + Inv(SchemeVersion) + "\n"` followed by the
canonical bytes RAW. The manifest digests take a canonical TEXT, the way `TileWorldHash` does.

- [ ] **Step 4: Run green, run the guards, run `check-doc-versions.sh`.**

~~~bash
dotnet build KhaozEngine.Catalog/KhaozEngine.Catalog.csproj -c Release
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
sh scripts/check-doc-versions.sh
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
~~~

- [ ] **Step 5: Commit.**

~~~bash
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests KhaozEngine.slnx README.md
git commit -m "catalog(format): add the package and the varint, hash and key primitives"
~~~

---

### Task 3: The field schema and the content type registry (medium)

Contracts 4.2, 4.3, 4.4, 4.5, 4.7, 11.1. Spec 2.2, 3.6, 7.1.

**Files:**

- Create: `KhaozEngine.Catalog/ContentTypeId.cs`
- Create: `KhaozEngine.Catalog/ContentVisibility.cs`
- Create: `KhaozEngine.Catalog/ContentFieldSchema.cs` (holds `ContentFieldKind` and `ContentFieldEntry`)
- Create: `KhaozEngine.Catalog/ContentRow.cs` (holds `ContentFieldValue`)
- Create: `KhaozEngine.Catalog/IContentRowCodec.cs`
- Create: `KhaozEngine.Catalog/IContentValidator.cs`
- Create: `KhaozEngine.Catalog/ContentTypeRegistry.cs`
- Create: `KhaozEngine.Catalog/ContentRegistrationException.cs`
- Create: `KhaozEngine.Catalog.Tests/Registry/ContentTypeRegistryTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Registry/ContentFieldSchemaTests.cs`

**Interfaces:**

- Consumes: Task 2
- Produces: the whole registration surface the engine types, Scope B and a game all call

**Write fresh.** The spike's `ContentSchema.cs` and `ContentTypeTable.cs` carry a measurement-shaped
registry with no band, no codec-schema check and no freeze. The rules below are most of the type.

- [ ] **Step 1: Write failing registry tests.**

Cover exactly these: type id 0 is refused, a `Game` band caller registering type 300 throws
`ContentRegistrationException` naming the band and the range, an `Engine` band caller registering 1024
throws, the same type id twice throws, two types sharing a type KEY throw (a key is unique across the
whole registry, spec 3.3), a `chunkSlots` that is not a power of two between 256 and 65,536 throws, a
`maxRowBytes` above `ContentPackFormat.MaxContentRowBytes` throws, a registration failing
`chunkSlots * (maxRowBytes + 8) + 36 <= MaxChunkUncompressedBytes` throws naming BOTH numbers, a codec
whose `WrittenFields` set differs from its schema's field names throws naming both sides, and a
registration after `Freeze()` throws. Assert `Freeze` is idempotent and that lookup by id and by key both
answer after it.

Assert REGISTRATION ORDER INDEPENDENCE at this level too: register five types in two different orders and
assert the registry's enumeration sorted by type id is identical. Nothing anywhere derives an ordinal from
registration order (contracts 4.3).

- [ ] **Step 2: Run and verify the missing types fail the build.**

- [ ] **Step 3: Implement the values.**

~~~csharp
public readonly record struct ContentTypeId(ushort Value)
{
    public bool IsEngine    => Value >= 1    && Value <= 255;
    public bool IsInstances => Value >= 256  && Value <= 1023;
    public bool IsGame      => Value >= 1024;
}

public enum ContentVisibility { Client = 0, ServerOnly = 1 }
public enum ContentRegistrationBand { Engine, Instances, Game }

public enum ContentFieldKind
{ Int, ScaledInt, Bool, KeyReference, TagList, LocalizedTextKey, OpaqueBytes }
~~~

`ContentFieldKind`'s numbering is durable because `catalog_row_field.field_kind` stores it with a
`CHECK (field_kind BETWEEN 0 AND 6)` (spec 4.4). Write the numbers explicitly rather than relying on
declaration order.

~~~csharp
public sealed record ContentFieldEntry(
    string Name,
    ContentFieldKind Kind,
    string? ReferenceTarget,      // a content type KEY for KeyReference, "tag" for TagList, else null
    ContentVisibility Visibility,
    bool Required,
    int Scale = 1)
{
    // A LocalizedTextKey is a MARKER: no value in the row, no bytes in the chunk (spec 3.2).
    public bool IsDerivedMarker => Kind == ContentFieldKind.LocalizedTextKey;
}

public sealed class ContentFieldSchema
{
    public ContentFieldSchema(IReadOnlyList<ContentFieldEntry> fields);
    public IReadOnlyList<ContentFieldEntry> Fields { get; }
    public bool TryGet(string name, out ContentFieldEntry entry);
}
~~~

**PLAN CHOICE.** Spec 2.2 names `ContentRow` as "id, key, parent id, an ordered field-value list" and never
names the element type. This plan defines it:

~~~csharp
public readonly struct ContentFieldValue
{
    public ContentFieldKind Kind { get; }
    public long Number { get; }                      // Int, ScaledInt, Bool (0 or 1), KeyReference
    public ReadOnlyMemory<byte> Bytes { get; }       // TagList (varint ids), OpaqueBytes
    public bool IsAbsent { get; }
}

public sealed class ContentRow
{
    public ContentTypeId Type { get; }
    public int Id { get; }
    public ContentKey Key { get; }
    public int ParentId { get; }                     // 0 in phase 1, KEC0031 refuses non-zero
    public bool IsRetired { get; }
    public IReadOnlyList<ContentFieldValue> Fields { get; }   // parallel to the schema's Fields
}
~~~

Fields are PARALLEL to the schema's ordered list rather than a name-keyed map, so a codec is a positional
walk and an absent optional field is `IsAbsent`. A marker field occupies a slot and carries `IsAbsent`,
which is what keeps the schema index and the row index the same number.

- [ ] **Step 4: Implement the two seams and the registry.**

~~~csharp
public interface IContentRowCodec
{
    IReadOnlyList<string> WrittenFields { get; }
    void Encode(ContentRow row, IBufferWriter<byte> destination);
    bool TryDecode(ReadOnlySpan<byte> body, out ContentRow row, out string? reason);
}

public interface IContentValidator
{
    void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings);
}

public sealed class ContentTypeRegistry
{
    public void RegisterContentType(
        ContentRegistrationBand band,
        ushort typeId,
        string typeKey,
        IContentRowCodec codec,
        IContentValidator? validator,
        ContentFieldSchema schema,
        ContentVisibility defaultVisibility,
        int chunkSlots,
        int maxRowBytes = ContentPackFormat.DefaultMaxRowBytes,
        int? maxDefinitionId = null,
        IContentLoadIndex? loadIndex = null);

    public void Freeze();
    public bool IsFrozen { get; }
    public bool TryGet(ContentTypeId type, out ContentTypeRegistration registration);
    public bool TryGetByKey(string typeKey, out ContentTypeRegistration registration);
    public IReadOnlyList<ContentTypeRegistration> ByTypeId { get; }   // sorted ascending, always
}
~~~

Two interfaces this signature names arrive in later tasks, and both are declared HERE as their own small
files so this task compiles on its own: `IContentSnapshot` (its seven members are written out in Task 9,
and `ContentSnapshot` implements it there) and `IContentLoadIndex` (`ContentTypeId Type { get; }` plus
`void Build(IContentSnapshot snapshot)`, wired into the boot in Task 21). Declaring an interface in one
task and implementing it in another is the whole reason the interface exists.

The registry is a map keyed by type id and is NOT a static. It is per instance, deliberately, which is why
nothing in this milestone needs a `DisableParallelization` collection (spec 2.6).

- [ ] **Step 5: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests
git commit -m "catalog(registry): add the field schema and the banded type registry"
~~~

---

### Task 4: The six engine content types and their row codecs (large)

Spec 3.1 to 3.5, 7.3, 7.9. Contracts 4.6, 12.1, 13.1.

**Files:**

- Create: `KhaozEngine.Catalog/Types/EngineContentTypes.cs` (the ids, keys and the one registration helper)
- Create: `KhaozEngine.Catalog/Types/TagContentType.cs`
- Create: `KhaozEngine.Catalog/Types/ItemContentType.cs`
- Create: `KhaozEngine.Catalog/Types/StatContentType.cs`
- Create: `KhaozEngine.Catalog/Types/LootTableContentType.cs`
- Create: `KhaozEngine.Catalog/Types/LootEntryContentType.cs`
- Create: `KhaozEngine.Catalog/Types/BaseSocketContentType.cs`
- Create: `KhaozEngine.Catalog/Types/ContentRowCodecBase.cs` (the positional encode and decode walk)
- Create: `KhaozEngine.Catalog.Tests/Types/EngineContentTypeTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Types/RowCodecRoundTripTests.cs`

**Interfaces:**

- Consumes: Tasks 2 and 3
- Produces: `EngineContentTypes.Register(ContentTypeRegistry)`, six schemas and six codecs

**Lift the row layout from the spike's `ContentRowCodec.cs` and `CatalogRows.cs`, then change the seam.**
The spike encodes typed structs positionally, which is the right byte layout and the wrong public shape.
The shipped codecs implement `IContentRowCodec` over `ContentRow`, so `ContentRowCodecBase` does the
positional walk once, driven by the schema, and each type's file is the schema plus any constraint the
generic walk cannot express (the 128-byte asset reference cap and its `a-z0-9_./-` character set).

- [ ] **Step 1: Write failing tests.**

`EngineContentTypeTests`: the six ids and keys are exactly `1 tag`, `2 item`, `3 stat`, `4 loot_table`,
`5 loot_entry`, `6 base_socket`, the default chunk slots are exactly `4096, 1024, 4096, 4096, 16384,
16384`, `loot_entry` and `base_socket` declare `maxRowBytes: 512`, `loot_table` and `loot_entry` are
`ServerOnly` at the TYPE level and the other four are `Client`, and every one of the six registers through
the `Engine` band helper rather than by a caller naming a band. Pin the field NAMES of each schema against
spec 3.2 to 3.5 literally, because a field name is what a localization key is derived from.

`RowCodecRoundTripTests`: every type round trips a fully populated row byte for byte, every type round
trips a row with every optional field absent, a marker field contributes ZERO bytes (assert the `tag` row
of spec 7.9 encodes to exactly one byte, `0A` for sort 10), an asset reference over 128 bytes is refused
with `field-malformed`, an asset reference carrying a character outside `a-z0-9_./-` is refused, a tag list
preserves AUTHORED ORDER and is never sorted, and a truncated body returns false with `field-truncated`
rather than throwing.

- [ ] **Step 2: Run and verify the missing types fail the build.**

- [ ] **Step 3: Implement the schemas exactly as the spec tables give them.**

`tag` is `name` (marker, required) and `sort` (int). `item` is the fifteen-row table of spec 3.3, in that
order: `name`, `examine`, `tags`, `stackable`, `max_stack`, `tradable`, `value`, `icon`, `mesh`,
`held_mesh`, `ground_pose`, `icon_tilt`, `icon_spin`, `durability_max`, `socket_max`, `equip_profile`.
`stat` is `name`, `scale`, `min`, `max`, `tags`, `display_format`. `loot_table` is `roll_count`, `tags`,
`guaranteed`. `loot_entry` is `table`, `item`, `nested_table`, `weight`, `chance_bp`, `min_count`,
`max_count`, `sort`, `required_tags`. `base_socket` is `item`, `sort`, `socket_type`.

`equip_profile` and `socket_type` are `KeyReference` fields whose `ReferenceTarget` is the TYPE KEY
`equip_profile` and `socket_type`. The engine writes the key once at registration and a game registers a
type under it in its own range. When no type is registered under the key the field MUST be 0 on every row,
which is `KEC0007`'s job in Task 11 and not this task's.

`icon`, `mesh` and `held_mesh` are `OpaqueBytes` carrying a varint length then UTF-8, capped at 128 bytes,
character set `a-z0-9_./-`. Spec CCR-1 asks the contracts for a dedicated `AssetReference` kind and this
plan builds on the contracts AS WRITTEN. If the owner accepts CCR-1 before this task runs, stop and report
rather than inventing the kind.

- [ ] **Step 4: Implement `ContentRowCodecBase`.**

A row body opens with its own KEY, varint length prefixed UTF-8, then each schema field in order. The key
is first because the runtime reads a key out of `Bodies` rather than out of a second array (spec 9.1), so
there is no `Keys` array anywhere and an id-to-key read is the same slice one varint in.

Per kind: `Int` and `KeyReference` are an unsigned varint, `ScaledInt` is an unsigned varint of the scaled
integer, `Bool` is one byte 0 or 1, `TagList` is a varint count then that many varint tag ids in authored
order, `OpaqueBytes` is a varint length then the bytes, and `LocalizedTextKey` writes NOTHING. An optional
field that is absent writes a single zero byte as its absence marker for the variable-length kinds and is
skipped by position for the fixed ones. Pick ONE absence convention, write it down in the file's doc
comment, and pin it in the golden of Task 12: a decoder and an encoder that disagree about absence is
exactly what `KEC0027` exists to catch.

Every decode path is TOTAL. A malformed row returns false with a reason and never throws.

- [ ] **Step 5: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
git add KhaozEngine.Catalog/Types KhaozEngine.Catalog.Tests/Types
git commit -m "catalog(types): add the six engine content types and their row codecs"
~~~

---

### Task 5: The `KECC` chunk file (medium)

Spec 7.2, 7.3, 7.5, 7.9. Contracts 15.

**Files:**

- Create: `KhaozEngine.Catalog/ContentChunk.cs`
- Create: `KhaozEngine.Catalog/ContentChunkCodec.cs`
- Create: `KhaozEngine.Catalog/ContentChunkAssembler.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/ContentChunkCodecTests.cs`

**Interfaces:**

- Consumes: Tasks 2 and 3
- Produces: encode and decode of one chunk file, plus the canonical bytes the chunk hash is taken over

**Lift from the spike.** `KhaozEngine.Benchmarks/Catalog/ContentChunkCodec.cs` and `ChunkAssembler.cs`
already carry this layout and were what produced the measured P1, P2 and P6 numbers. Copy, rename
`ChunkAssembler` to `ContentChunkAssembler`, and add the two header-level refusals below if the spike's
copy does not carry them.

- [ ] **Step 1: Write failing tests for the header, the body and every refusal.**

The 36-byte header is offsets 0 magic, 4 `formatVersion` u16, 6 `typeId` u16, 8 `chunkIndex` u32, 12
`slotBase` u32, 16 `slotCount` u32, 20 `rowCount` u32, 24 `visibility` byte, 25 `compression` byte, 26
`reserved` u16, 28 `uncompressedBytes` u32, 32 `storedBytes` u32, then the body. The header is NEVER
compressed.

Refusal facts, one test each, asserting the exact token: a bad magic, `chunk-format-version` on a version
other than 1, `chunk-reserved-set` on a non-zero `reserved`, `chunk-range-mismatch` when
`slotBase != chunkIndex * slotCount` or when `slotCount` disagrees with the registry, `chunk-too-large`
when the declared `uncompressedBytes` exceeds `ContentPackFormat.MaxChunkUncompressedBytes`,
`chunk-stored-length` when `storedBytes` differs from the body length received, `chunk-row-duplicate` on a
repeated definition id, `chunk-row-order` on a non-ascending id, and `chunk-row-flags` on a row flags byte
with any of bits 1 to 7 set.

**The two size refusals are taken from the 36 header bytes BEFORE any allocation and before the compressor
is touched.** The chunk hash is over the UNCOMPRESSED bytes, so a reader cannot verify a stored file
without decompressing first, which makes the integrity check late and the resource check early. Test that
a header declaring `uncompressedBytes = uint.MaxValue` is refused without allocating: assert with
`GC.GetAllocatedBytesForCurrentThread` that the refusal path allocates under a small bound.

- [ ] **Step 2: Write the canonical-bytes test.**

~~~
canonical = header(36 bytes, with compression = 0 and storedBytes = uncompressedBytes)
          || uncompressedBody
chunkHash = lowerHex(SHA256( utf8("kec/chunk/1\n") || canonical ))
~~~

Assert the canonical form is independent of the compressor: compress the same body at two Brotli qualities,
assert two different STORED files, and assert one identical chunk hash. That property is what makes a
compressor change a no-op for every cached client (spec 6.7).

Pin spec 7.9's worked example byte for byte: a `tag` chunk, type 1, index 0, 4,096 slots, two rows, tag 1
`metal` sort 10 live and tag 2 `two_handed` sort 20 retired, canonical bytes 44 in total.

**SPEC CONTRADICTION, resolved here and reported.** Spec 7.9's worked example gives the `tag` row body as
ONE byte, the `sort` varint alone, with no key. Spec 9.1 says "every row body already opens with its own
key, length prefixed UTF-8", which is what makes the runtime hold no `Keys` array, and spec 14.1 says the
124-byte item row figure "INCLUDES the row's own key, because the key is the first thing the encoder
writes". 7.9 is the stale side: the whole memory argument of 9.1 and 9.2 collapses without the key in the
body. **Implement the key in the body** and write the corrected 7.9 example into the golden and into
`ContentChunkCodec`'s doc comment, showing the row body as `[keyLen varint][key UTF-8][sort varint]`, so
`metal` is `05 6D 65 74 61 6C 0A`, seven bytes, and the canonical chunk is longer than 44 accordingly. Do
NOT edit the design document. Report the discrepancy to the orchestrator with the corrected arithmetic.

- [ ] **Step 3: Implement the codec and the assembler.**

The body is a row table of `rowCount` entries, each `[definitionId varint][flags byte][rowLength varint]`,
strictly ascending by id, followed by the row bodies concatenated in the SAME order. A row's offset is not
stored: it is the running sum of the preceding lengths, computed in one pass. `flags` bit 0 is the retired
bit, so a reader answers `IsRetired(id)` from the table with no row decode.

Compression is Brotli at quality 5, window 22, with a `compression` byte so the choice is per chunk. **A
chunk whose compressed body is not SMALLER than its uncompressed body is stored uncompressed.** The
decompression is BOUNDED: allocate exactly the declared length and refuse with `chunk-too-large` on the
first byte that would overrun it, because a Brotli stream can expand past whatever its container claims.

- [ ] **Step 4: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~ContentChunkCodecTests
git add KhaozEngine.Catalog/ContentChunk.cs KhaozEngine.Catalog/ContentChunkCodec.cs KhaozEngine.Catalog/ContentChunkAssembler.cs KhaozEngine.Catalog.Tests/Format/ContentChunkCodecTests.cs
git commit -m "catalog(kecc): add the chunk file codec and its canonical bytes"
~~~

---

### Task 6: The `KECM` manifest file and its canonical text (medium)

Spec 6.8, 7.4, 7.8. Contracts 7.3, 11.3.

**Files:**

- Create: `KhaozEngine.Catalog/ContentManifest.cs`
- Create: `KhaozEngine.Catalog/ContentManifestCodec.cs`
- Create: `KhaozEngine.Catalog/ContentManifestText.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/ContentManifestCodecTests.cs`

**Interfaces:**

- Consumes: Tasks 2, 3 and 5
- Produces: the manifest record, its file codec, and the canonical TEXT the two manifest digests are taken
  over

**Lift from the spike.** `KhaozEngine.Benchmarks/Catalog/ContentManifest.cs` and `ContentManifestCodec.cs`.
Split the canonical text out into its own file, because the FILE layout and the DIGEST text are two
different things that spec 7.4 deliberately keeps apart and a single file would fuse them.

- [ ] **Step 1: Write failing tests.**

The file layout is offsets 0 magic `KECM`, 4 `formatVersion` u16, 6 `side` byte, 7 `reserved` byte, 8
`versionNumber` u32, 12 `formatGeneration` u32, 16 `minimumServerBuild` u32, 20 `minimumClientBuild` u32,
24 `remapRuleChunkHash` 32 RAW bytes, 56 `typeCount` varint, then per type ASCENDING BY TYPE ID
`[typeId u16][typeKeyLen byte][typeKey UTF-8][chunkSlots u32][visibility byte][chunkCount varint]` and per
chunk ASCENDING BY INDEX `[chunkIndex varint][uncompressedBytes varint][chunkHash 32 raw bytes]`, then
`languageCount` varint and per language ASCENDING ORDINAL BY TAG `[tagLen byte][tag UTF-8][textHash 32 raw
bytes]`.

Facts: hashes are RAW 32 bytes in the file and lower hex only as text, a reader handed the wrong `side`
refuses with `manifest-wrong-side`, a `chunkSlots` disagreeing with the local registration refuses with
`manifest-chunk-slots`, types out of type-id order refuse, chunks out of index order refuse, and languages
out of ordinal tag order refuse.

- [ ] **Step 2: Write the canonical-text tests.**

The digest text, exactly as spec 6.8 gives it:

~~~
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
~~~

Every number goes through `CultureInfo.InvariantCulture`. Every string is length prefixed
`"{len}:{value} "` with a bare `"- "` for null. Assert that a type key containing a space cannot make two
different manifests digest the same, which is what the length prefix exists for.

**`uncompressedBytes` is in the FILE and NOT in the canonical text**, so the per-chunk size sits outside
the manifest hash by design (spec 7.4). Write a test that a chunk whose own header declares a different
`uncompressedBytes` than the manifest is refused with `chunk-range-mismatch`, because that refusal is the
only thing binding the un-digested field to reality.

Assert the server digest uses sub-domain `kec/manifest/server/` and the client digest
`kec/manifest/client/`, and that the two are never equal for one version.

- [ ] **Step 3: Implement, run green, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~ContentManifestCodecTests
git add KhaozEngine.Catalog/ContentManifest.cs KhaozEngine.Catalog/ContentManifestCodec.cs KhaozEngine.Catalog/ContentManifestText.cs KhaozEngine.Catalog.Tests/Format/ContentManifestCodecTests.cs
git commit -m "catalog(kecm): add the manifest file and its canonical digest text"
~~~

---

### Task 7: Remap rules and the `KECR` rule chunk (medium)

Contracts 8.1 to 8.6. Spec 7.7, 7.8.

**Files:**

- Create: `KhaozEngine.Catalog/RemapRule.cs` (holds `RemapRuleKind`)
- Create: `KhaozEngine.Catalog/RemapRuleCodec.cs`
- Create: `KhaozEngine.Catalog/RemapRuleSet.cs`
- Create: `KhaozEngine.Catalog/ContentRuleChunkCodec.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/RemapRuleCodecTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/RemapRuleSetTests.cs`

**Interfaces:**

- Consumes: Tasks 2 and 3
- Produces: `RemapRule`, `RemapRuleKind`, `RemapRuleSet`, the `KECR` chunk codec

**Lift from the spike's `ContentRuleChunkCodec.cs`**, which carries both the rule encoding and the chunk.
Split it: the rule's own encoding is `RemapRuleCodec`, the chunk is `ContentRuleChunkCodec`, and
`RemapRuleSet` is new.

- [ ] **Step 1: Write failing tests.**

The rule encoding is exactly contracts 8.4 and not one byte more:
`[Sequence varint][IntroducedIn varint][TypeId u16 LE][Kind byte][FromId varint][ToId varint]
[PayloadLength byte 0..64][Payload]`. Assert a typical rule is 9 to 12 bytes.

`RemapRuleKind` is `ReplacedBy = 1`, `Retired = 2`, `MovedToLegacy = 3`, `StackCapLowered = 4`. A `Retired`
payload's first byte is the POLICY, `0x01` placeholder or `0x02` replacement with bytes 1 to 4 an int32
destination. A `StackCapLowered` payload is an int32 new cap. There is deliberately NO delete policy.

Chunk facts: the `KECR` header is offsets 0 magic, 4 `formatVersion` u16, 6 `compression` byte, 7
`reserved` byte, 8 `ruleCount` u32, 12 `uncompressedBytes` u32, 16 `storedBytes` u32, then the body. A gap
in the sequence refuses with `rule-sequence-gap`, a non-ascending sequence with `rule-sequence-order`, and
an unknown `Kind` with `rule-kind`. **An unknown kind fails CLOSED and is never skipped**, which is
contracts 8.5's explicit rule and what `FormatGeneration` exists to announce in advance.

- [ ] **Step 2: Write the idempotence tests, which are the point of `RemapRuleSet`.**

Applying the whole ordered set TWICE to the same ids must produce the same ids as applying it once
(contracts 8.3). Two shapes are forbidden and both get a test: a rule whose `ToId` is an earlier rule's
`FromId` for the same type, and a rule whose effect depends on a value it also changes. Write the positive
too: a rule set that passes the check genuinely is idempotent over a generated corpus.

The negative must prove the check guards a REAL failure. Build a rule set where a rule's `ToId` is an
earlier rule's `FromId`, assert `IsIdempotent` is false, and assert that applying it twice genuinely
differs from applying it once. A guard nobody can see failing is a guard nobody trusts.

**PLAN CHOICE.** Spec 2.2 says `RemapRuleSet` carries "the full ordered list, its idempotence check and its
`Apply` pass" without naming a signature, and contracts 8.3 writes the pass against a durable PAGE, which
Scope B owns and this phase does not have. This plan ships the ID-LEVEL resolution and leaves the page walk
to Scope B:

~~~csharp
public sealed class RemapRuleSet
{
    public RemapRuleSet(IReadOnlyList<RemapRule> rulesInSequenceOrder);
    public IReadOnlyList<RemapRule> Rules { get; }
    public bool IsIdempotent(out RemapRule offending);
    public bool TryResolve(ContentTypeId type, int fromId, int pageStamp,
                           out int toId, out RemapRuleKind kind, out ReadOnlySpan<byte> payload);
    public int ActiveStamp { get; }
}
~~~

`TryResolve` applies every rule whose `IntroducedIn` is STRICTLY GREATER than `pageStamp`, in `Sequence`
order, in one pass, and answers the id the page should now carry. A rule is a no-op on an id it does not
name, which is the common case. Report this signature choice to the orchestrator so Scope B's spec author
sees it before building a page pass on a different shape.

- [ ] **Step 3: Implement, run green, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~RemapRule
git add KhaozEngine.Catalog/RemapRule.cs KhaozEngine.Catalog/RemapRuleCodec.cs KhaozEngine.Catalog/RemapRuleSet.cs KhaozEngine.Catalog/ContentRuleChunkCodec.cs KhaozEngine.Catalog.Tests/Format
git commit -m "catalog(kecr): add remap rules, the rule set and the rule chunk"
~~~

---

### Task 8: The `KECT` per-language text chunk (small)

Spec 7.6, 7.8. Contracts 12.1, 12.2, 12.4.

**Files:**

- Create: `KhaozEngine.Catalog/ContentTextChunk.cs`
- Create: `KhaozEngine.Catalog/ContentTextChunkCodec.cs`
- Create: `KhaozEngine.Catalog/ContentTextKey.cs`
- Create: `KhaozEngine.Catalog.Tests/Format/ContentTextChunkCodecTests.cs`

**Interfaces:**

- Consumes: Tasks 2 and 3
- Produces: the `KECT` codec plus the ONE derivation of a localized key

**Lift from the spike's `ContentTextChunkCodec.cs`.** The reader half (`ContentStringCatalog`) is Task 32,
not this task. **The format ships complete in phase 1 even though nothing reads it until milestone 1.5**,
because a manifest that gains a section later is a manifest hash that changes for every already-published
version (spec 18's rule).

- [ ] **Step 1: Write failing tests.**

Header: 0 magic `KECT`, 4 `formatVersion` u16, 6 `tagLen` byte, 7 `languageTag` UTF-8 at most 35 bytes,
then `compression` byte, `reserved` byte, `uncompressedBytes` u32, `storedBytes` u32, then the body. The
header is VARIABLE length, `17 + tagLen`. Body: `[entryCount varint]` then per entry ASCENDING ORDINAL BY
KEY `[keyLen byte 1..192][key UTF-8][valueLen varint 0..8192][value UTF-8]`.

Facts: entries out of ordinal key order refuse, a `keyLen` over 192 refuses, a `valueLen` over 8,192
refuses, a non-zero `reserved` is `chunk-reserved-set`, and the two header-level size refusals of Task 5
(`chunk-too-large`, `chunk-stored-length`) apply here identically and are taken before any allocation.

The canonical form includes the VARIABLE header WHOLE, language tag and all, so the same strings under two
language tags are two different chunks with two different hashes:

~~~
canonical = header(17 + tagLen bytes, with compression = 0 and storedBytes = uncompressedBytes)
          || uncompressedBody
textHash  = lowerHex(SHA256( utf8("kec/text/1\n") || canonical ))
~~~

- [ ] **Step 2: Implement `ContentTextKey`, the one derivation in the tree.**

~~~csharp
public static class ContentTextKey
{
    // <type key>.<content key>.<field>, contracts 12.1. Derived, never authored, never stored.
    public static string Derive(string typeKey, ReadOnlySpan<byte> contentKeyUtf8, string fieldName);
    public static bool ExceedsBound(string typeKey, int contentKeyLength, string fieldName); // 192, KEC0030
}
~~~

There is no authored override and no place to put one. A 64-character type key, a 64-character content key
and a 64-character field name derive 194 characters, past contracts 12.2's 192-character bound, so the
bound is REACHABLE and `KEC0030` in Task 11 checks it.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~ContentTextChunkCodecTests
git add KhaozEngine.Catalog/ContentTextChunk.cs KhaozEngine.Catalog/ContentTextChunkCodec.cs KhaozEngine.Catalog/ContentTextKey.cs KhaozEngine.Catalog.Tests/Format/ContentTextChunkCodecTests.cs
git commit -m "catalog(kect): add the per-language text chunk and the derived key"
~~~

---

### Task 9: `IContentSnapshot`, `ContentSnapshot` and `ItemRow` (medium)

Spec 2.2, 2.5, 9.1. Contracts 7.1.

**Files:**

- Modify: `KhaozEngine.Catalog/IContentSnapshot.cs` (the stub from Task 3 gains its seven members)
- Create: `KhaozEngine.Catalog/ContentSnapshot.cs`
- Create: `KhaozEngine.Catalog/ContentSnapshotBuilder.cs`
- Create: `KhaozEngine.Catalog/ContentVersionIdentity.cs`
- Create: `KhaozEngine.Catalog/ItemRow.cs`
- Create: `KhaozEngine.Catalog.Tests/Runtime/ContentSnapshotTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Runtime/ItemRowTests.cs`

**Interfaces:**

- Consumes: Tasks 2, 3, 4 and 7
- Produces: the read side everything outside this package compiles against

**This ships in milestone 1.1 deliberately, ahead of everything that consumes it**, because Scope B's phase
1 is designed behind it and the two phase 1s are meant to land in either order (spec 2.2).

- [ ] **Step 1: Write failing tests.**

Assert `IContentSnapshot` has EXACTLY seven members and no more, by reflection, so a later task cannot
widen the client's read surface by accident. Assert `ContentSnapshot` implements it, that `Rows(type)` is
ordered by id, that `TryGetId` is ordinal over the UTF-8 key, that `IsRetired` answers without decoding a
row, and that a snapshot built by the builder in two different type-registration orders is equal row for
row.

`ItemRowTests`: `TryGetItem` decodes `stackable`, `max_stack`, `durability_max` and `socket_max` from a
body span with no allocation (assert `GC.GetAllocatedBytesForCurrentThread` delta is 0 over a warm loop),
a retired item answers `IsRetired`, and an id with no row answers false.

- [ ] **Step 2: Implement the interface exactly.**

~~~csharp
public interface IContentSnapshot
{
    int VersionNumber { get; }
    ContentVersionIdentity Identity { get; }
    bool TryGetRow(ContentTypeId type, int id, out ContentRow row);
    bool TryGetId(ContentTypeId type, ContentKey key, out int id);
    IReadOnlyList<ContentRow> Rows(ContentTypeId type);
    IReadOnlyList<RemapRule> Rules { get; }
    bool IsRetired(ContentTypeId type, int id);
}

public readonly record struct ContentVersionIdentity(int Number, string ManifestHash);
~~~

Seven members and no more. It carries no authoring concept, no chunk, no hash beyond the identity pair and
no mutation, so a client holds one with the pure read graph.

- [ ] **Step 3: Implement `ContentSnapshot` and its builder.**

**PLAN CHOICE.** Spec 2.2 names `ContentSnapshot` and section 5.4 requires that "tests build a
`ContentSnapshot` in memory with no store, no file and no registry beyond the one they construct" without
naming a construction path. This plan adds `ContentSnapshotBuilder`, which takes a registry, accepts rows
per type, accepts the rule list and the version identity, and produces an immutable snapshot. It is the
only way to make a snapshot, so publish, boot and a test all build one the same way.

- [ ] **Step 4: Implement `ItemRow`, and only for `item`.**

~~~csharp
public readonly ref struct ItemRow
{
    public bool Stackable     { get; }
    public int  MaxStack      { get; }
    public int  DurabilityMax { get; }
    public int  SocketMax     { get; }
    public ContentKey Key     { get; }
    public bool IsRetired     { get; }
}
~~~

A `ref struct` over the body span, with the four hot fields decoded at construction. Spec 9.1 says "known
offsets" and the spike's `ItemRowView.cs` records that in a positional varint row the offsets are NOT
constant, so it walks the preceding fields with length skips instead: one pass, no branch on a name, no
allocation. Copy that approach and keep the spike's note in the doc comment, because the next reader will
otherwise try to hard-code an offset.

**Nothing else gets a typed view.** `ContentRow` stays the answer for every other type, because a typed
view over a schema the engine does not own could not be written.

- [ ] **Step 5: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests
git commit -m "catalog(snapshot): add the read seam, the snapshot builder and the item view"
~~~

---

### Task 10: `IPackStore`, `FileSystemPackStore` and `ContentPackReader` (medium)

Spec 8.1, 8.2, 6.9. Contracts 7.3.

**Files:**

- Create: `KhaozEngine.Catalog/IPackStore.cs` (holds `IPackStorePruning` and `PackDurability`)
- Create: `KhaozEngine.Catalog/FileSystemPackStore.cs`
- Create: `KhaozEngine.Catalog/ContentPackException.cs`
- Create: `KhaozEngine.Catalog/ContentPackReader.cs`
- Create: `KhaozEngine.Catalog.Tests/Store/FileSystemPackStoreTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Store/ContentPackReaderTests.cs`

**Interfaces:**

- Consumes: Tasks 2, 5, 6, 7, 8 and 9
- Produces: the content-addressed store seam, its local provider, and the reader that assembles a snapshot

**Lift the shard layout from the spike's `FileSystemPackStore.cs`, and change the shape.** The spike's copy
is SYNCHRONOUS because the measurement wanted the file system cost without a task per chunk. The shipped
store is async over `IPackStore`, so the lift is the layout and the temp-then-move idiom, not the
signatures.

- [ ] **Step 1: Write failing tests.**

Store facts: `GetAsync` returns null for absent rather than throwing, `PutAsync` VERIFIES the digest of the
bytes it was handed and throws `ContentPackException` on a mismatch, `PutAsync` of a hash that exists is a
no-op, a file lands at `<root>/<hash[0..2]>/<hash[2..4]>/<hash>.kec` through a `.tmp` then
`File.Move(temp, final, overwrite: true)`, the version pointer lands at `<root>/versions/<n>` OUTSIDE the
shard tree, and `ListAsync(version)` reads the pointer, then the two manifests it names, and yields every
hash they name plus the two manifest hashes themselves. `ListAsync` does NOT walk the directory: a store
answers what a version contains, not what happens to be on disk. A store whose pointer is absent or
unreadable yields an empty sequence, which is the sweep's skip condition in Task 16.

There is no `DeleteAsync` on `IPackStore`. Pruning is a separate `IPackStorePruning` a provider MAY
implement, so a read-only provider cannot be asked to prune and a misconfigured one cannot delete a
production pack through the common interface.

An `fsync` before the move happens only when the store is constructed with `PackDurability.PowerFail`. The
default is the cheaper mode, because a pack file lost to a power cut is refetchable from its hash.

- [ ] **Step 2: Write the reader tests.**

`ContentPackReader` verifies, decompresses and decodes ONE chunk, and assembles a snapshot lazily. Facts: a
chunk whose bytes do not hash to the name it was fetched under is refused and never used, a chunk that
decodes cleanly produces rows the snapshot returns in ascending id order, and assembling a snapshot from a
manifest touches only the chunks a lookup asks for.

- [ ] **Step 3: Implement, run green, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests
git commit -m "catalog(store): add the pack store seam, the file provider and the pack reader"
~~~

---

### Task 11: The one validator and its forty-one findings (large)

Spec 5.1 to 5.5, 2.7. Contracts 10.4.

**Files:**

- Create: `KhaozEngine.Catalog/Validation/ContentValidator.cs` (the sweep and the finding list only)
- Create: `KhaozEngine.Catalog/Validation/ContentFinding.cs` (holds `ContentValidationReport`)
- Create: `KhaozEngine.Catalog/Validation/ContentKeyChecks.cs`
- Create: `KhaozEngine.Catalog/Validation/ContentSchemaChecks.cs`
- Create: `KhaozEngine.Catalog/Validation/ContentReferenceChecks.cs`
- Create: `KhaozEngine.Catalog/Validation/ContentVisibilityChecks.cs`
- Create: `KhaozEngine.Catalog/Validation/ContentRemapChecks.cs`
- Create: `KhaozEngine.Catalog.Tests/Validation/ContentValidatorSweepTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Validation/ContentFindingCodeTests.cs`

**Interfaces:**

- Consumes: Tasks 3, 4, 7 and 9
- Produces: `ContentValidator.Validate`, shared by publish, boot and tests

**Write fresh.** The spike's `ContentValidator.cs` is a cut-down measurement copy carrying some of the
codes. The check families below are most of the work and the split by family is what keeps every file
under 800 lines: `ContentValidator.cs` holds the sweep and nothing else, so adding a check never grows it
(spec 2.7).

- [ ] **Step 1: Write one test per ISSUED code, forty-one of them, plus `KEC0000`.**

Each test builds the SMALLEST `ContentSnapshot` that triggers exactly that code, through
`ContentSnapshotBuilder` with no store and no file, and asserts the code, the type and the id. The codes
run `KEC0001` to `KEC0042`, `KEC0013` is WITHDRAWN and carries no check and is never reissued, and
`KEC0032` to `KEC0035` are the four inheritance codes. Spec 5.2 is the complete table and is the
authority for each code's meaning.

Two groups are not ordinary sweep tests. **The four inheritance codes are unreachable in phase 1**, so
their tests assert they do NOT fire on a candidate whose `parent_id` is 0 throughout, which is what proves
the row model allows inheritance without a resolver. **`KEC0039` is in NO pass** and is emitted by
`RollbackToAsync` in Task 16, so its test lives there.

`KEC0000` gets its own test and it is the one that asserts a finding does NOT set `IsValid` to false. It is
informational, it fires when `previous` is null, and its message names the publish-only checks that did not
run, so a clean report from a boot is never mistaken for a clean report from a publish.

Three sweep-level facts on top: findings ACCUMULATE rather than stopping at the first, the validator never
throws for content reasons, and a game validator that throws becomes ONE `KEC0040` carrying the exception
message rather than an escaping exception.

- [ ] **Step 2: Implement the signature exactly as spec 5.1 gives it.**

~~~csharp
public static ContentValidationReport Validate(
    ContentSnapshot candidate,
    ContentSnapshot? previous,
    IReadOnlyList<RemapRule> rules,
    ContentTypeRegistry registry);

public readonly record struct ContentFinding(ContentTypeId Type, int Id, string Code, string Message);
public sealed record ContentValidationReport(bool IsValid, IReadOnlyList<ContentFinding> Findings);
~~~

**SPEC NOTE.** Contracts 10.4 writes this as a three-argument member with no registry. Spec 5.1 REFINES it
with a fourth argument and a static form, which is a refinement rather than a contradiction because the
registry is an argument and not an ambient read, so purity holds. Use the four-argument form.

`previous` is NULL at boot and in almost every test, and is the base version's snapshot at publish. **The
three publish-only checks are a property of the ARGUMENT and never a mode flag**: `KEC0003` (a key changed
on a published row), `KEC0029` (a type id, type key or `chunk_slots` changed after its first publish) and
Scope B's tier-ordinal check. When `previous` is null they are skipped and `KEC0000` names them.

- [ ] **Step 3: Implement the five passes, in this order, never stopping early.**

1. **Structure.** `KEC0001` to `KEC0003`, `KEC0009` to `KEC0012`, `KEC0028`, `KEC0029`, `KEC0036`, `KEC0037`.
2. **Schema.** `KEC0004`, `KEC0005`, `KEC0020`, `KEC0021`, `KEC0025`, `KEC0030` to `KEC0035`.
3. **References.** `KEC0006` to `KEC0008`, `KEC0023`, `KEC0024`.
4. **Visibility and codec.** `KEC0014`, `KEC0022`, `KEC0026`, `KEC0027`, `KEC0038`.
5. **Remap rules.** `KEC0015` to `KEC0019`.

Pass 3 needs pass 1 to have built the live-row index and pass 5 needs pass 1 to know which ids ever
existed. Nothing else is ordered. The passes are single threaded.

**Pass 6 is Scope B's band, `KEC0100` to `KEC0199`, and it runs INSIDE the sweep** after pass 5 and BEFORE
any game validator. In phase 1 no Scope B type is registered, so leave the hook and skip it entirely when
the 256 to 1023 band is empty. Do not put Scope B in the game slot: an operator reading `KEC0040` could not
then tell an engine affix rule from a game rule.

Game validators run LAST, one per registered type, each handed only its own type's rows plus a read-only
lookup into the rest of the candidate. Their findings come back as `KEC0040` prefixed with the game's type
key. A game validator that throws is CAUGHT and reported as `KEC0040`, because game validators are
untrusted code and one bad one must not take a publish down with a stack trace instead of a finding.

**`KEC0027` is the check most likely to be skipped and the one that pays.** Encode every row and decode it
back, asserting byte identity. It is the only thing that can catch a codec whose encode and decode disagree
before the bytes are hashed into a manifest an operator then treats as an identity.

- [ ] **Step 4: Write down what the validator deliberately does NOT check**, in the type's doc comment, so
nobody adds one later without a decision: whether a value is sensible, whether a client has the art,
whether a localization key resolves, and whether a remap rule is a good idea (spec 5.5).

- [ ] **Step 5: Run green, check file sizes, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
sh scripts/check-file-size.sh --tree
git add KhaozEngine.Catalog/Validation KhaozEngine.Catalog.Tests/Validation
git commit -m "catalog(validate): add the one validator and its forty-one findings"
~~~

---

### Task 12: Goldens, decoder fuzzing and cross-version round trips (large, GATE for 1.1)

Spec 15.1, 15.2, 15.3, 2.5. Contracts 9.7.

**Files:**

- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/chunk-tag-0.kecc`
- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/chunk-item-0.kecc`
- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/manifest-server.kecm`
- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/manifest-client.kecm`
- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/rules.kecr`
- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/text-en-us.kect`
- Create: `KhaozEngine.Catalog.Tests/Goldens/v1/goldens.json`
- Create: `KhaozEngine.Catalog.Tests/Goldens/GoldenFormatTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Goldens/GoldenFormatVersionsTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Fuzz/DecoderFuzzTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Fuzz/MutationSource.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs`
- Modify: `KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj` (embed or copy the goldens)

**Interfaces:**

- Consumes: Tasks 2 to 11
- Produces: the milestone 1.1 gate

**A golden is added to and NEVER edited.** If a golden needs different bytes, the format changed, which
means a new version directory and a new set. Put that sentence in `GoldenFormatTests`'s doc comment.

- [ ] **Step 1: Generate the six goldens through the shipped encoders and check them in.**

`chunk-tag-0.kecc` is spec 7.9's two-row chunk, stored UNCOMPRESSED, with the key in the row body per Task
5's resolution. `chunk-item-0.kecc` is a three-row item chunk exercising every value kind including an
empty tag list, an absent optional field and a retired row, stored BROTLI COMPRESSED.
`manifest-server.kecm` is two types, four chunks and two languages. `manifest-client.kecm` is the same
version with the `ServerOnly` type omitted. `rules.kecr` is six rules covering all four kinds of contracts
8.2, including a 5-byte replacement payload and a zero-length payload. `text-en-us.kect` is twelve entries
including an empty value and a 192-character key.

`goldens.json` records, per file, the expected hash, the uncompressed length and the decoded field values.

- [ ] **Step 2: Write the three assertions per golden.**

**Decode**: the reader produces exactly the values in `goldens.json`. **Hash**: `ContentHash` over the
canonical bytes equals the recorded hash, which pins the digest domain, the scheme version and the
canonical form at once. **Re-encode**: encoding the decoded values reproduces the UNCOMPRESSED canonical
bytes byte for byte.

**The compressed bytes are deliberately NOT pinned.** `chunk-item-0.kecc` is checked in compressed so the
decompression path has a golden, and the test asserts on the DECOMPRESSED result, so a .NET upgrade that
changes Brotli's output does not turn into a red test with no defect behind it.

- [ ] **Step 3: Write the fuzzer.**

Seeded with `SeededRandomSource` from Task 1, so a failure reproduces from the seed printed in the
assertion message. Mutations: flip a random bit, truncate at a random offset, zero a random run, splice two
goldens, set a random varint byte's continuation bit, and set a reserved field non-zero. 10,000 mutants per
golden per run in CI, which is a few seconds, plus a `--soak` opt-in for more.

Three invariants. **It never throws**, every decode entry point returns false plus a reason. **Reasons are
stable**, every rejection carries a token from the fixed list below, asserted as membership rather than as
a specific token per mutant, because a bit flip can legitimately turn one failure into another. **A mutant
that DECODES must round trip**, re-encoding it reproducing the mutated bytes, which is what catches a
decoder that silently normalizes away a difference.

The fixed decode list, and a decode entry point may return only these:

~~~
chunk-format-version   chunk-reserved-set     chunk-range-mismatch   chunk-too-large
chunk-stored-length    chunk-row-duplicate    chunk-row-order        chunk-row-flags
manifest-wrong-side    manifest-chunk-slots   rule-kind              rule-sequence-gap
rule-sequence-order
~~~

`hash-mismatch`, `manifest-hash-mismatch` and `chunk-fetch-failed` are FETCH outcomes and the fuzzer never
produces them. The boot refusals of spec 9.6 are a third set and are not decode reasons at all.

- [ ] **Step 4: Write the cross-version round trips.**

`GoldenFormatVersionsTests` ENUMERATES the `Goldens/` directory and asserts every version subdirectory
present is readable by the current reader. Adding a format version means adding a directory, and forgetting
to keep reading the old one goes red immediately.

**PLAN CHOICE.** Spec 15.3's "a v2 reader handed a v1 chunk reads it" cannot be written in phase 1, because
v2 does not exist. What CAN be written, and is required here, is the other direction: hand the current
reader a hand-built chunk whose `formatVersion` is 2 and assert it refuses with `chunk-format-version`
rather than making a best-effort partial read. Do the same for `KECM`, `KECR` and `KECT`, and for a
manifest whose `formatGeneration` exceeds `ContentPackFormat.Generation`. The forward direction becomes
real the day `ChunkFormatVersion` becomes 2, and `GoldenFormatVersionsTests` is what makes it automatic.

- [ ] **Step 5: Add the architecture test that keeps the client graph clean.**

In `KhaozEngine.Tests/ArchitectureTests.cs`, in the same shape as the existing JsonSchema.Net containment
test at line 121: assert `KhaozEngine.Catalog` never references `KhaozEngine.Catalog.Authoring`, and that
`KhaozEngine.Catalog`'s package references are empty. That is what keeps the authoring types out of a
client graph, checked mechanically rather than by review.

- [ ] **Step 6: Run the milestone 1.1 gate green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter FullyQualifiedName~ArchitectureTests
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
git add KhaozEngine.Catalog.Tests KhaozEngine.Tests/ArchitectureTests.cs
git commit -m "catalog(goldens): pin the four formats, fuzz the decoders, gate on format versions"
~~~

Expected: every command exits zero. **This closes milestone 1.1.**

---

## Milestone 1.2: `Catalog.Authoring` and the two SQL providers

**Group acceptance (spec 18.1's gate for 1.2):** the twenty-three-fact provider conformance suite of spec
15.5 passes on SQLite and, under `KE_CATALOG_SQLSERVER`, on SQL Server, and the nine crash-safety cases of
spec 15.6 pass in process with the out-of-process probe available. Tasks 13 to 19.

### Task 13: The `KhaozEngine.Catalog.Authoring` package and its seam (medium)

Spec 2.1, 2.3, 3.7, 4.1, 4.2.

**Files:**

- Create: `KhaozEngine.Catalog.Authoring/KhaozEngine.Catalog.Authoring.csproj`
- Create: `KhaozEngine.Catalog.Authoring/README.md`
- Create: `KhaozEngine.Catalog.Authoring/IContentAuthoringStore.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentAuthoringSchemaMode.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentDraft.cs` (holds `ContentEdit`, `ContentEditOperation`)
- Create: `KhaozEngine.Catalog.Authoring/ContentChangeSet.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentAuditEntry.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentVersionRecord.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentFamily.cs` (holds `ContentFamilyBlock`)
- Create: `KhaozEngine.Catalog.Authoring/ContentAuthoringException.cs`
- Create: `KhaozEngine.Catalog.Tests/Authoring/AuthoringValueTests.cs`
- Modify: `KhaozEngine.slnx`, `README.md`, `KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj`
