# KhaozEngine.Ecs Seed-Stable Iteration - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD throughout.

**Goal:** Make engine ECS iteration follow a guaranteed reproducible order (archetype-creation order across archetypes; swap-remove-stable within), replacing reliance on `Dictionary.Values` enumeration (which is *undefined* per the BCL, even though it happens to be insertion-ordered today). Released as `1.5.0`. Determinism Cycle A.

**Architecture:** Additive. `World` gains an `ArchetypeOrder` list appended to whenever an archetype is created; the three archetype walks (`Query.Refresh`, `WorldSerializer` save accessor - `ForEach` flows through `Query.Refresh`) iterate that list instead of `Archetypes.Values`. Swap-remove within an archetype is unchanged (already reproducible for identical op sequences). No public API change.

**Tech Stack:** C#, .NET 10, xUnit.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-deterministic-iteration-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` first (`git checkout -b ecs-deterministic-iteration`).

---

## Task 1: Ordered archetype iteration

**Files:** Modify `KhaozEngine.Ecs/World.cs`, `KhaozEngine.Ecs/World.Components.cs`, `KhaozEngine.Ecs/Query.cs`, `KhaozEngine.Ecs/World.Serialization.cs`; Test `KhaozEngine.Tests/DeterministicIterationTests.cs`

- [ ] **Step 1: Write the failing/guard tests**

`KhaozEngine.Tests/DeterministicIterationTests.cs`:
```csharp
using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct DiA : IComponent { public int V; }
public struct DiB : IComponent { public int V; }
public struct DiC : IComponent { public int V; }

public class DeterministicIterationTests
{
    // Identical scripted sequence -> a fully-determined world.
    private static World Build()
    {
        var w = new World();
        var es = new Entity[12];
        for (int i = 0; i < 12; i++) es[i] = w.Spawn();
        for (int i = 0; i < 12; i++)
        {
            w.Set(es[i], new DiA { V = i });
            if (i % 2 == 0) w.Set(es[i], new DiB { V = i });
            if (i % 3 == 0) w.Set(es[i], new DiC { V = i });
        }
        w.Despawn(es[1]); w.Despawn(es[4]); w.Despawn(es[9]);   // swap-remove churn
        return w;
    }

    [Fact]
    public void IterationOrderIsReproducibleAcrossWorlds()
    {
        var oa = Build().Query().With<DiA>().Entities().ToArray();
        var ob = Build().Query().With<DiA>().Entities().ToArray();
        Assert.NotEmpty(oa);
        Assert.Equal(oa, ob);                       // identical handle sequence, element-for-element
    }

    [Fact]
    public void CrossArchetypeOrderFollowsCreationOrder()
    {
        var w = new World();
        var first = w.Spawn(); w.Set(first, new DiC { V = 1 });    // creates archetype {DiC}
        var second = w.Spawn(); w.Set(second, new DiA { V = 2 });  // then archetype {DiA}
        var order = w.Query().Entities().ToArray();                // no filter: spans all archetypes
        Assert.True(Array.IndexOf(order, first) < Array.IndexOf(order, second));
    }

    [Fact]
    public void SaveOutputIsByteStableAcrossIdenticalWorlds()
    {
        var ser = new WorldSerializer(typeof(DiA), typeof(DiB), typeof(DiC));
        Assert.Equal(ser.Save(Build()), ser.Save(Build()));
    }
}
```

- [ ] **Step 2: Run the suite** - `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. These lock the contract; they may already pass (today's `Dictionary` happens to enumerate in insertion order), but they guard against any future switch to an unordered source. Proceed to make the order an explicit guarantee.

- [ ] **Step 3: Add the ordered archetype list to `World.cs`**

Add the field next to `Archetypes`:
```csharp
    internal readonly Dictionary<ArchetypeSignature, Archetype> Archetypes = new();
    internal readonly List<Archetype> ArchetypeOrder = new();   // archetypes in creation order (deterministic iteration)
    internal int ArchetypeGen;
