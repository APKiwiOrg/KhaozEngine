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

**Interfaces:**

- Consumes: `KhaozEngine.Catalog`
- Produces: the provider seam every backend implements, plus the authoring value types

**The package is PURE .NET with no SQL.** Its only `ProjectReference` is `KhaozEngine.Catalog`. Ship its
README and its root catalog row in this commit, for the guard reason in the Global Constraints.

- [ ] **Step 1: Write failing tests for the value types.**

`ContentEditOperation` is `Add = 1`, `Update = 2`, `Retire = 3`, `Fork = 4`, matching the
`CHECK (operation IN (1, 2, 3, 4))` the DDL carries in Task 17, so write the numbers explicitly. Assert a
`ContentChangeSet` is ordered and deduplicated per TARGET, that a second edit naming an occupied target
collides even when its operation differs, and that an edit stores the CHANGED FIELDS ONLY rather than the
whole row.

**`Fork` carries five things and is atomic**: the source id, the copy's new key, the `Bool` flag field to
set on the copy, the changed fields for the ORIGINAL, and no id for the copy (ids are allocated at
publish). Assert its value type refuses construction with an empty `forkKey`. `Fork` exists because remap
rule kind 3 had no producer, and it is one operation rather than three because an author who did the three
separately could have the publish succeed with the rule missing, which is the state nothing can detect
afterwards.

- [ ] **Step 2: Implement `IContentAuthoringStore`.**

The members the rest of this milestone and milestone 1.4 call, named here so a provider implements one
shape: `InitializeAsync(ContentAuthoringSchemaMode)`, `GetSchemaVersionAsync`, `GetStoreEpochAsync`,
`GetActiveVersionAsync`, `GetPinnedVersionAsync`, `SetPinnedVersionAsync`, `ListVersionsAsync`,
`GetVersionAsync(int)`, `LoadSnapshotAsync(int version, ContentTypeRegistry)`, `GetOpenDraftAsync`,
`ApplyEditsAsync(IReadOnlyList<ContentEdit>, string actor, string operatorId, string note)`,
`DiscardDraftAsync`, `PublishAsync(ContentPublishRequest)`, `RollbackToAsync(int target)`,
`ListRowsAsync`, `GetRowHistoryAsync`, `ListAuditAsync`, `AllocateAsync(ContentTypeId, int count)`,
`AllocateInFamilyAsync(long familyId)`, `CreateFamilyAsync`, `ImportBundleAsync`, `ExportBundleAsync`.

`ContentAuthoringSchemaMode` is `AutoCreate` or `ValidateOnly`, the journal's enum shape
(`KhaozEngine.WorldStore.Sqlite/SqliteJournalSchema.cs:9-13`). `ValidateOnly` refuses an empty or
mismatched database rather than creating anything, which is what a production host sets so a typo in a
connection string cannot silently create a second empty catalog.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
sh scripts/check-doc-versions.sh
git add KhaozEngine.Catalog.Authoring KhaozEngine.Catalog.Tests KhaozEngine.slnx README.md
git commit -m "catalog(authoring): add the package, the store seam and the edit vocabulary"
~~~

---

### Task 14: The id allocator and the in-memory store (medium)

Spec 3.8, 4.7, 4.9. Contracts 5.1, 5.2, 6.2.

**Files:**

- Create: `KhaozEngine.Catalog.Authoring/ContentIdAllocator.cs`
- Create: `KhaozEngine.Catalog.Authoring/InMemoryContentAuthoringStore.cs`
- Create: `KhaozEngine.Catalog.Tests/Authoring/ContentIdAllocatorTests.cs`

**Interfaces:**

- Consumes: Task 13
- Produces: the reserve-then-issue allocator and a store every later authoring test builds on

- [ ] **Step 1: Write failing allocator tests.**

`AllocateAsync(typeId, count)`: when `issued_through + count <= reserved_through`, hand out
`[issued_through + 1, issued_through + count]` and advance `issued_through`. Otherwise compute
`newReserved = issued_through + max(count, 1024)`, write `reserved_through = newReserved` and **COMMIT
THAT WRITE ON ITS OWN**, then issue. Assert the ORDER by reading `reserved_through` after a single
allocate and finding it already above the issued id.

Assert: two allocations never return the same id across 10,000 in a loop, an allocation above a declared
`maxDefinitionId` throws `ContentAuthoringException` naming the type, the ceiling and the high-water mark,
and RETIRED rows count toward the ceiling because ids are never reused.

`AllocateInFamilyAsync(familyId)`: takes the family's blocks in ordinal order, finds the first whose
`next_free_id < base_id + block_size`, issues and advances. When every block is full it reserves a NEW
block: take the type's `reserved_through`, round UP to the family's declared `block_size` alignment,
reserve through the top of the new block, **advance `issued_through` to that same block top**, insert the
block row, and only then issue.

**Write the regression test for the advance, because it is the bug the spec spends a page on.** A fresh
`item` type allocates 10 plain ids and sits at `issued_through = 10, reserved_through = 1024`. A `sword`
family with `block_size = 16` takes the block `[1024, 1040)` and issues 1024. Without the advance, plain
adds climb to 1023 and the next one hands out 1024 a SECOND time. The fact is spec 15.5's number 23: a
plain allocation taken after a family block is reserved never returns an id inside that block, asserted by
draining the whole gap under the block and then some.

- [ ] **Step 2: Implement the allocator and the in-memory store.**

**PLAN CHOICE.** Spec 2.6 requires that draft, change set, publish, diff, allocator and bundle tests run
"against an in-memory store" and never names the type. This plan adds
`InMemoryContentAuthoringStore`, a full `IContentAuthoringStore` implementation in
`KhaozEngine.Catalog.Authoring` (not in the test project), because milestone 1.4's action tests need one
too and a test-project copy could not be reached from `KhaozEngine.Server.Tests`. It is documented as a
TEST AND TOOLING store and its doc comment says a production host uses a provider.

A crash between the two commits skips up to 1,024 ids, which is free: ids are 31 bits of positive `int`
space per type against an owner figure of 50,000 definitions.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~Authoring
git add KhaozEngine.Catalog.Authoring KhaozEngine.Catalog.Tests/Authoring
git commit -m "catalog(ids): add the reserve-then-issue allocator and the in-memory store"
~~~

---

### Task 15: The publish pipeline, steps 1 to 8 (large)

Spec 6.1 to 6.8, 2.7. Contracts 4.3, 11.3.

**Files:**

- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentPublisher.cs` (the ordered steps only)
- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentPublishRequest.cs` (holds `ContentPublishResult`)
- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentIdAllocation.cs`
- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentChunkBuilder.cs`
- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentManifestBuilder.cs`
- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentPublishStep.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/PublishPipelineTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/ChunkReuseTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/VisibilityTests.cs`

**Interfaces:**

- Consumes: Tasks 5 to 11, 13 and 14
- Produces: everything a publish does before it writes a durable row

**Lift the chunk selection and encode loop from the spike's `CatalogPublisher.cs`**, which is the code that
produced the measured P5 and P6 numbers. It is cut down to what the budgets needed and has no database, so
the temporal rows, the audit and the commit are fresh work in Task 16.

- [ ] **Step 1: Write failing tests for the step order and the id sources.**

**Step 3 allocates ids BEFORE step 4 validates**, because `KEC0006` and `KEC0010` need the ids the new rows
will carry. There is ONE path with two id sources and which runs is a property of the EDIT: an `Add` with
`definition_id = 0` is allocated one, an `Add` with a non-zero `definition_id` KEEPS it, and only
`catalog-import` into an empty database writes the second kind. After every `Add` has an id, the high-water
marks are SEEDED from the largest carried id per type, so the first ordinary `Add` after an import does not
allocate id 1 onto an imported row. Assert the seeding is a no-op for an ordinary publish.

A `Fork` allocates through the plain branch, never carries an id, and never names a family: its copy
inherits the SOURCE row's family so `KEC0037` stays quiet.

Assert that two publishes of the same id-free bundle into two empty databases produce the SAME ids, because
allocation follows edit ordinal order.

- [ ] **Step 2: Write failing tests for chunk selection and reuse.**

~~~
affected = { (type, id / chunkSlots[type])
             for every row whose valid_from_version = V
             or whose replaced_in_version = V }
~~~

Both halves matter: a row that ENTERED at V changes its chunk, and a row CLOSED at V also changes its chunk
because it leaves the live set. Publish, edit one row, publish again, assert EXACTLY ONE chunk hash changed
and every other is identical to the previous version's. Then edit a row in a different chunk and assert two
changed. Assert that adding a content TYPE rewrites no existing chunk and DOES change the manifest hash.

**The carry-forward is PER SIDE.** For an unaffected chunk the publisher copies forward every chunk row the
previous version holds for that `(typeId, chunkIndex)`, one row for a single-sided chunk and two when the
type is `Client` with a per-field `ServerOnly` override. Nothing recomputes a side from the type's default
visibility, because the schema may have gained a `ServerOnly` field at THIS version.

