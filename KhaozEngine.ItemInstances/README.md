# KhaozEngine.ItemInstances

Game-agnostic per-item instance record, in the `Foundation` umbrella. `KhaozEngine.Items` holds slots of
opaque `(ItemId, Count, InstanceId)` stacks and knows nothing about what an item IS. This package is the
other half of that split: the canonical property payload an individual item carries beyond its definition
id, and the registry that says what every property kind means to a walker that does not know what any kind
means.

It sits on `KhaozEngine.Items`, `KhaozEngine.Catalog` (the content ids a payload references, and the one
varint definition in the tree) and `KhaozEngine.Primitives`. It takes no third-party dependency, so a game
CLIENT decodes an item with no database and no journal type anywhere in its graph.

## The rules every type here obeys

- **The payload is a canonical TLV and the encoder is what makes it canonical.** Fields strictly ascending
  by kind, no kind twice, every varint minimal. The decoder CHECKS all three and refuses.
- **Byte equality IS the stacking rule.** Two occupied slots merge only when the definition matches, the
  game's predicate says yes, neither is quarantined, and the two payloads are byte identical. Nothing
  anywhere decodes two payloads to compare them.
- **Varints are unsigned minimal LEB128 and nothing is zig-zagged.** Content ids, instance ids, kind ids,
  lengths, counts and roll positions are all unsigned, through `ContentVarint`.
- **Little endian** through `System.Buffers.Binary.BinaryPrimitives`, with the endianness in the method
  name. `BitConverter` is forbidden, because it is host endian.
- **No floats on any path**, and no ambient statics: every dependency arrives as a constructor or a method
  argument, the registry included.
- **Decoders never throw.** Bytes arrive from a remote peer or from a stored page, so a decode answers false
  plus a stable reason token from a closed set, never an exception and never a partial read.

## The payload

One field after another, each a kind, a length and a body, with no header and no terminator:

```
[Kind: varint uint16][Length: varint uint32][Body: Length bytes]  *  fields
```

An unknown kind is preserved VERBATIM, keeping its exact bytes and its position in the ordering, so a
client built against content build N can read, display and re-save an item carrying a field only build N+1
knows. Preserved bytes participate in byte equality, so two items differing only in an unknown field do not
stack. That is the conservative answer and the right one: the decoder does not know whether the unknown
field is meaningful.

## Kind ranges and the band

| Range | Owner | Varint cost |
|---|---|---|
| `0` | reserved, never a valid kind | n/a |
| `1` to `127` | the ENGINE's generic instance fields (flags, item level, quality, charges, durability, bound-to, materials, tier) | 1 byte |
| `128` to `1023` | the item-instances fields (identification, unique template, rarity, affixes, sockets, enchantments, rare name) | 2 bytes |
| `1024` to `65535` | the GAME's own fields | 2 to 3 bytes |

`InstancePropertyKind` names every kind the engine and this package assign. `InstancePropertyRegistry`
holds them, and `InstancePropertyRegistry.CreateV1()` returns one with all of them registered.

## Registration

```csharp
InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

registry.Register(
    InstanceKindBand.Game,                 // which range the caller is entitled to
    2048,                                  // the kind id
    InstancePropertyCodec.ShapeOnly,       // rules the shape cannot express, or none
    PropertyVisibility.Everyone,           // how far the field travels
    -1,                                    // the fixed identification mask bit, -1 when not gated
    new InstanceFieldShape(default, InstanceCountWidth.Byte, entrySlots),
    new[] { new InstanceReferenceTarget("recipe", InstanceReferenceSite.Entry, 0) });

registry.Freeze();                         // the pack load path does this, and a later Register throws
```

Registration runs ONCE at process start, before any pack is loaded, and the registry freezes when the first
pack loads. Every refusal is a throw, because each one is a programming error in a registration and none of
them is data.

- **The band is the caller's own declaration of which range it may register into, and a mismatch throws.**
  `Engine` may register 1 to 127, `ScopeB` 128 to 1023 and `Game` 1024 and above. Without it, the first
  sign of a collision is an engine release landing on a kind a game took, months later, with two codecs and
  stored payloads under both.
