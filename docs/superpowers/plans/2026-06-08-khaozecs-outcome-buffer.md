# KhaozEngine.Ecs Deterministic Outcome Buffer + RNG - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD throughout.

**Goal:** Add the deterministic deferred-outcome model to `KhaozEngine.Ecs` - `EntityCommandBuffer.Defer`, a pull-model typed event channel, a `DeterministicRng`, and the RNG-draw-timing contract - released as `1.6.0`. Determinism Cycle B.

**Architecture:** Additive, opt-in. `Defer(Action<World>)` joins the existing ordered command list (drains in record order). `World.Emit`/`Events<T>` are a per-type, emission-ordered event store cleared by `AdvanceTick`. `DeterministicRng` is a standalone pinned-algorithm RNG. The engine owns ordering; games own RNG/meaning. Hardpoint/Nullwake unaffected.

**Tech Stack:** C#, .NET 10, xUnit.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-outcome-buffer-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` first (`git checkout -b ecs-outcome-buffer`).

---

## Task 1: `EntityCommandBuffer.Defer`

**Files:** Modify `KhaozEngine.Ecs/EntityCommandBuffer.cs`; Test `KhaozEngine.Tests/DeferTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/DeferTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct DefMark : IComponent { public int V; }

public class DeferTests
{
    [Fact]
    public void DeferRunsInterleavedInRecordOrder()
    {
        var w = new World();
        var e = w.Spawn();
        var log = new List<string>();
        var ecb = new EntityCommandBuffer();
        ecb.Set(e, new DefMark { V = 1 });
        ecb.Defer(_ => log.Add("A"));
        ecb.Defer(_ => log.Add("B"));
        ecb.Playback(w);
        Assert.Equal(new[] { "A", "B" }, log.ToArray());   // deferred actions ran in record order
        Assert.Equal(1, w.Get<DefMark>(e).V);              // structural op also applied
    }

    [Fact]
    public void DeferSeesEffectsOfEarlierCommands()
    {
        var w = new World();
        var e = w.Spawn();
        int seen = -1;
        var ecb = new EntityCommandBuffer();
        ecb.Set(e, new DefMark { V = 7 });                  // earlier structural op
        ecb.Defer(world => seen = world.Get<DefMark>(e).V); // reads it during playback
        ecb.Playback(w);
        Assert.Equal(7, seen);
    }
}
```

- [ ] **Step 2: Run to verify failure** - `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (no `Defer`).

- [ ] **Step 3: Add `Defer` to `EntityCommandBuffer.cs`**

- Add `Defer` to the op enum: `private enum Op { Create, Despawn, Set, Remove, Defer }`
- Add the method (after `Remove`):
```csharp
    /// <summary>Records an arbitrary deferred action, run in record order during <see cref="Playback"/>
    /// (interleaved with structural ops). Put non-structural deterministic logic - counters, RNG rolls - here.</summary>
    public void Defer(Action<World> action) =>
        _cmds.Add((Op.Defer, default, 0, (w, _) => action(w)));
```
- In `Playback`'s switch, add:
```csharp
                case Op.Defer: c.apply!(world, default); break;
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/EntityCommandBuffer.cs KhaozEngine.Tests/DeferTests.cs
git commit -m "ECS: EntityCommandBuffer.Defer (ordered deferred actions)"
```

---

## Task 2: Pull-model event channel

**Files:** Create `KhaozEngine.Ecs/World.Events.cs`; Modify `KhaozEngine.Ecs/World.ChangeTracking.cs`; Test `KhaozEngine.Tests/EventChannelTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/EventChannelTests.cs`:
```csharp
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public readonly record struct PlayerDamaged(float Amount);
public readonly record struct XpGranted(int Amount);

public class EventChannelTests
{
    [Fact]
    public void EventsReadBackInEmissionOrderPerType()
    {
        var w = new World();
        w.Emit(new PlayerDamaged(5));
        w.Emit(new XpGranted(10));
        w.Emit(new PlayerDamaged(3));
        Assert.Equal(new[] { 5f, 3f }, w.Events<PlayerDamaged>().Select(e => e.Amount).ToArray());
        Assert.Equal(new[] { 10 }, w.Events<XpGranted>().Select(e => e.Amount).ToArray());
        Assert.Empty(w.Events<int>());                       // unseen type -> empty
    }

