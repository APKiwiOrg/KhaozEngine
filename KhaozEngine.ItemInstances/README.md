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

## Player-facing strings

Three placeholder keys are engine owned and fixed. The engine ships the keys and no translation, and
nothing anywhere invents a literal in their place.

| `StringId` | Shown for |
|---|---|
| `khaoz.item.quarantined` | an item whose payload could not be read and is held verbatim |
| `khaoz.item.retired` | an item whose definition or a row it references has been retired |
| `khaoz.item.unidentified` | a gated field the revealed mask does not yet reveal |
