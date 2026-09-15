# KhaozEngine.Catalog

Game-agnostic tunable-content catalog, in the `Foundation` umbrella. Content is authored in a database,
published as immutable content-addressed packs, and loaded into a runtime of arrays indexed by id. This
package is the half every consumer needs: the registry, the byte formats, the digests, the validator, the
pack store seam, the read side and the loot roller. Authoring, publish and the SQL providers live in the
opt-in `KhaozEngine.Catalog.Authoring` and its two provider siblings.

**It takes no third-party dependency at all**, and that is load bearing: a game CLIENT needs the read side
and must never pull a database into its graph. `System.IO.Compression` and `System.Security.Cryptography`
are in box, and the single engine dependency is `KhaozEngine.Primitives`, which is already in every game's
graph and is where the `IRandomSource` seam the loot roller takes lives.

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
  once at process start, `Freeze()` closes it at the first pack load, and a later registration throws. Lookup
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
- `IContentLoadIndex` - a derived table one type builds ONCE at boot, after the engine's own indexes, in type
  id order, before the validator. It may read another type's rows and may not read another index, and it
  throws to fail the boot closed rather than returning a partial index.
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
  from every client manifest.
- `LootEntryContentType` - `loot_entry`, id 5, one weighted row of one table, with a larger chunk and a row
  cap of its own because entries outnumber tables.
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
  BYTES with a non-allocating walk over it, so nothing becomes a string until something asks.

The four formats are pinned by the checked-in golden set in `KhaozEngine.Catalog.Tests/Goldens`, which every
decoder is also fuzzed against.

## The read side

- `IContentSnapshot` - the narrow read seam every consumer outside this package is written against, seven
  members: the version number, the identity, a row by id, an id by key, every row of a type, the rule list
  and the retired bit. It carries no authoring concept, no chunk and no mutation.
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
  magic, so a caller hashes an object the way the publisher did rather than guessing.
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