- [ ] **Step 3: Write failing visibility tests.**

Publish a `Client` type with one `ServerOnly` field. Assert the client chunk decodes WITHOUT that field,
that its hash differs from the server chunk's, that BOTH hashes exist under the one
`(version, type, chunk index)` at two sides, that the next publish of an unrelated chunk carries both
forward, that the client manifest omits every `ServerOnly` TYPE, and that a stub encoder leaving the field
in the client-side bytes is refused by `KEC0014`.

- [ ] **Step 4: Write the manifest stability test, which is contracts 4.3's direct test.**

Register the same five types in a SHUFFLED order, publish the same content, and assert the manifest hashes
are byte identical. Ten shuffles from a seeded source. This is the test Ruinborne's wire index would have
failed, since the same item has a different byte index depending on whether the catalog loaded from SQL or
from code defaults.

- [ ] **Step 5: Implement steps 1 to 8, each delegating to its named type.**

Step 1 freezes the draft and takes a row lock. **The new version number is read INSIDE the commit
transaction at step 10, not at freeze time**, because reading it here and using it there is exactly the
race the lock is meant to close. Step 2 builds the candidate by applying the draft's edits to the base
version. Step 3 allocates. Step 4 validates with `previous` non-null, the ONLY place it is. Step 5 computes
the temporal rows. Step 6 selects affected chunks. Step 7 encodes each affected chunk with its live rows
SORTED ASCENDING BY ID, hashes over the UNCOMPRESSED canonical bytes, then compresses. Step 8 builds both
manifests.

`MinimumServerBuild` and `MinimumClientBuild` are CONSUMER-SUPPLIED on the request and the engine never
interprets them beyond comparing. Omitted, they carry FORWARD the previous version's values rather than
resetting to 0. `FormatGeneration` is `ContentPackFormat.Generation`, read from the engine and never
supplied by a caller. All three are INPUTS to the manifest hash, not stamps beside it.

`ContentPublishStep` is the internal enum the crash tests in Task 19 hook, copied from `MapTiledSaveStep`
(`KhaozEngine.MapDoc/MapDocumentForm.cs:58-74`): `BeforeIdAllocation`, `AfterIdAllocation`,
`BeforeChunkWrite`, `AfterChunkWrite`, `BeforeManifestWrite`, `AfterManifestWrite`, `BeforeCommit`,
`AfterCommit`, `DuringSweep`. Add the `OnStep` hook to the publisher now so Task 19 has something to throw
from.

- [ ] **Step 6: Run green, check file sizes, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
sh scripts/check-file-size.sh --tree
git add KhaozEngine.Catalog.Authoring/Publish KhaozEngine.Catalog.Tests/Publish
git commit -m "catalog(publish): add steps one to eight, chunk selection and both manifests"
~~~

---

### Task 16: The commit, the sweep, rollback, diff and the bundle (large)

Spec 6.9 to 6.13, 4.6, 4.8, 10.9. Contracts 8.6.

**Files:**

- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentPublishCommit.cs`
- Create: `KhaozEngine.Catalog.Authoring/Publish/ContentPackSweep.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentDiff.cs` (holds `ContentDiffEntry`)
- Create: `KhaozEngine.Catalog.Authoring/ContentBundle.cs`
- Create: `KhaozEngine.Catalog.Authoring/ContentRollback.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/PublishCommitTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/RollbackTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/ForkTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Authoring/BundleTests.cs`

**Interfaces:**

- Consumes: Tasks 13, 14 and 15
- Produces: the one commit point, the orphan sweep, the field-level diff, the bundle and rollback

- [ ] **Step 1: Write failing commit tests.**

Step 9 writes every chunk file and both manifest files to the pack store BEFORE the database commit, at
content-addressed names nothing references yet, plus the version POINTER at `versions/<n>`. A chunk whose
hash already exists is NOT rewritten, checked with `ExistsAsync` first, which is what makes a republish of
an unchanged chunk free.

Step 10 is ONE transaction, in this order inside it: read `MAX(version_number) + 1`, insert the
`catalog_version` row, apply every temporal row change, append every remap rule at `MAX(sequence) + 1`
upward, insert every chunk row ONE PER SIDE including the carried-forward ones, insert every audit row,
delete the draft and its edits, then `UPDATE catalog_metadata SET active_version`. Assert a reader sees
`active_version = N` only together with every row, rule, chunk and audit entry of version N. Assert the
active pointer moves at publish while a RUNNING server keeps serving what it loaded.

Step 11 sweeps only after a SUCCESSFUL commit. **The keep set is the union, over EVERY version the store
knows, of that version's pointer, the two manifest hashes it holds, and every hash named INSIDE either
manifest.** It is defined against the MANIFESTS and not against the chunk table, because the rule chunk and
the text chunks have no type row to hang a chunk row on and a keep set read from the chunk table would
delete both at the first publish. Write that as its own test. **The sweep is SKIPPED when the store listing
fails for any reason**, and an absent or unreadable pointer for any version IS a listing failure.

- [ ] **Step 2: Write the fork test, which is the one whose failure cannot be repaired afterwards.**

Publish a row, fork it, and assert all five properties in ONE test: the copy carries a new id and the
source row's WHOLE field set, the copy's flag field is true, the ORIGINAL id is live with the edit's new
values and NO flag, exactly one kind 3 rule was appended naming both ids in that direction, and a page
stamped before the fork resolves onto the copy while a page stamped after it does not. Plus the four
`KEC0041` negatives: an absent source row, an already-retired source, a taken or malformed `forkKey`, and
a `flagField` that is absent from the schema or is not `Bool`.

The ORDER inside a fork matters: the copy is written FIRST, so the kind 3 rule appended last names a
`to_id` that is already live at V, which is what `KEC0017` checks. Both rows land in the same transaction,
so there is no window in which the rule exists and the row it names does not.

- [ ] **Step 3: Write the rollback tests.**

`RollbackToAsync(targetVersion)` BUILDS A DRAFT rather than publishing directly, so an operator reviews the
diff and publishes it. For every row live at both versions whose field set differs, emit an `Update`
restoring the target's values. For every row live at the target and RETIRED since, **REFUSE with
`KEC0039`**, naming the row and the rule that retired it. For every row introduced AFTER the target, do
NOTHING: it keeps its id and its values, which is the difference between a rollback and a restore.

**There is no un-retire branch and there never was a reachable one**, because every retire appends a kind 2
rule, so a branch conditioned on "no rule names that id" could not run. Assert `KEC0039` fires and assert
the way out is an ordinary `Add` with a new key plus a `ReplacedBy` rule.

- [ ] **Step 4: Implement the diff and the bundle.**

The diff is FIELD LEVEL, computed over the per-field rows rather than by comparing chunk hashes, and it
carries a chunk summary so an operator sees the download cost of an edit before publishing it.

A `ContentBundle` is the whole catalog as one JSON document: a format version, the registered type list
with schemas, every live row with its id, key and fields, every family with its blocks, and the full rule
list. It is the seeding format AND the lossless export format and there is only one of them. **Import works
into an EMPTY database ONLY**, empty meaning the version table has no rows, otherwise 409 with nothing
written. A bundle row's id is OPTIONAL: named, it is imported with it, unnamed, it is allocated in edit
ordinal order. Export at N then import into an empty store reproduces the same rows, keys and IDS.

**A lossless export is not a backup**, and the bundle's doc comment says so: an import republishes at
version 1, so the version LINE restarts. When the line must be preserved the path is an ordinary database
restore of the authoring store, which is the provider's tooling and outside this engine.

- [ ] **Step 5: Run green, check file sizes, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
sh scripts/check-file-size.sh --tree
git add KhaozEngine.Catalog.Authoring KhaozEngine.Catalog.Tests
git commit -m "catalog(publish): add the commit, the sweep, rollback, diff and the bundle"
~~~

---

### Task 17: The SQLite authoring provider (large)

Spec 4.1 to 4.4, 4.7, 2.7. Contracts 5.3.

**Files:**

- Create: `KhaozEngine.Catalog.Sqlite/KhaozEngine.Catalog.Sqlite.csproj`
- Create: `KhaozEngine.Catalog.Sqlite/README.md`
- Create: `KhaozEngine.Catalog.Sqlite/SqliteCatalogSchema.cs` (the const DDL plus the two constants only)
- Create: `KhaozEngine.Catalog.Sqlite/SqliteCatalogSchemaValidation.cs`
- Create: `KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.cs`
- Create: `KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.Publish.cs`
- Create: `KhaozEngine.Catalog.Sqlite/SqliteContentAuthoringStore.Draft.cs`
- Modify: `KhaozEngine.slnx`, `README.md`

**Interfaces:**

- Consumes: Task 13's seam, `KhaozEngine.Sqlite`, `Microsoft.Data.Sqlite`
- Produces: the SQLite backend

**Precedents to copy, not to reinvent.** `KhaozEngine.WorldStore.Sqlite/SqliteJournalSchema.cs` is the
schema style: a `CurrentVersion`, a named `RequiredMigration`, an `AutoCreate` and `ValidateOnly` mode, a
metadata schema-version row, and validation of every schema object read back from `sqlite_master`. The
wallet's single inline bootstrap with no version at all is the style NOT to copy, because this schema will
gain tables as Scope B's types land and as inheritance ships.

**`SqliteStoreConnection` is not optional.** One held connection, one `SemaphoreSlim(1,1)` gate, and a
dispose that calls `SqliteConnection.ClearPool(connection)` BEFORE `connection.Dispose()`
(`KhaozEngine.Sqlite/SqliteStoreConnection.cs:76-82`). That line was copied wrong three times over and
there is one copy now. Every command runs under a lease from `EnterAsync`, and a transaction takes the
lease FIRST.

- [ ] **Step 1: Transcribe the DDL from spec 4.4, in full, without editing it.**

Spec 4.4 gives the complete SQLite DDL for all fourteen tables: `catalog_metadata`, `catalog_type`,
`catalog_version`, `catalog_row`, `catalog_row_field`, `catalog_family`, `catalog_family_block`,
`catalog_id_high_water`, `catalog_draft`, `catalog_draft_edit`, `catalog_draft_edit_field`,
`catalog_audit`, `catalog_remap_rule`, `catalog_chunk`, plus the seed insert. Copy it as a C# raw string
const. Every key column is `TEXT COLLATE BINARY`, every size cap is a `CHECK`, and every foreign key is
declared because `PRAGMA foreign_keys = ON` is set in the bootstrap.

Four details in that DDL are load bearing and each has a test below: the unique index on
`(type_id, definition_id, content_key)` makes an edit idempotent per TARGET, `visibility` is in
`catalog_chunk`'s primary key because one id range can be TWO chunks, `catalog_row_field` stores exactly
one of three value columns chosen by `field_kind` and writes NO row at all for a `LocalizedTextKey`, and
`catalog_metadata` carries `active_version`, `pinned_version` and `store_epoch` with no `sealed_flag`.

**There is no `UPDATE` and no `DELETE` statement for `catalog_remap_rule` anywhere in this provider.** Do
not add the journal's `BEFORE DELETE` trigger either: that guard exists to permit a retention sweep and
there is no retention sweep here.

If `SqliteCatalogSchema.cs` approaches 800 lines with the DDL in it, the validation half is already its own
file and that is the split. Do not split the DDL string.

- [ ] **Step 2: Write the provider tests as the conformance subclass only.**

The behaviour facts live in Task 19's shared conformance class. What belongs HERE is the schema half:
`AutoCreate` on an empty database creates and reports version 1, `ValidateOnly` on an empty database throws
naming `catalog-v1-initial`, `ValidateOnly` on a correct schema succeeds, and a mismatched object throws
`ContentAuthoringException` naming the object and the migration.

- [ ] **Step 3: Implement the store, split by responsibility across the three partial files.**

Raw parameterized ADO.NET with `$name` parameters, no EF and no ORM. The publish COMMIT is one explicit
transaction taken under the connection lease. Ship the package README and the root catalog row in this
commit. The description says OPT-IN and the package joins NO umbrella.

- [ ] **Step 4: Run green and commit.**

~~~bash
dotnet build KhaozEngine.Catalog.Sqlite/KhaozEngine.Catalog.Sqlite.csproj -c Release
sh scripts/check-doc-versions.sh && sh scripts/check-file-size.sh --tree
git add KhaozEngine.Catalog.Sqlite KhaozEngine.slnx README.md
git commit -m "catalog(sqlite): add the SQLite authoring provider and its versioned schema"
~~~

---

### Task 18: The SQL Server authoring provider (medium)

Spec 4.5, 4.1. Contracts 5.3.

**Files:**

- Create: `KhaozEngine.Catalog.SqlServer/KhaozEngine.Catalog.SqlServer.csproj`
- Create: `KhaozEngine.Catalog.SqlServer/README.md`
- Create: `KhaozEngine.Catalog.SqlServer/CatalogSchemaV1.sql` (EMBEDDED RESOURCE)
- Create: `KhaozEngine.Catalog.SqlServer/SqlServerCatalogSchema.cs`
- Create: `KhaozEngine.Catalog.SqlServer/SqlServerContentAuthoringStore.cs`
- Create: `KhaozEngine.Catalog.SqlServer/SqlServerContentAuthoringStore.Publish.cs`
- Modify: `KhaozEngine.slnx`, `README.md`

**Interfaces:**

- Consumes: Task 13's seam, `Microsoft.Data.SqlClient`
- Produces: the SQL Server backend

**Both providers are phase 1 and neither is deferred**, because Grimhollow's backend is env-selected
between SQLite and SQL Server.

**Precedent:** `KhaozEngine.WorldStore.SqlServer/JournalSchemaV1.sql` is the embedded-resource shape, its
line 10 is the collation precedent and its line 33 the `DATALENGTH` cap precedent.
`KhaozEngine.Commerce.SqlServer/SqlServerWalletStore.cs` is the connection shape: a pooled `SqlConnection`
per call and an `IsolationLevel.Serializable` transaction, with no in-process semaphore. There is no shared
SQL Server connection type in the engine and this plan does not add one.

- [ ] **Step 1: Transcribe the DDL with the idiom swaps of spec 4.5's table.**

`TEXT COLLATE BINARY` becomes `nvarchar(N) COLLATE Latin1_General_100_BIN2`. `INTEGER` becomes `int`, or
`bigint` for the audit id and the timestamps. `INTEGER PRIMARY KEY AUTOINCREMENT` becomes
`bigint IDENTITY(1,1)`. `BLOB` becomes `varbinary(max)` with `CHECK (DATALENGTH(x) <= N)`. The epoch-ms
timestamps become `datetimeoffset(7)`. `INSERT OR IGNORE` becomes `IF NOT EXISTS (...) INSERT`.
`CHECK (length(x) <= N)` becomes `LEN` for `nvarchar` and `DATALENGTH` for `varbinary`.

**Every constraint is NAMED**, `CONSTRAINT ck_<table>_<what> CHECK`, because an unnamed constraint gets a
generated name and the schema validator compares names.

- [ ] **Step 2: Implement, and keep the difference behavioural rather than mechanical.**

The one genuine behavioural difference is the transaction: SQL Server takes no in-process semaphore and
uses `IsolationLevel.Serializable`, which makes two consoles publishing concurrently a deadlock or an abort
rather than a race. The conformance suite asserts the OBSERVABLE behaviour and never the mechanism, so one
suite covers both. A serialization failure surfaces to the caller as the same 409 the optimistic
concurrency check produces.

Ship the package README and the root catalog row in this commit. OPT-IN, no umbrella.

- [ ] **Step 3: Build green and commit.**

~~~bash
dotnet build KhaozEngine.Catalog.SqlServer/KhaozEngine.Catalog.SqlServer.csproj -c Release
sh scripts/check-doc-versions.sh
git add KhaozEngine.Catalog.SqlServer KhaozEngine.slnx README.md
git commit -m "catalog(sqlserver): add the SQL Server authoring provider"
~~~

---

### Task 19: Provider conformance and publish crash safety (large, GATE for 1.2)

Spec 15.5, 15.6, 2.6, 6.11.

**Files:**

- Create: `KhaozEngine.Server.Tests/Catalog/ContentAuthoringStoreConformance.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/SqliteContentAuthoringStoreTests.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/SqlServerContentAuthoringStoreTests.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/CatalogSqlServerFactAttribute.cs`
- Create: `KhaozEngine.Catalog.Tests/Publish/PublishCrashSafetyTests.cs`
- Modify: `KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj`

**Interfaces:**

- Consumes: Tasks 13 to 18
- Produces: the milestone 1.2 gate

**The conformance class is the precedent's shape**, `KhaozEngine.Server.Tests/Commerce/WalletStoreContract.cs:9-11`:
an abstract class with `protected abstract IContentAuthoringStore NewStore()` and one concrete subclass per
backend. The provider tests live in `KhaozEngine.Server.Tests` because that is the existing home of every
Commerce and WorldStore provider test and it already references both SQL packages' siblings.

- [ ] **Step 1: Write the env gate, as a byte-for-byte copy with the variable renamed.**

~~~csharp
public sealed class CatalogSqlServerFactAttribute : FactAttribute
{
    public CatalogSqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_CATALOG_SQLSERVER")))
            Skip = "set KE_CATALOG_SQLSERVER to run";
    }
}
~~~

A SEPARATE variable rather than reusing `KE_COMMERCE_SQLSERVER`, because the two suites create different
schemas and an operator should be able to run one without the other. Its doc says the same thing the
Commerce one does: CI has no SQL Server, so these run locally or against a test database on demand.

The SQLite subclass uses the per-test unique in-memory idiom from
`KhaozEngine.Server.Tests/Commerce/SqliteWalletStoreTests.cs:7-17`,
`$"Data Source=catalog_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"`, and disposes it.

- [ ] **Step 2: Write the twenty-three conformance facts from spec 15.5, each asserting OBSERVABLE
behaviour and never a mechanism.**

1 `AutoCreate` on empty creates and reports version 1. 2 `ValidateOnly` on empty throws naming the required
migration. 3 `ValidateOnly` on a correct schema succeeds. 4 A key differing only in case is a DIFFERENT
key, which is the binary collation assertion. 5 Two `Add` edits for one key in one draft collide on the
unique index. 6 An `Update` MERGES fields rather than replacing the row's field set. 7 Publish assigns 1
then 2, never skipping. 8 Publish with a stale `expectedBaseVersion` is refused and nothing is written. 9 A
row untouched by a publish keeps its `valid_from_version`. 10 The live set at an old version excludes a row
added later. 11 A retire writes a successor row plus EXACTLY ONE remap rule. 12 A remap rule cannot be
updated or deleted through the API surface. 13 Allocation reserves before issuing, asserted by reading
`reserved_through` after a single allocate. 14 Two allocations never return the same id across 10,000. 15 A
family allocation stays inside its aligned block and reserves a second block when full. 16 Every audit row
carries a before and an after for a field change. 17 An audit insert failure ROLLS BACK the edit. 18 Import
into an empty database succeeds and reproduces the source ids. 19 Import into a non-empty database is
refused with nothing written. 20 Export at N then import into an empty store gives identical rows, keys and
ids. 21 A publish that fails at the validator leaves the draft intact. 22 The active pointer and the
version row commit together, asserted by a reader seeing both or neither. 23 A plain allocation taken after
a family block is reserved never returns an id inside that block, asserted by draining the whole gap under
the block and then some.

Fact 17 is worth a sentence, because the default it rejects is the common one: **the audit append is IN the
same transaction as the edit, not best effort.** A content edit with no audit row is indistinguishable from
no edit. If the audit insert fails, the edit fails. The before and after columns cap at 4,096 to match the
blob cap on the other side of the same transaction, and a rendering that still would not fit is abbreviated
VISIBLY with `+<n> more` for a tag list or `+<n> bytes` for a blob, never silently.

- [ ] **Step 3: Write the nine crash-safety cases, one per `ContentPublishStep`.**

The hook throws at the step, and the test asserts three things: the store is EITHER entirely at the old
version OR entirely at the new one, the pack store holds no file any version references but cannot serve,
and **a REPUBLISH after the kill succeeds and produces the same manifest hash it would have produced
without the kill.** That last clause is the idempotence assertion and it is the one that matters, because a
publish that merely fails safely but cannot be retried is not recoverable.

Two crash points have a specific expected state worth pinning. Between the reservation commit and step 4,
a gap of reserved-but-unissued ids, bounded by 1,024 per type, needing no recovery. Between step 9 and step
10, every file of the new version exists and so does its pointer, nothing in the database references them,
the old version is still active, and the retried publish takes the SAME version number and overwrites the
stale pointer.

The out-of-process version is a `--catalog-crash-probe` mode in `KhaozEngine.Benchmarks`, killing a child
process at each step against a real SQLite file, mirroring
`KhaozEngine.Benchmarks/Journal/JournalCrashProbe.cs`. In-process hooks prove the ORDERING and a real kill
proves the DURABILITY. Add the mode in this task and leave its structural test to Task 24.

- [ ] **Step 4: Run the milestone 1.2 gate green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~Catalog
KE_CATALOG_SQLSERVER="<a test database>" dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~SqlServerContentAuthoringStoreTests
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
git add KhaozEngine.Server.Tests KhaozEngine.Catalog.Tests KhaozEngine.Benchmarks
git commit -m "catalog(conformance): add the store conformance suite and the crash-safety cases"
~~~

Expected: the first two exit zero. The third exits zero when a SQL Server is reachable and reports only
env-gated skips otherwise. **This closes milestone 1.2.** If SQL Server is unreachable, say so in the
report rather than marking the gate met.

---

## Milestone 1.3: the server runtime, the boot and the loot roller

**Group acceptance (spec 18.1's gate for 1.3):** the twelve boot fail-closed facts of spec 15.7 pass, and
budgets P3, P7, P9 and P11 are measured at 50,000 definitions. Tasks 20 to 24.

### Task 20: `ContentTypeTable`, `ContentRuntime` and the atomic swap (medium)

Spec 9.1, 9.2, 9.3, 9.7.

**Files:**

- Create: `KhaozEngine.Catalog/Runtime/ContentTypeTable.cs`
- Create: `KhaozEngine.Catalog/Runtime/ContentRuntime.cs`
- Create: `KhaozEngine.Catalog/Runtime/ContentRuntimeHolder.cs`
- Create: `KhaozEngine.Catalog.Tests/Runtime/ContentRuntimeTests.cs`

**Interfaces:**

- Consumes: Tasks 3, 9 and 10
- Produces: the loaded active version, implementing `IContentSnapshot`

**Lift from the spike's `ContentTypeTable.cs` and `ContentRuntime.cs`**, which are the code P3 and P7 were
measured against. Change two things: the runtime implements `IContentSnapshot` from Task 9, and the boot
timing struct the spike carries stays out of the shipped type (it belongs to the benchmark).

- [ ] **Step 1: Write failing tests.**

A lookup is `offsets[id]`, one array read, then a span slice of `Bodies`. Assert NO dictionary, NO lock and
NO allocation on the lookup path, with a `GC.GetAllocatedBytesForCurrentThread` delta of 0 over a warm
loop. Assert `Offsets` is sized to the highest live id plus one and NOT to the sum of chunk slots, because
chunk slots are a transport unit the runtime does not inherit. Assert `KeyIds` is the open-addressed
key-to-id index with 0 meaning empty, which is unambiguous because 0 is not a legal id, and that a probe
compares the candidate's key SLICE ordinally. Assert there is no `Keys` array: the key blob IS `Bodies`.

Assert the swap: `Volatile.Read` of the single field, a reader taking the reference ONCE at the top of an
operation, and everything reachable from a runtime immutable after construction. **v1 never swaps at
runtime**, because a new version applies at server restart. The field and the `Volatile` pair exist anyway,
for a test fixture and for a later live-apply phase, and they cost two lines.

- [ ] **Step 2: Implement, run green, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~ContentRuntimeTests
git add KhaozEngine.Catalog/Runtime KhaozEngine.Catalog.Tests/Runtime
git commit -m "catalog(runtime): add the per-type tables and the atomically swapped runtime"
~~~

---

### Task 21: The four derived indexes and `IContentLoadIndex` (medium)

Spec 9.4, 3.6.

**Files:**

- Create: `KhaozEngine.Catalog/Runtime/ContentDerivedIndexes.cs`
- Modify: `KhaozEngine.Catalog/IContentLoadIndex.cs` (the stub from Task 3)
- Create: `KhaozEngine.Catalog.Tests/Runtime/DerivedIndexTests.cs`

**Interfaces:**

- Consumes: Tasks 3, 4 and 20
- Produces: the four engine indexes and the hook a Scope B or game type registers its own through

**Lift from the spike's `ContentIndexes.cs`**, which already builds all four as flat arrays rather than a
collection per row.

- [ ] **Step 1: Write failing tests for the four.**

Key to id per type is the open-addressed `int[]` of Task 20. Tag to ids is a sorted `int[]` per tag id per
content type. Family membership is the block list per family, cached, so a test is
`(id & ~(size - 1)) == base` against each block. Loot candidate arrays are, per `loot_table`, the resolved
entry list with weights PREFIX SUMMED, so a weighted draw is one binary search over an `int[]` with no
allocation and no per-roll summation.

**All four are built EAGERLY at load and none is built lazily**, because each is walked inside gameplay and
a lazy build inside a tick is a latency spike.

- [ ] **Step 2: Implement the registration hook.**

~~~csharp
public interface IContentLoadIndex
{
    ContentTypeId Type { get; }
    void Build(IContentSnapshot snapshot);
}
~~~

Registered through `RegisterContentType(..., loadIndex: ...)`, one per type at most, held by the runtime
and handed back through a typed accessor the registering code owns. The four rules that make it safe, each
a test: it runs at boot step 7b AFTER the engine's four and BEFORE the validator, in TYPE ID ORDER so an
index over engine rows is built before one over Scope B rows, it MAY read another type's rows through the
snapshot and may NOT read another index, and it THROWS to fail the boot closed rather than returning a
partial index.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~DerivedIndexTests
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests/Runtime
git commit -m "catalog(indexes): add the four derived indexes and the load-index hook"
~~~

---

### Task 22: `LootRoller`, the one implementation of the composition rule (medium)

Spec 1.1 ninth deliverable, 3.5, 9.4. Contracts 14.4.

**Files:**

- Create: `KhaozEngine.Catalog/LootRoller.cs` (holds `LootDraw`)
- Create: `KhaozEngine.Catalog.Tests/Loot/LootRollerTests.cs`

**Interfaces:**

- Consumes: Tasks 1, 20 and 21
- Produces: the draw the engine owns because the rule is the engine's

**Lift from the spike's `LootRoller.cs`, and change its seam.** The spike takes `DeterministicRng` by
constructor because `IRandomSource` did not exist. It exists now, from Task 1, so the shipped type takes
`IRandomSource`.

**Why this ships at all:** the spec defined `loot_table` and `loot_entry`, specified how guaranteed
entries, weighted picks, nested tables and tag draws compose, built the prefix-summed arrays and budgeted
the draw at P9, and shipped no code performing it. Scope B disclaims the roll. Every consumer would have
written the composition rule again and the first table using both `guaranteed` and `roll_count` would have
disagreed. The owner of a rule is the code that runs it.

~~~csharp
public sealed class LootRoller
{
    public LootRoller(ContentRuntime runtime, IRandomSource random);
    public int  Roll(int tableId, Span<LootDraw> destination);   // returns the count written
    public bool TryRoll(int tableId, Span<LootDraw> destination, out int written);
}

public readonly record struct LootDraw(int ItemId, int Count, int TableId);
~~~

- [ ] **Step 1: Write failing tests that pin the DRAW ORDER, which is the contract.**

Every `guaranteed` entry in `sort` order FIRST, each rolling its own `chance_bp` independently. Then
`roll_count` weighted picks over the non-guaranteed entries, each pick a `NextInt(0, total)` and a binary
search over the prefix-summed array. A `nested_table` entry RECURSES at the point it is drawn, with depth
bounded by the acyclicity `KEC0024` guarantees. A `required_tags` entry draws UNIFORMLY from its
precomputed candidate array.

Roll a seeded table and assert EXACT drops. Assert zero allocation over a warm loop. Assert a destination
too small is filled and `Roll` returns the span's length while `TryRoll` reports the overflow, so a caller
can size up rather than silently lose drops. Assert `TableId` on each draw names the table the LINE came
from, which is the one thing a caller cannot reconstruct after a nested draw.

**Assert what it does NOT do**, by absence of API: it builds no instance, places no ground stack, adds to
no inventory, emits no event and touches no journal. It reads content and a random source and returns
numbers. The journal event a game records is the GAME's, and the caller passes the `TableId` it got back
into whatever event it writes.

- [ ] **Step 2: Implement, run green, commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~LootRollerTests
git add KhaozEngine.Catalog/LootRoller.cs KhaozEngine.Catalog.Tests/Loot
git commit -m "catalog(loot): add the roller that owns the composition rule"
~~~

---

### Task 23: The boot sequence and the fail-closed exit path (medium)

Spec 9.5, 9.6, 3.10. Contracts 10.5.

**Files:**

- Create: `KhaozEngine.Catalog/Runtime/ContentBoot.cs`
- Create: `KhaozEngine.Catalog/Runtime/ContentBootResult.cs`
- Create: `KhaozEngine.Catalog.Tests/Runtime/ContentBootTests.cs`

**Interfaces:**

- Consumes: Tasks 10, 11, 20, 21
- Produces: the ordered boot and its twelve refusals

- [ ] **Step 1: Write the twelve fail-closed facts, one per row of spec 9.6's table.**

Each asserts exit code 3 and the EXACT stderr prefix. Run in process against a test host that CAPTURES the
exit rather than calling `Environment.Exit`, so the whole table runs in one assembly.

The twelve: no active version, manifest absent or hash mismatch, manifest declaring a different version,
generation too new, server build too old, a chunk absent or mismatched, a manifest naming an UNREGISTERED
type, a REGISTERED type absent from the manifest, a chunk decode failure, a registered load index that
threw, validator findings (one line per finding then a count line), and an unresolved world key.

**Both type-registration rows are refusals, in both directions.** A manifest naming a type this build does
not register has no codec to decode its rows. A registered type ABSENT from the manifest is the same
failure from the other side, and the tempting answer, an empty runtime table, is worse: every reference
into that type then resolves to nothing and the operator reads a page of `KEC0006` findings instead of one
line naming the missing type. Both refuse at step 6, before a single chunk is fetched, because the
manifest's type list is enough to decide it.

**Exit code 3 throughout**, distinct from the 2 a consumer already returns for a bad config, so a
supervisor script tells a content failure from a config failure without parsing text.

- [ ] **Step 2: Implement the ordered boot.**

1 register every type, 2 resolve the version, 3 fetch and verify the server manifest AND its embedded
`versionNumber`, 4 refuse a generation too new, 5 refuse a server build too old, 6 fetch and verify every
chunk then FREEZE the registry, 7 decode and build the four engine indexes, 7b build every registered
`IContentLoadIndex` in type id order, 8 run the validator with `previous` null, 9 publish the runtime with
a `Volatile.Write`, 10 load the world document, 11 resolve every world-to-content key, 12 build the connect
door, 13 accept connections.

**Version precedence, step 2, one order and no other:** a version pinned in the SERVER'S OWN CONFIG wins
always, otherwise the store's pinned version when not null, otherwise the store's active version. A server
configured with a pinned version and a pack store therefore needs NO authoring database at boot at all,
which is the deployment this design recommends: the authoring database is a TOOLING dependency.

**Content loads BEFORE the world and both load before the door opens**, because step 11 needs content and
because contracts 7.5 puts the content layer inside the world layer. Step 11 itself is a boot-time
fail-closed check that a world archetype or marker tag naming content resolves by KEY, and the world
document never carries a content ID.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~ContentBootTests
git add KhaozEngine.Catalog/Runtime KhaozEngine.Catalog.Tests/Runtime
git commit -m "catalog(boot): add the ordered boot and its twelve fail-closed refusals"
~~~

---

### Task 24: `CatalogBenchmarkTests`, the structural half of the scale runs (small, GATE for 1.3)

Spec 14.2, 15.4, 18.1.

**Files:**

- Create: `KhaozEngine.Server.Tests/Benchmarks/CatalogBenchmarkTests.cs`

**Interfaces:**

- Consumes: the EXISTING `KhaozEngine.Benchmarks/Catalog/` spike, which is already the `--catalog` mode
- Produces: the CI-side proof that the benchmark still works, mirroring `MutationJournalBenchmarkTests`

**`KhaozEngine.Server.Tests` already references `KhaozEngine.Benchmarks`**, so this task adds no project
reference. Put the class beside the existing benchmark test, in
`KhaozEngine.Server.Tests/Benchmarks/`, and follow
`KhaozEngine.Server.Tests/WorldStore/Journal/MutationJournalBenchmarkTests.cs` in shape.

**PLAN CHOICE, and the one place this plan knowingly leaves a duplicate.** The brief and spec 18.0 both
say the spike stays where it is and keeps building. This plan therefore does NOT re-point
`KhaozEngine.Benchmarks/Catalog/` at the shipped packages in phase 1: the measured numbers in spec 14.3
were taken against that code, and swapping the implementation under them mid-flight would make the
`Measured` column describe something nobody ran. The duplication is deliberate, it is bounded to one
gitignored-from-packaging benchmark project, and re-pointing it is a phase 3 follow-up the last task files.

- [ ] **Step 1: Write the structural facts. The TIMING half never runs in CI.**

`CatalogBenchmarkConfig.Parse` accepts `--definitions`, `--types`, `--chunk-slots`, `--languages`,
`--edit-count` and `--compose` and rejects a malformed value. The synthetic generator is DETERMINISTIC from
a seed, asserted by generating twice at one seed and comparing byte for byte, which is contracts 4.3's
registration-order independence made measurable. `CatalogBenchmarkResult.ToJson` serializes and
`CatalogBenchmarkOutput.WriteAsync` writes to a temp path. A TINY run completes, small enough to be a unit
test rather than a benchmark. Add the same three for the `--catalog-crash-probe` mode from Task 19.

- [ ] **Step 2: Run the milestone 1.3 gate.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~CatalogBenchmarkTests
dotnet run -c Release --project KhaozEngine.Benchmarks -- --catalog --definitions 50000 --compose --output ./catalog-p11.json
~~~

Record P3, P7, P9 and P11 from that JSON in the report. Do NOT edit spec 14's `Measured` column: that is the
owner's table and this plan does not touch a design document.

- [ ] **Step 3: Commit.**

~~~bash
git add KhaozEngine.Server.Tests/Benchmarks/CatalogBenchmarkTests.cs
git commit -m "catalog(bench): add the structural test for the catalog benchmark mode"
~~~

Expected: exit zero. **This closes milestone 1.3.**

---

## Milestone 1.4: the sixteen authoring actions

**Group acceptance (spec 18.1's gate for 1.4):** the action tests pass, INCLUDING one asserting a real 409
body carrying `expectedBaseVersion`, and budgets P5 and P6 are measured. Tasks 25 to 28.

### Task 25: `AdminActionStatus.Conflict` and the object error payload (small)

Spec 2.1, 10.1, 10.2.

**Files:**

- Modify: `KhaozEngine.NetWorld/AdminActionResult.cs`
- Modify: `KhaozEngine.Server.Admin/AdminHttpServer.cs`
- Modify: `KhaozEngine.NetWorld/README.md`, `KhaozEngine.Server.Admin/README.md`
- Create: `KhaozEngine.Server.Tests/ServerAdminEndpoint/AdminActionConflictTests.cs`

**Interfaces:**

- Consumes: the existing `ServerAdmin` action surface
- Produces: a 409 and a structured 400 body, which five actions in Task 27 need

**This is additive and breaks no caller**, which is why spec 10.1 makes it a milestone item rather than a
contract change request. It is named as its own task because the spec's entire answer to two failure modes
rides on it and an unnamed engine change is an unbudgeted one.

- [ ] **Step 1: Write failing tests.**

`AdminActionStatus` today is exactly `{ Ok, Accepted, BadRequest }`, `AdminActionResult.BadRequest(string)`
carries a bare string, and `DispatchActionAsync` maps `BadRequest` to `Results.BadRequest(new { error })`
with everything else falling to 500. Assert: a handler returning `Conflict(payload)` dispatches to HTTP 409
with the payload as the JSON body, a handler returning the new object-carrying `BadRequest(payload)`
dispatches to 400 with the payload as the body, and **every existing string overload keeps its exact
current shape**, asserted against the `new { error = result.Error }` body.

- [ ] **Step 2: Implement.**

Add `Conflict` to the enum, add `AdminActionResult.Conflict(object payload)` beside `BadRequest`, add an
object-carrying `BadRequest(object payload)` overload while KEEPING the string one, and add the two arms to
the dispatch switch. Nothing returns 202 in the catalog's actions, deliberately: a 202 means enqueued to
the host thread, and a content edit completes INSIDE the request against the database, so the operator gets
the real answer rather than an optimistic one.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~AdminAction
git add KhaozEngine.NetWorld KhaozEngine.Server.Admin KhaozEngine.Server.Tests/ServerAdminEndpoint
git commit -m "admin(result): add Conflict and an object-carrying error payload"
~~~

---

### Task 26: The five read actions (medium)

Spec 10.1 to 10.4, 10.7.

**Files:**

- Create: `KhaozEngine.Server.Admin/Catalog/CatalogAdminActions.cs` (the registration entry point only)
- Create: `KhaozEngine.Server.Admin/Catalog/CatalogReadActions.cs`
- Create: `KhaozEngine.Server.Admin/Catalog/CatalogActionPayloads.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/CatalogReadActionTests.cs`
- Modify: `KhaozEngine.Server.Admin/KhaozEngine.Server.Admin.csproj`, `README.md` catalog row text

**Interfaces:**

- Consumes: Tasks 13, 14, 25
- Produces: `catalog-schema`, `catalog-list`, `catalog-get`, `catalog-draft`, `catalog-versions`

**PLAN CHOICE, and the spec leaves this open.** Spec 10.1 names
`CatalogAdminActions.Register(ServerAdmin admin, IContentAuthoringStore store, ContentTypeRegistry registry)`
and never says which package holds it. It needs `ServerAdmin` (in `KhaozEngine.NetWorld`) and
`IContentAuthoringStore` (in `KhaozEngine.Catalog.Authoring`). Three homes were weighed:

| Home | Cost |
|---|---|
| `KhaozEngine.Catalog.Authoring` gains a NetWorld reference | Every opt-in SQL provider then transitively pulls NetWorld, Physics, Simulation and WorldStore into a tooling graph. Rejected. |
| A sixth package | Spec 2.1 says "no sixth is added". Rejected. |
| `KhaozEngine.Server.Admin` gains `Catalog` and `Catalog.Authoring` references | It already references NetWorld, it already owns the dispatch, it is already one of the three packages this phase changes, and it is deliberately OUT of the `Server` umbrella so nothing inherits the cost. TAKEN. |

The cost of the taken option is that a game registering catalog actions on a `ServerAdmin` with no HTTP
endpoint cannot reach the helper. There is one transport today and the status codes in spec 10.2 are HTTP,
so that cost is theoretical. Report the choice so the spec's author can confirm it.

- [ ] **Step 1: Write failing tests.**

Action names match `^[a-z0-9][a-z0-9-]{0,63}$` and a duplicate registration throws, both already enforced by
`ServerAdmin`. **Every handler runs on the HTTP REQUEST THREAD and must never touch simulation state**,
which these are compliant with by construction because they touch only the authoring store.

`catalog-schema` returns the full registered schema, which is what makes ONE generic editor render a type
the console has never heard of. Assert a `LocalizedTextKey` field is returned with `"derived": true` and no
value, so a console renders it READ ONLY.

`catalog-list` takes a type key, a version (0 meaning the draft-applied live set), a key prefix, an
include-retired flag, a skip and a take. **`take` is CAPPED at 500 server side and the response carries
`total`**, which is what makes a console page rather than silently truncate. Assert the cap and the total.
Assert the derived `name` key is returned READ ONLY and is not a stored value.

`catalog-get` takes a type key plus an id OR a key and returns one row plus its full version HISTORY, which
is the temporal model's payoff. `catalog-draft` returns the open draft with its edits expanded.
`catalog-versions` returns the active version, the pinned version and every version record.

- [ ] **Step 2: Implement, run green, commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~CatalogReadActionTests
git add KhaozEngine.Server.Admin KhaozEngine.Server.Tests/Catalog README.md
git commit -m "catalog(actions): add the five read actions and the schema endpoint"
~~~

---

### Task 27: The seven mutating actions (large)

Spec 10.5 to 10.8, 10.10, 11 row 4.

**Files:**

- Create: `KhaozEngine.Server.Admin/Catalog/CatalogEditActions.cs`
- Create: `KhaozEngine.Server.Admin/Catalog/CatalogPublishActions.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/CatalogEditActionTests.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/CatalogPublishActionTests.cs`

**Interfaces:**

- Consumes: Tasks 15, 16, 25, 26
- Produces: `catalog-edit`, `catalog-discard`, `catalog-validate`, `catalog-diff`, `catalog-publish`,
  `catalog-pin`, `catalog-rollback`

- [ ] **Step 1: Write failing edit tests.**

Every edit in one request applies in ONE database transaction or none of them does, so a batch save from a
grid is atomic. Edits are checked against the schema AT THE BOUNDARY and the response carries EVERY finding
rather than the first. **A payload naming a field the schema does not declare is 400 with `KEC0004`**, and
that is the direct answer to an upsert with no gate letting an operator save a row the server then rejects
at boot while the console reports success.

**No edit ever carries a localized text key.** A payload carrying a marker field is refused with `KEC0004`
and the message names the DERIVED key, so an operator sees what they were trying to set.

`fork` is an `op` VALUE rather than a seventeenth action, because it is an edit against the open draft like
the other three and it is saved, validated, diffed and published through the same path. Its `fields` are
the changes to the ORIGINAL row. `forkKey` is required. The response is the ordinary draft response and the
copy's allocated id does NOT appear in it, because ids are allocated at publish.

A `retire` names `placeholder` or `replacement`, and a `replacement` with no resolvable key is 400 with
`KEC0017`. `catalog-discard` writes one audit row carrying the edit count, so a discarded draft leaves a
trace, and it is 409 while a publish is in flight.

- [ ] **Step 2: Write failing publish tests.**

`catalog-validate` builds the candidate and runs the full sweep WITHOUT allocating ids and without writing
anything. It is the same validator, so a green validate followed by a red publish can only mean the draft
changed in between.

`catalog-diff` is FIELD LEVEL and carries a `chunkSummary`, which is the operator-facing half of the
one-item-edit budget.

**`expectedBaseVersion` on `catalog-publish` is REQUIRED optimistic concurrency.** Two consoles cannot both
publish the same draft: the second one's expectation is stale and it gets a 409 naming BOTH numbers. That
is the same shape as the journal's expected-version mutation, and it turns a race into an error message.
Write that 409 test against the real dispatch from Task 25, because it is the milestone gate.

- [ ] **Step 3: Write the pin and rollback tests.**

`catalog-pin` takes a version or null. A pin naming a version that does not exist is 400. A pin naming a
version whose `minimumServerBuild` exceeds the running build is ACCEPTED with a warning, because the
operator may be pinning ahead of an upgrade on purpose and the boot check is the real gate. **When the
server's own CONFIG already pins a version, the action returns 200 carrying `configPinnedVersion` and a
warning naming it**, because a bare 200 for a call with no effect on the next restart is the failure that
paragraph exists to prevent.

`catalog-rollback` BUILDS A DRAFT and returns `draftCreated`, `editCount` and `blockedByRules`. Blocked by
an irreversible retire, it is 409 carrying `KEC0039`, the blocking rules, and a `remedy` naming the
mint-a-new-id path, so the operator is not left guessing.

**There is deliberately NO validation override flag anywhere in these actions.** A publish that bypasses
validation is how a bad row reaches a pack, and a force flag would make boot the only real gate while boot
fails closed, so the operator would have published a version that cannot be served. The repair path for a
validator bug is an engine patch: export the failing candidate through `catalog-export` and replay it in a
unit test.

- [ ] **Step 4: Run green, check file sizes, commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~CatalogPublishActionTests
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~CatalogEditActionTests
sh scripts/check-file-size.sh --tree
git add KhaozEngine.Server.Admin/Catalog KhaozEngine.Server.Tests/Catalog
git commit -m "catalog(actions): add the seven mutating actions and optimistic publish"
~~~

---

### Task 28: Import, export, operator identity and the operational pair (medium, GATE for 1.4)

Spec 10.9, 10.10, 10.11, 10.2.

**Files:**

- Create: `KhaozEngine.Server.Admin/Catalog/CatalogBundleActions.cs`
- Create: `KhaozEngine.Server.Admin/Catalog/CatalogOperationalActions.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/CatalogBundleActionTests.cs`
- Create: `KhaozEngine.Server.Tests/Catalog/CatalogActionInventoryTests.cs`

**Interfaces:**

- Consumes: Tasks 16, 26, 27
- Produces: `catalog-import`, `catalog-export`, `catalog-sweep`, `catalog-verify`, and the milestone gate

- [ ] **Step 1: Write the inventory test, because the count is a number the spec states once and means.**

`CatalogActionInventoryTests` asserts `CatalogAdminActions.Register` leaves EXACTLY SIXTEEN action names on
the `ServerAdmin`, and asserts the sixteen names literally: `catalog-schema`, `catalog-list`,
`catalog-get`, `catalog-edit`, `catalog-draft`, `catalog-discard`, `catalog-validate`, `catalog-diff`,
`catalog-publish`, `catalog-versions`, `catalog-pin`, `catalog-rollback`, `catalog-import`,
`catalog-export`, `catalog-sweep`, `catalog-verify`. Earlier spec drafts counted eleven in one place and
fourteen in another, so the test is what stops the count drifting again. Assert no content action returns
501: a registered action that does not exist is a 404 from the lookup, and the 501 arms belong to four
built-in routes gated on capability flags.

- [ ] **Step 2: Write the import, export and identity tests.**

**Import works into an EMPTY database ONLY and is refused otherwise**, empty meaning the version table has
no rows, with a 409 carrying the active version and no partial write. That single rule is the answer to a
whole class of seeding defects: an insert-if-absent seed that runs repeatedly against live data ends up
carrying guarded corrections that knowingly revert an operator's value. A deployed database's values change
through `catalog-edit` and `catalog-publish` and through nothing else, ever.

Export at N then import into an empty store reproduces the same rows, keys and IDS. A bundle row's id is
OPTIONAL and a bundle may MIX the two, because the id is per row and there is one import path.

**Operator identity is a forwarded, unverified field.** The bearer token is ONE token and is not an
identity. The console forwards `operator` on every mutating request and the engine records it beside its
own `actor`, keeping both columns because `actor` is what the engine AUTHENTICATED and `operator` is what
the console ASSERTED. A request with NO `operator` is ACCEPTED and audited with an empty operator, because
refusing it would break a scripted maintenance call with no human behind it. An `operator` over 128
characters is a 400. Document the field as taking a STABLE identity rather than a display name, and say why
in the doc comment: a display name breaks the audit trail the day someone renames themselves.

- [ ] **Step 3: Implement the operational pair.**

`catalog-sweep` runs publish step 11 alone, returns the count deleted and the count skipped, and obeys the
same skip-on-listing-failure rule. `catalog-verify` walks the active version's manifest, fetches every
chunk and rehashes it, and returns the chunks that do not match. **It is read only and NEVER repairs**,
because a repair means deciding which copy is right and only a republish can know that.

- [ ] **Step 4: Run the milestone 1.4 gate and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~Catalog
dotnet run -c Release --project KhaozEngine.Benchmarks -- --catalog --definitions 50000 --edit-count 1 --output ./catalog-p5-p6.json
git add KhaozEngine.Server.Admin/Catalog KhaozEngine.Server.Tests/Catalog
git commit -m "catalog(actions): add the bundle pair, operator identity and the operational pair"
~~~

Record P5 and P6 from that JSON in the report. **This closes milestone 1.4.**

---

## Milestone 1.5: the connect door, the client fetch and the layered catalog

**Group acceptance (spec 18.1's gate for 1.5):** the five door tests of spec 15.7 pass, and budgets P4 and
P10 are measured. Tasks 29 to 32.

### Task 29: `KhaozEngine.Catalog.Netcode` and the connect door layer (medium)

Spec 2.1, 2.4, 8.5. Contracts 7.5.

**Files:**

- Create: `KhaozEngine.Catalog.Netcode/KhaozEngine.Catalog.Netcode.csproj`
- Create: `KhaozEngine.Catalog.Netcode/README.md`
- Create: `KhaozEngine.Catalog.Netcode/ContentIdentityLayer.cs`
- Create: `KhaozEngine.Catalog.Netcode/ContentRefusal.cs`
- Create: `KhaozEngine.Catalog.Netcode/ContentIdentityGateAuthenticator.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/Catalog/ContentIdentityGateTests.cs`
- Modify: `KhaozEngine.slnx`, `README.md`

**Interfaces:**

- Consumes: `KhaozEngine.Catalog`, `KhaozEngine.Netcode`
- Produces: the content layer of the handshake nest

**The precedent is `WorldIdentityGateAuthenticator` at `KhaozEngine.Netcode/ConnectionGate.cs:53-96`** and
nothing here needs inventing: unwrap ONE layer, compare ORDINAL, refuse with a stable wire token carrying
BOTH sides, otherwise delegate inward. Implement `IConnectionAuthenticator`, `IConnectionDisplayName` and
`IConnectionPersistenceKey` exactly as the world gate does, each unwrapping and delegating.

**This is its own small package rather than a type inside `KhaozEngine.Catalog`** because `Foundation`
cannot reference a `Server`-side package. Ship the README and the root catalog row in this commit.

- [ ] **Step 1: Write the five door tests, in `KhaozEngine.TileWorld.Netcode.Tests` where the existing gate
tests live.**

A matching layer ADMITS and delegates inward. A version mismatch REFUSES with both sides in the token. A
hash mismatch with MATCHING version numbers refuses, which is the case the number alone cannot catch. An
ABSENT layer unwraps to the empty label and refuses with EMPTY client fields. A client below
`minimumClientBuild` gets `ke:content-client-too-old` rather than the generic mismatch, so the client can
tell the player to update.

The layer value is `<versionNumber>|<clientManifestHash>`, a decimal number and a 64-character lower hex
hash joined by a pipe. The refusal tokens are exactly:

~~~
ke:content-mismatch:<serverVersion>|<serverHash>|<clientVersion>|<clientHash>
ke:content-client-too-old:<minimumClientBuild>
~~~

The PIPE separates fields inside the payload and the COLON separates the token's own fields, matching the
existing world-mismatch shape. Assert no value in either token ever contains a colon.

- [ ] **Step 2: Pin the layer ORDER, outermost first: protocol version, world, CONTENT, the game's token
auth, the ban check.** Content sits inside world and outside auth because a disagreement about content is a
cheaper and more specific refusal than a failed credential, and the ban check stays innermost because it
needs the subject the token produced. Write that as a composition test over `ConnectionGate.Wrap`.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~ContentIdentityGateTests
sh scripts/check-doc-versions.sh
git add KhaozEngine.Catalog.Netcode KhaozEngine.TileWorld.Netcode.Tests KhaozEngine.slnx README.md
git commit -m "catalog(netcode): add the content identity layer and its gate"
~~~

---

### Task 30: `HttpPackStore` and `CachingPackStore` (medium)

Spec 8.3, 8.4, 8.6, 13.4.

**Files:**

- Create: `KhaozEngine.Catalog/HttpPackStore.cs`
- Create: `KhaozEngine.Catalog/CachingPackStore.cs`
- Create: `KhaozEngine.Catalog.Tests/Store/HttpPackStoreTests.cs`
- Create: `KhaozEngine.Catalog.Tests/Store/CachingPackStoreTests.cs`

**Interfaces:**

- Consumes: Task 10
- Produces: the read-only cloud provider and the verifying decorator

**No cloud SDK, and that is a package decision rather than a preference.** Taking a blob SDK would put a
third-party dependency into a `Foundation` package and force every client of every game to carry it. Every
blob service worth using serves an HTTP GET, and the WRITE side is the publisher's, which runs on a server
that can implement `IPackStore` over whatever SDK the game already has.

- [ ] **Step 1: Write failing `HttpPackStore` tests.**

Read only, over one INJECTED `HttpClient`. `GetAsync` issues
`GET <base>/<hash[0..2]>/<hash[2..4]>/<hash>.kec`, the same shard layout as the filesystem provider, so one
tree serves both. It reads `<base>/versions/<n>` for a pointer by the same rule. **`PutAsync` and
`ListAsync` THROW `NotSupportedException`**, which is what makes it obviously a fetch path rather than a
half-working publish target. A 404 answers null rather than throwing.

- [ ] **Step 2: Write failing `CachingPackStore` tests, which carry the whole value of the decorator.**

`GetAsync` asks local, on a miss asks remote, VERIFIES, writes through to local and returns.
`ExistsAsync` asks local then remote. `PutAsync` goes to local only.

~~~
bytes  = await remote.GetAsync(hash)
actual = ContentHash.OfBytesForKind(bytes)
if (actual != hash) -> discard, do NOT cache, report "hash-mismatch", try the next source
~~~

**The verification happens on every READ from the cache, not only on write**, which is what makes a local
file replaced with attacker bytes self-healing rather than permanent. Write that test.

**The decompression that feeds the verify is BOUNDED and the ORDER is load bearing**, because verifying a
hash taken over uncompressed bytes means decompressing bytes that are not yet trusted. The steps, and each
gets a test: check `storedBytes` against the received body length (`chunk-stored-length`), check the
declared `uncompressedBytes` against `MaxChunkUncompressedBytes` (`chunk-too-large`), allocate exactly the
declared length, decompress INTO that buffer refusing on the first overrunning byte, THEN rebuild the
canonical form and hash. The overrun refusal is NOT redundant with the declared-length check, because a
Brotli stream can expand past whatever its container claims and the declared length is the sender's number
too. Write the hostile case explicitly: a 40 KB body whose header declares `uncompressedBytes = 0xFFFFFFFF`
is refused before any allocation.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~PackStore
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests/Store
git commit -m "catalog(store): add the http provider and the verifying cache decorator"
~~~

---

### Task 31: The client fetch loop (medium)

Spec 8.7, 8.8, 9.3, 11 rows 2, 3, 6, 7, 8.

**Files:**

- Create: `KhaozEngine.Catalog/ContentFetchLoop.cs`
- Create: `KhaozEngine.Catalog/ContentFetchProgress.cs`
- Create: `KhaozEngine.Catalog.Tests/Store/ContentFetchLoopTests.cs`

**Interfaces:**

- Consumes: Tasks 29 and 30
- Produces: refusal to reconnect, with no state in which a client holds half a version

**Lift the loop shape from the spike's `ClientFetchSimulator.cs`**, which is the code P4 was measured
against, and drop its token-bucket shaper and its HTTP test server (`TokenBucket.cs`, `PackHttpServer.cs`),
which are benchmark harness and stay in the benchmark.

- [ ] **Step 1: Write failing tests for the six steps.**

1 refused at the door with the server's version and hash. 2 get the manifest from cache or remote, verify,
cache. 3 fail and ask the player to update when `formatGeneration` exceeds the reader's or the local build
is below `minimumClientBuild`. 4 compute `missing` as every chunk hash in the manifest not in the local
cache. 5 fetch each at BOUNDED CONCURRENCY 4, verifying, retrying ONCE against the same source on a
mismatch, then failing that chunk. 6 reconnect when every chunk arrived, otherwise report progress and
retry the missing set with backoff.

Bounded concurrency 4 because a home connection saturates at two or three streams and unbounded parallelism
against a CDN buys nothing.

**Decode is LAZY on the client.** Step 5 stores BYTES. Nothing is decompressed or decoded until a lookup
asks for a row in that chunk, which is what makes the cold start a download budget rather than a decode
budget. Assert a client that reads one item id touches one chunk and leaves the rest compressed.

- [ ] **Step 2: Write the six failure-mode tests of spec 8.8.**

An interrupted fetch caches what arrived and the next attempt recomputes `missing`, with NO resume state
beyond the cache. A truncated chunk fails its hash, is discarded, retried once, then reports
`chunk-fetch-failed` with the client staying at the door. A valid hash over a malformed body means the
PUBLISHER wrote a bad chunk and the client refuses with the decode reason and does not cache it. A cached
chunk gone bad on disk is detected on first use, deleted and refetched. A manifest hash mismatch is
refetched once, then `manifest-hash-mismatch` and the client STOPS, because it cannot tell a bad CDN from a
bad configuration and guessing is worse than stopping. The server's version moving mid fetch needs no
special casing: the client finishes, reconnects, is refused with the NEW hash, and the second fetch
downloads only what differs.

**A partial download never becomes a partial catalog.** The client does not reconnect until every chunk in
the manifest verifies, which is what makes the door comparison a hash equality rather than a negotiation.
Assert there is no code path that returns a usable snapshot from an incomplete fetch.

**The client gets its base URL from configuration, NEVER from the refusal token.** A URL in a refusal token
is a redirect an unauthenticated party controls. Assert the loop takes its base address as a constructor
argument and that nothing parses a URL out of a refusal.

- [ ] **Step 3: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~ContentFetchLoopTests
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests/Store
git commit -m "catalog(fetch): add the client fetch loop and its failure modes"
~~~

---

### Task 32: `ContentStringCatalog`, the layered catalog (medium, GATE for 1.5)

Spec 7.6, 15.7. Contracts 12.3, 12.4.

**Files:**

- Create: `KhaozEngine.Catalog/ContentStringCatalog.cs`
- Create: `KhaozEngine.Catalog/ContentTextIndex.cs`
- Create: `KhaozEngine.Catalog.Tests/Text/ContentStringCatalogTests.cs`

**Interfaces:**

- Consumes: Task 8's `KECT` codec
- Produces: an `IStringCatalog` layered over the game's shipped `.resx` catalog

**`KhaozEngine.Catalog` does NOT reference `KhaozEngine.App`, so read this before implementing.**
`IStringCatalog` and `StringId` live in `KhaozEngine.App`, and `Catalog`'s only dependency is
`KhaozEngine.Primitives`. Adding an `App` reference would widen a `Foundation` package's graph for one
interface. **PLAN CHOICE:** implement `ContentStringCatalog` with the same member shape
(`Get(key)`, `Format(key, args)`) and NO `IStringCatalog` implements clause, plus a `Fallback` delegate
taking the next catalog, so a game adapts it in one line. If the implementer finds `App` already sits below
`Catalog` in the graph, or that `IStringCatalog` has moved, implement the interface instead and say so.
Either way, stop and report if it looks like `Catalog` needs a new dependency: that is a layering decision
the plan does not get to make quietly.

- [ ] **Step 1: Write failing tests.**

**Content is asked FIRST, then the game's catalog, then the standard behaviour of returning THE KEY ITSELF
as a visible non-fatal placeholder.** Content first, because content is the thing that ships without a
client release, so a content string must be able to override a stale shipped one. Assert all three layers
and assert a miss never throws.

`Format` routes through the SAFE formatting behaviour: a malformed translator-authored template falls back
to the UNFORMATTED template rather than taking the frame loop down. A template is content arriving as data
rather than a caller bug, and Gui resolves inside the frame loop with nothing above it to catch.

- [ ] **Step 2: Write the layout tests, which are what P10 measures.**

**A decoded language is the chunk BODY itself plus one index, and no decoded entry at all.** The decode
decompresses into a `byte[]` and KEEPS it, and builds one open-addressed `int[]` holding each entry's byte
offset plus one, hashed on the entry's UTF-8 key and probed by an ordinal span compare. **There is no
`Dictionary<string, string>` and nothing exists as UTF-16 until something asks for it.** Assert both by
reflection over the type's fields.

The resolved-string cache is a DIRECT-MAPPED table of 512 entries keyed on the entry offset, bounded by
construction rather than by a policy, so it cannot grow into the thing the budget exists to prevent. A miss
is one UTF-8 decode over a slice the catalog already holds. Assert the bound and assert a repeat `Get`
returns the same instance.

- [ ] **Step 3: Run the milestone 1.5 gate and commit.**

~~~bash
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~ContentIdentityGateTests
dotnet run -c Release --project KhaozEngine.Benchmarks -- --catalog --definitions 50000 --languages 1 --output ./catalog-p4-p10.json
git add KhaozEngine.Catalog KhaozEngine.Catalog.Tests/Text
git commit -m "catalog(text): add the layered content string catalog"
~~~

Record P4 and P10 from that JSON in the report. **This closes milestone 1.5.**

---