- **`identificationMaskBit` is a FIXED bit, assigned at registration**, and never the kind's position in
  the ascending list of gated kinds. The revealed mask lives inside kind 128 in every stored payload, so a
  derived index would re-point every partially identified item in the world the moment an engine release
  added a gated kind below 129, with no byte changing. A new gated kind takes the NEXT FREE bit, and a
  duplicate bit throws exactly as a duplicate kind does.
- **A kind is never unregistered and a codec is never replaced.** Either would make two previously distinct
  items stack and destroy one identity.
- **The shape and the reference targets are what make the remap pass and the drift checks DERIVED.** The
  shape says WHERE a value sits in the field's bytes and the target says WHICH content type it belongs to,
  so a walker holding both finds, reads and rewrites every content id in a payload without knowing what any
  kind means. A game kind at or above 1024 that declares its targets gets remap, drift detection and
  quarantine for free. A kind that declares no references is never visited.

The v1 gated bits are fixed: kind 129 `UniqueTemplate` is bit 0, 131 `Affixes` bit 1, 133 `Enchantments`
bit 2 and 134 `RareName` bit 3. Bits 4 to 31 are unassigned.

The types a registration is made of: `IInstancePropertyCodec` is the per-kind rule a shape cannot express,
one total `TryValidate` that is handed a body the shape has already vetted, and `InstancePropertyCodec` holds
the four the engine ships (`ShapeOnly`, `Identification`, `AffixList`, `SocketList`). `InstanceFieldShape`
describes the bytes as a header run and a repeating entry run over `InstanceSlotKind` slots, with an
`InstanceCountWidth` for the entry count. `InstanceReferenceTarget` names a content type key, the
`InstanceReferenceSite` it sits at (header or entry) and its slot index. `InstancePropertyRegistration` is
the whole of one kind's registration, which is what `TryGet(kind, out registration)` and
`TryGetByIdentificationMaskBit` hand back, and `ByKind` is every one of them ascending.
`MaxIdentificationMaskBit` is 31, because the revealed mask is a `uint`.

## Building a payload

`ItemInstancePayloadBuilder` is the WRITE half, and it is the thing that makes the encoding canonical. It
holds its fields strictly ascending by kind whatever order they were added in, refuses the same kind twice,
sorts an affix list ascending by mod id, and writes every varint minimally. Every refusal THROWS, because a
builder is handed values by code rather than bytes by a peer, which is the opposite of the decoder below.

```csharp
byte[] payload = new ItemInstancePayloadBuilder()
    .AddScalar(InstancePropertyKind.ItemLevel, 42)          // kinds 1, 2, 3, 6, 8 and 129
    .AddScalars(InstancePropertyKind.Durability, 55, 60)    // kinds 4 and 5, current then maximum
    .AddByte(InstancePropertyKind.Rarity, 2)                // kind 130
    .AddIdentification(identified: false, revealedMask: 0)  // kind 128
    .AddAffixes(InstancePropertyKind.Affixes, affixes)      // kinds 131 and 133
    .ToArray();
```

`Add(kind, body)` is the opaque door under all of them, which is how an unknown kind is carried and how a
caller writes a body there is no helper for. `AddMaterials` is kind 7, `AddSockets` is kind 132, keeps
AUTHORED order rather than sorting, and refuses a socket whose nested payload is not structurally canonical,
and `AddRareName` is kind 134. `Length` is what `ToArray` will write, so
a caller can size a buffer, and `FieldCount` is how many fields it holds.

Three value types are the list entries those helpers take. `InstanceMaterial` is an input item definition and
how many parts of it went in, which is what makes a materials rebalance a publish rather than a rewrite of
every crafted item. `InstanceAffix` is a mod id, the mod's AUTHORED tier ordinal (1 to 255, never a content
id, because a payload naming one would break the moment an author reordered a mod's tiers), a roll position
and a reserved flags field that is 0 in v1. `InstanceSocket` is a socket type, the contained item's
definition and INSTANCE id, and that item's own nested payload, one level only: the socket carries the
instance id so unsocketing RESTORES that identity rather than minting a new one.

