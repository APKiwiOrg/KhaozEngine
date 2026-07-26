# KhaozEngine.Stats

Game-agnostic layered stat computation for equipment, skills, and buffs. GPU-free, zero third-party
dependencies, in the `Foundation` umbrella (usable by clients and headless servers alike).

The engine owns the channels, the fold, and the recompute. It never learns what a channel means: there is no
stat identity, no enum, no balance constant, no item, no equipment slot, and no derivation between channels
in this package. Your game defines its own enum and casts it to the `int` channel index.

## The fold

`StatSet` holds a `Base` value per channel plus any number of named `StatSourceId` contributions, and
computes one formula per channel:

`Value(c) = (Base(c) + sum of Flat over all sources) * max(1 + sum of Percent over all sources, MinimumScale)`

```csharp
public readonly record struct StatModifier(int Channel, float Flat, float Percent);
public readonly record struct StatSourceId(int Value);

public sealed class StatSet
{
    public StatSet(int channelCount, float minimumScale = 0f);

    public int ChannelCount { get; }
    public float MinimumScale { get; }
    public int SourceCount { get; }

    public void SetBase(int channel, float value);
    public float GetBase(int channel);

    public void AddSource(StatSourceId id, ReadOnlySpan<StatModifier> modifiers);   // replaces any existing modifiers under this id
    public bool RemoveSource(StatSourceId id);
    public void ClearSources();

    public float Value(int channel);
    public void CopyValuesTo(Span<float> destination);
}
```

Channels are dense `int` indices into a `float[]`, so `CopyValuesTo` reads every channel for a HUD or a
stat-sheet screen in one allocation-free call.

Recompute is lazy and per-channel: a read refolds that channel from scratch only when it is dirty, and it
never mutates a running total, because removing a source is not the exact float inverse of adding it. The
fold order is source insertion order, preserved across removals, and replacing a source under an id it
already owns keeps that source's original position. That is what makes "add a source, remove it, get exactly
the base value back" true, bit for bit, every time.

`MinimumScale` floors the percent multiplier so a stack of negative percents cannot invert a channel's sign.
Default `0`. `float.NegativeInfinity` disables the floor entirely. A value like `0.1f` gives a "mitigation
caps at 90%" shape without your game clamping its own multiplier stack.

## Usage

```csharp
using KhaozEngine.Stats;

enum Channel { Attack, Defense, MoveSpeed }   // your own enum, the engine never sees it

var stats = new StatSet(channelCount: 3, minimumScale: 0.1f);   // mitigation floors at 90% reduction
stats.SetBase((int)Channel.Attack, 10f);
stats.SetBase((int)Channel.Defense, 5f);
stats.SetBase((int)Channel.MoveSpeed, 6f);

// Equip a sword: +4 flat attack, +15% attack. swordId is whatever int identity
// your inventory already uses for this equipped instance.
var swordId = new StatSourceId(swordInstanceId);
stats.AddSource(swordId, stackalloc StatModifier[]
{
    new StatModifier((int)Channel.Attack, Flat: 4f, Percent: 0.15f),
});
float attack = stats.Value((int)Channel.Attack);   // (10 + 4) * 1.15 = 16.1

// Unequip: remove the whole source in one call, every channel it touched updates.
stats.RemoveSource(swordId);
attack = stats.Value((int)Channel.Attack);   // back to exactly 10, bit for bit
```

## The seam (stays game-side)

- **Stat identity.** The channel enum, what each index means, and how many channels exist are entirely
  yours. `StatSet` only ever sees `int`.
- **Balance.** Which modifiers an item, skill, or buff grants is game data. The package folds numbers. It
  does not source them.
- **Items and equipment.** No item type, no equipment slot, no inventory. `StatSourceId` is just a
  caller-supplied identity for "the group of modifiers I want to add or remove together."
- **The "buff" half.** Stacking rules, durations, expiry, and diminishing returns are not modeled here. A
  timed buff is your system calling `AddSource`/`RemoveSource` when it starts and ends. This is the same
  split `KhaozEngine.Locomotion` already draws for its per-entity speed scale: the engine owns the multiplier
  and its plumbing, the game owns duration, stacking, and what granted it.

## Testing

`StatSet` is a plain in-memory fold with no I/O and no ambient state, so a headless test constructs one,
adds and removes sources, and asserts `Value`/`CopyValuesTo` directly. No fakes or clock injection needed.