    [Fact]
    public void AdvanceTickClearsEvents()
    {
        var w = new World();
        w.Emit(new XpGranted(1));
        w.AdvanceTick();
        Assert.Empty(w.Events<XpGranted>());
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Create `World.Events.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    private readonly Dictionary<Type, List<object>> _events = new();

    /// <summary>Records a typed event for this tick. Read via <see cref="Events{T}"/>; cleared by <see cref="AdvanceTick"/>. Boxes value-type events.</summary>
    public void Emit<T>(T evt)
    {
        if (!_events.TryGetValue(typeof(T), out List<object>? list))
        {
            list = new List<object>();
            _events[typeof(T)] = list;
        }
        list.Add(evt!);
    }

    /// <summary>This tick's events of type <typeparamref name="T"/>, in emission order (empty if none).</summary>
    public IEnumerable<T> Events<T>() =>
        _events.TryGetValue(typeof(T), out List<object>? list) ? list.Cast<T>() : Enumerable.Empty<T>();

    internal void ClearEvents() => _events.Clear();
}
```

- [ ] **Step 4: Clear events in `AdvanceTick`**

In `World.ChangeTracking.cs`, add `ClearEvents();` to `AdvanceTick`:
```csharp
    public void AdvanceTick()
    {
        Tick++;
        _added.Clear();
        _changed.Clear();
        _removed.Clear();
        ClearEvents();
    }
```

- [ ] **Step 5: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/World.Events.cs KhaozEngine.Ecs/World.ChangeTracking.cs KhaozEngine.Tests/EventChannelTests.cs
git commit -m "ECS: pull-model typed event channel (Emit/Events, cleared by AdvanceTick)"
```

---

## Task 3: `DeterministicRng`

**Files:** Create `KhaozEngine.Ecs/DeterministicRng.cs`; Test `KhaozEngine.Tests/DeterministicRngTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/DeterministicRngTests.cs`:
```csharp
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class DeterministicRngTests
{
    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new DeterministicRng(1337);
        var b = new DeterministicRng(1337);
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(sa, sb);
        Assert.True(sa.Distinct().Count() > 8);             // not a constant stream
    }

    [Fact]
    public void KnownVectorLocksAlgorithm()
    {
        // Captured from the implementation (xorshift128+ seeded via splitmix64, seed 42).
        // FILL with the first three NextULong() values observed on first green run, to lock the algorithm.
        var r = new DeterministicRng(42);
        ulong[] expected = { /* v0 */ 0, /* v1 */ 0, /* v2 */ 0 };   // replace with captured values
        Assert.Equal(expected, new[] { r.NextULong(), r.NextULong(), r.NextULong() });
    }

    [Fact]
    public void StateRoundTrips()
    {
        var r = new DeterministicRng(99);
        r.NextULong(); r.NextULong();
        var saved = r.State;
        ulong next = r.NextULong();
        var restored = new DeterministicRng(1) { State = saved };
        Assert.Equal(next, restored.NextULong());          // resumes the exact sequence
    }

    [Fact]
    public void RangeAndFloatBounds()
    {
        var r = new DeterministicRng(7);
        for (int i = 0; i < 1000; i++)
        {
            int n = r.Next(10, 20);
            Assert.InRange(n, 10, 19);
            float f = r.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement `DeterministicRng.cs`**

```csharp
namespace KhaozEngine.Ecs;

/// <summary>
/// Seeded, fixed-algorithm pseudo-random generator (xorshift128+, seeded via splitmix64). Reproducible
/// across .NET versions and platforms - unlike <see cref="System.Random"/>. Opt-in: a game owns an
/// instance and persists <see cref="State"/> for save/resume. Used inside deferred commands so draws
/// occur in a deterministic order (see the outcome-buffer contract).
/// </summary>
public sealed class DeterministicRng
{
    private ulong _s0, _s1;

    public DeterministicRng(ulong seed)
    {
        ulong z = seed;
        _s0 = SplitMix(ref z);
        _s1 = SplitMix(ref z);
        if ((_s0 | _s1) == 0) _s1 = 1;   // xorshift must not be all-zero
    }

    /// <summary>The full internal state, for save/resume of an in-progress deterministic run.</summary>
    public (ulong S0, ulong S1) State
    {
        get => (_s0, _s1);
        set { _s0 = value.S0; _s1 = value.S1; }
    }

    public ulong NextULong()
    {
        ulong s1 = _s0, s0 = _s1;
        _s0 = s0;
        s1 ^= s1 << 23;
        _s1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);
        return _s1 + s0;
    }

    public uint NextUInt() => (uint)(NextULong() >> 32);

    /// <summary>A double in [0, 1) with 53 bits of precision.</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);   // 2^53

    /// <summary>A float in [0, 1).</summary>
    public float NextFloat() => (float)NextDouble();

    /// <summary>An int in [0, <paramref name="maxExclusive"/>). Uses modulo (negligible bias for game ranges).</summary>
    public int Next(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);

    /// <summary>An int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
```

- [ ] **Step 4: Capture the known vector** - run the suite; `KnownVectorLocksAlgorithm` fails on the placeholder zeros. Read the actual first three `NextULong()` values for seed 42 from the failure output and replace the `expected` array. Re-run: it passes, locking the algorithm against accidental change.

- [ ] **Step 5: Commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/DeterministicRng.cs KhaozEngine.Tests/DeterministicRngTests.cs
git commit -m "ECS: DeterministicRng (xorshift128+, seedable, save/resume state)"
```

---

## Task 4: RNG-timing integration test + release 1.6.0

**Files:** Test `KhaozEngine.Tests/RngTimingTests.cs`; Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, `CHANGELOG.md`

- [ ] **Step 1: Write the end-to-end determinism test**

`KhaozEngine.Tests/RngTimingTests.cs`:
```csharp
using System.Collections.Generic;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class RngTimingTests
{
    // Simulate "kills recorded in iteration order -> deferred loot rolls drawing from one RNG".
    private static List<int> Run(ulong seed)
    {
        var w = new World();
        var rng = new DeterministicRng(seed);
        var loot = new List<int>();
        var ecb = new EntityCommandBuffer();
        for (int kill = 0; kill < 25; kill++)
            ecb.Defer(_ => loot.Add(rng.Next(100)));   // RNG drawn at playback, in record order
        ecb.Playback(w);
        return loot;
    }

    [Fact]
    public void DeferredRngDrawSequenceIsReproducible()
    {
        Assert.Equal(Run(2024), Run(2024));            // identical seed + order -> identical loot
    }

    [Fact]
    public void DifferentSeedDiffers()
    {
        Assert.NotEqual(Run(1), Run(2));
    }
}
```

- [ ] **Step 2: Run to verify pass** (Defer + DeterministicRng already in).

- [ ] **Step 3: Bump version + changelog**

In `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, change `<Version>1.5.0</Version>` to `<Version>1.6.0</Version>`.

Prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine.Ecs 1.6.0

- Deterministic outcome model: `EntityCommandBuffer.Defer(Action<World>)` (ordered deferred actions);
  a pull-model typed event channel (`World.Emit<T>` / `Events<T>`, cleared by `AdvanceTick`); and
  `DeterministicRng` (xorshift128+, seedable, save/resume `State`). Drawing RNG inside deferred actions
  gives a reproducible draw sequence (record order = the deterministic iteration order from 1.5.0).
  Additive and opt-in. Completes the determinism work (Cycles A + B).
```

- [ ] **Step 4: Test, pack, commit**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.6.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.6.0 (deterministic outcome buffer + RNG)"
```
> Tag `ecs-v1.6.0` and push from `main` after the branch merges (the finishing step).

---

## Self-Review

**Spec coverage:**
- `Defer` interleaved in record order → Task 1.
- Pull event channel, emission order, cleared by `AdvanceTick` → Task 2.
- `DeterministicRng` (pinned algorithm, seed, state round-trip, ranges, known-vector lock) → Task 3.
- RNG-draw-timing contract proven end-to-end → Task 4 (`DeferredRngDrawSequenceIsReproducible`).
- Opt-in / no regression → all tasks (full suite green; no existing API changed).
- Additive `1.6.0` release → Task 4.

**Placeholder scan:** one intentional - the known-vector `expected` array is captured on first run (Task 3 Step 4), not a logic gap.

**Type consistency:** `EntityCommandBuffer.Defer(Action<World>)` + `Op.Defer`; `World.Emit<T>(T)` / `Events<T>()` / internal `ClearEvents`; `_events` is `Dictionary<Type, List<object>>`; `DeterministicRng` API per spec. `AdvanceTick` clears the event store alongside the change sets.

---

## Execution Handoff

After all tasks green, finish the branch (merge `ecs-outcome-buffer` → `main`), tag `ecs-v1.6.0`, push so CI publishes. This completes the determinism work. SpaceGame can then mirror the `Defer` + pull-event + `DeterministicRng` pattern in `SimulationWorld` to unblock its five-system seam refactor. A shared `KhaozEngine.Combat`/toolkit layer (reusable game logic above the core ECS) remains a possible future package, deliberately out of the core.
