# KhaozEngine.Catalog

Game-agnostic tunable-content catalog, in the `Foundation` umbrella. Content is authored in a database,
published as immutable content-addressed packs, and loaded into a runtime of arrays indexed by id. This
package is the half every consumer needs: the registry, the byte formats, the digests, the validator, the
pack store seam and the read side. Authoring, publish and the SQL providers live in the opt-in
`KhaozEngine.Catalog.Authoring` and its two provider siblings.

**It takes no third-party dependency at all**, and that is load bearing: a game CLIENT needs the read side
and must never pull a database into its graph. `System.IO.Compression` and `System.Security.Cryptography`
are in box, and the single engine dependency is `KhaozEngine.Primitives`, which is already in every game's
graph and is where the `IRandomSource` seam this catalog's random consumers take lives.

## The rules every type here obeys

- **Integers only on any path a client and a server must agree.** Every stat value, roll, threshold and
  scaled number is an integer. No `float`, no `double`, no `MathF`, and rounding is round half up through
  floor division rather than `Math.Round`.
- **Little endian**, written and read through `System.Buffers.Binary.BinaryPrimitives` with the endianness
  in the method name. `BitConverter` is forbidden, because it is host endian.
- **Decoders never throw.** Bytes arrive from a remote peer, so every decode entry point is total: it
  returns `false` plus a stable reason token, never an exception and never a best-effort partial read.
- **Every digest is domain separated.** SHA-256 under `kec/` with its own sub-domain per digest and
  `ContentHash.SchemeVersion` folded into the prefix, so a head gating on one digest can never accidentally
  agree with a head gating on another.

## Format primitives

- `ContentPackFormat` - the format constants: the four `ushort` format versions (`KECC` chunk, `KECM`
  manifest, `KECR` rule chunk, `KECT` text chunk), the four magics, the engine's own `Generation`, the
  `HashSchemeVersion`, the row and chunk size ceilings (`MaxContentRowBytes` 4096 absolute,
  `DefaultMaxRowBytes` 1024 per type, `MaxChunkUncompressedBytes` 16 MiB), the header widths and the
  compressor settings. A version is bumped and never reused, and a mismatched version refuses the whole
  record.
- `ContentVarint` - the one varint definition in the tree, shared with the instance payload codec. Unsigned
  LEB128, seven value bits per byte, low group first, the high bit set on every byte but the last, at most
  five bytes for a 32 bit value and ten for a 64 bit one. A field declared SIGNED is zig-zag transformed
  first, so 0 is 0, -1 is 1 and 1 is 2, and a negative number does not cost ten bytes. Content ids, chunk
  indices, kind ids, lengths, counts and row counts are declared unsigned and are never zig-zagged.
  Encodings MUST be minimal: `TryRead` rejects `0x81 0x00` with `varint-not-minimal`, a non-terminating
  varint with `varint-overflow`, and a read that would run past the end of the span with `field-truncated`,
  leaving the offset where it found it in every case.
- `ContentHash` - every digest in the pack. `OfChunk`, `OfRuleChunk` and `OfTextChunk` take a UTF-8 domain
  prefix followed by the canonical bytes RAW. `OfServerManifest` and `OfClientManifest` take the canonical
  manifest TEXT, in which every number is formatted through `CultureInfo.InvariantCulture` and every string
  is length prefixed as `"{len}:{value} "` with a bare `"- "` for null (`AppendNumber` and `AppendText`).
  `OfBytesForKind` dispatches on the four-byte magic, so a caller verifying a downloaded file hashes it
  under the right sub-domain and refuses an unknown magic before reading a length.
- `ContentKey` - a content row's string key as the runtime holds it: a slice of the loaded UTF-8 blob
  rather than a string, compared ordinally, materialising a string only on demand for a log line, a console
  response or a validator finding. It hashes over its UTF-8 bytes, so it keys a dictionary directly. The
  character set and the 64 character cap are the VALIDATOR's rules, not the value type's, so an over-long
  or malformed key reaches the validator intact and is reported rather than silently truncated.

