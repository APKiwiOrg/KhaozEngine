# KhaozEngine.Ecs System Ordering / Groups — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD throughout.

**Goal:** Replace the flat system list with named system groups (definable run order + per-group update), released as `1.4.0`.

**Architecture:** Additive. `World.Systems.cs` stores systems in named groups (`Dictionary<string, List<ISystem>>` + an ordered `List<string>`), runs all groups in order on `Update`, and runs one group on `UpdateGroup`. `AddSystem` gains an optional `group` parameter defaulting to `"default"`, so existing usage is unchanged. No constraint graph.

**Tech Stack:** C#, .NET 10, xUnit.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-system-groups-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` first (`git checkout -b ecs-system-groups`).

---

## Task 1: Named system groups

**Files:** Rewrite `KhaozEngine.Ecs/World.Systems.cs`; Test `KhaozEngine.Tests/SystemGroupsTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/SystemGroupsTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class RecordSystem : ISystem
{
    private readonly List<string> _log;
    private readonly string _name;
    public RecordSystem(List<string> log, string name) { _log = log; _name = name; }
    public void Update(World world, float dt) => _log.Add(_name);
}

public class SystemGroupsTests
{
    [Fact]
    public void GroupsRunInDefinedOrderRegistrationWithin()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "a1"), "alpha");
        w.AddSystem(new RecordSystem(log, "a2"), "alpha");
        w.AddSystem(new RecordSystem(log, "b1"), "beta");
        w.SetGroupOrder("beta", "alpha");
        w.Update(0f);
        Assert.Equal(new[] { "b1", "a1", "a2" }, log.ToArray());
    }

    [Fact]
    public void SetGroupOrderPreservesUnlistedGroups()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "a"), "alpha");
        w.AddSystem(new RecordSystem(log, "b"), "beta");
        w.AddSystem(new RecordSystem(log, "c"), "gamma");
        w.SetGroupOrder("gamma");                       // only gamma listed
        w.Update(0f);
        Assert.Equal("gamma", w.SystemGroups[0]);
        Assert.Equal(new[] { "c", "a", "b" }, log.ToArray());   // gamma first, alpha/beta preserved after
    }

    [Fact]
    public void UpdateGroupRunsOnlyThatGroupAndRepeats()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "sim"), "simulation");
        w.AddSystem(new RecordSystem(log, "draw"), "presentation");
        w.UpdateGroup("simulation", 0f);
        w.UpdateGroup("simulation", 0f);                // fixed-timestep shape
        Assert.Equal(new[] { "sim", "sim" }, log.ToArray());
    }

    [Fact]
    public void UnknownGroupThrows()
    {
        var w = new World();
        Assert.Throws<ArgumentException>(() => w.UpdateGroup("nope", 0f));
    }

    [Fact]
    public void DefaultGroupBackwardCompatible()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "x"));
        w.AddSystem(new RecordSystem(log, "y"));
        w.Update(0f);
        Assert.Equal(new[] { "x", "y" }, log.ToArray());
        Assert.Equal(new[] { "default" }, w.SystemGroups.ToArray());
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → FAIL (`AddSystem` overload / `SetGroupOrder` / `UpdateGroup` / `SystemGroups` missing).

- [ ] **Step 3: Rewrite `World.Systems.cs`**

Replace the whole file:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>A unit of per-frame logic. Run in registration order within its group by <see cref="World.Update"/>.</summary>
public interface ISystem
{
    void Update(World world, float dt);
}

public sealed partial class World
{
    private const string DefaultGroup = "default";
    private readonly Dictionary<string, List<ISystem>> _groups = new();   // group -> systems (registration order)
    private readonly List<string> _groupOrder = new();                    // group run order
    private readonly Dictionary<Type, object> _resources = new();

    /// <summary>Deferred structural changes recorded by systems during iteration. Played back (and cleared)
    /// after each system runs, so one system's changes are visible to the next.</summary>
    public EntityCommandBuffer Commands { get; } = new();

    /// <summary>The current group run order.</summary>
    public IReadOnlyList<string> SystemGroups => _groupOrder;

    /// <summary>Registers a system in a named group (created on first use). Systems run in registration order within their group.</summary>
    public void AddSystem(ISystem system, string group = DefaultGroup) => GetOrCreateGroup(group).Add(system);

    /// <summary>Defines the group run order: the listed groups first (in order), then any other existing group in its current order. Listed groups are created if new.</summary>
    public void SetGroupOrder(params string[] groups)
    {
        foreach (string g in groups) GetOrCreateGroup(g);

        var ordered = new List<string>();
        var seen = new HashSet<string>();
        foreach (string g in groups)
            if (seen.Add(g)) ordered.Add(g);
        foreach (string g in _groupOrder)
            if (!seen.Contains(g)) ordered.Add(g);

        _groupOrder.Clear();
        _groupOrder.AddRange(ordered);
    }

    /// <summary>Runs every group in order, flushing <see cref="Commands"/> after each system.</summary>
    public void Update(float dt)
    {
        for (int i = 0; i < _groupOrder.Count; i++)
            RunGroup(_groups[_groupOrder[i]], dt);
    }

    /// <summary>Runs a single group's systems in registration order, flushing <see cref="Commands"/> after each. Throws if the group does not exist.</summary>
    public void UpdateGroup(string group, float dt)
    {
        if (!_groups.TryGetValue(group, out List<ISystem>? systems))
            throw new ArgumentException($"No system group named '{group}'.", nameof(group));
        RunGroup(systems, dt);
    }

    private void RunGroup(List<ISystem> systems, float dt)
    {
        for (int i = 0; i < systems.Count; i++)
        {
            systems[i].Update(this, dt);
            Commands.Playback(this);
        }
    }

    private List<ISystem> GetOrCreateGroup(string group)
    {
        if (!_groups.TryGetValue(group, out List<ISystem>? list))
        {
            list = new List<ISystem>();
            _groups[group] = list;
            _groupOrder.Add(group);
        }
        return list;
    }

    /// <summary>Stores a world-global singleton of type <typeparamref name="T"/>.</summary>
    public void SetResource<T>(T value) where T : class => _resources[typeof(T)] = value;

    /// <summary>Gets the world-global singleton of type <typeparamref name="T"/>. Throws if unset.</summary>
    public T GetResource<T>() where T : class => (T)_resources[typeof(T)];

    /// <summary>True if a resource of type <typeparamref name="T"/> has been set.</summary>
    public bool HasResource<T>() where T : class => _resources.ContainsKey(typeof(T));
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. New `SystemGroupsTests` pass **and the existing `WorldSystemsTests` still pass** (default group preserves registration order + per-system command-buffer flush).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Ecs/World.Systems.cs KhaozEngine.Tests/SystemGroupsTests.cs
git commit -m "ECS: named system groups (AddSystem group, SetGroupOrder, UpdateGroup)"
```