A socket list keeps authored order ON PURPOSE, unlike an affix list: two otherwise identical items whose gems
sit in different sockets do NOT stack, and that is correct, because they are different items.

`ItemInstancePayload` is the READ half and the one place the format lives.

| Member | Answers |
|---|---|
| `TryDecode(registry, payload, fields, out count, out reason)` | the FULL check: the canonical rules, the cap, every declared length, each known kind's shape and codec, and the one level nesting limit |
| `TryDecode(payload, fields, out count, out reason)` | STRUCTURE alone, treating every kind as unknown, so no field body is ever read |
| `Validate(...)` | the same two checks as a reason token or null, which is the surface the validator calls rather than writing a second copy |
| `IsCanonical(...)` | the same as a bool, and the registry-free overload is the shape `ItemContainer`'s door predicate takes. That overload is STRUCTURAL: it reads no field body, so it never recurses into a nested payload, and a host wanting the per-kind checks at the container door passes a registry-bound predicate instead |
| `SequenceEqual(left, right)` | the stacking rule, a byte compare, because the encoding is canonical |
| `Encode(builder, destination)` | writes a builder's fields out and answers the bytes written |
| `PublicView(...)` | a STUB in phase 1 that returns the whole payload. The visibility rule lands with the wire |

A decode hands back `PayloadField` positions rather than copies, so an unknown kind is kept verbatim by
construction and nothing on the read path copies a body to look at it. `MaxInstancePayloadBytes` is the cap,
which is `ItemSlot.MaxPayloadBytes` rather than a second copy of it, and `MaxFields` is the most fields a
legal payload can hold, which is what a caller sizes its `PayloadField` span to.

## Refusals: one closed set per layer

**Decoders never throw for a byte.** Bytes arrive from a remote peer or from a stored page, so a decode
answers false plus a stable token, never an exception and never a partial read. What DOES throw is a caller
error: a destination span too short to hold the answer, or an item built over the cap.

`InstancePayloadReason` is the payload set, EIGHT tokens and no others. The set is closed because things
downstream are keyed on it: a counter is dimensioned by the token and the quarantine wrapper stores a durable
ordinal per token, so a ninth reason is a deliberate additive act rather than something a task does in
passing. `InstancePayloadReason.All` is the tokens in their contract order, which is also the order their
ordinals are numbered.

| Token | Raised when |
|---|---|
| `payload-too-long` | the payload exceeds `MaxInstancePayloadBytes` |
| `field-truncated` | a field's declared length, or a value's bytes, run past the end of the payload |
| `kind-out-of-order` | a kind is not strictly greater than its predecessor |
| `kind-duplicate` | the same kind appears twice |
| `varint-not-minimal` | a varint is longer than its value needs, which would give one value two byte forms |
| `varint-overflow` | a varint does not terminate within its declared width |
| `socket-nesting` | a nested payload carries a field that itself nests, which is the one level limit |
| `field-malformed` | a known kind's bytes do not match its own shape or its own rules |

`ItemContainerPageReason` is the PAGE set, and it is separate because the eight above say nothing about the
page a payload rides in. Two other closed sets reach a caller through the same `reason` parameter and neither
is folded in: a malformed varint answers with `ContentVarint`'s own token, which is more precise about which
byte failed than any page token could be, and a version 1 blob the version 1 reader refuses answers with that
reader's own message, because the refusal belongs to the reader that owns the format.

## Quarantine: the `KECQ` wrapper

`QuarantineWrapper` keeps a failed record's bytes VERBATIM, with the reason it failed and the content version
it was stamped with. Nothing is truncated, nothing is normalized and nothing is re-encoded.

```
[Magic: 4 bytes 'K','E','C','Q']
[Version: uint16 LE, currently 1]
[ReasonCode: byte]
[StampedVersion: varint]
[OriginalLength: varint]
[Original: OriginalLength bytes]
```