```csharp
Span<byte> buffer = stackalloc byte[5];
int written = ContentVarint.Write(buffer, 16384);      // 3 bytes

int offset = 0;
if (!ContentVarint.TryRead(buffer, ref offset, out uint id, out string? reason))
    return Refuse(reason);                              // never throws, always a stable token

string chunkHash = ContentHash.OfChunk(canonicalChunkBytes);
var key = new ContentKey(rowBlob, start, length);       // no string materialised
```

## The registry and the schema

- `ContentTypeRegistry` - the registry of contracts 4.2, per INSTANCE and never a static: registration runs
  once at process start, `Freeze()` closes it at the first pack load (`ContentBoot` calls it at step 6), and
  a later registration throws. Lookup
  is by `ContentTypeId` or by type key, ordinally, and `ByTypeId` is sorted ascending always, so no ordinal
  anywhere depends on the order a host registered in.
- `ContentTypeRegistration` - what one registration was handed, held immutably: the band, the id, the key, the
  codec, the optional per-type validator, the schema, the default visibility, the chunk slots, the row cap,
  the optional id ceiling and the optional load index.
- `ContentRegistrationBand` - which ids a caller is entitled to, `Engine` 1 to 255, `Instances` 256 to 1023
  and `Game` 1024 to 65535. It is an entitlement rather than a capability, and id 0 is reserved forever.
- `ContentTypeId` - the stable numeric type id as a value, with `IsEngine`, `IsInstances` and `IsGame`.
- `ContentFieldSchema` and `ContentFieldEntry` - one type's ORDERED field list, which a row's values are
  parallel to BY INDEX rather than keyed by name, so a codec is a positional walk. An entry carries its name,
  its kind, a reference target, its visibility, whether a live row must carry it and a scaled int's scale.
- `ContentFieldKind` - the seven value kinds of contracts 4.7, numbered durably because the authoring store
  writes the number: `Int`, `ScaledInt`, `Bool`, `KeyReference`, `TagList`, `LocalizedTextKey` and
  `OpaqueBytes`. A `LocalizedTextKey` is a MARKER carrying no value and no bytes.
- `ContentVisibility` - `Client` or `ServerOnly`, per type and per field, and the whole basis of the two
  manifests.
- `IContentRowCodec` and `ContentRowCodecBase` - the only path between a row and its canonical bytes.
  The base class IS the positional walk, driven by the schema: a row body opens with the row's own key,
  every field follows in declared order, an ABSENT optional field writes the zero form of its kind (one
  `00` byte in every case) and a derived marker writes nothing at all. A type subclasses it only to add a
  constraint the generic walk cannot express, checked on both sides so an encoder cannot write a row its own
  decoder refuses.
- `IContentLoadIndex` - a derived table one type builds ONCE at boot step 7b, after the engine's own
  indexes, in type id order, before the validator. It may read another type's rows and may not read another index, and it
  throws to fail the boot closed rather than returning a partial index. `ContentRuntime.BuildLoadIndexes`
  runs them and `ContentRuntime.TryGetLoadIndex` hands one back typed.
- `ContentLoadIndexException` - a registered load index failed, or one was asked for before the step that
  builds them finished. It carries the type and its key, which is what the boot's refusal line names, and it
  keeps the index's own failure as the inner exception rather than flattening it.
- `ContentRegistrationException` - a registration rule of contracts 4.2 to 4.5 or 4.7 was broken, which is a host
  bug at process start and never a content defect.
- `ContentTextKey` - the ONE derivation of a content string's localization key,
  `<type key>.<content key>.<field>` (contracts 12.1). Derived, never authored, never stored, capped at
  `MaxKeyLength` 192.

## The six engine content types

`EngineContentTypes.Register(registry)` registers all six, once, before any pack loads. It carries their
stable ids and keys, plus the two type keys the engine writes down and a GAME registers under,
`equip_profile` and `socket_type`.

- `TagContentType` - `tag`, id 1, the tag vocabulary contracts 4.6 makes content rather than strings. A
  derived name and the console's `sort` order, nothing else.
- `ItemContentType` - `item`, id 2, the item base of spec 3.3: tags, stacking, tradability, value, three
  asset references, the two icon-shot angles, durability, the socket CAP and the late-bound equip profile.
  `IsAssetReference` is the shape it enforces, at most 128 bytes of `a-z0-9_./-`.
- `StatContentType` - `stat`, id 3, contracts 13.1's table. A fixed power-of-ten `scale` with the stored
  integer scaled by it, so there is no float stat and no float modifier anywhere.