---

## Task 2: Release 1.4.0

**Files:** Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, `CHANGELOG.md`

- [ ] **Step 1: Bump the Ecs package version** — change `<Version>1.3.0</Version>` to `<Version>1.4.0</Version>` in `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`.

- [ ] **Step 2: Changelog** — prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine.Ecs 1.4.0

- Add named system groups: `AddSystem(system, group)`, `SetGroupOrder(...)`, `UpdateGroup(name, dt)`,
  and `SystemGroups`. `Update(dt)` runs all groups in order; `UpdateGroup` runs one (e.g. a
  fixed-timestep simulation group). Systems without a group use `"default"`, so existing usage is
  unchanged. Additive.
```

- [ ] **Step 3: Test, pack, commit**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.4.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.4.0 (system groups)"
```
> Tag `ecs-v1.4.0` and push from `main` after the branch merges (the finishing step).

---

## Self-Review

**Spec coverage:**
- Named groups, registration order within, definable run order → Task 1 (`GroupsRunInDefinedOrderRegistrationWithin`).
- `SetGroupOrder` lists-first + preserves-unlisted → Task 1 (`SetGroupOrderPreservesUnlistedGroups`).
- `UpdateGroup` runs one group, repeatable; unknown throws → Task 1 (`UpdateGroupRunsOnlyThatGroupAndRepeats`, `UnknownGroupThrows`).
- Backward-compatible default group + command-buffer flush → Task 1 (`DefaultGroupBackwardCompatible`; existing `WorldSystemsTests` retained).
- `SystemGroups` introspection → Task 1.
- Additive `1.4.0` release → Task 2.

**Placeholder scan:** none — the full file is shown.

**Type consistency:** `AddSystem(ISystem, string="default")`, `SetGroupOrder(params string[])`, `Update(float)`, `UpdateGroup(string, float)`, `SystemGroups => IReadOnlyList<string>`; internal `RunGroup`/`GetOrCreateGroup`. `Commands`/`Resources`/`ISystem` unchanged. Per-system `Commands.Playback` preserved.

---

## Execution Handoff

After both tasks green, finish the branch (merge `ecs-system-groups` → `main`), tag `ecs-v1.4.0`, push so CI publishes. This completes all four deferred ECS features (serialization, change detection, relationships, system groups). The SpaceGame lockstep-determinism model remains parked for a separate decision.