**A magic here and none on a payload, and the two are consistent.** A magic is forbidden on a format always
embedded in a larger versioned record, and a payload is such a format. A wrapper is not: it has to be
tellable from a payload at a glance in a hex dump of a page, and by a tool that never saw the entry flag.
Four bytes for that, once per quarantined entry, on a path that is rare by definition. Nothing SNIFFS for the
magic either, because `K` is 0x4B and a perfectly legal property kind varint. The page entry's own flag is
what says a slot is quarantined.

`Verify` and `TryUnwrap` are TOTAL and answer false at every length and for every byte. `Wrap` is the other
half and throws, because it is handed values by code. `Wrap` accepts an original of ANY length, the cap
included, because `payload-too-long` is a reason and refusing to wrap the thing that failed for being too big
would destroy exactly the item the wrapper exists to keep.

**`InstanceQuarantineReason` is the DURABLE ordinal table** and the reason a `ReasonCode` byte means anything
to a later build. Ordinal 0 is RESERVED and never assigned, so a half written wrapper is detectable. Ordinals
1 to 8 are the eight payload tokens above, in `InstancePayloadReason.All` order rather than re-typed, so the
two cannot drift. Ordinals 9 to 13 are the reasons the validator raises: `unknown-definition`,
`unknown-content-reference`, `instance-id-missing`, `instance-id-duplicate` and `stack-not-instanceable`. **A
new reason appends at the next free number and an assigned number is never reused**, even when a reason is
withdrawn. `TryGetOrdinal` and `TryGetReason` are the two directions, and an ordinal this build has not been
given answers false rather than a guess, because it was written by a LATER engine.

Checks 12 and 13 have NO ordinal, deliberately. `over-cap` is tolerated and `definition-retired` produces the
Retired finding, so neither ever writes a wrapper, and giving either a number would invite one to be written.

## Instance ids

`InstanceIdAllocator` mints the durable name an owned item carries: `(node << 48) | counter`, 16 node bits
and 48 counter bits, the counter starting at 1, never recycled, throwing rather than wrapping.

- **The packing is `NetIdAllocator`'s and the counter is NOT.** A net id and an instance id are different
  spaces that must never share a counter. `NetIdAllocator` is a Server package and this one is Foundation, so
  `Pack`, `NodeOf`, `CounterOf` and the four constants are MIRRORED, and a test in the one project that
  references both pins the two producing identical packed values for identical inputs.
- **The high-water mark is persisted BEFORE any id in its block is issued.** `ReservationBlock` is 4,096 and
  the batch size is free, while the ORDER is the contract: persisting after issuing leaves a window in which
  a crash hands the next boot an id it has already put on an item. A boot SKIPS the unissued remainder of the
  previous block, which is free at 2^48 per node and is what makes a reissue impossible.
- **`CanIssue` is false when the persisted store epoch is not the live one**, which is a point-in-time
  restore nobody rotated after, and every issue then refuses. The refusal cannot be added later without a
  durable migration, which is why it ships with the allocator rather than with the journal.
- **`Rotate(newNodeId)` moves the store onto a fresh node and retires the old one.** A boot on a retired node
  throws, because it would reissue ids that node has already handed out.
- `WriteId` and `TryReadId` are the varint pair, UNSIGNED over the int64 bit pattern and never zig-zagged,
  because `Pack(65535, counter)` sets the high bit and is a negative `long`. `SizeOf` sizes a page without
  writing one.

`IInstanceIdStore` is the durable half, taken as a CONSTRUCTOR SEAM rather than an ambient static. The host
owns the implementation because a real one persists into the journal store, and **the engine ships the seam,
the arithmetic and no provider at all**, which is what keeps this package in Foundation with no reference to
a server package. Neither member is async, deliberately: making the seam async would push an await into every
call site that wants an id, and a host whose store is async owns that bridge.

`InstanceIdState` is the whole durable record, and all four fields travel together on purpose: a
point-in-time restore rolls the packed high-water mark, the node id and the retired list back as one, so only
the store epoch can tell a restore apart from an ordinary boot.