- `LootTableContentType` - `loot_table`, id 4, `ServerOnly` at the type level, so the whole family is omitted
  from every client manifest. Two fields: `roll_count` and `tags`.
- `LootEntryContentType` - `loot_entry`, id 5, one weighted row of one table, with a larger chunk and a row
  cap of its own because entries outnumber tables. **`guaranteed` is a field of this type and not of the
  table**, settled by `LootRoller` below: the composition spec 3.5 is written around is a table that drops one
  thing on its own chance AND another out of a weighted draw, which a table-level flag cannot express and which
  would make `roll_count` meaningless on the table that set it.
- `BaseSocketContentType` - `base_socket`, id 6, one socket an item base is authored WITH, in authored order,
  which `item.socket_max` caps rather than describes.

## The four pack formats

Every one of them opens with a four-character ASCII magic and a `ushort` format version, and a version
mismatch refuses the WHOLE record with a reason token. Every decode entry point is total.

- `ContentChunkCodec` - the `KECC` chunk file: a 36 byte header that is never compressed, then a row table of
  `[id varint][flags byte][length varint]` strictly ascending by id and the row bodies concatenated in the
  same order. A row's offset is not stored, it is the running sum. `Encode` produces the canonical bytes, the
  content address and the stored file, storing uncompressed whenever Brotli does not shrink the body.
  `TryReadHeader` takes every refusal derivable from the header alone and allocates nothing, `TryDecode`
  walks the table, and `TryVerify` checks a stored file against an address without decoding its rows.
  `TryDecodeVerified` is the load path's one pass, doing both over a SINGLE decompression and comparing the
  digest before it walks a row, so nothing escapes a buffer that has not been verified.
- `ContentChunk`, `ContentChunkRow`, `ContentChunkHeader` and `EncodedContentChunk` - the decoded chunk with
  its walked table, one row on its way in, the 36 header bytes as a value, and the encode result carrying the
  canonical bytes, the hash and the stored file. `IsRetired(id)` is answered from the table with no row
  decode. `RowBodyAt` and `RowBodyMemoryAt` both slice the chunk's own body rather than copying it, the
  second in the form a caller can store.
- `ContentChunkAssembler` - the publish-side arena: every row body lands in ONE growable buffer and the row
  list is built from offsets at the end, so a chunk of a thousand rows is one buffer rather than a thousand.
  It is itself the `IBufferWriter<byte>` a row codec encodes into.
- `ContentManifestCodec` - the `KECM` manifest FILE, hashes raw 32 bytes in the file and lower hex only as
  text. It refuses the other side's manifest, a client manifest naming a server-only type, a slot count that
  disagrees with the local registry, and any ordering the format declares.
- `ContentManifest`, `ManifestTypeEntry`, `ManifestChunkEntry`, `ManifestLanguageEntry` and
  `ContentManifestSide` - one side's manifest: the version number, the engine format generation, the two
  consumer build ordinals, the rule chunk hash, every type with its chunks and every language.
  `TryMatchChunkHeader` is the one refusal that binds the per-chunk `uncompressedBytes` to reality.
- `ContentManifestText` - the canonical TEXT the two manifest digests are taken over, which is a different
  thing from the file layout: collections are sorted here rather than assumed sorted, so the digest does not
  depend on the order a publisher walked its registry in.
- `ContentRuleChunkCodec`, `RemapRule`, `RemapRuleKind`, `RemapRuleCodec` and `RemapRuleSet` - the `KECR`
  chunk, ONE per manifest, holding the FULL rule list from sequence 1 rather than a delta, because a durable
  page can be arbitrarily old. Four v1 kinds (`ReplacedBy`, `Retired`, `MovedToLegacy`, `StackCapLowered`),
  append only, no delete kind and no delete path. `RemapRuleSet.TryResolve` walks a from-id and a page stamp
  forward to the id a page should carry now.
