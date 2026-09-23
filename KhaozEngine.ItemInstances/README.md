# KhaozEngine.ItemInstances

Game-agnostic per-item instance record, in the `Foundation` umbrella. `KhaozEngine.Items` holds slots of
opaque `(ItemId, Count, InstanceId)` stacks and knows nothing about what an item IS. This package is the
other half of that split: the canonical property payload an individual item carries beyond its definition
id, and the registry that says what every property kind means to a walker that does not know what any kind
means.

It reads in layers, and the sections below are in that order. The RECORD is the payload, the property
registry, the refusal sets, the `KECQ` quarantine wrapper and the instance id allocator. The CONTAINER is
container codec version 2, the paged container and its capacity gate, the merge rule and the registry-derived
remap pass that brings a stored page forward. The WIRE is the visibility projection every replicated byte
passes through, the one frame page delta and the whole viewer page built on it, and the two byte resync
request a client answers with.
The validator, the player-facing strings and the one telemetry call sit under those three.

Over them sit the four things that PRODUCE and CHANGE a payload, which are the last four sections. The
CONTENT TYPES are the eighteen rows an author writes and the `KEC0100` band that refuses a bad set of them.
GENERATION is the precomputed candidate tables and the thirteen ordered steps that roll one item.
CRAFTING is the working copy, the fourteen primitives a currency composes in data, and the three standing
rules no currency can opt out of. STATS is the integer evaluator and the builder that turns one item's
payload into the lines it folds.

It sits on `KhaozEngine.Items`, `KhaozEngine.Catalog` (the content ids a payload references, and the one
varint definition in the tree) and `KhaozEngine.Primitives`. It takes no third-party dependency, so a game
CLIENT decodes an item with no database and no journal type anywhere in its graph. The journal SIDE of a
paged container, its section names, its load path and the batched commit builder, is
`KhaozEngine.ItemInstances.Journal`, which is a `Server` package because composing a `JournalCommit` needs
`KhaozEngine.WorldStore`.

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
| `PublicView(registry, payload, level, identified, revealedMask, destination)` | the payload one viewer may see. It DELEGATES to `ItemInstanceVisibility` rather than repeating the rule |

A decode hands back `PayloadField` positions rather than copies, so an unknown kind is kept verbatim by
construction and nothing on the read path copies a body to look at it. `MaxInstancePayloadBytes` is the cap,
which is `ItemSlot.MaxPayloadBytes` rather than a second copy of it, and `MaxFields` is the most fields a
legal payload can hold, which is what a caller sizes its `PayloadField` span to.

## Visibility: one `CanSee`, two projections

`ItemInstanceVisibility` holds the only function in the engine answering "may this viewer see this field".
The replication filter calls it and the tooltip builder calls it, and nothing else does. A tooltip that
computed its own answer is how a client eventually renders something the server never sent, so the one
function is a call-site constraint rather than a convenience.

| Member | Answers |
|---|---|
| `CanSee(kind, viewerLevel, identified, revealedMask)` | the rule, over a registration the caller already holds, which is the replication filter's door |
| `CanSee(registry, kind, viewerLevel, identified, revealedMask)` | the same rule reached by kind id, which is the tooltip builder's door |
| `PublicView(registry, payload, level, identified, revealedMask, destination)` | the payload one viewer may see, as bytes written into the caller's span |
| `OwnerRemainder(registry, payload, identified, revealedMask, destination)` | the exact complement of `PublicView` at `Everyone`, which is what the targeted owner message carries |

**The rule, in order.** `ServerOnly` is never visible to anyone. `OwnerOnly` is visible when the viewer
level is `OwnerOnly`. `Everyone` is visible always. THEN, and only then, the identification gate: a kind
registered with an `identificationMaskBit` is hidden while the item is unidentified and its bit in the
revealed mask is clear, EVEN FROM THE OWNER. That last clause is why unidentified is a mechanic rather than
a fourth visibility level, and it is the whole of the partial reveal: a primitive that sets one bit uncovers
one field, with no format change.

Two things fail CLOSED and both are deliberate. A viewer level of `ServerOnly` sees nothing, because a
viewer has two levels (`Everyone`, and `OwnerOnly` for the item it owns) and `ServerOnly` is a level a KIND
carries. And an UNREGISTERED kind is visible to nobody, because the rule is an if and only if over the
kind's registered visibility and a kind this process cannot classify may well be `ServerOnly` in the build
that wrote it. That is about a projection only: an unknown kind is still kept verbatim in storage and still
survives a decode and rebuild untouched.

**Both projections are a forward pass over the RETAINED RUNS of the input, except at a field that carries a
nested payload.** Fields are already ascending and each is length prefixed, so a filtered payload is a
sequence of copies over contiguous ranges with no decode into values and no re-sort. The levels are
deliberately not monotonic in the kind id (kinds 4, 5 and 6 are owner-only while 7 and 8 are public), so the
run walk is the only correct shape, and that is the measurement behind declining to couple kind ids to
visibility. The output is canonical and decodes, because what is left is still a subsequence of an
ascending, duplicate-free, minimally encoded list. A destination as long as the payload always suffices, and
a destination that is too short answers `-1` with nothing written rather than a truncated view.

**A field whose registered shape nests is REBUILT rather than copied, because its own visibility says
nothing about what is inside it.** Kind 132 is `Everyone`, so a projection that kept or dropped whole
top-level fields shipped the gem in a socket exactly as stored, and a socketed gem's durability and bound-to
reached every viewer including a passer-by reading a ground stack. Each socket entry's nested payload is
projected through the same function at the same viewer level, and the entry's length varint, the entry run
and the field's own length are recomputed innermost first. One level is the whole of it, because contracts
9.5 allows no second, and the walk is derived from the registered shape rather than from kind 132, so a game
kind that declares a nesting slot is projected at both levels too. The remainder does the complement one
level down: a socket's frame is public, so it carries a copy of that frame only to position the owner-only
fields inside, and a socket holding none of them leaves no frame behind. Nothing allocates beyond the
destination span either way, and the walk runs twice, once to total what the viewer may see and once to
write it, so a rebuilt field's length varint is known before a byte is written.

The revealed mask is a `ulong` on every member here. Kind 128's mask is a varint and the shape walk reads a
varint at the full 64 bits, so taking a `uint` would force a narrowing at some call site. Bits above
`InstancePropertyRegistry.MaxIdentificationMaskBit` gate nothing, because the registry refuses to register
one.

**A GROUND item has no owner, and that is a rule rather than an omission.** A drop's entity is the drop,
whose net id is nobody's, so there is no viewer this design calls the owner of a ground stack. A ground
item's public view is `PublicView` at `Everyone` and there is NO owner remainder for a drop. Kind 6
`BoundTo` is owner-only, so it is stripped before the sibling component is written and a passer-by cannot
read who a dropped item is bound to, which is a fact about a PLAYER rather than about an item. Kinds 4 and 5
go with it, which is why the 58 byte reference rare replicates as 54 bytes on the ground: it carries one of
the three owner-only kinds, kind 5 durability, whose whole field is four bytes.

The owner remainder's BYTES live here, beside the projection they complement, so the two cannot disagree.
The MESSAGE KIND stays the game's, because `TileProtocol` reserves the `ushort` kind space to the game and
the engine only caps the frame.

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

