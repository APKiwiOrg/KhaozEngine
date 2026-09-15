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