`InstanceIdAllocator.NeedsInstanceId(encodedPayloadLength, declared)` is the which-items-get-an-id rule,
pure and static, so a commit path can ask it per item without touching an allocator. An item gets an id when
its encoded payload is non-empty, or its definition declares durability, sockets or any other per-instance
field (`DeclaredInstanceProperties`). **The rule is a property of the ITEM rather than of the definition**,
which is why the payload length comes first and is enough on its own: a definition gaining a per-instance
property later does not reach back into every stored copy.

## Container codec version 2: the page

`ItemContainerPageCodec` is the version 2 format, a page of a container, sparse by slot, carrying an instance
id, an opaque payload and an entry flag per occupied slot, under a header that declares which page it is and
which content version it was last brought up to date with.

```
[Version: uint16 LE, 2]
[PageIndex: varint]
[FirstSlot: varint]
[SlotCount: uint16 LE]
[ContentVersion: varint]
[EntryCount: varint]

then EntryCount entries, strictly ascending by slot:
[Slot - FirstSlot: varint][Flags: varint][DefinitionId: varint][Count: varint]
[InstanceId: varint over the int64 bit pattern][PayloadLength: varint][Payload: PayloadLength bytes]
```

**Byte 0 dispatches**, exactly as `ItemContainerCodec.Version` describes it. The value 1 runs the version 1
path, which seats every entry with instance id 0, an empty payload and the quarantined flag clear, and takes
page stamp 0, which is older than every published version. Anything else is a `ushort` version, which must be
2.

- `ContainerPageSlots` is 100 rather than 128, so slot 743 is page 7 slot 43 and an operator reading a
  section name can do the arithmetic in their head.
- `FirstSlot` is redundant against `PageIndex` ON PURPOSE. It costs two bytes per page and it is what catches
  a page written into the wrong section. `SlotCount` is bounded by the caller's geometry in the same breath
  and under the same `page-slot-origin` token, so a page cannot declare more slots than the container it is
  read into holds. That bound is ONE SIDED: a short last page is legal, so an equality would refuse a
  container whose slot count is not a whole number of pages.
- `EntryFlagQuarantined` is bit 0 of an entry's flags. Bits 1 to 31 are reserved and 0 in v1. The flag is
  what says a payload is a wrapper, rather than a sniff for the `KECQ` magic.
- The two payload bounds contradict on purpose. A NON-quarantined payload is capped at
  `ItemSlot.MaxPayloadBytes`, and a quarantined one is bounded by `MaxPageBytes` instead, because a wrapper
  may by construction be larger than the cap the thing it preserves broke.
- `MaxPageBytes` is 2 MiB, the journal's projection section cap, because a page is written as one section.
  The number is COPIED rather than referenced, because the package that declares it is a Server package.
  Nothing realistic approaches it: a hundred entries at the maximum non-quarantined entry size is 53,209
  bytes.

`Encode` is the write half and it THROWS, because the caller has already validated. `EncodedSize` beside it
is a pure sizing query that answers the bytes `Encode` would write and never a verdict, which is why it takes
the slot count and does not read it.
`WriteEntry` and `EntrySize` are one entry on its own, so a page delta can reuse the entry shape without
re-deriving it. `TryDecode` REFUSES rather than throws, answering a `ItemContainerPageReason` token, and
`Validate` is the same call shaped for a persistence layer. `PageHeader` and `PageEntry` are what comes back,
and an entry's payload is a WINDOW into the page buffer rather than a copy, so a decode allocates nothing per
entry. `PageSlotInput` is one entry on the way in, and its payload is borrowed rather than copied until the
page is written.

## The validator

`InstanceValidator` sweeps a decoded container through thirteen checks and reports everything it found.

**It is PURE.** No store read, no ambient state, no logging, no counter and no throw for a content reason. A
throw from it is a bug in the validator. The caller logs, counts and quarantines.