```
In the constructor, append the empty archetype:
```csharp
    public World()
    {
        _empty = new Archetype(Array.Empty<int>(), Reg);
        Archetypes[new ArchetypeSignature(Array.Empty<int>())] = _empty;
        ArchetypeOrder.Add(_empty);
        ArchetypeGen++;
    }
```

- [ ] **Step 4: Append new archetypes in `World.Components.cs`**

In `GetOrCreateArchetype`, record creation order:
```csharp
        if (!Archetypes.TryGetValue(key, out Archetype? a))
        {
            a = new Archetype(sortedSig, Reg);
            Archetypes[key] = a;
            ArchetypeOrder.Add(a);
            ArchetypeGen++;
        }
```

- [ ] **Step 5: Iterate the ordered list in `Query.cs`**

In `Refresh`, change the source:
```csharp
        foreach (Archetype a in _world.ArchetypeOrder)
```
(All `ForEach` overloads and `Entities()` flow through `Refresh` → `_matched`, so this is the only query-side change.)

- [ ] **Step 6: Iterate the ordered list in `World.Serialization.cs`**

Change the save accessor:
```csharp
    internal IEnumerable<Archetype> SaveArchetypes => ArchetypeOrder;
```

- [ ] **Step 7: Run to verify pass** - `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. All `DeterministicIterationTests` pass plus the full existing suite (97). No query/ForEach test should regress (they assert sets/contents, order-independent).

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Ecs/World.cs KhaozEngine.Ecs/World.Components.cs KhaozEngine.Ecs/Query.cs KhaozEngine.Ecs/World.Serialization.cs KhaozEngine.Tests/DeterministicIterationTests.cs
git commit -m "ECS: deterministic iteration via creation-ordered archetype list"
```

---

## Task 2: Release 1.5.0

**Files:** Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, `CHANGELOG.md`

- [ ] **Step 1: Bump the Ecs package version** - change `<Version>1.4.0</Version>` to `<Version>1.5.0</Version>` in `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`.

- [ ] **Step 2: Changelog** - prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine.Ecs 1.5.0

- Deterministic iteration order: queries, `ForEach`, and serialization now walk archetypes in a
  guaranteed creation order (an explicit ordered list) rather than relying on `Dictionary` enumeration.
  Iteration is reproducible for an identical operation sequence, run-to-run and across processes
  (foundation for lockstep determinism). Swap-remove within an archetype is unchanged. Additive.
```

- [ ] **Step 3: Test, pack, commit**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.5.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.5.0 (deterministic iteration)"
```
> Tag `ecs-v1.5.0` and push from `main` after the branch merges (the finishing step).

---

## Self-Review

**Spec coverage:**
- Cross-archetype iteration follows a guaranteed creation order → Task 1 Steps 3-6 (`CrossArchetypeOrderFollowsCreationOrder`).
- Reproducible run-to-run → Task 1 (`IterationOrderIsReproducibleAcrossWorlds`).
- Swap-remove kept → unchanged (verified by existing despawn tests + churn in `Build`).
- Save byte-stability bonus → Task 1 (`SaveOutputIsByteStableAcrossIdenticalWorlds`).
- Change-detection set order, order-preserving removal, zero-alloc, outcome buffer → out of scope (per spec).
- Additive `1.5.0` release → Task 2.

**Placeholder scan:** none - every edit shown in full.

**Type consistency:** `World.ArchetypeOrder` is `List<Archetype>`, appended in the ctor and `GetOrCreateArchetype`; consumed by `Query.Refresh` and `World.SaveArchetypes`. `Archetypes` dictionary retained for O(1) signature lookup. No public API change.

---

## Execution Handoff

After both tasks green, finish the branch (merge `ecs-deterministic-iteration` → `main`), tag `ecs-v1.5.0`, push so CI publishes. Then Cycle B (deterministic outcome/event buffer + RNG-draw timing) is the remaining determinism work.
