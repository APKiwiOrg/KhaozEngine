# KhaozEngine.Items

Game-agnostic item container kernel. GPU-free, zero third-party dependencies, part of the
`KhaozEngine.Foundation` umbrella.

The engine owns the slot arithmetic and never learns what an item is: there is no catalog, no icon, no equip
slot, no use effect, and no economy rule in this package. A game supplies its own item ids and ONE fact about
them, whether an id stacks, as a predicate. The design twin of `KhaozEngine.Stats`: kernel in the engine,
meaning in the game.

The per-item instance record that rides in a slot is the package ABOVE this one,
`KhaozEngine.ItemInstances`. This one holds the bytes and never reads them.

## `ItemStack`

One slot's contents, an opaque `(int ItemId, int Count, long InstanceId)` record struct. `ItemId` zero is the
empty slot and never a real item. `ItemStack.Empty` is the default value, so a cleared slot and a never-filled
one are the same value.

`InstanceId` is the owned item's durable identity, allocated once and never recycled.

- **Zero is the ABSENCE of an instance**, not a low id, so a plain stack carries 0 forever and costs nothing.
  `HasInstance` is the question to ask.
- **`ItemStack.MergeInstanceId(left, right)` is the id that SURVIVES a merge**: the numerically lower of the
  two, so the merge is commutative and a replay in either order agrees on one id. A side carrying 0 never
  wins, so the other id survives intact. A merge DESTROYS an id, which the journal answers for.

## `ItemSlot`

One slot's WHOLE state: the stack, the instance payload as opaque bytes, and whether those bytes are a
quarantine wrapper rather than a readable instance.

- **Equality is BYTE equality over the payload**, not the reference equality a record struct generates for a
  `ReadOnlyMemory<byte>` field, and `GetHashCode` hashes the bytes so the two agree. That is load bearing
  rather than cosmetic: the stacking rule is a span compare over a canonical payload, and a slot that has
  been through a codec holds a different array with the same bytes.
- **`ItemSlot.MaxPayloadBytes` is 512** and it moves ONE WAY. Raising it is backward compatible, lowering it
  strands items already over it. A QUARANTINED slot is not bound by it, because a wrapper preserves bytes
  that may already have broken the cap.

## `ItemContainer`

A fixed number of slots with the rules stated once:

- **Stack-first adds.** A stackable id tops up the first existing stack before opening a new slot, one stack
  per container wherever possible, saturating at `int.MaxValue` rather than overflowing. A non-stackable id
  occupies one slot per unit. A slot carrying a payload or a quarantine flag is never a top-up target.
- **Honest overflow.** `Add` returns how many units actually entered. A full container answers with a
  remainder instead of throwing or silently dropping, and what to do with the remainder (drop it, refuse the
  pickup, spill to the ground) is a game rule this kernel deliberately does not have.
- **Ordered removes.** `Remove` walks slots first to last, the visible order a player expects.
- **Swaps.** `Swap(a, b)` exchanges two slots outright, which is click-to-move. The payload and the
  quarantine flag travel with the stack.
- **The codec doors.** `SetAt` writes a stack with no stacking rule, for the decoder restoring a state the
  rules already produced, and it sanitises: a zero id or non-positive count writes the empty slot. It also
  CLEARS that slot's payload and quarantine flag, which every door that empties or overwrites a slot owes
  the next reader. `SetSlotAt` is its payload-carrying sibling and `TakeSlotAt` is `TakeAt`'s.

### The two payload predicates

`ItemContainer` takes two OPTIONAL predicates at construction, beside the stackable rule and for the same
reason: the checks they make belong to a decoder in the package above this one, and writing a second varint
reader here is forbidden.

- `payloadCanonical` answers whether an instance payload is canonical. `ItemInstancePayload.IsCanonical` is
  the implementation.
- `quarantineWellFormed` answers whether a quarantined slot's bytes are a well formed wrapper.
  `QuarantineWrapper.Verify` is the implementation.

**Left null, the matching door is SHUT rather than half open.** A container built with no `payloadCanonical`
refuses every non-empty, non-quarantined payload, and one built with no `quarantineWellFormed` refuses every
non-empty quarantined payload.