**Two doors over ONE sweep.** `Validate` takes a whole decoded page, which is exactly what
`ItemContainerPageCodec.TryDecode` hands back, and is a loop over `ValidateEntry` plus the two answers that
are container wide: check 10 and the report. `ValidateEntry` is the standalone door for a caller holding one
item without a container, and it runs every check except 10.

The sweep STOPS at the first quarantine, because every later check would be reading bytes already shown to
mean nothing, and it RUNS PAST a policy finding, because those change nothing about how the bytes are read.

| # | What it checks | Token | Outcome |
|---|---|---|---|
| 1 | the three canonical rules: strictly ascending kinds, no kind twice, every varint minimal | `kind-out-of-order`, `kind-duplicate`, `varint-not-minimal`, `varint-overflow` | Quarantined |
| 2 | every declared field length lies inside the payload | `field-truncated` | Quarantined |
| 3 | a known kind's bytes match its registered shape and its own codec | `field-malformed` | Quarantined |
| 4 | a nested payload carries no field that itself nests, the one level limit | `socket-nesting` | Quarantined |
| 5 | the payload is at most `MaxInstancePayloadBytes` | `payload-too-long` | Quarantined |
| 6 | the entry's own definition resolves, and so does a socket's contained definition at any depth | `unknown-definition` | Quarantined |
| 7 | every content id a registered `InstanceReferenceTarget` names resolves, at any depth | `unknown-content-reference` | Quarantined |
| 8 | an affix entry's `(mod id, tier ordinal)` pair names a LIVE tier | `unknown-content-reference` | Quarantined |
| 9 | a non-empty payload sits on an entry whose instance id is not 0 | `instance-id-missing` | Quarantined |
| 10 | no two entries in one page carry the same instance id | `instance-id-duplicate` | Quarantined, EVERY sharer of the id, the first included |
| 11 | an entry carrying kind 5 or kind 132 is not a stack of several | `stack-not-instanceable` | Quarantined |
| 12 | the count is not above the definition's cap | `over-cap` | Valid, tolerated by contract and counted |
| 13 | the definition, or a socket's contained definition, is not retired | `definition-retired` | Valid, shown through the retired placeholder |

Four of those rows deserve their reason spelled out.

- **Checks 1 to 5 are ONE call**, because the payload decoder already IS them. A second copy of them here is
  the thing that drifts, so the sweep calls `ItemInstancePayload.TryDecode` and maps its token back to the
  row that owns it.
- **Checks 6 and 7 are DERIVED from the property registry** rather than from a list here, walking the same
  `InstanceReferenceTarget` descriptors the remap pass will walk, in the same recursive order, over the same
  nested payloads. A kind cannot be remapped-but-not-validated or validated-but-not-remapped, and a game kind
  at or above 1,024 gets drift detection by declaring its shape and nothing else. Check 8 stays HAND WRITTEN,
  because a tier ordinal is not a content id: it is a key INTO the row check 7 already resolved.
- **Check 10 quarantines every sharer of a duplicated id, the first included.** Which of them is the original
  is unknowable from the bytes, and leaving one usable would be choosing arbitrarily in favour of whoever
  duplicated it. Instance id 0 is what every plain stack carries, so it is not an identity and cannot
  collide.
- **Check 13 is not a fourth outcome.** The bytes are not wrapped, the entry still decodes, the container
  still loads, and what changes is the presentation and the refusals that ride with it.

The two POLICY tokens of rows 12 and 13 live in `InstanceValidationReason` rather than beside the quarantine
ones, exactly because neither has a durable ordinal and neither ever writes a wrapper.

**An entry whose quarantined flag is set carries a WRAPPER rather than a payload.** A caller re-validating one
unwraps it through `QuarantineWrapper.TryUnwrap` first and hands the ORIGINAL bytes in. That is how the first
load after a missing remap rule lands restores the item exactly.

`InstanceValidationOutcome` has THREE members and there is no fourth. In particular there is no "dropped": a
record that did not resolve is KEPT, unusable, never discarded. `Remapped` is here from the start because the
vocabulary belongs to the contracts rather than to this package, and **no phase 1 path produces it**, because
the remap pass ships with the pages.