- `ContentTextChunkCodec`, `ContentTextChunk` and `ContentTextChunkEnumerator` - the `KECT` per-language
  chunk, one per language with its own hash so a client downloads only what it wants. The header is VARIABLE
  because the language tag sits inside it and inside the digest, and the decoded chunk keeps its body as
  BYTES with a non-allocating walk over it, so nothing becomes a string until something asks. The language
  tag, every key and every value are held to strictly valid UTF-8 with no replacement of an invalid
  sequence (`text-language-tag`, `text-key-encoding`, `text-value-encoding`), because a substituted U+FFFD
  is a mojibake string on a player's screen behind a chunk that verified.

The four formats are pinned by the checked-in golden set in `KhaozEngine.Catalog.Tests/Goldens`, which every
decoder is also fuzzed against.

## The read side

- `IContentSnapshot` - the narrow read seam every consumer outside this package is written against, seven
  members: the version number, the identity, a row by id, an id by key, every row of a type, the rule list
  and the retired bit. It carries no authoring concept, no chunk and no mutation. Both read-side holders
  implement it: `ContentSnapshot` is the CANDIDATE shape and `ContentRuntime` is the ACTIVE one.
- `ContentSnapshot` - the immutable holder behind it, rows ordered by id whatever order they arrived in.
- `ContentSnapshotBuilder` - the ONE way a snapshot is made, so publish, boot and a test build one the same
  way. It refuses a programming error and never a content defect: a duplicate id, a duplicate key and a
  malformed key all go in untouched, because each of them IS a finding for the validator to report.
- `ContentRow` and `ContentFieldValue` - the generic row a codec-free consumer sees, its values parallel to
  the schema by index. A value holds a number or bytes, never both, and an absent optional field carries
  `IsAbsent` rather than a sentinel.
- `ContentVersionIdentity` - the version number and its manifest hash, the pair that travels together.
- `ItemRow` - the typed view over the engine `item` type, and the only typed view in the catalog: a
  `ref struct` over the row body with the four hot fields decoded at construction, for the stacking and
  generation paths a field-by-name walk does not budget for.

## The loaded runtime

`ContentRuntime.FromSnapshot(snapshot, registry)` is the one way an active version comes into being, and the
path is one way: reader to snapshot to runtime to holder. The snapshot is the CANDIDATE shape a publish
validates and a test builds by hand. The runtime is the ACTIVE shape a server reads for the rest of the
process, and it implements the same `IContentSnapshot` seam over arrays indexed by id.

- `ContentRuntime` - the loaded active version. Per type it holds a table of parallel arrays: `Offsets` by
  id into one concatenated `Bodies` blob, `Lengths` beside it, a retired BITSET, and the open-addressed
  key-to-id index. A lookup is `Offsets[id]`, one array read, then a span slice. No dictionary on that path,
  no lock, no allocation. `Offsets` is sized to the highest id the version carries plus one and NOT to the
  sum of the type's chunk slots, because a chunk slot is a transport unit the runtime does not inherit.
  There is no `Keys` array either, because every row body already opens with its own key, so an id-to-key
  read is the same slice one varint further in and a key costs only its bucket. `Body`, `Key`, `TryGetItem`,
  `TryGetId` over raw UTF-8 and the seven seam members all answer out of those arrays. The hand-off from the
  snapshot SHARES its per-type body blob rather than copying it, so the two hold one copy of the catalog
  between them. `FromSnapshot` is boot step 7 and derives the four indexes below with it. `BuildLoadIndexes`
  is step 7b and runs whatever the registered types declared, which `ContentBoot` sequences separately
  because it falls between the engine's four and the validator.
- `ContentRuntimeHolder` - the ONE field the active runtime lives in, published at boot step 9 with a
  `Volatile.Write` and read with a `Volatile.Read`, and no lock anywhere. A reader takes the reference once at the top of an
  operation and uses that instance throughout, so a swap cannot hand it a half-old half-new answer, which
  works because a runtime and everything reachable from it is immutable after construction. v1 never swaps
  at runtime, since a new version applies at server restart: the pair exists for a test fixture and for a
  later live-apply phase. An unloaded holder THROWS rather than serving a default catalog, because there is
  no fallback to code defaults anywhere in this package.

## The four derived indexes

`ContentDerivedIndexes` is built eagerly in the runtime's own constructor, so a runtime never exists with its
engine indexes missing. None of the four is built lazily, because each is walked inside gameplay and a lazy
build inside a tick is a latency spike.

