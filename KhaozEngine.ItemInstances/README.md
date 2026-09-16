# KhaozEngine.ItemInstances

Game-agnostic per-item instance record, in the `Foundation` umbrella. `KhaozEngine.Items` holds slots of
opaque `(ItemId, Count, InstanceId)` stacks and knows nothing about what an item IS. This package is the
other half of that split: the canonical property payload an individual item carries beyond its definition
id, and the registry that says what every property kind means to a walker that does not know what any kind
means.

It reads in three layers, and the sections below are in that order. The RECORD is the payload, the property
registry, the refusal sets, the `KECQ` quarantine wrapper and the instance id allocator. The CONTAINER is
container codec version 2, the paged container and its capacity gate, the merge rule and the registry-derived
remap pass that brings a stored page forward. The WIRE is the visibility projection every replicated byte
passes through, the one frame page delta built on it and the two byte resync request a client answers with.
The validator, the player-facing strings and the one telemetry call sit under all three.

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
NEWER than the active version is never rewound. `CopyDirtyPagesTo` is what the commit builder asks for: the
dirty pages join whatever commit comes next rather than causing one.

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
means the caller sends the WHOLE PAGE through the fragmenter. Never a second delta frame: two deltas for one
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
takes, and which is what a quarantine wrapper hits by construction.

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

## What this release does not ship

This package is a strong base rather than a partial catalog. What is settled in it is every byte format,
every id space, every ordering rule, the stacking test, the paging shape and the projection every replicated
byte passes through, which are the expensive things to change once data exists. What is absent is breadth,
which is content, plus four named gaps on surfaces that already exist.

| Not here | Where it lands |
|---|---|
| the affix content types and the item generator | spec 20 phase 4, gated on the authoring registry and publish path being real |
| the crafting framework and the content stat evaluator | spec 20 phase 5. `ContainerOperationKind.Craft` is the operation and the page write, and the event BODY is the crafting framework's to encode |
| a READER for the page delta, which ships the encoder alone while the fragmenter ships both halves | [#933](https://github.com/APKiwiOrg/KhaozEngine/issues/933) |
| a per-viewer projection on the FULL PAGE send, which carries the stored bytes and therefore disagrees with the delta about what a non-owner sees | [#932](https://github.com/APKiwiOrg/KhaozEngine/issues/932) |
| the lowered `max_stack` cap on the merge path, which saturates at `int.MaxValue` here and is reported after the fact by validator check 12 | [#924](https://github.com/APKiwiOrg/KhaozEngine/issues/924) |
| enforcement of spec 3.3's per-kind value widths, so the revealed mask is a full `ulong` at every door here | [#917](https://github.com/APKiwiOrg/KhaozEngine/issues/917) |

The journal side of a paged container, the `<container>/p<NN>` section naming, the load path and the batched
commit builder, is `KhaozEngine.ItemInstances.Journal`, a `Server` package. The fragmenter a full page send
falls back to, and the sibling ground component a drop carries, are `KhaozEngine.TileWorld.Netcode`.

The reasoning behind every decision above is `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md`, written
against the shared contracts in `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md`.