`CraftRefusalKind` is the CRAFT set, and it is deliberately not one of the above. The eight payload tokens
are about BYTES that arrived from a peer or a page, and a craft refusal is about an OPERATION a caller asked
for, so the two stay apart and `CraftRefusalKind.PayloadMalformed` is the one place they meet, as the working
copy's answer to a target whose stored bytes do not decode at all.

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
  because contracts 15 declares instance ids unsigned and `Pack(65535, counter)` sets the high bit, so the
  two encodings are different bytes for the same id. Unsigned costs the high node (`Pack(65535, 1)` is ten
  bytes against a zig-zag's seven) and buys node 0, the common case, which stays four bytes up to
  268,435,455 where a zig-zag would cost five. `SizeOf` sizes a page without writing one.

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
- The quarantined bound caps from above only, so the flag over NO payload is refused separately, at both
  doors: `Encode` throws and `TryDecode` answers `page-entry-malformed`. Spec 4.4 says a quarantined entry's
  payload IS the wrapper and spec 12.4 pairs the two, so the flag over zero bytes preserves nothing, nothing
  can rescue it, and `ItemContainer` refuses to seat it. A reader that accepted it would have to choose
  between seating it live, which clears the flag, and dropping the entry.
- `MaxPageBytes` is 2 MiB, the journal's projection section cap, because a page is written as one section.
  The number is COPIED rather than referenced, because the package that declares it is a Server package.
  Nothing realistic approaches it: a hundred entries at the maximum non-quarantined entry size is 53,209
  bytes.

`Encode` is the write half and it THROWS, because the caller has already validated. `EncodedSize` beside it
is a pure sizing query that answers the bytes `Encode` would write and never a verdict, which is why it takes
the slot count and does not read it.
`WriteEntry` and `EntrySize` are one entry on its own, so a page delta can reuse the entry shape without
re-deriving it. `TryDecode` REFUSES rather than throws, answering a stable token, and `Validate` is the same call shaped for
a persistence layer. That token is usually an `ItemContainerPageReason` and not always: a malformed varint
answers with `ContentVarint`'s own token, an over-cap entry payload answers `payload-too-long`, and a version
1 blob the version 1 reader refuses answers with that reader's message, because a refusal belongs to whatever
owns the rule it broke. `PageHeader` and `PageEntry` are what comes back,
and an entry's payload is a WINDOW into the page buffer rather than a copy, so a decode allocates nothing per
entry. `PageSlotInput` is one entry on the way in, and its payload is borrowed rather than copied until the
page is written.

**`Encode` projects nothing, so it is for the server's own reads.** It writes every payload as handed in, which
for a container's own entries is the durable bytes, server-only and owner-only fields and whole quarantine
wrappers included. That is right for the journal's projection section, a store and a snapshot, and wrong for
anything a client receives. A page going to any VIEWER, its owner included, goes through
`EncodeProjected(registry, viewerLevel, pageIndex, firstSlot, slotCount, contentVersion, entries)`, whose
entries are the same `ContainerPageChange` values the page delta takes. Every payload goes through
`ContainerPageProjection.ProjectPayload`, the member the delta projects through, so a page and a delta handed
the same entries carry the same bytes for each
([#1049](https://github.com/APKiwiOrg/KhaozEngine/issues/1049)). The page is spec 4.4's format and decodes
through `TryDecode` like any other: only the payloads differ from the stored page.

## Paged containers, the capacity gate and the merge rule

`PagedItemContainer` is a container held as pages, which is the shape a journal commit can rewrite one page
of instead of rewriting the whole thing. It splits the two concepts `ItemContainer` conflates.

- **`SlotSpace`** is the page geometry, fixed at construction and `PageCount * ContainerPageSlots`. It is an
  ADDRESS space and it never shrinks. `ItemContainerPage.MaxPageIndex` is the largest page a stored header can
  name, which is what bounds `PageCount`.
- **`Capacity`** is a separate mutable integer, the number of OCCUPIED slots a grant may leave behind. It is a
  gate consulted by `Add` and by nothing else, and `IsAtCapacity` is the question it asks. `Occupancy` counts
  what it gates and `FreeSlots` counts the address space, so a container at capacity usually has free slots
  and refuses to open one anyway.

The four capacity rules, which are one consumer's bag model restated as engine behaviour:

1. A grant that opens a NEW slot is refused when occupancy is at or above capacity.
2. A grant that merges entirely into existing stacks is allowed at any occupancy.
3. Lowering capacity below occupancy is LEGAL. The container loads intact, is never trimmed, and is refused
   new slots until occupancy falls. State that already exists is never destroyed to satisfy a number that
   moved under it, which is the generalisation of the over-cap stack policy.
4. Capacity is never read from content. It is a per-owner number the game sets, because it is player
   progression rather than balance data, and nothing in the type's surface names a content type.

**Every page declares the FULL geometry.** Slot space is always the whole multiple, so a 30 slot bag is ONE
full page whose capacity is 30 rather than a page declaring 30 slots. Slot 743 is page 7 slot 43 for every
container in the fleet, a bag that grows from 30 slots to 40 is a capacity edit rather than a re-paging of
stored bytes, and a stored page declaring fewer slots than the geometry is a codec level anomaly for the load
path to refuse. `ContainerPageSlots` itself has ONE home, on `ItemContainerPageCodec`, and the page
references it rather than declaring a second copy.

**Entries are SPARSE and nothing compacts them.** A hole is the absence of an entry and costs zero bytes, so
a dense renumber is not something this container can do by accident.

`ItemContainerPage` holds one page's decoded slots, its content version stamp, its dirty flag and its page
index. Its slots live in an `ItemContainer` of exactly one page's width rather than in a second array, so the
payload doors and their four invariants are the SAME code a whole-container consumer runs.

**Exactly two things dirty a page**: an operation that CHANGED a slot (`Write`, `Take`) and a remap that
changed an id (`ApplyRemap`). Reading never does, a write that leaves the slot holding what it already held
never does, and seating a decoded page (`Seat`, `SeatStamp`) never does either, because that IS the page's
stored state. `ApplyRemap` moves the stamp only when something changed and only upward, because a clean page
claiming a version no stored byte carries would lose the claim on the next load anyway, and a page stamped
NEWER than the active version is never rewound. `CopyDirtyPagesTo` hands the dirty pages out, and they join
whatever commit comes next rather than causing one.

**`PagedItemContainer` is an `IPagedContainerWorkingCopy`**, the narrow door the commit builder in
`KhaozEngine.ItemInstances.Journal` reads and writes a container through
([#1045](https://github.com/APKiwiOrg/KhaozEngine/issues/1045)). It is ten members and no page object:
`PageCount`, `Stackable`, `IsAtCapacity` and `SlotAt`, the three writes `SetSlotAt`, `TakeSlotAt` and
`MarkClean`, and the page reads by INDEX, `IsPageDirty`, `PageContentVersion` and `CopyPageEntriesTo`. A page
is read by index because an `ItemContainerPage` carries write members of its own, so handing one out would be a
second door around the first. The container implements the three page reads explicitly, because `Pages`
already answers them for a caller holding it. A host that shares its containers copy on write implements the
interface itself, runs its ownership check inside the three writes, and opens the builder over that instead.

```csharp
var bag = new PagedItemContainer(
    pageCount: 1,
    capacity: 30,
    stackable: definitionId => catalog.Item(definitionId).Stackable,
    payloadCanonical: ItemInstancePayload.IsCanonical,
    quarantineWellFormed: QuarantineWrapper.Verify);

int entered = bag.Add(potionId, 40);      // merges into one stack, or opens one slot
ItemContainerPage page = bag.PageForSlot(12);
Span<PageSlotInput> entries = new PageSlotInput[page.SlotCount];
int count = page.CopyEntriesTo(entries);  // ready for ItemContainerPageCodec.Encode
```

`InstanceStacking.CanMerge` is the merge rule: the definition matches, the game's predicate says yes, neither
entry is quarantined, and the two payloads are byte identical. Rule 4 is a `memcmp` rather than a structural
comparison ONLY because the payload is canonical, so a decode inside it would be a defect. Two items
differing only in a field neither build understands do not merge, which is the conservative answer. On top of
the four rules, an entry carrying durability (kind 5) or sockets (kind 132) never merges whatever the
predicate says, because a definition can gain either AFTER its items exist and publish only sees the
definitions it publishes. `InstanceStacking.Merge` is the arithmetic and never the rules: the surviving
instance id is the numerically LOWER of the two, so a replay in either order agrees, and the count saturates
at `int.MaxValue` rather than overflowing.

**The stack cap of a lowered `max_stack` is the CALLER's**, applied above this kernel. The container reads no
content, so it saturates at the engine ceiling and the load-time validator is what reports an over-cap count.
Where that rule should live is
[#924](https://github.com/APKiwiOrg/KhaozEngine/issues/924).

## The remap pass

`InstanceRemapPass.Apply` brings one page forward: every rule whose `IntroducedIn` is strictly greater than
the page stamp, in `Sequence` order, in ONE pass. It runs BEFORE the validator, which is what makes a drift
finding mean "no rule covered it".

```csharp
VettedRemapRules vetted = VettedRemapRules.Vet(rules);   // once per container, never once per page
InstanceRemapOutcome outcome = InstanceRemapPass.Apply(
    page, vetted, page.ContentVersion, properties, types, entries, abandonedSlots);
// outcome.EntriesTouched, outcome.IdsRewritten, outcome.BytesDelta, outcome.EntriesAbandoned
// entries now holds the page's entries as the pass left them, ready for the codec
// abandonedSlots names the entries a rule reached and the pass could not move
```

**The rule set is vetted ONCE.** `RemapRuleSet.IsIdempotent` is a nested loop over the whole rule list, about
`n^2 / 2` comparisons, and the set cannot change between a container's pages, so `VettedRemapRules.Vet` does
the walk and the pass takes the vetted handle. The type IS the assertion: there is no "already vetted" flag
for a caller to get wrong, and the doors taking a bare `RemapRuleSet` vet on the way through, so a broken set
earns the same refusal whichever one is used ([#928](https://github.com/APKiwiOrg/KhaozEngine/issues/928)).

**The walk is DERIVED from the property registry.** It visits the entry's own definition id, every id a
registered `InstanceReferenceTarget` names inside every field, and, through a nesting slot, every id inside a
socket's nested payload along with that socket's own contained definition. Nothing is skipped for being
nested: a gem socketed into a sword is rewritten by the same rule that rewrites the same gem lying in a bag
slot, which is what stops an item surviving three publishes invisibly and then quarantining on the day a
player unsockets it. A game kind at or above 1,024 is remapped by declaring its shape and its targets and
nothing else.

**It walks the same descriptors, in the same recursive order, as the validator's checks 6 and 7.** Both read
`Shape` and `References` off the registration, and both resolve a run's targets AFTER recursing into a nested
payload that run carries, so a kind cannot be remapped-but-not-validated or validated-but-not-remapped.

**It RE-ENCODES rather than patching bytes in place**, because a replacement id can change a varint's WIDTH.
A hit recomputes, innermost first, the nested payload's bytes, then the socket entry's nested length, then the
field's length, then the entry's payload length in the page. It also restores canonical order on a list whose
codec declares one (the shipped affix list), because a replacement can move a mod id past its neighbour and a
list that lost its order no longer stacks with its own twins.

| Rule kind | What the pass does to an entry carrying `FromId` |
|---|---|
| 1 `ReplacedBy` | rewrites the id at the entry's own definition and at every depth inside the payload, counts and positions carried over |
| 2 `Retired`, policy `0x01` | nothing: the reference is kept as is, the page is not dirtied and the stamp does not move. The placeholder presentation is the caller's, through validator check 13 |
| 2 `Retired`, policy `0x02` | rewrites to the destination held IN the payload, which is kind 1's behaviour |
| 3 `MovedToLegacy` | rewrites onto the legacy copy, at every depth, exactly as kind 1 does |
| 4 `StackCapLowered` | nothing: `ToId` is 0 and the id does not move. The rule RESOLVES, which is what a caller enforcing the cap reads, and an existing over-cap stack is legal by contract |

**A rule that changes nothing is a SCAN.** The page is not dirtied, its stamp does not move, and no byte is
replaced, which is almost every rule on almost every page. A rule that DOES change something dirties the page
and moves its in-memory stamp, and never lowers it, so a page stamped newer than the active version is left as
it stands. The page is NOT written: the rewrite is lazy and rides the next ordinary commit, and that is safe
because applying the whole ordered set twice produces what applying it once produced.

**A set whose destination is an earlier rule's source is REFUSED.** One pass is enough because the publish
validator is required to reject that shape, and a fixed-point loop here would hide the gap rather than surface
it. An entry the pass cannot rewrite is left WHOLE, its definition id included: a quarantined entry, whose
wrapper preserves bytes verbatim rather than offering them to be read, a payload that does not decode, and a
rewrite whose result the decoder would refuse, which is what a rule naming an id the same item already carries
produces. **An abandoned entry is COUNTED**, in `EntriesAbandoned` and in the caller's slot span, so a rule
that could not be APPLIED is tellable from a rule nobody wrote. Bringing a quarantined entry back is the load
path's unwrap step, in `KhaozEngine.ItemInstances.Journal`, because the page stamp governs a page's live
entries and a wrapper carries its own. A game kind with its own sorted list still cannot ask for the re-sort
([#930](https://github.com/APKiwiOrg/KhaozEngine/issues/930)).

## The page delta and the resync request

`ContainerPageDelta` is the one frame message that says which slots of one page changed and what they hold
now, so a craft costs 73 bytes rather than a 6.9 KB page. It is the OPTIMISATION beside the fragmenter of
`KhaozEngine.TileWorld.Netcode`, not an alternative to it: a cold open and a correction resync both have to
send a whole page, and a delta cannot express "this page is now these bytes".

```
[ContainerId: byte][PageIndex: byte][ChangedCount: byte]

then ChangedCount changes, strictly ascending by slot:
[Slot - FirstSlot: varint] then either
  [0x00]                       the slot is now empty
  [0x01][the entry body]       the 4.4 entry WITHOUT its slot field, projected for this viewer
```

**It MEASURES as it writes and ABANDONS rather than truncates.** Nothing bounds how many slots one operation
changes, so a sort over a hundred slot page produces a delta many times the frame cap. That is not a
truncated message, it is a THROW out of the per-viewer serve loop, which takes the tick down for every player
on the server. So `TryBuild` answers the bytes written, or `-1` when the next change would not fit, and `-1`
means the caller sends the WHOLE PAGE, encoded for that viewer by `ItemContainerPageCodec.EncodeProjected`,
through the fragmenter. Never a second delta frame: two deltas for one
page would have to be applied in order by a client that may have missed the first, which is the reassembly
problem the fragmenter already solves once.

- The budget is `MaxChangeBytes`, 1,017: the 1,024 byte frame cap less the four byte game message envelope
  less the delta's own three byte header. A changed rare slot costs 70 bytes, so FOURTEEN fit in one frame
  and the fifteenth does not. An emptied slot costs two or three, so bytes are not what binds there and
  `MaxChanges` is, at the 255 a byte count field holds.
- The frame cap is COPIED rather than referenced, because `TileProtocol` is a Server package and this one is
  Foundation. `PageSyncFrameBoundTests` in `KhaozEngine.TileWorld.Netcode.Tests` is the one place that sees
  both and holds the copy equal.
- On `-1` the destination's contents are UNSPECIFIED, because the builder writes as it measures rather than
  sizing twice. A short destination simply lowers the budget, so it abandons early rather than overruns.
- `WriteEntryBody` and `EntryBodySize` on `ItemContainerPageCodec` are what write the body, so the delta and
  the page cannot come to write an entry differently.

**The bodies are PER VIEWER, and there is one door.** A change is a `ContainerPageChange`, either
`ContainerPageChange.Emptied(slot)` or `ContainerPageChange.Occupied(entry, identified, revealedMask)`
carrying the item's FULL payload, because the bytes handed in are the stored ones rather than a view somebody
else already filtered. `TryBuild` takes the viewer's level and every payload goes through
`ItemInstanceVisibility.PublicView` before it is written. A caller cannot build a delta that skipped the
filter, which is the point: an owner-only field reaches the owner and nobody else. The same change to the
same rare is 73 bytes to the owner and 69 to everyone else, and the four bytes are the durability field. A
payload that does not project carries NO bytes, which is the same fail-closed direction an unregistered kind
takes. That projection is `ContainerPageProjection.ProjectPayload`, the one member `EncodeProjected` writes
through as well, so the whole page a delta falls back to cannot show a viewer more than the delta would.

**A QUARANTINED entry abandons the delta.** A wrapper never decodes, so the projection has nothing to hand
back, and writing the entry anyway produced the quarantined flag over a payload of zero bytes, which is a
shape no spec defines and which the page codec refuses at both doors. Sending the wrapper's own bytes is not
the other option: they are unprojected by construction, so a non-owner would receive the owner-only fields of
the item inside. `TryBuild` answers `-1` and the caller sends the whole page through the fragmenter, which is
the same rule an oversized change set already takes. That page is `EncodeProjected`'s, and it carries the entry
HOLLOW: a wrapper holding the stored reason and stamped version over no original bytes. It verifies and seats
on the client, so the item is visibly broken there, and the preserved bytes never leave the server.

`ContainerPageSyncRequest` is the other half and the ONE new client-to-server message: two bytes,
`[ContainerId][PageIndex]`, carrying nothing about an item's properties. It is what the client sends when it
cannot apply a delta, and what it sends for each page a journal correction named.

- **A client REFUSES a delta for a page it has not fully received** and sends this instead, so a delta is
  never applied to bytes the client guessed at.
- **On the last chunk of a fragmented page the assembled bytes go through the SAME decoder the server encoded
  with**, `ItemContainerPageCodec.TryDecode`, and a failure quarantines rather than throwing. A client that
  trusted its own reassembly would draw a bank from bytes nothing validated.
- **The server rate limits this at ONE PAGE PER CLIENT PER TICK.** That is a documented server rule rather
  than engine code, because the engine caps the frame while the game owns the message kinds and the tick. It
  bounds the worst case a malicious client can ask for at one page of fragments per tick, the same shape the
  snapshot already costs. A server that serves every request it receives has handed an unauthenticated peer
  an amplifier: two bytes in, about 7 KB out, for as long as it cares to ask.

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
  `InstanceReferenceTarget` descriptors `InstanceRemapPass` walks, in the same recursive order, over the same
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
record that did not resolve is KEPT, unusable, never discarded. `Remapped` is the outcome the pass above
produces, and the vocabulary belongs to the contracts rather than to this package.

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

**Two doors, one emitter.** The pair above takes ONE report, which is one page's sweep.
`Report(reports, loadReasons, streamKey, activeVersion, logger, counter)` takes a whole PAGED container: one
report per page that decoded, plus one reason token per record no report covers (a page quarantined as a unit,
an entry that arrived already wrapped, an entry whose remap was abandoned). It emits one line for the
container and counts every record once. The load tokens ride the same histogram deliberately, because an
operator reading one line wants the whole picture, and an abandoned entry is counted under its own token as
well as under whatever the validator then says about it: the two answer different questions.

## The content types: eighteen rows an author writes

Everything a roll or a craft reads is CONTENT, authored in a database, published through
`KhaozEngine.Catalog` and loaded at boot. This package registers eighteen types for it, in ONE block of ids
the catalog's Instances band reserves, and it still names no mod, no rarity, no currency and no tier
anywhere in engine code.

`InstanceContentTypes.Register(registry, modCandidateTables, craftPlans)` is the one call that registers all
eighteen, mirroring `EngineContentTypes.Register` one package down. It runs ONCE, at process start, before
any pack loads, and a second call on the same registry throws because the ids are already taken. Both index
arguments are optional, and they are the whole of the difference between a server and a client: a host that
rolls items hands in a `ModCandidateTablesIndex`, a host that crafts hands in a `CraftPlanIndex`, and a
process that does neither hands in nothing and pays nothing.

```csharp
var tables = new ModCandidateTablesIndex();          // a host that rolls
var plans = new CraftPlanIndex(operations);          // a host that crafts

InstanceContentTypes.Register(types, tables, plans); // a client passes neither
```

`InstanceContentTypeIds` holds every id and every key as a constant, so nothing reads a number off a
document. **Seven of the eighteen are the types an author thinks in and eleven are their CHILDREN.** A child
row carries a key reference to its parent, and a `sort` wherever its order is meaningful.

| Id | Key | Parent | One row is | Visibility | Chunk slots | Row cap |
|---|---|---|---|---|---|---|
| 256 | `mod` | none | one affix: its `kind` (1 prefix, 2 suffix, 3 to 255 game), its `group_id`, its `legacy` flag and its display `line` | Client | 4,096 | 1,024 |
| 257 | `mod_group` | none | an exclusivity group and its `max_per_item` | Client | 256 | 1,024 |
| 258 | `rarity_rule` | none | how many affixes a rarity permits, its per kind caps, its `name_word_positions`, its `upgrade_from` and optional `display_rgb` | Client | 256 | 1,024 |
| 259 | `unique_template` | none | a fixed item on a `base_id`, with its `name`, its `item_level_min` and its `weight` | Client | 4,096 | 1,024 |
| 260 | `socket_type` | none | what a socket accepts: a `display_format` and a `max_nested_bytes` budget | Client | 256 | 1,024 |
| 261 | `crafting_currency` | none | a named sequence of steps, what it consumes and its `max_steps` | Client | 4,096 | 1,024 |
| 262 | `rare_name_word` | none | one word at one `position` of a composed rare name | Client | 4,096 | 1,024 |
| 263 | `mod_tier` | `mod` | one tier: its `ordinal` and its inclusive item level gate | Client | 16,384 | 512 |
| 264 | `mod_tier_weight` | `mod_tier` | one tier's spawn `weight` against one `tag_id` | ServerOnly | 65,536 | 128 |
| 265 | `stat_line` | `mod_tier` | one stat a tier grants: its `combine`, its inclusive `min` to `max`, its `tag_scope` and its `condition_id` | Client | 32,768 | 384 |
| 266 | `rarity_weight` | `rarity_rule` | one rarity's `weight` against one `tag_id` | ServerOnly | 1,024 | 1,024 |
| 267 | `rarity_kind_limit` | `rarity_rule` | how many of one mod kind above 2 a rarity permits | Client | 1,024 | 1,024 |
| 268 | `unique_line` | `unique_template` | one mod, at one `tier_ordinal`, a template grants | Client | 16,384 | 512 |
| 269 | `unique_socket` | `unique_template` | one socket a template forces, whose `sort` IS its index in kind 132 | Client | 4,096 | 1,024 |
| 270 | `socket_tag_rule` | `socket_type` | one accept or reject `tag_id` of a socket type | Client | 1,024 | 1,024 |
| 271 | `currency_step` | `crafting_currency` | one step: an `operation` and its four parameters | Client | 16,384 | 512 |
| 272 | `currency_guard` | `crafting_currency` | one guard, on the currency's target or on one of its steps | Client | 16,384 | 512 |
| 273 | `rare_name_word_weight` | `rare_name_word` | one word's `weight` against one `tag_id` | ServerOnly | 16,384 | 512 |

The `mod` codec refuses a `kind` outside 1 to 255 on encode and decode. This is the same row-local domain
the `rarity_kind_limit` codec uses for the game-owned kinds above 2.

**The chunk slot count and the row cap are per TYPE rather than the format's defaults**, because the shapes
differ by two orders of magnitude. A `mod_tier_weight` row is three small numbers and there are more of them
than of anything else, so it packs 65,536 to a chunk, while a `rarity_rule` carries a display format string
and there are at most 256 of them ever. **The caps below 1,024 are forced rather than chosen.** The registry
refuses a registration whose worst-case chunk could not load, which is the slot count times the row cap
against the format's 16 MiB chunk ceiling, and the four widest types here would not fit at the default cap of
1,024 ([#959](https://github.com/APKiwiOrg/KhaozEngine/issues/959) is the spec correction that follows).

**Three of the eighteen are `ServerOnly` as WHOLE TYPES**, the three weights, 264, 266 and 273. The client
chunk builder omits whole FIELDS, and a weight buried inside a mixed-visibility row had no way out, so a
client downloads no weight row at all rather than a row with a hole in it.

**`socket_type` registers under the key the engine already writes.** `base_socket.socket_type` points at
`EngineContentTypes.SocketTypeTypeKey`, this band is what registers a type under it, and the late binding
resolves at registry freeze. That is the one key here the engine owns.

**A rarity's display colour is published content.** `rarity_rule.display_rgb` is optional and carries
exactly three RGB bytes. The bundle exports them as six lower-case hex digits such as `000000` for black,
`75b7f0` for blue, or `ffffff` for white. A seven-field row published before this field existed keeps its
original bytes and reads white. The item instance payload stores kind 130's rarity ID, never colour bytes.
`RarityDisplayColors.Over(runtime)` resolves the active client pack once, then `RgbOf(slot.Payload.Span)`
returns a 24-bit RGB integer. No rarity property, an absent colour, an unknown rarity ID, or a malformed
payload reads `0xFFFFFF`. A retired rarity row still supplies its authored colour to an owned item. A game
converts the returned RGB to its own drawing colour type.

```csharp
RarityDisplayColors colours = RarityDisplayColors.Over(runtime);
int rgb = colours.RgbOf(slot.Payload.Span);
```

**Every type states its own field names AND its own schema positions**, as `<Field>Field` and `<Field>Index`
constants on the type (`StatLineContentType.CombineField` is `"combine"` and `StatLineContentType.CombineIndex`
is 3). A positional reader reads the constant rather than counting the schema by hand, which is the thing
that rots silently when a field is inserted. The engine's OWN content types expose the names and not the
positions, which is [#968](https://github.com/APKiwiOrg/KhaozEngine/issues/968).

### The `KEC0100` band

`KEC0100` to `KEC0199` is reserved for these types, so an operator reading a code knows an affix rule failed
rather than a catalog structure rule. `InstanceContentFindings` holds every code the band issues as a
constant, and `All` is the pinned ascending list of them. A code is a STABLE token a counter, a test and a
runbook all key on, so it is never renumbered and a withdrawn one is never reissued.

| Code | Constant | What it refuses |
|---|---|---|
| `KEC0100` | `ParentUnresolved` | a child row whose parent reference does not resolve, or a `currency_guard` naming a `currency_step` of a DIFFERENT currency |
| `KEC0101` | `TierItemLevelRange` | a `mod_tier` whose `item_level_min` is above its `item_level_max` |
| `KEC0102` | `TierOrdinal` | a tier ordinal outside 1 to 255, past the candidate table's packed ceiling, or duplicated within its own mod |
| `KEC0103` | `TierOrdinalMoved` | PUBLISH ONLY. a tier ordinal that moved since the previous published version, or one that was live then and is gone now with no remap rule naming its row |
| `KEC0104` | `StatLineShape` | a `stat_line` whose `stat_id` does not resolve, whose `combine` is not 1, 2 or 3, or whose `min` is above its `max` |
| `KEC0105` | `ModGroupCount` | a `mod_group` whose `max_per_item` is below 1 |
| `KEC0106` | `RarityRuleShape` | a rarity whose counts contradict each other, whose `upgrade_from` chain cycles, or whose present `display_rgb` is not three bytes |
| `KEC0107` | `UniqueLineShape` | a `unique_line` whose mod does not resolve, whose ordinal names no tier of it, or whose mod carries a weight row |
| `KEC0108` | `SocketTagOverlap` | a `socket_type` whose accept and reject tag sets overlap |
| `KEC0109` | `RareNameCoverage` | a word `position` outside 1 to 255, or a rarity that rolls a name position no word of non-zero weight can fill |
| `KEC0110` | `CurrencyStepSet` | a currency carrying more steps than its `max_steps`, declaring one past the v1 ceiling, or carrying two steps with the same `sort` |
| `KEC0111` | `RarityRuleIdMoved` | PUBLISH ONLY. a `rarity_rule` key naming a different definition id than it did in the previous published version |
| `KEC0112` | `WeightBelowZero` | a negative weight on any of the three weight types |
| `KEC0113` | `WeightBucketOverflow` | a weight bucket whose members sum past `int.MaxValue` |
| `KEC0114` | `GenerationTagPositions` | an `item` base whose authored tag list is longer than `ModCandidateTables.MaxGenerationTagPositions` |
| `KEC0115` | `UniqueSocketSort` | two `unique_socket` rows of one template taking the same `sort`, which is a socket list with two readings |
| `KEC0116` | `RarityKindClaimed` | two `rarity_kind_limit` rows claiming one mod kind for one rarity, which is a second answer to one question |
| `KEC0117` | `WeightRowRepeated` | two rows of one weight type carrying the same (parent, tag) pair. The second is SUPPRESSED rather than summed, so its whole weight does not exist at any draw |

`InstanceContentChecks.Run(candidate, previous, rules, findings)` is the band's whole public face. It is
PURE, exactly as the catalog's own sweep is: no logging, no counters, no mutation of the candidate, no
ambient read and no throw for a content reason. It ACCUMULATES, so one run reports every defect rather than
the earliest, and every check is VACUOUS over an empty row set.

**The previous snapshot is an ARGUMENT, not a mode flag.** `KEC0103` and `KEC0111` are statements about a
CHANGE, so they are SKIPPED when `previous` is null, which is every boot and almost every test, and the
catalog's own `KEC0000` is what says a clean report came from a boot rather than from a publish. The remap
rule set arrives the same way, because a publish judges a candidate against the rules as they will STAND,
appended rules included.

**The band hangs off ONE registration**, the lowest id in it. Every check is CROSS TYPE, over a parent and
its children or over rarities, words and tags together, so there is no honest way to split it into eighteen
per-type validators and attaching it to all eighteen would run the whole band eighteen times.
`InstanceContentValidator` is the registered instance, it carries no state and reads nothing ambient, and it
implements `IContentHistoryValidator` so the catalog's pass 6 reaches the overload that carries `previous`.
`KhaozEngine.Catalog/README.md` has the pass 6 half.

## The candidate tables: what one roll reads

`ModCandidateTables` is built ONCE at boot and immutable for the life of the process. **The base count
contributes almost nothing to it, which is the whole trick.** The naive table is keyed by (base, item level),
and at 50,000 bases and 100 levels that is 5,000,000 candidate arrays. A base never enters a table here, only
its authored tag LIST does, so fifty thousand bases cost the signature intern and nothing else.

Three levels, and a roll reads all three as scalars:

- **BANDS** are the intervals between every distinct `item_level_min` and `item_level_max + 1`. Within one
  band no tier's gate changes, so the live tier set is constant and `BandOf(itemLevel)` is a binary search.
- **A BUCKET** is one flat pair of arrays per (tag, mod kind, band): the packed key
  `(mod id << TierBits) | tier ordinal` and the CUMULATIVE weight through that entry. Ascending packed order
  IS (mod id, tier ordinal) order, so a mod's tiers are one contiguous run and deducting a whole mod is one
  binary search and one subtraction.
- **The OVERLAP** is what spec 8.3's first-tag-wins rule DISCARDS, precomputed per (tag signature, kind,
  band, tag position), so a roll subtracts two scalars instead of merging two lists.

`GenerationTagSignature` is the intern of authored tag lists, and
`ModCandidateTables.MaxGenerationTagPositions` is 8, which is what bounds one. The suppression header count is
signatures times bands times kinds times tag POSITIONS, so an unbounded position count would make the build
unbounded, and a publish refuses a longer list with `KEC0114`. The header block is sized by the positions the
version's signatures ACTUALLY carry, so a pack whose widest base lists three tags pays for three.

**Nothing is allocated, memoized or evicted at a roll.** There is no cache, so there is no hit rate, no
eviction policy and no pathological pack that degrades to a merge per roll. A roll reads the two to eight
buckets its base's tags name and nothing else, which is also why its cost does not scale with the size of the
candidate pool.

**Three things fold in at BUILD time rather than per candidate**: the kind, which IS the bucket, the legacy
flag, because a legacy tier never enters a table at all, and the running weight, because a cumulative array
is a binary search and a weight array is a walk.

`ResidentBytes` is the self-reported size of everything a boot holds for good, which is the one budget number
that does not depend on the garbage collector's mood. `ConsistencyFailures` compares each (signature, kind,
band) live count and live weight against the merge the build ran to produce its overlap lists, and it MUST be
zero. **A zero there is not proof the lists are right**: both sides come from the same pass, so it catches a
divergence between the merge and the recording of it and nothing more. The independent check is a second
merge written in the test file rather than shared with this code.

**The build REFUSES rather than clamps.** A tier ordinal past `MaxTierOrdinal`, a bucket past
`MaxBucketEntries`, a union past `int.MaxValue` and a base past `MaxGenerationTagPositions` each throw, which
fails the boot closed. Every one of them is something the `KEC0100` band already refuses at publish, so a
throw here means a version reached a boot without one, and the alternative to throwing is a silently
different probability.

`ModCandidateTablesIndex` is the boot-side half: an `IContentLoadIndex` registered against `mod`, so the
tables are built at boot step 7b with no second pass wired anywhere. It owns the WHOLE of what a roll reads
rather than just the candidate tables. `Tables` is the candidates and `Generation` is a `GenerationTables`,
which carries them plus the content fold and the run ceiling, all functions of the same snapshot. That is
what makes a replay harness's second generator cost one object rather than a second fold, which used to be
tens of milliseconds at benchmark scale.

## Rolling an item: thirteen steps, in order

`ItemGenerator(GenerationTables, IRandomSource, InstanceIdAllocator)` turns (base, item level, source of
randomness) into a canonical payload. It does NOT decide WHICH base drops: that is a loot table, one package
down, and the seam between a loot roll and an affix roll is deliberate.

```csharp
var generator = new ItemGenerator(tables.Generation, random, allocator);

GenerationResult rolled = generator.Generate(new GenerationContext(
    BaseId: bronzeSword,
    ItemLevel: 42,
    ForcedRarityId: 0,              // 0 rolls one against the base's tags
    ForcedUniqueTemplateId: 0,      // a unique is FORCED by the loot table, never rolled here
    Quality: 0));

// rolled.Payload is canonical, rolled.InstanceId is 0 when the payload is empty,
// and rolled.AffixCount below rolled.RequestedAffixCount means the pool ran dry.
```

**It takes its `IRandomSource` in the CONSTRUCTOR and holds it.** A per call source keeps "does this roll"
answerable at the method and loses it at the TYPE, which is the half that matters: without it a caller
anywhere could hand a seeded source to the production generator with nothing in any signature to notice.

The thirteen steps, in the order the draws happen, which IS the contract:

| Step | What it does | Draws |
|---|---|---|
| 1 | opens the base's tag tables for this band, with the overlap already deducted | none |
| 2 | a FORCED unique seats its lines and its sockets and stops | none |
| 3 | resolves the rarity, first-tag-wins over the base's tags, unless the caller forced one | one, and none when forced |
| 4 | rolls the affix count over the rule's inclusive range | one |
| 5 | picks the mod KIND, weighted by the live candidate count of each kind still under its cap | one per pick |
| 6 | excludes the placed mod's whole run of tiers, and its group once the group is at `max_per_item` | none |
| 7 | picks the entry, weighted, over the pool with the overlap and the exclusions SUBTRACTED | one per pick |
| 8 | draws the roll position | one per pick |
| 9 | sorts the affix list ascending by mod id, which is what makes kind 131 canonical | none |
| 10 | rolls one rare name word per name position, weighted against the base's tags | one per position |
| 11 | seats the sockets in AUTHORED order, every one empty. There is NO draw here in v1 | none |
| 12 | encodes the payload through `ItemInstancePayloadBuilder` | none |
| 13 | takes the instance id, and ONLY when the payload is non-empty | none |

**The reproducibility contract is that every draw is a function of the affix COUNT and of nothing else.** A
candidate that is filtered out leaves the pool BEFORE the draw rather than being drawn and rejected, and a
pick whose live pool is EMPTY still consumes both of its draws and discards them. Without those two discards
one item consumes fewer draws than another of the same rarity on the same base, and a seeded session diverges
at the first item whose pool runs dry.

**A collapsed bound takes `IRandomSource.Skip`, and that is not a formality.** `NextInt`'s own contract says
a one-wide range consumes nothing, so a discard written as `NextInt(0, 1)` is no discard at all and a real
draw over a live weight of one costs the stream nothing either. `Skip` advances the stream by exactly one
draw whatever the bound is, so the position after an item is a function of the pick count and never of what
the pool happened to hold.

**Step 12 writes kind 128 explicitly, state 0 and revealed mask 0.** An item carrying no `Identification`
field at all is indistinguishable from an identified one under the visibility function, so the generator,
which is what decides a new item is unidentified, is what writes the field.

`GenerationResult` carries `AffixCount` and `RequestedAffixCount` separately, because an item whose pool ran
dry ends with fewer affixes than the count asked for, which is a legal outcome and is REPORTED rather than
retried. A caller that cannot tell a three affix roll from a six affix roll that ran dry cannot log the
difference, and running dry is the signal that a pack's pool is thinner than its rarity rules assume.

Two members exist for the CRAFT side rather than for a roll, because a second weighted pick would be a second
distribution and there is exactly one of those. `TryDrawAffix` is steps 6 to 8 as one pick over an affix list
that already exists, and `RedrawAffixes` is steps 4 to 9 over one restricted to a kind mask.
`AffixCeiling(rarityId)` answers how many affixes one rarity rule permits, so a craft refuses at the ask
rather than being dropped at the write, and `PresentCeiling` answers how many the pack's WIDEST LIVE rule
permits, which is the seat capacity. **An item can be past that ceiling with nothing wrong.** A rule a later
version RETIRED still has items in the world carrying the affix count it permitted, and `AffixCeiling`
answers 0 for a rarity no live rule names, so `PresentCeiling` is the door that catches them: a craft refuses
`AffixListFull` before any draw, `TryDrawAffix` answers false and leaves its out affix default, and
`RedrawAffixes` places nothing. The seat's own throw stays underneath as the guard beneath that door, and
nothing public reaches it.

**It is NOT reentrant and it is NOT thread safe. One instance per thread, or per executor.** Every working
array is instance state that a roll overwrites and reads back inside one call. A second call that starts
while one is running does not corrupt memory and does not throw: it quietly hands the outer roll the inner
roll's pool, so the item that comes out is a legal-looking item nobody authored. Shared IMMUTABLE tables
across threads are the supported shape, so build a generator each.

`RollPosition.Resolve(position, minimum, maximum)` is contracts 6.4's formula in the ONE place the engine
keeps it. A roll is stored as a `ushort` POSITION rather than as the rolled value, so a published range
change RESCALES an existing item instead of re-rolling it. The generator writes positions, the stat line
builder turns one into a value and a tooltip shows it, and if each carried a copy they would disagree the
first time one was touched.

## Crafting: fourteen primitives a currency composes

A currency is DATA. `crafting_currency` names an ordered list of `currency_step` rows, each naming one
operation and four integer parameters, with `currency_guard` rows as its preconditions. The engine ships the
operations and composes nothing.

`CraftWorkingCopy` is a craft in progress, and it is where the shape lives rather than in a rule a reviewer
enforces:

- **A craft NEVER mutates in place.** `Open` decodes the target, every step applies into a builder, and
  `TryEncode` re-encodes canonically at the end. A refusal at any step discards the builder and the durable
  bytes are untouched, so there is no partial craft and no rollback path to get wrong.
- **It is a `ref struct` because it is opened over the STORED SPAN.** A field nobody wrote is held as a slice
  of those bytes rather than as a copy, so a craft that touches one field of six copies one field.
- **The four powers a game operation must not have are properties of the TYPE.** An unregistered kind has no
  door, because every write asks the property registry first. The cap is checked at EVERY write rather than
  at the encode. Canonical order is the builder's. And there is no `InstanceIdAllocator` anywhere in the
  type, so a working copy provably cannot mint an instance id.
- **A socket list's NESTED payloads are judged at that same write door**, in `CraftSocketRules` rather than
  in the one primitive that seats them, so a game operation handing `SetSockets` its own list meets every
  rule `Socket` meets. Each nested payload is decoded against the property registry rather than judged
  structurally, which is what makes contracts 9.5's ONE LEVEL limit and the socket type's `max_nested_bytes`
  budget properties of the COPY, and standing rule 2 runs over the nested affix lists through the two list
  `CraftStandingRules.CheckAffixWrite` overload, so a legacy entry inside a socketed item cannot be moved
  either. Only the bytes a write BRINGS are judged, so a socket whose nested payload is unchanged is not
  re-judged against content that moved under it.
- An unknown kind the decode preserved survives verbatim, its position in the ordering included.

`CraftPrimitive` is the closed fourteen, and the numbers ARE the authored `currency_step.operation` values,
bounded on both sides of the row codec by `CurrencyStepContentType.MinPrimitiveOperation` and
`MaxPrimitiveOperation`, 1 to 14: `AddRandomMod`, `RemoveMod`, `RerollValues`, `RerollMods`, `SetRarity`,
`AddSocket`, `Socket`, `Unsocket`, `ApplyEnchant`, `RemoveEnchant`, `Repair`, `SetQuality`, `Identify` and
`SetFlag`. Each has
exactly one static apply method on `CraftPrimitives`, split across three files by SUBJECT rather than by line
count: affixes, sockets and scalars.

Three of the vocabularies are closed on purpose, because a closed set is what a counter can bucket, a client
can localize and a test can assert on:

- **`CraftRefusalKind`, 1 to 21.** Why a step said no. There is no message and no exception, because a
  refusal is an ordinary outcome of asking for something the item cannot have. `CraftRefusal` pairs it with
  the ONE number that names what it was about. The FIRST refusal wins, and every later write is a no-op.
- **`CraftGuardKind`, 1 to 15**, bounded the same way by `CurrencyGuardContentType.MinGuardKind` and
  `MaxGuardKind`. A precondition asked of the working copy. Guards are ANDed and there is no OR, no NOT and
  no nesting: a `CraftGuard` carries two INTEGERS and never another guard, so the shape itself is what makes
  an expression tree impossible. A currency that needs an OR is two currency rows. `RarityIsAtMost` is the
  one guard that walks content rather than reading a field: it follows the `upgrade_from` chain down from its
  OWN rule over LIVE rarity rules only, so a rule a later version retired breaks the chain at that link, the
  way every other reader of a rarity rule already gates on the live row. It takes its own NAME rather than
  spec 10.3's "true when" cell, which reads the chain from the item's end and inverts it
  ([#989](https://github.com/APKiwiOrg/KhaozEngine/issues/989)).
- **`CraftSelectorKind`, 1 to 6.** Which entries primitives 2, 3 and 10 act on. `RandomOfKind` is the only
  one that draws, and it draws exactly ONCE, through the same bounded draw the generator uses, so a selection
  with one candidate costs the stream what a selection with nine costs and so does a selection with none.

**Three STANDING rules no currency can opt out of**, which is why they are not in any guard set:

1. **A corrupted item cannot be modified at all.** Kind 1 bit 0 set refuses every write, whatever the guard
   set says, because "corrupted" means "cannot be modified further" and a refusal a currency can FORGET is
   not that. It is seated at the working copy's ONE write door, so it covers every primitive and every game
   operation by construction rather than by fourteen remembered checks.
2. **A legacy affix ENTRY is frozen.** No primitive rewrites any part of an entry whose mod row carries
   `legacy`: not its roll position, not its tier, not its flags. An earlier draft scoped this to primitives
   that ADD a mod, which left `RerollValues` free to draw a fresh position against a legacy tier's preserved
   range until the roll sat at 65,535, turning the mechanism that FREEZES old rolls into a farm for them. A
   legacy entry can still be REMOVED, which is a loss no currency can undo.
3. **A legacy mod ROW can never be added.** The candidate tables leave legacy rows out entirely so a draw
   can never reach one, and this is the same refusal stated where the WRITE is.

`CraftGuardEvaluator` answers a set with `CraftGuardOutcome`, and its `CraftGuardScope` is what decides what
a failure costs. **A TARGET guard is a precondition on the craft, so failing one refuses everything and
consumes nothing. A STEP guard is a condition on one step, so failing one SKIPS that step and the craft
continues**,
which is how a whetstone repairs an item already at quality 20 instead of refusing to touch it. Those are ONE
authored type told apart by one empty `currency_step_id` reference.

`CraftPlanIndex` is the boot-side half: an `IContentLoadIndex` registered against `crafting_currency`, so
every currency is resolved into an immutable `CraftPlan` at boot step 7b and never inside a tick. It also
FREEZES the `CraftingRegistry` it was handed, because a plan resolved before a registration and one resolved
after it would reach different operations for the same authored row.

A plan is its target guard set plus its ordered `CraftPlanStep`s, each carrying the `currency_step` row id a
refusal names, the operation, its `CraftPlanStep.ParameterCount` authored parameters and the guards evaluated
immediately before it. A SELECTOR occupies TWO of those parameter slots, its kind then its parameter, and the
executor is the one place that parameter map lives, so no two readers can drift apart on it.

`CraftExecutor(snapshot, GenerationTables, CraftingRegistry, IRandomSource)` composes it all: the target
guard set once, then every step in authored order with its own guard set evaluated immediately before it.

```csharp
var executor = new CraftExecutor(content, tables.Generation, operations, random);

var copy = CraftWorkingCopy.Open(properties, content, definitionId, storedPayload);
CraftOutcome outcome = executor.Apply(plan, ref copy);

if (!outcome.IsRefused && copy.TryEncode(out byte[] crafted))
{
    // crafted is canonical. The durable bytes were never patched.
}
```

It holds its source by CONSTRUCTOR for the reason the generator does, and the generator it drives for
`AddRandomMod`, `RerollMods` and the `RandomOfKind` selector is built HERE over that SAME source, so a
craft's draws all come off one stream and a seeded session replays. That generator is built over an
allocator that refuses every request, which is the standing guard on a craft never minting an instance id.
Like the generator, an executor is NOT reentrant and NOT thread safe: build one per thread, or per craft
loop.

`CraftOutcome` answers what ran and what was skipped as two `uint` masks rather than a list, allocating
nothing, which is a gameplay call rather than a boot one. That is what fixes `CraftOutcome.MaxMaskedSteps` at
a `uint`'s bit count, which v1's step cap sits well under. **Ran and skipped are not complements**: a refused
craft stops where it stopped, so the steps after the refusal are in NEITHER mask, which is the difference
between a step a guard skipped and one the craft never reached.

`ICraftOperation` is the seam a GAME reaches its own exotic operations through, registered by id at 1,024 or
above in `CraftingRegistry`. An operation that ROLLS takes its own `IRandomSource` in ITS OWN constructor,
which is why `Apply` has no random parameter. It gets the working copy and the same refusal vocabulary and it
gets NO new powers. **An operation this process has no registration for refuses AT USE** with
`OperationUnregistered` rather than failing the boot: a missing registration is a deploy mismatch rather than
a missing content version, and `CraftExecutor.UnregisteredOperationRefusals` is the counter a host watches
for it.

Four ceilings are worth knowing before authoring a currency. A currency carries at most
`CraftingCurrencyContentType.MaxSteps` steps in v1, and a game operation id starts at
`CurrencyStepContentType.FirstGameOperation`, with the gap between the primitives and that number refused on
both sides of the step codec. An affix list holds at most `CraftWorkingCopy.MaxAffixes` entries, because kind
131's count is a byte. `ItemGenerator.MaxMaskedModKind` is 32, because a kind mask is one 32 bit authored
parameter while a mod kind runs to 255, so the smaller number is a CRAFT authoring ceiling rather than a
silent alias, and `ItemGenerator.AllModKinds` is the mask naming every kind, which an ordinary roll uses and
which a craft parameter of 0 resolves to
([#983](https://github.com/APKiwiOrg/KhaozEngine/issues/983)). And `Socket` and `Unsocket` move an item
between two slots, which a four integer `currency_step` row cannot name, so no authored currency reaches
them in v1 and a game that needs one writes an `ICraftOperation`
([#990](https://github.com/APKiwiOrg/KhaozEngine/issues/990)).

## Stats: one fold, integers throughout

`ContentStatEvaluator(IContentSnapshot, IStatConditionRegistry?)` is the integer stat evaluator, with a per
stat inverted index so a read walks only the lines that touch that stat. **It replaces `StatSet` for content
driven stats and does not touch it**: `KhaozEngine.Stats` is a shipped float kernel, it is unchanged by any
of this, and a game uses one or the other for a given stat rather than both.

**Nothing on the read path allocates.** `Value` and `CopyValuesTo` write into a caller span and every working
array is built when a SOURCE is added, which happens on exactly five events: equip, unequip, socket, unsocket
and a craft that rewrites a worn item's payload. An evaluation that allocates per attack is an evaluation
that runs per attack.

**No float anywhere.** Intermediates are `long`, every divide is FLOOR division, and the result is checked
into `int` before the clamp to the stat row's own `min` and `max`. Floor division is written out rather than
left to `/`, which truncates toward zero and so rounds a debuff differently from a buff of the same size:
`floordiv(-9000, 10000)` is -1 and `(-9000) / 10000` is 0, so a plain divide reports no penalty where there
is a whole unit of one. It is never `Math.Round`, which takes a `double` and would put floating point back on
the path. Contracts 6.4's roll formula is a DIFFERENT formula, its numerator is never negative, and it keeps
its `/`.

`StatModifierLine` is the unit the fold reads: a stat id, a `StatCombineKind`, a value, a tag scope and a
condition id. **Flat is summed in the stat's scaled units. Increased is an ADDITIVE basis point pool, summed
once and applied once. More is a MULTIPLICATIVE basis point factor, each applying as its own step.** A line's
tag scope indexes ONE shared tag array the evaluator owns rather than a per line array, so a source with
eight lines hands over one tag span and eight structs.

**The fold order is `(SourceKind, Ordinal, InstanceId, ModifierIndex)`, always, and the order IS the
displayed number.** Integer multiplication with rounding at each step is not associative, so `(a * x) * y`
and `(a * y) * x` can differ by one unit. That is why the key is fixed on `StatSourceKey` rather than left to
whichever order a caller happened to equip in, and why `InstanceId` is in the key at all: the engine's own
four kinds cannot tie on kind and ordinal, and a game source can.

| `StatSourceKey` kind | What it is | Ordinal |
|---|---|---|
| 1 `WornItemKind` | a worn item's base and implicit lines | the worn slot index |
| 2 `AffixKind` | a worn item's affixes | the worn slot, then the index in the sorted list |
| 3 `EnchantmentKind` | its enchantments | the same packing |
| 4 `SocketedItemKind` | an item socketed INTO a worn item | the worn slot, then the socket index in AUTHORED order |
| 5 `ReservedPassiveKind` | RESERVED for passives, unassigned in v1 | |
| 6 `ReservedBuffKind` | RESERVED for buffs and auras, unassigned in v1 | |
| 7 `FirstGameKind` | the first kind a GAME may register, through 255 | the game's own, and it must be deterministic |

`InstanceStatSourceKind` fixes the packing kinds 2, 3 and 4 use, and nowhere else: `EntryOrdinal(wornSlot,
entryIndex)` is `(wornSlot * EntryStride) + entryIndex` and `WornSlotOf` and `EntryIndexOf` take one apart
again. The stride is 256 because an affix list's count is a BYTE on the wire, so slot 0 index 255 is 255 and
slot 1 index 0 is 256 and no two entries of different slots can land on one ordinal, and `MaxWornSlot` is
where the packing runs out of `int`. Changing the
packing changes a DISPLAYED number, so it is durable in the same sense the kinds are.

`IStatConditionRegistry` is where a GAME answers whether one of its own conditions holds. It arrives by
constructor, never through a static, and an evaluator built without one drops every conditional line, which
is the right answer for a game that authors none. **The engine owns ids 0 to `EngineBandMaximum` and defines
NONE of them in v1**, so the whole band stays free and no game id can collide with one nobody has written
yet. It is consulted at RECOMPUTE time only: a cached value asks nothing, so a condition over a moving value
is one the game must dirty the evaluator on through `Recompute`.

**A cached value is keyed by the WHOLE context, so nothing dirties the evaluator to change context.** A read
compares the stored condition mask, the tag count and every tag ELEMENT in order against the context it is
handed, and refolds when any of the three differs, because spec 11.5's dirty events are all SOURCE events and
a context is not one: keyed by stat id alone, a `[fire, spell]` line read under a spell context would be
credited to the melee read of the same stat in the same tick. The compare is deliberately conservative, so
two contexts holding the same tags in a different ORDER are two contexts here and the second refolds, an
order insensitive compare being a sort or a set on a read path that allocates nothing. The stored context
lives in an evaluator owned buffer sized when a source is added, `ContentStatEvaluator.MaxContextTags` wide
per stat, and a context carrying more tags than that is folded UNCACHED every time and stores nothing,
because a number that cannot be compared is a number that cannot be trusted. `CopyValuesTo` goes through the
same rule, one stat at a time.

`StatContext` is what the SITUATION contributes, and it is pure data: the tags in play and a mask a game
condition may read. **Its tags are half of a scope match and never all of it.** A line applies when every tag
in its scope is in the UNION of the context's tags and the target `stat` row's own tags, which is what keeps
the taxonomy out of the call site: "increased fire resistance" needs no `fire` in the context, because the
stat row carries it.

`InstanceStatLines` is the other half, and it is the piece that turns an item into lines. It indexes one
content version's `mod_tier` and `stat_line` rows once at construction, and `Build` walks a payload's kind
131, kind 133 and kind 132 and writes into caller spans, allocating nothing.

```csharp
var lines = new InstanceStatLines(content);
var evaluator = new ContentStatEvaluator(content, conditions);

Span<StatModifierLine> produced = stackalloc StatModifierLine[64];
Span<int> scopes = stackalloc int[64];
Span<InstanceStatSource> sources = stackalloc InstanceStatSource[8];

int written = lines.Build(
    payload, wornSlot: 3, instanceId, produced, scopes, sources,
    out int tagCount, out int sourceCount);

for (int i = 0; i < sourceCount; i++)
{
    InstanceStatSource source = sources[i];
    evaluator.AddSource(
        source.Key,
        produced.Slice(source.LineStart, source.LineCount),
        scopes.Slice(source.TagStart, source.TagCount));
}

int armour = evaluator.Value(armourStatId, new StatContext(contextTags, conditionMask));
```

`MaxTierLineCount` and `MaxTierTagCount` are what a caller sizes those spans from. A `Build` that cannot
finish answers `InstanceStatLines.Refused` and writes nothing, rather than throwing, because the payload came
from a stored page or a remote peer.

**ONE stored roll position drives EVERY line on the tier.** A two line tier resolves both from the same
`ushort` through `RollPosition.Resolve`, so the two move together, and `stat_line.sort` is what makes "the
tier's second line" a stable phrase across a republish.

**Kind 1 is not produced here.** A worn item's base and implicit lines come from the item DEFINITION rather
than from the payload, which carries no field for them. `InstanceStatSourceKind.WornItemOrdinal` states that
kind's ordinal so a caller building them from its own content uses the same rule.

**A gated kind IS emitted, because this is not a visibility boundary.** An unidentified item folds every one
of its affix lines, because the evaluator runs SERVER side over the TRUE payload and a hidden line would make
the item weaker rather than mysterious. A tooltip that wants the player's view asks
`ItemInstanceVisibility.PublicView` first and builds from the projection, which is the same one function the
replication filter uses.

## What this release does not ship

This package is a strong base rather than a partial catalog. What is settled in it is every byte format,
every id space, every ordering rule, the stacking test, the paging shape, the projection every replicated
byte passes through, the eighteen content types a roll and a craft read, and the three engines over them,
which are the expensive things to change once data exists. What is absent is breadth, which is CONTENT: this
package names no mod, no rarity, no currency and no tier, and a game authors every one of them. Beside that
sit three named gaps on surfaces that already exist.

| Not here | Where it lands |
|---|---|
| a READER for the page delta, which ships the encoder alone while the fragmenter ships both halves | [#933](https://github.com/APKiwiOrg/KhaozEngine/issues/933) |
| the lowered `max_stack` cap on the merge path, which saturates at `int.MaxValue` here and is reported after the fact by validator check 12 | [#924](https://github.com/APKiwiOrg/KhaozEngine/issues/924) |
| enforcement of spec 3.3's per-kind value widths, so the revealed mask is a full `ulong` at every door here | [#917](https://github.com/APKiwiOrg/KhaozEngine/issues/917) |

The journal side of a paged container, the `<container>/p<NN>` section naming, the load path and the batched
commit builder, is `KhaozEngine.ItemInstances.Journal`, a `Server` package. The fragmenter a full page send
falls back to, and the sibling ground component a drop carries, are `KhaozEngine.TileWorld.Netcode`.

The reasoning behind every decision above is `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md`, written
against the shared contracts in `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md`.