- **Key to id, per type.** The open-addressed `int[]` on the type table itself, because it is keyed on a slice
  of that table's own blob. Read through `ContentRuntime.TryGetId`.
- `ContentTagIndex` - tag id to the sorted, distinct ids of the rows carrying it, per content type. Flat
  arrays sliced three deep rather than a dictionary of lists, so a lookup is two searches over small sorted
  runs and hands back a span. It covers EVERY registered type declaring a tag-list field rather than just
  `item`, and it holds retired rows, because the retired bit is the reader's filter and an admin listing wants
  them.
- `ContentFamilyIndex` and `ContentIdBlock` - the block list per family and the two-comparison membership test
  `(id & ~(size - 1)) == base`. **Empty for a version loaded from a pack:** a family and its blocks are
  authoring rows and none of the four pack formats carries them, so the read side has no source and the index
  refuses to invent one out of the id clustering. `FromBlocks` is the seam a declared block list arrives
  through, and it refuses a block that is not a power of two between 16 and 65,536 aligned to its own size.
  Tracked as https://github.com/APKiwiOrg/KhaozEngine/issues/934.
- `ContentLootIndex` and `ContentLootEntry` - per `loot_table`, the resolved entries with the weights PREFIX
  SUMMED, so a weighted draw is one `NextInt(0, total)` and one binary search over an `int[]` with no
  allocation and no per-roll summation. Entries come back in `sort` then id order, a negative weight is clamped
  and the running total saturates, both so the prefix array stays monotonic and searchable. The sums run over
  the NON-GUARANTEED entries only: a guaranteed entry rolls its own chance instead of competing, so it has zero
  width and a pick steps straight over it, which keeps one array and one search. `TotalWeight` is therefore the
  weighted pool's total rather than the sum of every authored weight. A `required_tags` entry resolves at load
  into a candidate array of the live items carrying every listed tag, retired rows excluded.

## The loot roll

`LootRoller(runtime, random)` is the ONE implementation of spec 3.5's composition rule, and it exists because
the rule is the engine's: the spec defined the two types, said how guaranteed entries, weighted picks, nested
tables and tag draws compose, and shipped no code performing it, so every consumer would have written it again
and the first table using both `guaranteed` and `roll_count` would have had two answers.

```csharp
Span<LootDraw> drops = stackalloc LootDraw[16];
var roller = new LootRoller(runtime, random);                 // IRandomSource, by constructor, no default

if (!roller.TryRoll(tableId, drops, out int written))
    // the table had more lines than the span: size up and roll again
```

- **The draw order is the contract.** Every `guaranteed` entry in `sort` order first, each rolling its own
  `chance_bp` independently, then `roll_count` weighted picks over the non-guaranteed entries, each one
  `NextInt(0, total)` plus a binary search over the prefix-summed array. A `nested_table` entry recurses at the
  point it is drawn and a `required_tags` entry draws uniformly from its precomputed candidates.
- **A weighted pick does not roll `chance_bp`.** Winning the draw was its chance, and its share of the pool is
  what that chance IS. `chance_bp` is read for a guaranteed entry and nowhere else. A chance at or above 10,000
  is a certainty and consumes no draw, one at or below zero drops nothing and consumes no draw, so a table of
  certainties never advances the stream.
- **`LootDraw` is `(ItemId, Count, TableId)` and nothing else.** `TableId` names the table the LINE came from,
  which after a nested draw is the nested table, and it is the one thing a caller cannot reconstruct. A game's
  own drop event passes it back through.
- **A destination too small is FILLED**, `Roll` returns the span's length and `TryRoll` reports the overflow, so
  a caller sizes up rather than silently losing drops. The roll stops at the first line that does not fit, so
  nothing is drawn for lines nobody gets and the same seeded source rolled into a bigger span gives the whole
  table.
- **`MaxNestedDepth` is 16**, a hard cap below `KEC0024`'s acyclicity guarantee. A published pack cannot reach
  it, and a roll runs over bytes a pack store handed the process, so a hand-edited pack must not be able to run
  a server out of stack. A nested entry at the cap draws nothing and the rest of the table still rolls.
- **It creates nothing**: no instance, no ground stack, no inventory write, no event, no journal. It reads
  content and a random source and returns numbers, which is what lets a generator take its output as an input
  without the two depending on each other. That is asserted by absence of API, not by a comment.