`SetSlotAt` enforces four invariants, throwing for each, because its only caller is a decoder that has
already validated and a violation is a caller bug rather than bad data. One, an empty stack writes
`ItemSlot.Empty` and clears everything. Two, a payload is at most `ItemSlot.MaxPayloadBytes`. Three, a
non-empty payload needs a non-zero instance id, while an empty payload WITH one is allowed, because a
definition may declare durability and have it at full with nothing else set. Four, a payload that is not
canonical is refused, on every call rather than under a `Debug.Assert`, because a door that only guards on a
developer machine is not a door. Invariants two and four are SKIPPED on a quarantined slot and the wrapper
check stands in for them, because a wrapper is not canonical, is not meant to be, and may be larger than the
cap because the thing it preserves was.

```csharp
public readonly record struct ItemStack(int ItemId, int Count, long InstanceId = 0);
public readonly record struct ItemSlot(ItemStack Stack, ReadOnlyMemory<byte> Payload, bool Quarantined);

public sealed class ItemContainer
{
    public ItemContainer(
        int slotCount,
        Func<int, bool> stackable,
        Func<ReadOnlyMemory<byte>, bool>? payloadCanonical = null,
        Func<ReadOnlyMemory<byte>, bool>? quarantineWellFormed = null);

    public int SlotCount { get; }
    public ItemStack this[int slot] { get; }
    public int FreeSlots { get; }
    public int CountOf(int itemId);

    public int Add(int itemId, int count);      // returns units that entered
    public int Remove(int itemId, int count);   // returns units that left
    public ItemStack TakeAt(int slot);
    public void Swap(int a, int b);
    public void SetAt(int slot, ItemStack stack);
    public void Clear();

    public ItemSlot SlotAt(int slot);                  // a window over bytes the container owns
    public void SetSlotAt(int slot, ItemSlot value);   // the payload bytes are COPIED
    public ItemSlot TakeSlotAt(int slot);
}
```

## `ItemContainerCodec`

The durable and wire form, sparse by slot index, entries ascending, little-endian on every host.

**Byte 0 is the version dispatch and it costs one rule.** Version 1 put a single `byte` at offset 0, so a
reader has to tell a version 1 blob from a later one before it knows how wide the version field is. The
value 1 at byte 0 means the version 1 format, and ANYTHING ELSE means a `ushort` version whose low byte is
that value. The cost is that container codec versions CONGRUENT TO 1 MODULO 256 are never assigned: version
257 is skipped and versions 2 through 256 are free. Assigning 257 would make every stored version 1 bank in
the fleet unreadable.

- `ItemContainerCodec.Version` is the current format version, a `ushort` 2.
- `ItemContainerCodec.Version1` is the legacy single-byte version, the value byte 0 carries on every blob
  written before version 2 existed.
- **`Encode` writes the VERSION 1 format**, which is what this type writes: version byte, `ushort` slot
  count, then one 10-byte entry per OCCUPIED slot (`ushort` slot, `int` item id, `int` count). An empty bank
  costs three bytes.
- **Version 2 is a PAGE**, carrying instance ids, opaque payloads and a content version stamp, and its codec
  is `ItemContainerPageCodec` in `KhaozEngine.ItemInstances`, the package above this one. A version 2 blob
  handed to this type is refused by number rather than guessed at.

`TryDecode(blob, slotCount, stackable, out container)` builds the container on the CALLER's geometry and
rules, refusing a blob that declares a different slot count. `Validate(blob, expectedSlotCount)` is the
quarantine gate for a persistence layer: it names the reason (version, geometry, entry order, a slot out of
range, a non-positive count) or returns null for a well-formed blob. Null or empty input is "no state", not a
fault.

## Usage

```csharp
using KhaozEngine.Items;

// The game's catalog answers the one engine-visible fact.
bool Stackable(int id) => id == Coins || id == Arrows;

var inventory = new ItemContainer(slotCount: 28, Stackable);
int entered = inventory.Add(Coins, 25);            // one stack
inventory.Add(BronzeSword, 1);                     // one slot
int overflow = 3 - inventory.Add(Bread, 3);        // 0 while there is room

byte[] blob = ItemContainerCodec.Encode(inventory);            // version 1: persist or send
ItemContainerCodec.TryDecode(blob, 28, Stackable, out var back);
```

Seating an instance needs the two doors open, and the payload itself comes from one package up:

```csharp
using KhaozEngine.ItemInstances;

var bank = new ItemContainer(
    slotCount: 28,
    stackable: Stackable,
    payloadCanonical: ItemInstancePayload.IsCanonical,
    quarantineWellFormed: QuarantineWrapper.Verify);

bank.SetSlotAt(0, new ItemSlot(new ItemStack(BronzeSword, 1, instanceId), payload, Quarantined: false));

ItemSlot seated = bank.SlotAt(0);
long id = seated.Stack.InstanceId;
```
