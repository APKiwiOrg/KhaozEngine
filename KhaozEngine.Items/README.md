# KhaozEngine.Items

Game-agnostic item container kernel. GPU-free, zero third-party dependencies, part of the
`KhaozEngine.Foundation` umbrella.

The engine owns the slot arithmetic and never learns what an item is: there is no catalog, no icon, no equip
slot, no use effect, and no economy rule in this package. A game supplies its own item ids and ONE fact about
them, whether an id stacks, as a predicate. The design twin of `KhaozEngine.Stats`: kernel in the engine,
meaning in the game.

## `ItemStack`

One slot's contents, an opaque `(int ItemId, int Count)` record struct. `ItemId` zero is the empty slot and
never a real item. `ItemStack.Empty` is the default value, so a cleared slot and a never-filled one are the
same value.

## `ItemContainer`

A fixed number of slots with the rules stated once:

- **Stack-first adds.** A stackable id tops up the first existing stack before opening a new slot, one stack
  per container wherever possible, saturating at `int.MaxValue` rather than overflowing. A non-stackable id
  occupies one slot per unit.
- **Honest overflow.** `Add` returns how many units actually entered. A full container answers with a
  remainder instead of throwing or silently dropping, and what to do with the remainder (drop it, refuse the
  pickup, spill to the ground) is a game rule this kernel deliberately does not have.
- **Ordered removes.** `Remove` walks slots first to last, the visible order a player expects.
- **Swaps.** `Swap(a, b)` exchanges two slots outright, which is click-to-move.
- **The codec door.** `SetAt` writes a slot with no stacking rule, for the decoder restoring a state the
  rules already produced. It sanitises: a zero id or non-positive count writes the empty slot.

```csharp
public readonly record struct ItemStack(int ItemId, int Count);

public sealed class ItemContainer
{
    public ItemContainer(int slotCount, Func<int, bool> stackable);

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
}
```

## `ItemContainerCodec`

The durable and wire form: version byte, `ushort` slot count, then one 10-byte entry per OCCUPIED slot
(`ushort` slot, `int` item id, `int` count), ascending by slot, little-endian on every host. Sparse, so an
empty bank costs three bytes.

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

byte[] blob = ItemContainerCodec.Encode(inventory);            // persist or send
ItemContainerCodec.TryDecode(blob, 28, Stackable, out var back);
```