- **Zero allocation**, which is budget P9 together with under 100 ns for one weighted draw over a 200 entry
  table: it writes into the caller's span, walks the load-time arrays and recurses on the stack.

## Pack store and reader

- `IPackStore` - the content-addressed store of spec 8.1, four members and no more: `ExistsAsync`,
  `GetAsync` (null for absent rather than a throw), `PutAsync` and `ListAsync`. The name IS the content.
- `IPackStorePruning` - the delete path, deliberately separate, so a read-only provider cannot be asked to
  prune and a misconfigured one cannot delete a production pack through the common interface.
- `PackVersionPointer` and `PackDurability` - the two manifest hashes of one published version, which is the
  ONE object in a store not named by its own hash, and how hard a provider works to survive a power cut.
- `FileSystemPackStore` - the local provider: one file per hash under a two-level shard derived from the
  hash itself, written to a temporary name in the same directory and then moved. The version pointer lives
  outside the shard tree under `versions/`, because a shard name is derived from a hash and a version number
  is not one.
- `ContentPackReader` - the ONE reader, shared by the server and the client, which differ only in when they
  call it. `ReadAllAsync` is the server's eager boot path, `ReadRowAsync` is the client's lazy one and
  touches at most the chunk whose slots cover the id, and `ReadChunkAsync` never refetches a chunk it holds.
  Verify comes before decode, always. `ReadManifestAsync` fetches one manifest by hash and checks that its
  canonical text digests back to the name it was fetched under, and the static `TryVerify` dispatches on the
  magic, so a caller hashes an object the way the publisher did rather than guessing. `BuildSnapshot` HANDS
  the decoded chunks over: the snapshot copies every row body into its own blob, so the reader drops them
  and reading rows through the reader afterwards is not a supported mode. The constructor cross-checks every
  manifest type's `chunkSlots` against the local registration and throws on a disagreement, because the
  manifest digest does not cover `chunkSlots` and a registry-free decode leaves it unchecked.
- `ContentManifestRead`, `ContentChunkRead`, `ContentRowRead` and `ContentPackRead` - one attempt each, every
  one carrying a stable reason token rather than throwing. The reasons this type adds (`hash-mismatch`,
  `manifest-hash-mismatch`, `chunk-fetch-failed`, `chunk-type-unregistered`) are FETCH outcomes and are
  outside the decode set on purpose.
- `ContentPackException` - a pack STORE asked to write bytes under a name they do not digest to, or under a
  name that is not a content address at all. It is the one pack failure here that throws, because a bad PUT
  is the publisher's own programming error on the publisher's own machine and not bytes from a peer.

## Validation

`ContentValidator.Validate(candidate, previous, rules, registry)` is the ONE validator, shared by publish,
server boot and every test. It is PURE: it takes its whole world as arguments, reads no store, no file and
no ambient static, mutates nothing, and never throws for a content reason. It ACCUMULATES, so one run
reports every defect rather than the earliest.

- `ContentValidationReport` is `(bool IsValid, IReadOnlyList<ContentFinding> Findings)`, and
  `ContentFinding` is the value `(ContentTypeId Type, int Id, string Code, string Message)`. The CODE is a
  stable token a counter, a test and an operator runbook all key on, so a code is never renumbered and a
  withdrawn one is never reissued.
- `previous` is the base version's snapshot at publish and NULL everywhere else, including at boot. The
  three checks that are statements about a CHANGE rather than about a snapshot need it, so they are skipped
  when it is null and the report carries `KEC0000` naming them. That is a property of the ARGUMENT rather
  than a mode flag, and `KEC0000` is the one finding that leaves `IsValid` true.
- **Five passes, in order, none of them stopping early**: structure, schema, references, visibility and
  codec, then the remap rules. `KEC0001` to `KEC0099` are the engine's own and 1 to 42 are issued.
  `KEC0100` to `KEC0199` are reserved for the item-instances band, which runs INSIDE the sweep after pass 5.
- A per-type `IContentValidator` runs LAST, one per registered type, and may only ADD a constraint. Its
  findings come back as `KEC0040` with its type key on the message, so the token stays stable. A per-type
  validator is untrusted code, so a throw from one is caught and reported as `KEC0040` rather than taking a
  publish down with a stack trace where a finding was expected.