`InstanceValidationReport` is the only thing the sweep produces. `Findings` is every `InstanceValidationFinding`
in slot order with the container-wide ones last, `TryGetQuarantine(slot)` is what a caller writes a wrapper
from (at most one per slot, because the entry sweep stops at its first), `IsRetiredAt(slot)` is check 13's
presentation question, `ReasonCounts` is descending by count then ordinally by token so the same run reads the
same on every machine, and `DominantReason` is the one the log line names. `QuarantinedRecords` is how many
times the counter is incremented. The two POLICY tokens are NOT in `ReasonCounts`, because the counter is
dimensioned by reason CODE and neither of them has an ordinal, so neither is ever an alert.

## Player-facing strings

Three placeholder keys are engine owned and fixed. The engine ships the keys and no translation, and
nothing anywhere invents a literal in their place.

| `StringId` | Shown for |
|---|---|
| `khaoz.item.quarantined` | an item whose payload could not be read and is held verbatim |
| `khaoz.item.retired` | an item whose definition or a row it references has been retired |
| `khaoz.item.unidentified` | a gated field the revealed mask does not yet reveal |

`InstanceValidationStrings` is where they live, `All` is the three of them, and that is the whole of what this
package contributes to a text catalog. They are prefixed `khaoz.` deliberately, because they are ENGINE
strings rather than content rows, so the derived content key grammar does not name them. They resolve through
the catalog's layered string catalog, which is a later milestone: nothing here resolves anything.

A reason code and a stamped version are NOT player text and are never formatted into these strings. They
belong in the one log line below.

## Telemetry: one line, one counter

`InstanceValidationTelemetry.Report(report, streamKey, logger, counter)` is the ONE named place a finished
report becomes side effects. It exists as a member rather than as a paragraph in a document, because the
validator is pure, so the emission has to live somewhere and one named place beside it is what stops a second
emitter with its own wording.

- **ONE line per CONTAINER**, at warning level, under the `ContentValidation` category. A container that
  fails wholesale would otherwise emit a hundred identical lines, which is how an operator learns to filter
  the category out. `Line(report, streamKey)` is the text on its own, for a test: the stream key, the page,
  how many of how many records quarantined, the dominant reason, the stamped and active versions, and the
  per-reason counts. It NEVER carries the payload bytes or a raw account id.
- **One counter increment per RECORD**, because a counter is what a dashboard reads and a log line is what a
  human reads. `QuarantinedRecordsCounter` is the name, dimensioned by content type id and reason code.
- The counter arrives as an `Action<int, string>` delegate rather than a metrics abstraction invented on the
  way past, because the engine has no counter seam of its own. Null counts nothing.
- The logger arrives as an `ILogger` ARGUMENT, obtained under `LogCategory` through `LogManager.GetLogger`
  rather than through the ambient facade. Null emits no line.
- Nothing at all is emitted when nothing was quarantined, which is why checks 12 and 13 are never an alert.

## What phase 1 does not ship, and where it lands

This package is a strong base rather than a partial catalog. What is settled in it is every byte format,
every id space, every ordering rule and the stacking test, which are the expensive things to change once
data exists. What is absent is breadth, which is content.

| Not here | Where it lands |
|---|---|
| paging (`PagedItemContainer`, `ItemContainerPage`, the section naming) | `docs/superpowers/plans/2026-09-15-item-instances-phase2-3.md` |
| the registry-derived remap pass, which is what produces the `Remapped` outcome | the same plan, with the pages |
| the journal commit path (`ContainerCommitBuilder`) | the same plan |
| the wire: the fragmenter, the ground component, the page delta, the owner remainder, and a real `PublicView` | the same plan |
| the affix content types and the item generator | spec 20 phase 4, gated on the authoring registry and publish path being real |
| the crafting framework and the content stat evaluator | spec 20 phase 5 |

The reasoning behind every decision above is `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md`, written
against the shared contracts in `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md`.