- **What it deliberately does NOT check**: whether a value is sensible, whether a client has the art,
  whether a localization key resolves, and whether a remap rule is a good idea. The owner owns the numbers.

```csharp
ContentValidationReport report = ContentValidator.Validate(candidate, previous: null, rules, registry);
foreach (ContentFinding finding in report.Findings)
    Log(finding.Code, finding.Type.Value, finding.Id, finding.Message);

if (!report.IsValid)
    return Refuse(report);                              // publish writes nothing, boot exits non-zero
```

## The boot

`ContentBoot.RunAsync(options)` is spec 9.5's order, run once at server start, and spec 9.6's twelve
refusals. **It fails closed and it never exits the process**: every refusal comes back as a
`ContentBootResult` carrying exit code 3 and the operator's exact lines, and the HOST writes them and exits.
That is what makes the whole exit table testable in process, and it is why the engine never decides the
shutdown order of a process it knows nothing about. There is no fallback to code defaults anywhere on this
path, because a silent fallback catalog serves content no version names and an outage is at least noticed.

The order, and who owns each step. Step 1, registering every content type, is the CALLER's, and so are step
10, loading the world document, and steps 12 and 13, the connect door and accepting connections. Content
loads before the world and both load before the door opens.

1. **Step 2, the version, from exactly one place.** A version pinned in the SERVER'S OWN CONFIG wins always,
   otherwise the authoring database's pinned version when it is not null, otherwise its active version. A
   server with a config pin and a pack store reads no authoring database at boot at all, which is the
   deployment this design recommends: the authoring database is a TOOLING dependency.
2. **Step 3, the manifest**, through the `versions/<n>` pointer, verified against the name it was fetched
   under AND against the version the boot resolved. A manifest whose embedded number differs means the
   pointer and the pack disagree, and the server would otherwise announce one number at the door while
   serving another version's chunks.
3. **Steps 4 and 5**, a pack generation this build cannot read and a server build the pack will not be
   served by.
4. **Step 6, both type lists, before a single chunk is fetched**, then `Freeze()` on the registry. A
   manifest naming a type this build does not register has no codec for its rows, and a registered type
   absent from the version is the same failure from the other side.
5. **Steps 7 and 7b**, the runtime and its four engine indexes, then every registered `IContentLoadIndex` in
   type id order.
6. **Step 8**, the one validator with `previous` null. **Step 9**, one `Volatile.Write` into the holder.
   **Step 11**, every world-to-content key resolved by KEY against the loaded version.

- `ContentBootOptions` - everything the boot needs, handed in rather than reached for, so it reads no
  ambient static, no environment variable and no file of its own: the registry, the store, the holder, this
  build's server build number, the optional config pin, the optional `IContentVersionDirectory` and
  `IContentVersionPointerSource`, and the world's content keys.
- `IContentVersionDirectory` - the authoring database's pinned and active version reads, which is the only
  thing the boot wants from one. `IContentVersionPointerSource` - the READ half of the version pointer,
  which `FileSystemPackStore` implements and a store that does not is handed separately.
- `ContentWorldKeyReference` - one place a world document names content, as `(source, type key, content
  key)`. The world document never carries a content ID, because ids are allocated by the authoring store and
  a world file naming id 17 breaks the moment a content database is rebuilt from a bundle.
- `ContentBootResult` and `ContentBootRefusal` - the published runtime, or which of the twelve rows stopped
  the boot, the step it stopped at, and the stderr lines. `ExitCode` is 3 for every refusal, deliberately
  distinct from the 2 a consumer already returns for a bad config, so a supervisor script tells a content
  failure from a config failure without parsing text.

```csharp
ContentBootResult result = await ContentBoot.RunAsync(new ContentBootOptions
{
    Registry = registry,                                // step 1 is the caller's, and the boot freezes it
    Store = store,
    Holder = holder,
    ServerBuild = ThisBuild,
    ConfiguredVersion = config.ContentVersion,          // wins always, and null falls to the directory
    WorldKeys = world.ContentKeys(),                    // step 10 is the caller's too
});

if (!result.Success)
{
    result.WriteStandardError(Console.Error);
    return result.ExitCode;                             // 3, and the HOST is what exits
}
```
