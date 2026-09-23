# Frame Cost Round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the Grimhollow town frame's `Scene3D.RenderInternal` recording from about 8.5 ms to about 3 ms on native Metal, and remove the steady per-frame allocation, without changing what a frame draws beyond two bounded pixel differences.

**Architecture:** Seven independent items in six item branches, each cut from `feature/frame-cost-round` into its own worktree and merged back in spec order. Point shadows gain a per-frame caster index and word-wise, dissolve-quantized signatures. Point light clusters keep their buffer but pack compactly and upload only the used prefix. Water routes non-displacing planes to flat quads, uploads all geometry before the pass and culls what the view cannot reach. Metal holds one autorelease pool per command list member and stages without managed allocation. D3D11 prefers the high-performance adapter. ChatBox draws only visible rows.

**Tech Stack:** C# on .NET 10, xUnit, GLSL 450 cross-compiled to SPIR-V, MSL and HLSL, native Metal through the engine's own Objective-C interop, Direct3D 11 through Vortice 2.3.0, GitHub Actions GPU legs.

**Spec:** [`docs/design/FRAME-COST-ROUND-DESIGN-2026-09-23.md`](../../design/FRAME-COST-ROUND-DESIGN-2026-09-23.md). Executors read the spec before their item. The "Plan amendments" section of the spec records every place the planning read of the code changed a spec detail.

## Global Constraints

- The round adds no public API and no setting.
- Output stays identical except the two differences the spec names: a fading prop's point shadow advances in sixteen dissolve steps, and zero-swell water draws as flat quads within mean 0.002 and worst 0.02. The D3D11 adapter default is an intended behaviour change.
- The only committed goldens that may move are `tileworld_river` and `scene3d_sky_two_discs`, both rebaked in Task C1. Any other moved grid is a regression until attributed.
- Steady frames allocate nothing in every path the round touches: point shadow preparation, cluster building, the water frame, Metal staging and recording, and chat history drawing.
- Warnings are errors. Every build and test command runs in Release.
- New behaviour goes in a new type. No `.filesize-baseline` entry grows.
- No em or en dash characters anywhere. No semicolons in Markdown prose or comment prose. Semicolons in code are fine.
- Tests live in the matching `KhaozEngine.<Area>.Tests` project under namespaces `KhaozEngine.Tests.*`. GPU tests use `[GpuFact]` or `[GpuTheory]` and run with `KE_GPU_TESTS=1` on the local native Metal device. A test that reads allocation joins the `AllocSensitive` collection.
- Commit subjects use `area(scope): summary`. Stage explicit paths. Never use the shared git stash.
- Each item branch is `feature/frame-cost-<item>`, cut from `feature/frame-cost-round` into `/Users/antonio/KhaozEngine/.worktrees/frame-cost-<item>`, with `mkdir -p local-feed` inside it before the first build.
- Implementers never push, merge, tag, bump `<KhaozEngineVersion>` or edit `CHANGELOG.md`. The orchestrating session owns the bake half of Task C1 and Tasks G1 to G5.

## Review Focus

1. A caster whose centre sits a float rounding error past a light's grown sphere, on a cell edge, must still be found and still dirty the light's static map. Covered by `ACasterTheFloatTestKeepsJustPastTheGrownSphereIsStillFound` and the seeded equivalence tests in Task A1.
2. A light behind or beside the camera that reaches the near plane must keep every cluster the exact plane test assigns it, even when its sphere lies wholly outside the frustum. Covered by `ASphereWhollyOutsideTheFrustumThatThePlaneTestAcceptsIsStillAssigned` and the seeded equivalence tests in Task B1.
3. A water plane just outside the view whose Gerstner pinch carries crests back into it must still draw. Covered by `TheReachBoundsEveryOffsetTheGerstnerMirrorProduces` and the `(90, 0, 8, 5, 0, true)` row of `AnOrthographicCameraKeepsExactlyThePlanesItCanReach` in Task C3.
4. No Metal message send may run without an enclosing autorelease pool, and nothing retained may depend on a pool it outlives. Covered by the walk tests in Task D2, `TheEncoderSeamsOpenNoPoolOfTheirOwn` in Task D3, and the full native Metal suite under `MTL_DEBUG_LAYER=1` in Tasks D3 and D4.
5. A chat row cut by either edge of the history viewport must still draw while the player scrolls. Covered by `A_partly_visible_row_at_either_edge_is_drawn` and `Scrolling_mid_history_draws_only_the_rows_meeting_the_viewport` in Task F2.

## Execution order

| Wave | Item branches | Tasks | Notes |
| --- | --- | --- | --- |
| 1 | `feature/frame-cost-point-shadows`, `feature/frame-cost-clusters`, `feature/frame-cost-water` | A1 to A4, B1 to B3, C1 to C4 | Parallel. Disjoint files. The C1 bake runs once C1 is reviewed. |
| 2 | `feature/frame-cost-metal`, `feature/frame-cost-d3d11-adapter`, `feature/frame-cost-chatbox` | D1 to D4, E1 and E2, F1 and F2 | Parallel. Disjoint files. |
| 3 | `feature/frame-cost-round` | G1 to G5 | Sequential, in the orchestrating session. |

Tasks inside one item run in order, because each consumes the interfaces the one before it produces. Items never consume each other's interfaces. Each task gets a fresh reviewer before the next task in its item starts, and each item gets a whole-branch review before G1 merges it.

---

## Items 1 and 2: Point caster index and quantized dissolve (#1110, #1111)


**Where the code and the spec differ.** None of these contradicts what the spec intends. Each one is something the spec does not cover, and the plan handles it as stated.

1. The spec says the index is "built once per frame inside `PreparePointShadows` when at least one point request exists". Two paths render rows on frames that queued no request:
   - `DebugRenderPointShadowSlot` (`KhaozEngine.Render3D/Scene3D.PointShadowPass.cs:302-320`)
   - `PointShadowPassScene.RenderTwoLights`, which calls `RenderPointShadowSlots` directly (`KhaozEngine.Render.Tests/Gpu/PointShadowPassGpuTests.cs:294-326`)

   An index built only inside `PreparePointShadows` would be stale or empty on those paths. The plan builds the index lazily, once per point-shadow frame, keyed on `_pointShadowFrame`. `PreparePointShadows` triggers the build explicitly, after the atlas check at `Scene3D.PointShadows.cs:86-90`. That point already implies a request exists, and it also avoids the build on the first frame, which has no atlas.
2. Growing the query "by one cell size" is not enough under float rounding. The exact test (`Scene3D.PointShadows.cs:352-355`) can accept a caster centre that sits a rounding error past the grown sphere, and that centre can fall one cell outside the range. The plan adds `QuerySlack = 0.25f` m, and Task A1 has a concrete failing case for it. Some inputs go to the oversize list or to a full walk of the grid, which is still exact:
   - non-finite centres
   - centres outside the packed cell range
   - negative or NaN radii
   - a query covering more columns than there are binned casters
3. `MixPointSignature` is `private` (`Scene3D.PointShadows.cs:399`). It becomes `internal` for the mixer test. That is not a public API change.

**Worktree, once before Task A1.**
```bash
git -C /Users/antonio/KhaozEngine worktree add .worktrees/frame-cost-point-shadows -b feature/frame-cost-point-shadows feature/frame-cost-round
mkdir -p /Users/antonio/KhaozEngine/.worktrees/frame-cost-point-shadows/local-feed
```
Every command below runs from `/Users/antonio/KhaozEngine/.worktrees/frame-cost-point-shadows`.

**File-size check.** None of the files touched here are in `.filesize-baseline`, so the 800-line cap applies. `Scene3D.PointShadows.cs` is 438 lines and ends near 470. `Scene3D.PointShadowPass.cs` is 407 and shrinks. The new files stay under 250. `Scene3D.cs`, the only baselined file nearby (2636 lines), is not touched.

---

### Task A1: PointCasterIndex and the sphere overload of InstanceTouchesLight

**Branch:** `feature/frame-cost-point-shadows` (worktree created from `feature/frame-cost-round`)

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/PointCasterIndex.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs:331-367` (`InstanceTouchesLight`)
- Create: `KhaozEngine.Render.Tests/Render3D/PointCasterFullWalk.cs` (the oracle)
- Test: `KhaozEngine.Render.Tests/Render3D/PointCasterIndexTests.cs`

**Interfaces:**
- Consumes: `Scene3D.IsExclusionBox(Vector3, Vector3)`, `MeshBounds.WorldSphere(in Matrix4x4, out Vector3, out float)`
- Produces:
  - `internal static bool Scene3D.InstanceTouchesLight(Vector3 centre, float sphereRadius, Vector3 lightPosAbsolute, float radius, float nearRadius = 0f, Vector3 exclusionMin = default, Vector3 exclusionMax = default)`
  - `internal sealed class KhaozEngine.Render3D.Rendering.PointCasterIndex` with:
    - `internal const float CellSize = 8f`
    - `internal const float QuerySlack = 0.25f`
    - `internal int Count`
    - `internal int OversizeCount`
    - `internal int TouchTests`
    - `internal void Reset(int slotCount)`
    - `internal void Add(int slot, int run, Vector3 centre, float radius)`
    - `internal void Seal()`
    - `internal int RunOf(int slot)`
    - `internal void Query(Vector3 lightPosAbsolute, float radius, float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax, List<int> hits)`
  - Test helper `KhaozEngine.Tests.Render3D.PointCasterFullWalk` with `record struct Caster(int Slot, Vector3 Centre, float Radius)` and `List<int> Slots(IReadOnlyList<Caster>, Vector3, float, float, Vector3, Vector3)`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Render.Tests/Render3D/PointCasterFullWalk.cs`
```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point caster walk as it stood before <see cref="PointCasterIndex"/>, kept here as the oracle the index is
/// compared against. It visits every caster for every light, which is the cost the index removes, and it is the
/// definition of the right answer: the index must return exactly these slots in exactly this order.
/// </summary>
internal static class PointCasterFullWalk
{
    /// <summary>One caster as the index sees it: its instance slot and its world bounding sphere.</summary>
    internal readonly record struct Caster(int Slot, Vector3 Centre, float Radius);

    /// <summary>Every caster that touches the light's shadowing shell, in the order given, which is ascending slot
    /// order because the scene's run walk adds casters that way.</summary>
    internal static List<int> Slots(IReadOnlyList<Caster> casters, Vector3 light, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        var slots = new List<int>();
        foreach (Caster caster in casters)
            if (Scene3D.InstanceTouchesLight(caster.Centre, caster.Radius, light, radius, nearRadius,
                exclusionMin, exclusionMax))
                slots.Add(caster.Slot);
        return slots;
    }
}
```

`KhaozEngine.Render.Tests/Render3D/PointCasterIndexTests.cs`
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="PointCasterIndex"/> answers every light exactly as the walk over every caster does, slot for slot and
/// in slot order, while testing only the casters near the light. The oracle is <see cref="PointCasterFullWalk"/>.
/// Seeded, so a failure names a seed that reproduces it.
/// </summary>
[Collection("AllocSensitive")]   // one case is a zero-allocation reading (#264)
public sealed class PointCasterIndexTests
{
    [Fact]
    public void TheSphereOverloadAnswersExactlyAsTheBoundsOverload()
    {
        var random = new Random(1110);
        var bounds = new MeshBounds(new Vector3(-0.5f, 0f, -0.25f), new Vector3(0.5f, 2f, 0.25f));
        for (int i = 0; i < 2_000; i++)
        {
            Matrix4x4 model = Matrix4x4.CreateScale(Range(random, 0.2f, 4f))
                * Matrix4x4.CreateRotationY(Range(random, 0f, MathF.Tau))
                * Matrix4x4.CreateTranslation(RandomPoint(random, 30f));
            Vector3 light = RandomPoint(random, 30f);
            float radius = Range(random, 0.5f, 20f);
            float near = random.Next(3) == 0 ? Range(random, 0f, 3f) : 0f;
            bool boxed = random.Next(2) == 0;
            Vector3 min = boxed ? light - new Vector3(Range(random, 0f, 4f)) : default;
            Vector3 max = boxed ? light + new Vector3(Range(random, 0f, 4f)) : default;
            bounds.WorldSphere(model, out Vector3 centre, out float sphereRadius);

            Assert.Equal(
                Scene3D.InstanceTouchesLight(bounds, model, light, radius, near, min, max),
                Scene3D.InstanceTouchesLight(centre, sphereRadius, light, radius, near, min, max));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryQueryReturnsTheFullWalksSlotsInSlotOrder(int seed)
    {
        var random = new Random(seed);
        var index = new PointCasterIndex();
        var hits = new List<int>();
        int queries = 0, touched = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            // Frame 0 holds nothing, so an empty frame is asked too. Later frames shrink and grow, so the reused
            // arrays carry stale entries past the live count, and a query that read one would fail here.
            List<PointCasterFullWalk.Caster> casters = frame == 0
                ? new List<PointCasterFullWalk.Caster>()
                : RandomCasters(random, random.Next(1, 400));
            index.Reset(casters.Count == 0 ? 0 : casters[^1].Slot + 1);
            foreach (PointCasterFullWalk.Caster caster in casters)
                index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
            index.Seal();

            for (int q = 0; q < 32; q++)
            {
                (Vector3 at, float radius, float near, Vector3 min, Vector3 max) = RandomLight(random);
                List<int> expected = PointCasterFullWalk.Slots(casters, at, radius, near, min, max);
                index.Query(at, radius, near, min, max, hits);
                Assert.Equal(expected, hits);
                queries++;
                if (expected.Count > 0) touched++;
            }
        }
        // Two empty lists agreeing proves nothing, so a seed whose lights all miss is not a pass.
        Assert.True(touched > queries / 8, $"only {touched} of {queries} queries touched a caster");
    }

    [Fact]
    public void ACasterTheFloatTestKeepsJustPastTheGrownSphereIsStillFound()
    {
        // The light's sphere grown by one cell ends exactly on the x = 0 cell edge. The caster's centre is a hair
        // below it, in cell -1, and at this magnitude the hair rounds away, so the exact test sees a distance of
        // exactly its reach and keeps the caster. A query that visited only the grown sphere's own cells would
        // start at cell 0 and lose it, which is what QuerySlack is for.
        var light = new Vector3(16f, 0f, 0f);
        var casters = new List<PointCasterFullWalk.Caster>
        {
            new(0, new Vector3(-1e-7f, 0f, 0f), PointCasterIndex.CellSize),
        };
        var index = new PointCasterIndex();
        index.Reset(1);
        index.Add(0, 0, casters[0].Centre, casters[0].Radius);
        index.Seal();
        var hits = new List<int>();

        index.Query(light, 8f, 0f, default, default, hits);

        Assert.Equal(new[] { 0 }, PointCasterFullWalk.Slots(casters, light, 8f, 0f, default, default));
        Assert.Equal(new[] { 0 }, hits);
    }

    [Fact]
    public void NonFiniteAndFarCastersAreAnsweredAsTheFullWalkAnswersThem()
    {
        // A NaN centre or an infinite radius touches every light under the shared test, because each comparison
        // that would reject it is false. A centre past the packed cell range cannot be binned. All three are
        // always candidates, and a light radius the queue would never admit walks the whole grid.
        var casters = new List<PointCasterFullWalk.Caster>
        {
            new(0, new Vector3(float.NaN, 0f, 0f), 1f),
            new(1, new Vector3(3f, 0f, 0f), float.PositiveInfinity),
            new(2, new Vector3(1e12f, 0f, 0f), 1f),
            new(3, new Vector3(2f, 0f, 0f), 1f),
        };
        var index = new PointCasterIndex();
        index.Reset(4);
        foreach (PointCasterFullWalk.Caster caster in casters)
            index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
        index.Seal();
        var hits = new List<int>();

        Assert.Equal(3, index.OversizeCount);
        foreach ((Vector3 light, float radius) in new[]
        {
            (Vector3.Zero, 6f), (new Vector3(500f, 0f, 0f), 6f), (new Vector3(1e12f, 0f, 0f), 6f),
            (Vector3.Zero, -20f), (Vector3.Zero, float.NaN),
        })
        {
            index.Query(light, radius, 0f, default, default, hits);
            Assert.Equal(PointCasterFullWalk.Slots(casters, light, radius, 0f, default, default), hits);
        }
    }

    [Fact]
    public void TouchTestsFollowTheCastersNearEachLightRatherThanLightsTimesCasters()
    {
        // A sparse town from above: 4,096 unit casters on a 20 m lattice, 1.26 km on a side.
        const int side = 64;
        const float spacing = 20f;
        const int lights = 32;
        const float radius = 10f;
        var index = new PointCasterIndex();
        index.Reset(side * side);
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                index.Add(x * side + z, 0, new Vector3(x * spacing, 0.5f, z * spacing), 0.87f);
        index.Seal();

        var random = new Random(1110);
        var hits = new List<int>();
        int kept = 0;
        for (int i = 0; i < lights; i++)
        {
            // Over a lattice point, so every light has at least the caster beneath it.
            var light = new Vector3(random.Next(side) * spacing, 3f, random.Next(side) * spacing);
            index.Query(light, radius, 0f, default, default, hits);
            Assert.NotEmpty(hits);
            kept += hits.Count;
        }

        // A query visits the cells under its light grown by radius, one cell and the slack, 18.25 m, and the cell
        // floor adds under one more cell on each side, so its window is under 52.5 m per horizontal axis. Casters
        // 20 m apart put at most three columns in that, so no light tests more than nine. The walk this replaced
        // tested all 4,096 for every light.
        Assert.InRange(index.TouchTests, kept, 9 * lights);
        Assert.True(index.TouchTests < side * side,
            $"{lights} lights ran {index.TouchTests} touch tests, more than one full walk of {side * side} casters");
    }

    [Fact]
    public void AWarmRebuildAndItsQueriesAllocateNothing()
    {
        List<PointCasterFullWalk.Caster> casters = RandomCasters(new Random(7), 300);
        var index = new PointCasterIndex();
        var hits = new List<int>();
        void Frame()
        {
            index.Reset(casters[^1].Slot + 1);
            foreach (PointCasterFullWalk.Caster caster in casters)
                index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
            index.Seal();
            for (int x = -96; x <= 96; x += 32)
                index.Query(new Vector3(x, 0f, 0f), 20f, 0f, default, default, hits);
        }

        for (int i = 0; i < 4; i++) Frame();   // warm every grow-only array and the sort helpers
        AllocAssert.NoPerCallAllocation("PointCasterIndex rebuild and queries", () =>
        {
            for (int i = 0; i < 10; i++) Frame();
        });
    }

    static List<PointCasterFullWalk.Caster> RandomCasters(Random random, int count)
    {
        var casters = new List<PointCasterFullWalk.Caster>(count);
        int slot = -1;
        for (int i = 0; i < count; i++)
        {
            // Skipped slots stand for the instances the scene never adds: opted out, a stale handle, terrain.
            slot += random.Next(3) == 0 ? 2 : 1;
            Vector3 centre = RandomPoint(random, 96f);
            if (random.Next(6) == 0) centre = Snap(centre);   // on a cell corner, where a floor is easiest to get wrong
            float radius = random.Next(10) switch
            {
                0 => Range(random, 8.5f, 40f),      // oversize: a ground chunk, a cliff
                1 => PointCasterIndex.CellSize,      // the widest caster the grid still bins
                _ => Range(random, 0.05f, 4f),
            };
            casters.Add(new PointCasterFullWalk.Caster(slot, centre, radius));
        }
        return casters;
    }

    static (Vector3 At, float Radius, float Near, Vector3 Min, Vector3 Max) RandomLight(Random random)
    {
        float radius = Range(random, 1f, 24f);
        Vector3 at = RandomPoint(random, 100f);
        // One in three stands where its sphere grown by a cell ends on a cell edge on every axis.
        if (random.Next(3) == 0) at = Snap(at) + new Vector3(radius + PointCasterIndex.CellSize);
        float near = random.Next(4) == 0 ? Range(random, 0f, radius * 0.5f) : 0f;
        bool boxed = random.Next(4) == 0;
        var half = new Vector3(Range(random, 0.1f, 3f), Range(random, 0.1f, 3f), Range(random, 0.1f, 3f));
        return (at, radius, near, boxed ? at - half : default, boxed ? at + half : default);
    }

    static Vector3 RandomPoint(Random random, float extent) => new(
        Range(random, -extent, extent), Range(random, -extent * 0.25f, extent * 0.25f), Range(random, -extent, extent));

    static Vector3 Snap(Vector3 p) => new(
        MathF.Round(p.X / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Y / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Z / PointCasterIndex.CellSize) * PointCasterIndex.CellSize);

    static float Range(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);
}
```

- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCasterIndexTests"`
Expected: the build fails. The errors are CS0246 on `PointCasterIndex`, and CS1503 on the `Scene3D.InstanceTouchesLight(Vector3, float, ...)` calls (argument 1 cannot convert from `Vector3` to `in MeshBounds`).

- [ ] **Step 3: Implement**

`KhaozEngine.Render3D/Scene3D.PointShadows.cs` replaces lines 331-367. Keep the existing `<summary>` block on the bounds overload and change its first sentence to "Shared by the frame's `PointCasterIndex`, and through it by the pass's caster cull and the signature above".
```csharp
        internal static bool InstanceTouchesLight(in MeshBounds bounds, in Matrix4x4 model,
            Vector3 lightPosAbsolute, float radius, float nearRadius = 0f,
            Vector3 exclusionMin = default, Vector3 exclusionMax = default)
        {
            bounds.WorldSphere(model, out Vector3 centre, out float r);
            return InstanceTouchesLight(centre, r, lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
        }

        /// <summary>The same test on a world sphere already in hand, which is what the frame's
        /// <see cref="PointCasterIndex"/> stores for every caster. The overload above transforms the bounds and asks
        /// this one, so the index, the signature and the pass all read one definition of touching a light.</summary>
        internal static bool InstanceTouchesLight(Vector3 centre, float sphereRadius,
            Vector3 lightPosAbsolute, float radius, float nearRadius = 0f,
            Vector3 exclusionMin = default, Vector3 exclusionMax = default)
        {
            float reach = sphereRadius + radius;
            float distanceSq = (centre - lightPosAbsolute).LengthSquared();
            if (distanceSq > reach * reach) return false;
            if (nearRadius > 0f)
            {
                // Wholly inside the clearance: the farthest point of the sphere is still nearer than the near
                // radius.
                float farthest = MathF.Sqrt(distanceSq) + sphereRadius;
                if (farthest <= nearRadius) return false;
            }
            if (!IsExclusionBox(exclusionMin, exclusionMax)) return true;
            return !(centre.X - sphereRadius >= exclusionMin.X && centre.X + sphereRadius <= exclusionMax.X
                && centre.Y - sphereRadius >= exclusionMin.Y && centre.Y + sphereRadius <= exclusionMax.Y
                && centre.Z - sphereRadius >= exclusionMin.Z && centre.Z + sphereRadius <= exclusionMax.Z);
        }
```
Doc comments must not use a bare `cref="InstanceTouchesLight"`. Once the overload exists that reference is ambiguous (CS0419), and warnings are errors. Write `<c>InstanceTouchesLight</c>` instead.

`KhaozEngine.Render3D/Rendering/PointCasterIndex.cs`
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The frame's rigid point-shadow casters, binned by where they stand, so a light tests the casters near it
    /// instead of every queued instance (issue #1110). A pure CPU structure with no device and no scene, filled once
    /// per point-shadow frame by <see cref="Scene3D"/> and asked once per static signature and once per row the pass
    /// draws.
    /// <para>
    /// THE ANSWER IS THE FULL WALK'S ANSWER. A query returns exactly the slots a walk over every added caster keeps,
    /// in ascending slot order, because the grid only decides which casters are TESTED. The test is
    /// <c>Scene3D.InstanceTouchesLight</c> on the stored sphere, the one definition of touching a light (design
    /// decision 8 of the point shadow design). The grid may hand the test a caster that misses, and it never
    /// withholds one that could touch.
    /// </para>
    /// <para>
    /// A UNIFORM GRID OF <see cref="CellSize"/> METRE CELLS, binned by sphere centre. A caster no wider than a cell
    /// can only touch a light whose sphere, grown by one cell, contains its centre, so a query visits the cells
    /// under that grown sphere's box. A caster wider than a cell (a region ground chunk, a cliff) goes to an
    /// oversize list that every query tests, so a few huge casters never force a coarse grid on the many small
    /// ones. Cells are packed keys in a sorted array rather than a dense volume, so a world kilometres across costs
    /// only the cells that hold something.
    /// </para>
    /// <para>
    /// ALLOCATION-FREE ONCE WARM. Every array only grows, so a frame with no more casters or slots than the largest
    /// frame before it allocates nothing, which <c>PointShadowAllocationTests</c> holds the frame method to.
    /// </para>
    /// </summary>
    internal sealed class PointCasterIndex
    {
        /// <summary>The grid's cell edge in metres. Fixed: a lamp radius is a few cells, so a query visits tens of
        /// columns, and almost every prop is well under a cell wide, so the oversize list stays short.</summary>
        internal const float CellSize = 8f;

        /// <summary>Added to a query's grown reach before it becomes a cell range. The touch test runs in float, so
        /// at the very edge of a caster's reach it can keep a centre a rounding error past the grown sphere. A
        /// quarter metre is far above that error for any world under a thousand kilometres across, and slack can
        /// only add candidates, which the exact test then rejects.</summary>
        internal const float QuerySlack = 0.25f;

        const float InverseCellSize = 1f / CellSize;

        // Cell coordinates are packed 21 bits per axis into one sortable key, x major and z minor, so one (x, y)
        // column's z range is one contiguous stretch of the sorted keys. A centre whose cell falls outside that
        // range (thousands of kilometres out) or that is not finite goes to the oversize list instead.
        const int CellBias = 1 << 20;
        const int MinCell = -CellBias;
        const int MaxCell = CellBias - 1;

        // Per SLOT, and only meaningful for the slots added this frame: the world sphere (centre in xyz, radius in
        // w) and the mesh run the slot belongs to. Indexed by slot so a candidate list sorted by slot reads them
        // directly, and a slot not added this frame is never a candidate, so a stale entry is never read.
        Vector4[] _spheres = Array.Empty<Vector4>();
        int[] _runs = Array.Empty<int>();

        // The binned casters, parallel arrays sorted together by cell key in Seal.
        long[] _cellKeys = Array.Empty<long>();
        int[] _cellSlots = Array.Empty<int>();
        int _binned;

        int[] _oversize = Array.Empty<int>();
        int _oversizeCount;

        // One query's candidates before the exact test. Sized in Seal to every caster added, so no query grows it.
        int[] _candidates = Array.Empty<int>();
        bool _sealed;

        /// <summary>How many casters were added this frame, binned and oversize together.</summary>
        internal int Count => _binned + _oversizeCount;

        /// <summary>How many of them were too wide for a cell, or unbinnable, and are tested by every query.</summary>
        internal int OversizeCount => _oversizeCount;

        /// <summary>How many exact touch tests the queries since the last <see cref="Reset"/> ran. A diagnostic for
        /// the count tests, which assert it follows the casters near each light rather than every caster.</summary>
        internal int TouchTests { get; private set; }

        /// <summary>Forget the last frame's casters and make room for slots below <paramref name="slotCount"/>.
        /// Grows the per-slot arrays only when this frame has more slots than any frame before it.</summary>
        internal void Reset(int slotCount)
        {
            _binned = 0;
            _oversizeCount = 0;
            _sealed = false;
            TouchTests = 0;
            if (_spheres.Length >= slotCount) return;
            int capacity = GrownCapacity(_spheres.Length, slotCount);
            _spheres = new Vector4[capacity];
            _runs = new int[capacity];
        }

        /// <summary>Add one caster: its instance slot, the mesh run it belongs to and its world bounding sphere.
        /// Each slot is added at most once per frame. Call <see cref="Seal"/> after the last one.</summary>
        internal void Add(int slot, int run, Vector3 centre, float radius)
        {
            _spheres[slot] = new Vector4(centre, radius);
            _runs[slot] = run;
            // Written as a negation so a NaN radius, which fails every comparison, goes to the oversize list too.
            if (!(radius <= CellSize) || !TryCellOf(centre, out long key))
            {
                EnsureCapacity(ref _oversize, _oversizeCount + 1);
                _oversize[_oversizeCount++] = slot;
                return;
            }
            EnsureCapacity(ref _cellKeys, _binned + 1);
            EnsureCapacity(ref _cellSlots, _binned + 1);
            _cellKeys[_binned] = key;
            _cellSlots[_binned++] = slot;
        }

        /// <summary>Finish the frame's build: sort the binned casters by cell so a query finds a column with one
        /// binary search, and size the candidate scratch for the largest possible query.</summary>
        internal void Seal()
        {
            _cellKeys.AsSpan(0, _binned).Sort(_cellSlots.AsSpan(0, _binned));
            EnsureCapacity(ref _candidates, Count);
            _sealed = true;
        }

        /// <summary>The mesh run a slot added this frame belongs to, as handed to <see cref="Add"/>.</summary>
        internal int RunOf(int slot) => _runs[slot];

        /// <summary>
        /// Write into <paramref name="hits"/> every added caster that touches the light's shadowing shell, in
        /// ascending slot order: exactly what a walk over every added caster keeps. Clears
        /// <paramref name="hits"/> first, and allocates nothing once it has held a frame's worth.
        /// </summary>
        internal void Query(Vector3 lightPosAbsolute, float radius, float nearRadius,
            Vector3 exclusionMin, Vector3 exclusionMax, List<int> hits)
        {
            if (!_sealed) throw new InvalidOperationException("Seal the point caster index before querying it.");
            hits.Clear();
            Span<int> candidates = _candidates.AsSpan(0, GatherCandidates(lightPosAbsolute, radius));
            candidates.Sort();   // the full walk's order, which the signature and the span grouping both rely on
            foreach (int slot in candidates)
            {
                Vector4 sphere = _spheres[slot];
                TouchTests++;
                if (Scene3D.InstanceTouchesLight(new Vector3(sphere.X, sphere.Y, sphere.Z), sphere.W,
                    lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax))
                    hits.Add(slot);
            }
        }

        /// <summary>Copy every caster that could touch the light into the candidate scratch and return how many.
        /// The oversize list always. Binned casters from the columns under the light's grown box, or all of them
        /// when that box is unbinnable or covers more columns than there are binned casters, which is the full walk
        /// and still exact.</summary>
        int GatherCandidates(Vector3 light, float radius)
        {
            int count = 0;
            for (int i = 0; i < _oversizeCount; i++) _candidates[count++] = _oversize[i];
            if (_binned == 0) return count;

            float reach = radius + CellSize + QuerySlack;
            if (!(radius >= 0f)
                || !TryCellRange(light, reach, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
                || (long)(x1 - x0 + 1) * (y1 - y0 + 1) > _binned)
            {
                for (int i = 0; i < _binned; i++) _candidates[count++] = _cellSlots[i];
                return count;
            }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    long last = Pack(x, y, z1);
                    for (int i = LowerBound(Pack(x, y, z0)); i < _binned && _cellKeys[i] <= last; i++)
                        _candidates[count++] = _cellSlots[i];
                }
            return count;
        }

        /// <summary>The first sorted position whose key is at or past <paramref name="key"/>. Hand-written because
        /// a cell holds many casters and <c>BinarySearch</c> may land on any one of them.</summary>
        int LowerBound(long key)
        {
            int low = 0, high = _binned;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_cellKeys[middle] < key) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        static bool TryCellOf(Vector3 centre, out long key)
        {
            key = 0;
            if (!TryCell(centre.X, out int x) || !TryCell(centre.Y, out int y) || !TryCell(centre.Z, out int z))
                return false;
            key = Pack(x, y, z);
            return true;
        }

        // Non-short-circuit on purpose, so every bound is assigned whatever the answer.
        static bool TryCellRange(Vector3 light, float reach,
            out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
            => TryCell(light.X - reach, out x0) & TryCell(light.Y - reach, out y0) & TryCell(light.Z - reach, out z0)
             & TryCell(light.X + reach, out x1) & TryCell(light.Y + reach, out y1) & TryCell(light.Z + reach, out z1);

        static bool TryCell(float metres, out int cell)
        {
            float scaled = MathF.Floor(metres * InverseCellSize);
            // A NaN fails both comparisons, so a non-finite coordinate is refused here too.
            bool packable = scaled >= MinCell && scaled <= MaxCell;
            cell = packable ? (int)scaled : 0;
            return packable;
        }

        static long Pack(int x, int y, int z) =>
            (long)(x + CellBias) << 42 | (long)(y + CellBias) << 21 | (long)(z + CellBias);

        static void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (array.Length < required) Array.Resize(ref array, GrownCapacity(array.Length, required));
        }

        static int GrownCapacity(int current, int required) =>
            Math.Max(required, current > int.MaxValue / 2 ? required : Math.Max(16, current * 2));
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCasterIndexTests"`. Then run `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointShadow|FullyQualifiedName~ShadowCaster"`, which must stay green. Then `sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree`.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Rendering/PointCasterIndex.cs \
  KhaozEngine.Render3D/Scene3D.PointShadows.cs \
  KhaozEngine.Render.Tests/Render3D/PointCasterFullWalk.cs \
  KhaozEngine.Render.Tests/Render3D/PointCasterIndexTests.cs
git commit -m "render3d(point-shadows): index point casters by cell"
```

---

### Task A2: Point casters read from the index (signature, pass, dynamic rows)

**Branch:** `feature/frame-cost-point-shadows`

**Files:**
- Create: `KhaozEngine.Render3D/Scene3D.PointCasters.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs:92-96` (the index build before `AcquirePointShadowSlots`), and `:259-329` (the `PointCasterSignature` doc paragraph at 269-272 and the walk at 298-327)
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadowPass.cs:17-23` (class remarks), `:41-44` (drop `_pointCasterKinds`), `:254-290` (`BuildPointCasterSpans`)
- Create: `KhaozEngine.Render.Tests/Render3D/PointShadowRig.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/PointCasterFullWalk.cs` (add `Spans`)
- Test: `KhaozEngine.Render.Tests/Render3D/PointCasterWalkEquivalenceTests.cs`, `KhaozEngine.Render.Tests/Render3D/PointCasterSignatureTests.cs`

**Interfaces:**
- Consumes: Task A1's `PointCasterIndex` API and the sphere overload. Also existing members: `Scene3D.GroupInstances`, `AppendCasterSpans`, `MeshCastsShadows`, `ShadowCasterSpan`, `MeshRun`, `_runs`, `_meshes`, `_slots`, `_instanceData`, `_instanceCastKinds`, `_pointShadowFrame`, `_pointCasterSpans`.
- Produces (all internal):
  - `void Scene3D.EnsurePointCasterIndex()`
  - `void Scene3D.QueryPointCasters(Vector3, float, float, Vector3, Vector3)`, which fills `List<int> _pointCasterHits`
  - `ShadowCastKind Scene3D.PointCasterKind(int slot)`
  - `void Scene3D.AppendPointCasterSpans(List<ShadowCasterSpan>)`
  - `IReadOnlyList<ShadowCasterSpan> Scene3D.DebugPointCasterSpans(Vector3 lightPosAbsolute, float radius, float nearRadius = 0f, Vector3 exclusionMin = default, Vector3 exclusionMax = default)`
  - `int Scene3D.PointCasterTouchTests`
  - Test side: `PointShadowRig` (`Scene`, `Settings`, `ShadowPassDiagnostics RenderFrame(Action<Scene3D>)`) and `PointCasterFullWalk.Spans(IReadOnlyList<SceneInstances.Instance>, Func<MeshHandle, MeshBounds?>, bool terrainCastsShadows, Vector3, float, float, Vector3, Vector3) -> List<Scene3D.ShadowCasterSpan>`

`BuildPointCasterSpans` keeps its signature, and `PackedPointShadowSlot` and `RenderPointShadowSlots` are left as they are, so #1054 Task 6 can rebase onto this.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Render.Tests/Render3D/PointShadowRig.cs`
```csharp
using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>A headless point-shadow scene over the fake device, for tests that read what a frame decided (rebuild
/// counts, draw counts, the caster index) rather than what it drew. The key light is off so its pass stays out of
/// every reading, and frustum culling is off so every queued instance keeps the slot its queue position gives it,
/// which is what lets a test regroup the same queue itself.</summary>
internal sealed class PointShadowRig : IDisposable
{
    readonly FakeGpuDevice _device;
    readonly FakeGpuResourceFactory _factory;
    readonly IGpuTexture _targetTexture;
    readonly IGpuFramebuffer _target;

    internal PointShadowRig()
    {
        _device = new FakeGpuDevice();
        _factory = (FakeGpuResourceFactory)_device.Factory;
        _targetTexture = _factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = _factory.CreateFramebuffer(null, _targetTexture);
        Settings = new ShadowSettings { Mode = ShadowMode.Off };
        Scene = new Scene3D(_device, _target.Outputs, Settings) { FrustumCulling = false };
    }

    internal ShadowSettings Settings { get; }
    internal Scene3D Scene { get; }

    /// <summary>One whole frame of <paramref name="describe"/>, returning the shadow diagnostics it published.</summary>
    internal ShadowPassDiagnostics RenderFrame(Action<Scene3D> describe)
    {
        Scene.Begin();
        describe(Scene);
        Scene.PrepareFrame();
        using IGpuCommandList commands = _factory.CreateCommandList();
        commands.Begin();
        Scene.RenderInternal(commands, 16, 16, _target);
        commands.End();
        return Scene.LastShadowPassDiagnostics;
    }

    public void Dispose()
    {
        Scene.Dispose();
        _target.Dispose();
        _targetTexture.Dispose();
        _device.Dispose();
    }
}
```

Append to `PointCasterFullWalk.cs`. Add `using System;` to its usings, and add the method inside the class after `Slots`:
```csharp
    /// <summary>
    /// The pre-index <c>Scene3D.BuildPointCasterSpans</c>, copied line for line apart from reading the scene's state
    /// from arguments: the queue it grouped, each mesh's local bounds (null for a stale or unloaded handle, which
    /// stands for both of the scene's handle checks) and the terrain flag. It regroups the queue with the scene's
    /// own <c>GroupInstances</c>, so its slots are the scene's slots when frustum culling is off.
    /// </summary>
    internal static List<Scene3D.ShadowCasterSpan> Spans(IReadOnlyList<SceneInstances.Instance> items,
        Func<MeshHandle, MeshBounds?> boundsOf, bool terrainCastsShadows, Vector3 lightPosAbsolute, float radius,
        float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax)
    {
        var instanceData = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var instanceCastKinds = new List<ShadowCastKind>();
        Scene3D.GroupInstances(items, instanceData, runs, castKinds: instanceCastKinds);

        var spans = new List<Scene3D.ShadowCasterSpan>();
        var pointCasterKinds = new List<ShadowCastKind>();
        if (instanceData.Count == 0) return spans;
        for (int i = 0; i < instanceData.Count; i++)
            pointCasterKinds.Add(i < instanceCastKinds.Count ? instanceCastKinds[i] : ShadowCastKind.Opaque);

        foreach (Scene3D.MeshRun run in runs)
        {
            if (boundsOf(run.Mesh) is not { } bounds) continue;
            // Every mesh the tests load is a plain model mesh, splat material -1.
            if (!Scene3D.MeshCastsShadows(-1, terrainCastsShadows)) continue;
            for (uint s = 0; s < run.Count; s++)
            {
                int slot = (int)(run.Start + s);
                if (slot >= pointCasterKinds.Count) break;
                if (pointCasterKinds[slot] == ShadowCastKind.None) continue;
                if (!Scene3D.InstanceTouchesLight(bounds, instanceData[slot].Model, lightPosAbsolute, radius,
                    nearRadius, exclusionMin, exclusionMax))
                    pointCasterKinds[slot] = ShadowCastKind.None;
            }
            Scene3D.AppendCasterSpans(run.Mesh.Index, run.Mesh.Generation, run.Start, run.Count,
                pointCasterKinds, spans);
        }
        return spans;
    }
```

`KhaozEngine.Render.Tests/Render3D/PointCasterWalkEquivalenceTests.cs`
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point pass's caster spans, read from the index, are the spans the walk over every instance built, span for
/// span and in the same order. Compared on a real <see cref="Scene3D"/> over the fake device, so the eligibility
/// rules (a stale handle, an opted-out instance, dissolving and inverted casters) and the span grouping are the
/// production code, and the oracle is the old walk kept in <see cref="PointCasterFullWalk"/>.
/// </summary>
public sealed class PointCasterWalkEquivalenceTests
{
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void EveryLightsCasterSpansMatchTheFullWalk(int seed)
    {
        using var rig = new PointShadowRig();
        var meshes = new TestMeshes(rig.Scene);
        var random = new Random(seed);
        int lights = 0, touched = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            // Frame 0 queues nothing. Later frames shrink and grow, so the index's reused arrays hold slots from an
            // earlier frame past the live count, which a query must never read.
            List<Queued> queue = frame == 0 ? new List<Queued>() : RandomQueue(random, meshes, random.Next(20, 300));
            // No light is queued, so the frame never builds the index itself and the first ask below builds it
            // lazily, which is the path the pass's diagnostic entry points take.
            rig.RenderFrame(s => QueueAll(s, queue));
            SceneInstances mirror = Mirror(queue);
            for (int l = 0; l < 24; l++)
            {
                (Vector3 at, float radius, float near, Vector3 min, Vector3 max) = RandomLight(random);
                List<Scene3D.ShadowCasterSpan> expected = PointCasterFullWalk.Spans(
                    mirror.Items, meshes.BoundsOf, false, at, radius, near, min, max);
                Assert.Equal(expected, rig.Scene.DebugPointCasterSpans(at, radius, near, min, max));
                lights++;
                if (expected.Count > 0) touched++;
            }
        }
        Assert.True(touched >= lights / 4, $"only {touched} of {lights} lights touched a caster");
    }

    [Fact]
    public void ADynamicRowDrawsExactlyTheFullWalksSpans()
    {
        using var rig = new PointShadowRig();
        var meshes = new TestMeshes(rig.Scene);
        List<Queued> queue = RandomQueue(new Random(1110), meshes, 200);
        queue.Insert(0, new Queued(meshes.Ground, Matrix4x4.Identity, 0f, true, false));   // under the light
        var light = new Vector3(4f, 2f, -3f);
        void Queue(Scene3D s)
        {
            QueueAll(s, queue);
            s.AddLight(light, Color.White, 12f, 1f, LightShadow.Dynamic);
        }

        rig.RenderFrame(Queue);                                  // asks for the atlas
        ShadowPassDiagnostics drawn = rig.RenderFrame(Queue);    // draws the dynamic row

        List<Scene3D.ShadowCasterSpan> expected = PointCasterFullWalk.Spans(
            Mirror(queue).Items, meshes.BoundsOf, false, light, 12f, 0f, default, default);
        Assert.Equal(1, drawn.PointDynamicRenders);
        Assert.NotEmpty(expected);
        Assert.Equal(6 * expected.Count, drawn.PointFaceDrawCalls);   // one draw per span per face
    }

    [Fact]
    public void ASteadyFrameTestsOnlyTheCastersNearEachStaticLight()
    {
        using var rig = new PointShadowRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        const int side = 40;
        const float spacing = 16f;
        void Queue(Scene3D s)
        {
            for (int x = 0; x < side; x++)
                for (int z = 0; z < side; z++)
                    s.Draw(box, Matrix4x4.CreateTranslation(x * spacing, 0.5f, z * spacing));
            for (int light = 0; light < 8; light++)
                s.AddLight(new Vector3((5 * light + 3) * spacing, 3f, (4 * light + 1) * spacing), Color.White,
                    6f, 1f, LightShadow.Static(light + 1));
        }

        rig.RenderFrame(Queue);                                  // asks for the atlas
        rig.RenderFrame(Queue);                                  // draws all eight rows
        ShadowPassDiagnostics steady = rig.RenderFrame(Queue);

        Assert.Equal(0, steady.PointStaticRebuilds);
        Assert.Equal(8, rig.Scene.PointShadowedLights);
        // Eight signatures, one query each. Each query's window is under 44.5 m per horizontal axis (radius 6, one
        // cell, the slack and the cell floor), which holds at most three lattice columns 16 m apart, so at most
        // nine tests a light. The walk over every run tested all 1,600 instances for every light.
        Assert.InRange(rig.Scene.PointCasterTouchTests, 8, 8 * 9);
    }

    readonly record struct Queued(MeshHandle Mesh, Matrix4x4 World, float Dissolve, bool Casts, bool Invert);

    /// <summary>The meshes every case queues: a small prop, a caster far wider than a cell, a ground plane and a
    /// handle whose mesh was unloaded, with the local bounds the scene computed for each at load.</summary>
    sealed class TestMeshes
    {
        readonly Dictionary<(int, int), MeshBounds> _bounds = new();

        internal TestMeshes(Scene3D scene)
        {
            Small = Load(scene, MeshPrimitives.Box(1f));
            Huge = Load(scene, MeshPrimitives.Box(30f));
            Ground = Load(scene, MeshPrimitives.Plane(40f, 40f));
            Stale = scene.LoadMesh(MeshPrimitives.Box(2f));
            scene.UnloadMesh(Stale);   // still queued, which the walk skips by generation
        }

        internal MeshHandle Small { get; }
        internal MeshHandle Huge { get; }
        internal MeshHandle Ground { get; }
        internal MeshHandle Stale { get; }

        internal MeshBounds? BoundsOf(MeshHandle mesh) =>
            _bounds.TryGetValue((mesh.Index, mesh.Generation), out MeshBounds bounds) ? bounds : null;

        MeshHandle Load(Scene3D scene, GltfMesh mesh)
        {
            MeshHandle handle = scene.LoadMesh(mesh);
            _bounds[(handle.Index, handle.Generation)] = MeshBounds.FromVertices(mesh.Vertices);
            return handle;
        }
    }

    static List<Queued> RandomQueue(Random random, TestMeshes meshes, int count)
    {
        MeshHandle[] pick = { meshes.Small, meshes.Small, meshes.Small, meshes.Small, meshes.Huge, meshes.Ground, meshes.Stale };
        var queue = new List<Queued>(count);
        for (int i = 0; i < count; i++)
        {
            var at = new Vector3(Range(random, -60f, 60f), Range(random, -4f, 12f), Range(random, -60f, 60f));
            if (random.Next(5) == 0) at = Snap(at);   // on a cell corner
            Matrix4x4 world = Matrix4x4.CreateScale(Range(random, 0.5f, 3f))
                * Matrix4x4.CreateRotationY(Range(random, 0f, MathF.Tau))
                * Matrix4x4.CreateTranslation(at);
            float dissolve = random.Next(4) == 0 ? Range(random, 0.1f, 0.9f) : 0f;
            queue.Add(new Queued(pick[random.Next(pick.Length)], world, dissolve,
                Casts: random.Next(8) != 0, Invert: dissolve > 0f && random.Next(3) == 0));
        }
        return queue;
    }

    static void QueueAll(Scene3D scene, List<Queued> queue)
    {
        foreach (Queued q in queue)
            scene.Draw(q.Mesh, q.World, Color.White, Material.None, q.Dissolve, 0f, Color.White, q.Casts, q.Invert);
    }

    static SceneInstances Mirror(List<Queued> queue)
    {
        var mirror = new SceneInstances();
        foreach (Queued q in queue)
            mirror.Add(q.Mesh, q.World, Color.White, Material.None, q.Dissolve, 0f, Color.White, q.Casts, q.Invert);
        return mirror;
    }

    static (Vector3 At, float Radius, float Near, Vector3 Min, Vector3 Max) RandomLight(Random random)
    {
        float radius = Range(random, 2f, 24f);
        var at = new Vector3(Range(random, -70f, 70f), Range(random, 0f, 8f), Range(random, -70f, 70f));
        if (random.Next(3) == 0) at = Snap(at) + new Vector3(radius + PointCasterIndex.CellSize);
        float near = random.Next(4) == 0 ? Range(random, 0f, radius * 0.5f) : 0f;
        bool boxed = random.Next(4) == 0;
        var half = new Vector3(Range(random, 0.1f, 3f), Range(random, 0.1f, 3f), Range(random, 0.1f, 3f));
        return (at, radius, near, boxed ? at - half : default, boxed ? at + half : default);
    }

    static Vector3 Snap(Vector3 p) => new(
        MathF.Round(p.X / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Y / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Z / PointCasterIndex.CellSize) * PointCasterIndex.CellSize);

    static float Range(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);
}
```

`KhaozEngine.Render.Tests/Render3D/PointCasterSignatureTests.cs`. These are regression guards and pass before and after this task. They pin headlessly what the GPU signature tests pin with pixels.
```csharp
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// A static point light's row is rebuilt when, and only when, something its map depends on changed. Headless over
/// the fake device, reading <see cref="ShadowPassDiagnostics.PointStaticRebuilds"/>, so the change detection is
/// pinned on every CI leg rather than only where the GPU suites run.
/// </summary>
public sealed class PointCasterSignatureTests
{
    static readonly Vector3 Light = new(0f, 2f, 0f);
    const float Radius = 6f;

    static void QueueLight(Scene3D scene) => scene.AddLight(Light, Color.White, Radius, 1f, LightShadow.Static(1));

    /// <summary>Frame one asks for the atlas, frame two draws the row once.</summary>
    static void Warm(PointShadowRig rig, System.Action<Scene3D> queue)
    {
        rig.RenderFrame(queue);
        Assert.Equal(1, rig.RenderFrame(queue).PointStaticRebuilds);
    }

    [Fact]
    public void ACasterMovingFarFromTheLightNeverDirtiesItsRow()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float farX)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.5f, 0f));
            s.Draw(box, Matrix4x4.CreateTranslation(farX, 0.5f, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 100f));
        for (int frame = 1; frame <= 4; frame++)
        {
            float x = 100f + frame * 3f;
            Assert.Equal(0, rig.RenderFrame(s => Queue(s, x)).PointStaticRebuilds);
        }
    }

    [Fact]
    public void ACasterMovingOneFloatStepInsideTheLightRebuildsItsRow()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float x)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(x, 0.5f, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 1.5f));
        float moved = MathF.BitIncrement(1.5f);
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, moved)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, moved)).PointStaticRebuilds);
    }

    [Fact]
    public void ACasterEnteringAndLeavingTheLightAcrossACellEdgeRebuildsItsRowEachTime()
    {
        // x = 9 is outside the light (9.1 m against a 6.9 m reach) and one cell over from x = 4, which is inside.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float x)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(x, 0.5f, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 9f));
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, 4f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, 4f)).PointStaticRebuilds);
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, 9f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, 9f)).PointStaticRebuilds);
    }

    [Fact]
    public void AnOversizeGroundMovingUnderTheLightRebuildsItsRow()
    {
        // A 40 m plane has a 28 m sphere, wider than a cell, so the index only ever finds it through the oversize
        // list.
        using var rig = new PointShadowRig();
        MeshHandle ground = rig.Scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        void Queue(Scene3D s, float y)
        {
            s.Draw(ground, Matrix4x4.CreateTranslation(0f, y, 0f));
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, 0f));
        Assert.Equal(0, rig.RenderFrame(s => Queue(s, 0f)).PointStaticRebuilds);
        Assert.Equal(1, rig.RenderFrame(s => Queue(s, 0.01f)).PointStaticRebuilds);
    }

    [Fact]
    public void AnOptedOutCasterMovingInsideTheLightNeverDirtiesItsRow()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Queue(Scene3D s, float x)
        {
            s.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.5f, 0f));
            s.Draw(box, Matrix4x4.CreateTranslation(x, 0.5f, 1f), Color.White, Material.None, castsShadows: false);
            QueueLight(s);
        }
        Warm(rig, s => Queue(s, -1f));
        for (int frame = 1; frame <= 3; frame++)
        {
            float x = -1f + frame * 0.5f;
            Assert.Equal(0, rig.RenderFrame(s => Queue(s, x)).PointStaticRebuilds);
        }
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCasterWalkEquivalenceTests|FullyQualifiedName~PointCasterSignatureTests"`
Expected: the build fails with CS1061. `Scene3D` does not contain a definition for `DebugPointCasterSpans` or for `PointCasterTouchTests`.

- [ ] **Step 3: Implement**

`KhaozEngine.Render3D/Scene3D.PointCasters.cs`
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// WHICH RIGID INSTANCES CAN CAST INTO A POINT LIGHT THIS FRAME, answered once per frame into a
/// <see cref="PointCasterIndex"/> and then asked per light (issue #1110). Both halves of the point shadow ask here:
/// the static signature in <c>Scene3D.PointShadows.cs</c> and the spans the pass in <c>Scene3D.PointShadowPass.cs</c>
/// draws, static and dynamic rows alike. One answer for both is what keeps a signature from missing a change the
/// pass would have drawn.
/// <para>
/// BUILT LAZILY, ONCE PER POINT-SHADOW FRAME. <c>PreparePointShadows</c> builds it before the first signature, and
/// every query goes through the same guard, so the diagnostic entry points that render a row outside a frame's
/// request (<see cref="DebugRenderPointShadowSlot"/>, or a test driving <see cref="RenderPointShadowSlots"/>
/// directly) read an index built from the instances they are drawing rather than an older one.
/// </para>
/// </summary>
public sealed partial class Scene3D
{
    readonly PointCasterIndex _pointCasterIndex = new();

    // One light's touching casters, ascending by slot. Shared by the signature and the pass, which never hold it
    // at the same time.
    readonly List<int> _pointCasterHits = new();

    // The point-shadow frame the index was last built for. Starts at -1 so a scene that has never rendered still
    // builds on its first ask.
    int _pointCasterIndexFrame = -1;

    /// <summary>How many exact touch tests the point caster queries ran on the last built frame. Internal for the
    /// count tests, which assert it follows the casters near each light rather than every instance.</summary>
    internal int PointCasterTouchTests => _pointCasterIndex.TouchTests;

    /// <summary>
    /// Build this point-shadow frame's caster index, once. Every rigid slot the point pass could draw goes in with
    /// its world bounding sphere, under exactly the rules the pass has always applied: a run whose handle is stale or
    /// whose mesh is gone is skipped, a splat mesh casts only under <see cref="TerrainCastsShadows"/>, and an instance
    /// classified <see cref="ShadowCastKind.None"/> never casts. The sphere is the same
    /// <see cref="MeshBounds.WorldSphere"/> call on the same inputs the touch test used to make once per light.
    /// </summary>
    void EnsurePointCasterIndex()
    {
        if (_pointCasterIndexFrame == _pointShadowFrame) return;
        _pointCasterIndexFrame = _pointShadowFrame;

        // Read by reference: an InstanceData is 128 bytes and only its matrix is wanted here.
        Span<ModelRenderer.InstanceData> instances = CollectionsMarshal.AsSpan(_instanceData);
        _pointCasterIndex.Reset(instances.Length);
        for (int r = 0; r < _runs.Count; r++)
        {
            MeshRun run = _runs[r];
            if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
            var m = _meshes[run.Mesh.Index];
            if (m is not { } mesh) continue;
            if (!MeshCastsShadows(mesh.SplatMaterial, TerrainCastsShadows)) continue;
            for (uint s = 0; s < run.Count; s++)
            {
                int slot = (int)(run.Start + s);
                if (slot >= instances.Length) break;
                if (PointCasterKind(slot) == ShadowCastKind.None) continue;
                mesh.Bounds.WorldSphere(instances[slot].Model, out Vector3 centre, out float radius);
                _pointCasterIndex.Add(slot, r, centre, radius);
            }
        }
        _pointCasterIndex.Seal();
    }

    /// <summary>Fill <see cref="_pointCasterHits"/> with the casters that touch one light, ascending by slot,
    /// building the index first when this frame has not.</summary>
    void QueryPointCasters(Vector3 lightPosAbsolute, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        EnsurePointCasterIndex();
        _pointCasterIndex.Query(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax, _pointCasterHits);
    }

    /// <summary>A slot's cast kind, with the unclassified default the grouping has always implied.</summary>
    ShadowCastKind PointCasterKind(int slot) =>
        slot < _instanceCastKinds.Count ? _instanceCastKinds[slot] : ShadowCastKind.Opaque;

    /// <summary>
    /// Group <see cref="_pointCasterHits"/> into caster spans: maximal stretches of consecutive slots in one mesh run
    /// with one cast kind, in slot order. That is what <see cref="AppendCasterSpans"/> makes of a run whose every
    /// missed slot reads <see cref="ShadowCastKind.None"/>, which is how the pass built its spans before the index,
    /// without visiting a slot the light does not touch.
    /// </summary>
    void AppendPointCasterSpans(List<ShadowCasterSpan> spans)
    {
        int run = -1;
        uint start = 0, count = 0;
        ShadowCastKind kind = ShadowCastKind.None;
        foreach (int slot in _pointCasterHits)
        {
            int slotRun = _pointCasterIndex.RunOf(slot);
            ShadowCastKind slotKind = PointCasterKind(slot);
            if (count > 0 && slotRun == run && slotKind == kind && (uint)slot == start + count)
            {
                count++;
                continue;
            }
            if (count > 0) AddPointCasterSpan(spans, run, start, count, kind);
            run = slotRun;
            start = (uint)slot;
            count = 1;
            kind = slotKind;
        }
        if (count > 0) AddPointCasterSpan(spans, run, start, count, kind);
    }

    void AddPointCasterSpan(List<ShadowCasterSpan> spans, int run, uint start, uint count, ShadowCastKind kind)
    {
        MeshHandle mesh = _runs[run].Mesh;
        spans.Add(new ShadowCasterSpan(mesh.Index, mesh.Generation, start, count, kind));
    }

    /// <summary>Diagnostic: this light's caster spans as the pass would draw them now. For the equivalence tests,
    /// which compare them with the walk over every instance. The list is the pass's own scratch and the next build
    /// overwrites it.</summary>
    internal IReadOnlyList<ShadowCasterSpan> DebugPointCasterSpans(Vector3 lightPosAbsolute, float radius,
        float nearRadius = 0f, Vector3 exclusionMin = default, Vector3 exclusionMax = default)
    {
        BuildPointCasterSpans(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
        return _pointCasterSpans;
    }
}
```

`KhaozEngine.Render3D/Scene3D.PointShadows.cs`: in `PreparePointShadows`, directly before `AcquirePointShadowSlots(cache, frame);` (line 95):
```csharp
            // THE CASTERS ARE INDEXED ONCE, here, where a request and a live atlas both exist. Every static
            // signature and every row the pass draws below asks this one index rather than walking every instance.
            EnsurePointCasterIndex();
```

In `PointCasterSignature`, replace lines 298-327 (the comment, the `instances` span and the `foreach (MeshRun run in _runs)` block) with the following. The FNV mixing is unchanged here, so the signature values are the same as before.
```csharp
            // The casters come from the frame's index (Scene3D.PointCasters.cs): exactly the ones the pass will draw
            // for this light, in ascending slot order, which is the order the walk over every run visited them in.
            // Read by reference: an InstanceData is 128 bytes and this runs once per touching caster per static
            // request per frame.
            QueryPointCasters(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
            Span<ModelRenderer.InstanceData> instances = CollectionsMarshal.AsSpan(_instanceData);
            foreach (int slot in _pointCasterHits)
            {
                MeshHandle mesh = _runs[_pointCasterIndex.RunOf(slot)].Mesh;
                ref ModelRenderer.InstanceData data = ref instances[slot];
                MixPointSignature(ref hash, (ulong)(uint)mesh.Index);
                MixPointSignature(ref hash, (ulong)(uint)mesh.Generation);
                MixPointSignature(ref hash, (ulong)(uint)PointCasterKind(slot));
                MixPointSignature(ref hash, MatrixBits(data.Model));
                MixPointSignature(ref hash, (ulong)(uint)BitConverter.SingleToInt32Bits(data.Dissolve.X));
            }
```
Replace the doc paragraph at lines 269-272 with:
```csharp
        /// <para>
        /// It reads the same index query <c>BuildPointCasterSpans</c> reads, so a signature can only miss a change
        /// that the pass would also not have drawn.
        /// </para>
```

`KhaozEngine.Render3D/Scene3D.PointShadowPass.cs`: replace the remarks paragraph at lines 17-23:
```csharp
    /// <para>
    /// The casters come from the frame's <see cref="PointCasterIndex"/>, which holds every instance the cascade walk
    /// (<see cref="BuildShadowCasterSpans"/>) would draw, binned by where it stands, and answers a light with the ones
    /// whose world bounding sphere intersects the light sphere (design decision 8). They are grouped into spans
    /// exactly as <see cref="AppendCasterSpans"/> groups the cascade pass's, so "what a caster span is" still reads
    /// the same for both passes.
    /// </para>
```
Replace lines 41-44:
```csharp
        // Per-render scratch, reused rather than reallocated: the caster spans one light draws.
        readonly List<ShadowCasterSpan> _pointCasterSpans = new();
```
Replace lines 254-290:
```csharp
        /// <summary>
        /// Build <see cref="_pointCasterSpans"/>: this light's caster draw list, in the exact order
        /// <see cref="RenderPointShadowSlots"/> draws it, for a static rebuild and a dynamic light alike. The casters
        /// are the frame's <see cref="PointCasterIndex"/> answer for this light. A stale handle, a receive-only splat
        /// mesh and anything the consumer opted out of casting are never in the index, and the light sphere, the
        /// near radius and the exclusion box are its exact test.
        /// </summary>
        void BuildPointCasterSpans(Vector3 lightPosAbsolute, float radius, float nearRadius,
            Vector3 exclusionMin, Vector3 exclusionMax)
        {
            _pointCasterSpans.Clear();
            if (_instanceData.Count == 0) return;
            QueryPointCasters(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
            AppendPointCasterSpans(_pointCasterSpans);
        }
```

- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Release`.
Then `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCaster|FullyQualifiedName~PointShadow|FullyQualifiedName~ShadowCaster"`. `PointShadowAllocationTests.PreparePointShadowsOnASteadyFrameAllocatesNothing` must pass.
Then the GPU suites on local Metal: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.PointShadow"`, with zero skipped. This covers `AStaticMapIsNotRebuiltWhileNothingUnderItMoves`, `AStaticMapIsRebuiltWhenItsCastersMove`, the face-resolution, near-radius and exclusion-box redraw tests, and `PointShadowPassGpuTests`, which drives the debug paths.
Finally `sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree`.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Scene3D.PointCasters.cs \
  KhaozEngine.Render3D/Scene3D.PointShadows.cs \
  KhaozEngine.Render3D/Scene3D.PointShadowPass.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowRig.cs \
  KhaozEngine.Render.Tests/Render3D/PointCasterFullWalk.cs \
  KhaozEngine.Render.Tests/Render3D/PointCasterWalkEquivalenceTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointCasterSignatureTests.cs
git commit -m "render3d(point-shadows): read point casters from the index"
```

---

### Task A3: Word-wise signature mixer

**Branch:** `feature/frame-cost-point-shadows`

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs`: the `PointCasterSignature` seed and per-caster words (originally lines 285-325), and `MatrixBits` plus `MixPointSignature` (originally lines 377-406)
- Test: `KhaozEngine.Render.Tests/Render3D/PointSignatureMixerTests.cs`

**Interfaces:**
- Consumes: Task A2's `QueryPointCasters`, `_pointCasterHits`, `PointCasterKind`, `_pointCasterIndex.RunOf`
- Produces:
  - `internal static void Scene3D.MixPointSignature(ref ulong hash, ulong word)`
  - `internal static void Scene3D.MixPointSignature(ref ulong hash, in Matrix4x4 model)`
  - private `SignatureWord(uint low, uint high) -> ulong` and `FloatBits(float) -> uint`
  - `MatrixBits` is removed

- [ ] **Step 1: Write the failing test**
```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point caster signature's mixer: xxHash64's round over whole 64-bit words, with fixed constants. Fixed is the
/// point. <c>System.HashCode</c> is seeded per process, and while a signature is only compared within one process, a
/// mixer that changed between runs would make a rebuild count irreproducible.
/// </summary>
public sealed class PointSignatureMixerTests
{
    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(0x0123456789ABCDEFUL, 1UL)]
    [InlineData(ulong.MaxValue, 0x8000000000000000UL)]
    public void OneWordIsOneFixedMultiplyAndRotateRound(ulong hash, ulong word)
    {
        ulong expected = BitOperations.RotateLeft(hash + word * 0xC2B2AE3D27D4EB4FUL, 31) * 0x9E3779B185EBCA87UL;
        Scene3D.MixPointSignature(ref hash, word);
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void AMatrixIsEightWordsOfTwoElementsInRowOrder()
    {
        var m = new Matrix4x4(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f);
        ulong byMatrix = 7UL, byWords = 7UL;
        Scene3D.MixPointSignature(ref byMatrix, m);
        for (int element = 1; element <= 16; element += 2)
            Scene3D.MixPointSignature(ref byWords, (uint)BitConverter.SingleToInt32Bits(element)
                | (ulong)(uint)BitConverter.SingleToInt32Bits(element + 1) << 32);
        Assert.Equal(byWords, byMatrix);
    }

    [Fact]
    public void OneFloatStepInAnyMatrixElementChangesTheSignature()
    {
        // For a fixed word the round is a bijection of the running value, and for a fixed running value it is
        // injective in the word, so two sequences that differ in one word can never collide. Pinned per element.
        Matrix4x4 m = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(12.5f, 1f, -40f);
        ulong reference = 0;
        Scene3D.MixPointSignature(ref reference, m);
        for (int e = 0; e < 16; e++)
        {
            Matrix4x4 moved = m;
            moved[e / 4, e % 4] = MathF.BitIncrement(moved[e / 4, e % 4]);
            ulong changed = 0;
            Scene3D.MixPointSignature(ref changed, moved);
            Assert.NotEqual(reference, changed);
        }
    }
}
```
- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointSignatureMixerTests"`
Expected: the build fails. CS0122 because `Scene3D.MixPointSignature` is inaccessible due to its protection level. CS1503 on the `Matrix4x4` argument because no matrix overload exists.

- [ ] **Step 3: Implement**
In `PointCasterSignature`, change the first line to `ulong hash = PointSignatureSeed;` and keep the eleven `Quantise` light words as they are. Replace the per-caster loop body from Task A2:
```csharp
            foreach (int slot in _pointCasterHits)
            {
                MeshHandle mesh = _runs[_pointCasterIndex.RunOf(slot)].Mesh;
                ref ModelRenderer.InstanceData data = ref instances[slot];
                MixPointSignature(ref hash, SignatureWord((uint)mesh.Index, (uint)mesh.Generation));
                MixPointSignature(ref hash, SignatureWord(FloatBits(data.Dissolve.X), (uint)PointCasterKind(slot)));
                MixPointSignature(ref hash, data.Model);
            }
```
Replace `MatrixBits` and `MixPointSignature` (originally lines 377-406):
```csharp
        // xxHash64's published primes. Fixed constants rather than System.HashCode, whose seed changes with every
        // process, so a signature, and so a rebuild count, is the same in every run.
        const ulong PointSignatureSeed = 0x27D4EB2F165667C5UL;
        const ulong PointSignaturePrimeA = 0x9E3779B185EBCA87UL;
        const ulong PointSignaturePrimeB = 0xC2B2AE3D27D4EB4FUL;

        /// <summary>
        /// Fold one whole 64-bit word into a signature with xxHash64's round: a multiply, a rotate and a multiply.
        /// The byte-wise FNV it replaced cost eight multiplies per word and was most of the point-shadow frame.
        /// <para>
        /// A ONE-WORD CHANGE CAN NEVER COLLIDE. For a fixed word the round is a bijection of the running value (an
        /// add, a rotate and an odd multiply), and for a fixed running value it is injective in the word (the word is
        /// multiplied by an odd prime), so a caster that moved, changed kind or changed mesh always changes its
        /// light's signature. A change in how many casters touch the light is left to the 64-bit odds.
        /// </para>
        /// Internal for the mixer tests.
        /// </summary>
        internal static void MixPointSignature(ref ulong hash, ulong word)
        {
            hash += word * PointSignaturePrimeB;
            hash = BitOperations.RotateLeft(hash, 31);
            hash *= PointSignaturePrimeA;
        }

        /// <summary>Fold a world matrix in as eight words, two elements each in row order, by their raw bits, so a
        /// caster that moves by one float step changes the signature.</summary>
        internal static void MixPointSignature(ref ulong hash, in Matrix4x4 model)
        {
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M11), FloatBits(model.M12)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M13), FloatBits(model.M14)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M21), FloatBits(model.M22)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M23), FloatBits(model.M24)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M31), FloatBits(model.M32)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M33), FloatBits(model.M34)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M41), FloatBits(model.M42)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M43), FloatBits(model.M44)));
        }

        static ulong SignatureWord(uint low, uint high) => low | (ulong)high << 32;

        static uint FloatBits(float value) => (uint)BitConverter.SingleToInt32Bits(value);
```
`BitOperations` is in `System.Numerics`, which the file already imports.

- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointSignatureMixerTests|FullyQualifiedName~PointCaster|FullyQualifiedName~PointShadow"`.
Then `KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.PointShadow"`.
Then `sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree`.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Scene3D.PointShadows.cs \
  KhaozEngine.Render.Tests/Render3D/PointSignatureMixerTests.cs
git commit -m "render3d(point-shadows): mix point signatures by word"
```

---

### Task A4: Dissolve quantized in the point signature (#1111)

**Branch:** `feature/frame-cost-point-shadows`

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs`: the per-caster dissolve word in `PointCasterSignature`, the signature summary (originally lines 260-262) and its "ONLY THE LIGHT IS QUANTISED" paragraph (originally lines 273-280), plus a new `PointDissolveWord`
- Test: `KhaozEngine.Render.Tests/Render3D/PointCasterSignatureTests.cs`

**Interfaces:**
- Consumes: Task A3's `SignatureWord`, `MixPointSignature`, and Task A2's `PointShadowRig`
- Produces: private `static uint Scene3D.PointDissolveWord(in ModelRenderer.InstanceData data)`, `const float PointDissolveSteps = 16f`, `const uint PointDissolveComplementBit = 1u << 8`

The spec asks for the churn to be confirmed before the change. So this task runs the reproduction against the current code first, then inverts it. That adds one confirm step to the usual five.

- [ ] **Step 1: Write the reproduction, run it, and confirm the churn**
Add to `PointCasterSignatureTests`:
```csharp
    static void QueueFadingProp(Scene3D scene, MeshHandle box, float threshold, float complement = 0f)
    {
        scene.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.5f, 0f), Color.White, Material.None,
            threshold, 0.05f, Color.White, true, false, complement);
        QueueLight(scene);
    }

    [Fact]
    public void AFadeAdvancingEveryFrameRebuildsTheStaticRowEveryFrame()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.35f));
        for (int frame = 1; frame <= 5; frame++)
        {
            float threshold = 0.35f + frame * 0.01f;
            Assert.Equal(1, rig.RenderFrame(s => QueueFadingProp(s, box, threshold)).PointStaticRebuilds);
        }
    }
```
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCasterSignatureTests.AFadeAdvancingEveryFrame"`
Expected: PASS. That confirms one static rebuild on every frame of a fade on the current code. If it fails, stop and report, because the premise of #1111 does not hold on this code.

- [ ] **Step 2: Invert the reproduction into the failing tests**
Delete `AFadeAdvancingEveryFrameRebuildsTheStaticRowEveryFrame` and add:
```csharp
    [Fact]
    public void AFadeThatStaysInsideOneStepDoesNotRebuildTheStaticRow()
    {
        // 0.35 to 0.40 is 5.6 to 6.4 sixteenths, all step 6, so six frames of a fade band leave the map alone.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.35f));
        for (int frame = 1; frame <= 5; frame++)
        {
            float threshold = 0.35f + frame * 0.01f;
            Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, threshold)).PointStaticRebuilds);
        }
    }

    [Fact]
    public void AFadeCrossingOneStepRebuildsTheStaticRowExactlyOnce()
    {
        // 0.40 is 6.4 sixteenths (step 6) and 0.41 is 6.56 (step 7). The two frames after it stay on step 7.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.40f));
        Assert.Equal(1, rig.RenderFrame(s => QueueFadingProp(s, box, 0.41f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.42f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.43f)).PointStaticRebuilds);
    }

    [Fact]
    public void ThresholdsPastOneAreAllTheLastStep()
    {
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 1.2f));
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 1.4f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 1.0f)).PointStaticRebuilds);
    }

    [Fact]
    public void AComplementFlipAtAConstantThresholdRebuildsTheStaticRow()
    {
        // The dissolve pipeline keeps the opposite noise set once the complement is set, so the map changes while
        // the threshold does not.
        using var rig = new PointShadowRig();
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        Warm(rig, s => QueueFadingProp(s, box, 0.5f, complement: 0f));
        Assert.Equal(1, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 1f)).PointStaticRebuilds);
        Assert.Equal(0, rig.RenderFrame(s => QueueFadingProp(s, box, 0.5f, complement: 1f)).PointStaticRebuilds);
    }
```
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCasterSignatureTests"`
Expected: four failures. Each reads `Assert.Equal() Failure`:
- the within-step test: expected 0, actual 1
- the crossing test at 0.42: expected 0, actual 1
- the clamp test: expected 0, actual 1
- the complement test: expected 1, actual 0

The five Task A2 tests stay green.

- [ ] **Step 3: Implement**
Change the per-caster dissolve word in `PointCasterSignature`:
```csharp
                MixPointSignature(ref hash, SignatureWord(PointDissolveWord(data), (uint)PointCasterKind(slot)));
```
Add below `FloatBits`:
```csharp
        /// <summary>
        /// The dissolve half of one caster's signature word: its threshold in <see cref="PointDissolveSteps"/> steps,
        /// <c>round(clamp(threshold, 0, 1) * 16)</c>, plus <see cref="PointDissolveComplementBit"/> when the caster
        /// keeps the complementary noise set.
        /// <para>
        /// QUANTISED ON PURPOSE, AND ONLY HERE (issue #1111). A prop inside a distance fade or an LOD crossfade band
        /// changes its threshold on every frame the draw focus moves, and the raw bits made every static light over
        /// it dirty on every one of those frames, which spent the whole rebuild budget on dithers nobody could tell
        /// apart. Sixteen steps keep a fading prop's point shadow moving in visible increments. The row itself is
        /// still drawn with each instance's ACTUAL threshold at the moment it is rebuilt, so between steps the map
        /// keeps the previous step's dither. That is the one intended pixel difference in the point pass.
        /// </para>
        /// <para>
        /// THE COMPLEMENT FLAG IS PART OF WHAT IS DRAWN. The dissolve pipeline keeps the exact opposite noise set when
        /// it is set, so a flip at a constant threshold is a different map, and a signature that left it out kept the
        /// old one. It is read the way the shader reads it, above one half.
        /// </para>
        /// </summary>
        static uint PointDissolveWord(in ModelRenderer.InstanceData data)
        {
            // A NaN threshold passes the clamp and converts to step 0, which is still one fixed answer per frame.
            uint step = (uint)(int)MathF.Round(Math.Clamp(data.Dissolve.X, 0f, 1f) * PointDissolveSteps);
            return data.DissolveComplement > 0.5f ? step | PointDissolveComplementBit : step;
        }

        const float PointDissolveSteps = 16f;
        const uint PointDissolveComplementBit = 1u << 8;
```
In the `PointCasterSignature` summary, change "by mesh identity, world matrix, cast kind and dissolve threshold" to "by mesh identity, world matrix, cast kind and dissolve (its threshold in sixteen steps and its complement phase)". Retitle the paragraph "ONLY THE LIGHT IS QUANTISED" to "ONLY THE LIGHT AND THE DISSOLVE ARE QUANTISED", and add one sentence: "The dissolve is quantised for a different reason, see <see cref="PointDissolveWord"/>."

- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointCasterSignatureTests|FullyQualifiedName~PointCaster|FullyQualifiedName~PointShadow|FullyQualifiedName~PointSignatureMixer"`.
Then `KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.PointShadow"`, which checks that rendering is unchanged.
Then `sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree`.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Scene3D.PointShadows.cs \
  KhaozEngine.Render.Tests/Render3D/PointCasterSignatureTests.cs
git commit -m "render3d(point-shadows): quantize dissolve in point signatures"
```
The key-light half of the complement gap is already filed as
[#1131](https://github.com/APKiwiOrg/KhaozEngine/issues/1131) and stays out of this round.

---


### Decisions and risks

1. The index build is not confined to `PreparePointShadows`. `Scene3D.PointShadowPass.cs:302-320` and `KhaozEngine.Render.Tests/Gpu/PointShadowPassGpuTests.cs:294-326` render rows outside a requested frame, so the plan uses a lazy build stamped with `_pointShadowFrame` (declared at `Scene3D.PointShadows.cs:47`, incremented at `:68`). `PreparePointShadows` triggers it after the atlas check at `:86-90`.
2. The spec's "grown by one cell size" misses casters at float precision (`Scene3D.PointShadows.cs:352-355`). The plan adds `QuerySlack`, oversize handling for non-finite and out-of-range centres, and a full-grid fallback. All of these only add candidates.
3. Zero allocation depends on `Span<long>.Sort(Span<int>)` and `Span<int>.Sort()` not allocating once warm. `AWarmRebuildAndItsQueriesAllocateNothing` and `PointShadowAllocationTests` would catch a regression. The fallback would be a hand-written sort inside `PointCasterIndex`.
4. Rebase with #1054: its Task 6 rewrites `PackedPointShadowSlot` and `RenderPointShadowSlots` (`docs/superpowers/plans/2026-09-23-skinned-point-shadows.md:1012-1032`), which this round leaves untouched. It also keeps calling `BuildPointCasterSpans`, whose signature stays the same. Its `PointShadowCasterSphere.TouchesShadowingShell` (plan lines 174-185) repeats the shell test. On rebase it should call the new `Scene3D.InstanceTouchesLight(Vector3, float, ...)` so design decision 8 keeps one definition.
5. The Grimhollow probe confirmation for #1111 (spec "Design", item 2) runs outside this repo. The headless reproduction in Task A4 confirms the churn on the engine side, and the forest and meadow rebuild counts belong to the acceptance run. `MixPointSignature` also goes from private to internal (`Scene3D.PointShadows.cs:399`), which does not change the public API.


---

## Item 3: Compact point light clusters (#1112)


### Spec conflicts found in the code (read first)

1. **The frustum cull rationale is wrong for lights that reach the near plane.** Spec lines 218-219 say a light wholly outside the view frustum "could not pass any cluster test". The exact test at `KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs:280-290` checks six half-spaces independently. Behind the eye, a column's two side planes cross, so it accepts some spheres that lie wholly outside the frustum.
   - Worked case: a 90 degree camera at the origin looking down -Z with near 0.1, and a light at `(-10.5, 0, 4.8)` with radius 5. The sphere is 10.8 m outside the left frustum plane, yet today's builder assigns it to cluster (15, 4, 0).
   - A real six-plane frustum cull would change the assignment.
   - This plan culls a light only when its conservative range is empty. The side planes are used only for lights that do not reach the near plane, which is the spec's own near-plane rule. Output stays identical. Only the spec's rationale sentence needs correcting. Pinned by `ASphereWhollyOutsideTheFrustumThatThePlaneTestAcceptsIsStillAssigned` (B1).
2. **Building planes lazily is not identical to today when the camera is degenerate.** Today, any cluster whose planes cannot be built marks the whole frame invalid (`PointLightClusterBuilder.cs:62-67`), whether or not a light reaches it. Spec lines 226-228 say planes are built only for clusters some light's range reaches. Spec lines 224-225 say the output stays byte-identical.
   - The two collide for a near plane so small that first-slice clusters are degenerate (below about 1 mm at common fields of view). Today the whole frame falls back to the full list. With lazy building the frame stays clustered.
   - Pixels are the same either way. The fallback walks every light, and a light outside its radius adds exactly zero.
   - What differs is the diagnostics (`Projection`, `OverflowedClusterCount`) and the buffer contents, for those cameras only.
   - The plan follows the lazy rule and pins the difference in `ADegenerateClusterThatNoLightReachesNoLongerForcesTheFullListFallback` (B1), so a reviewer can reject it. Strict identity would mean building all 3,456 plane sets again every frame, which gives back most of the saving. **Owner decision.**
3. **The reference count under overflow.** Today an overflowed cluster adds 64 to `LightReferenceCount` (`PointLightClusterBuilder.cs:179-189`). In the new layout it stores no indices (spec lines 211-212). This plan keeps adding 64, so `PointLightClusterDiagnostics.LightReferenceCount` has the same value and meaning ("cluster references", `docs/USING-KHAOZENGINE.md:3226`). The upload size is tracked separately in `UsedUIntCount`.

---

### Task B1: Range-limited cluster assignment in the existing layout

**Branch:** `feature/frame-cost-clusters` (worktree created from `feature/frame-cost-round`)

**Files:**
- Create: `KhaozEngine.Render3D/Internal/PointLightClusterRanges.cs`
- Modify: `KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs`
  - Lines 7-72: summary, new fields and counters, `Build`.
  - Lines 165-190: `PackCluster` replaced, `AssignClusters`, `FrameGeometryScale` and `EnsureCandidateCapacity` added.
  - Lines 247-258: `BoundaryIndex`, `MaxAbs(Vector3)` and `Finite(Vector3)` go from private to `internal static`.
- Create (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterOracle.cs`
- Create (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterImage.cs`
- Create (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterEquivalenceTests.cs`
- Create (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterRangeTests.cs`

**Interfaces:**
- Consumes: `PointLightClusterBuilder.Build(ReadOnlySpan<ModelRenderer.PointLightData>, Matrix4x4, Vector3, Vector3, Matrix4x4, Vector3)`, unchanged.
- Produces:
  - `internal sealed class PointLightClusterRanges`:
    - `internal const float MarginScale = 1e-3f`
    - `internal void Gather(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin, Vector3 eye, Vector3 cameraForward, ReadOnlySpan<Vector3> clipNear, ReadOnlySpan<Vector3> clipFar, ReadOnlySpan<float> sliceDepth, float geometryScale)`
    - `internal int Count`, `internal int MinSlice`, `internal int MaxSlice`
    - `internal ReadOnlySpan<int> Lights`, `internal ReadOnlySpan<ClusterRange> Ranges`
    - nested `internal readonly struct ClusterRange` with `MinX/MaxX/MinY/MaxY/MinZ/MaxZ`, `ContainsColumn(int)`, `ContainsRow(int)`, `ContainsSlice(int)` and `static ClusterRange Full`
  - `PointLightClusterBuilder.PlaneSetsBuilt` and `PointLightClusterBuilder.SphereTests` (`internal int { get; private set; }`)
  - `internal static int PointLightClusterBuilder.BoundaryIndex(int x, int y)`, `internal static float MaxAbs(Vector3)`, `internal static bool Finite(Vector3)`
  - Test side: `PointLightClusterOracle` (the builder's API as of `03b63e2e`) and the static class `PointLightClusterImage` with:
    - `ClusterIndex(int x, int y, int z)`
    - `Lights(PointLightClusterOracle, int cluster, out bool overflow)` and `Lights(PointLightClusterBuilder, int cluster, out bool overflow)`
    - `Contains(oracle|builder, int x, int y, int z, uint light)`
    - `AssertSameAssignment(PointLightClusterOracle, PointLightClusterBuilder, string context)`

- [ ] **Step 1: Write the failing test**

First create the oracle as a verbatim copy:
```bash
git show 03b63e2e:KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs > KhaozEngine.Render.Tests/Render3D/PointLightClusterOracle.cs
```
Then change only lines 5, 7, 8 and 28 so the head of the file reads as below. Lines 9-27 and 29-291 stay byte for byte.
```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Render3D;

/// <summary>THE BRUTE-FORCE CLUSTER BUILDER as it stood before issue #1112, copied verbatim from PointLightClusterBuilder.cs at 03b63e2e. It builds planes for all 3,456 clusters and tests every light against every one. It is the equivalence oracle for the range-limited builder and is never edited to follow it.</summary>
internal sealed class PointLightClusterOracle
{
    // ... lines 10 to 27 unchanged ...
    internal PointLightClusterOracle()
    {
        Image = new uint[ImageUIntCount];
    }
    // ... lines 32 to 291 unchanged ...
```

`KhaozEngine.Render.Tests/Render3D/PointLightClusterImage.cs`:
```csharp
using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Reads per-cluster light lists out of a built cluster image, so tests assert on assignments rather than on
/// the layout that stores them. The oracle keeps the fixed seventeen-uvec4 layout for good.</summary>
internal static class PointLightClusterImage
{
    internal static int ClusterIndex(int x, int y, int z) =>
        (z * PointLightClusterBuilder.ClusterCountY + y) * PointLightClusterBuilder.ClusterCountX + x;

    internal static ReadOnlySpan<uint> Lights(PointLightClusterOracle oracle, int cluster, out bool overflow)
    {
        int offset = cluster * PointLightClusterOracle.ClusterStrideUInts;
        overflow = oracle.Image[offset + 1] != 0u;
        return oracle.Image.AsSpan(offset + PointLightClusterOracle.HeaderUInts, (int)oracle.Image[offset]);
    }

    /// <summary>The builder's stored indices and overflow flag, in the fixed layout it still shares with the oracle.</summary>
    internal static ReadOnlySpan<uint> Lights(PointLightClusterBuilder builder, int cluster, out bool overflow)
    {
        int offset = cluster * PointLightClusterBuilder.ClusterStrideUInts;
        overflow = builder.Image[offset + 1] != 0u;
        return builder.Image.AsSpan(offset + PointLightClusterBuilder.HeaderUInts, (int)builder.Image[offset]);
    }

    internal static bool Contains(PointLightClusterOracle oracle, int x, int y, int z, uint light) =>
        Lights(oracle, ClusterIndex(x, y, z), out _).IndexOf(light) >= 0;

    internal static bool Contains(PointLightClusterBuilder builder, int x, int y, int z, uint light) =>
        Lights(builder, ClusterIndex(x, y, z), out _).IndexOf(light) >= 0;

    internal static void AssertSameAssignment(PointLightClusterOracle oracle, PointLightClusterBuilder builder,
        string context)
    {
        Assert.True(oracle.Depth == builder.Depth, $"{context}: depth {builder.Depth}, brute force {oracle.Depth}");
        Assert.True(oracle.CameraForward == builder.CameraForward,
            $"{context}: forward {builder.CameraForward}, brute force {oracle.CameraForward}");
        Assert.True(oracle.OverflowedClusters == builder.OverflowedClusters,
            $"{context}: {builder.OverflowedClusters} overflowed clusters, brute force {oracle.OverflowedClusters}");
        Assert.True(oracle.LightReferenceCount == builder.LightReferenceCount,
            $"{context}: {builder.LightReferenceCount} references, brute force {oracle.LightReferenceCount}");
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            ReadOnlySpan<uint> expected = Lights(oracle, cluster, out bool expectedOverflow);
            ReadOnlySpan<uint> actual = Lights(builder, cluster, out bool actualOverflow);
            bool same = expectedOverflow == actualOverflow && (expectedOverflow || expected.SequenceEqual(actual));
            if (!same)
                Assert.Fail($"{context}: cluster {cluster} holds [{string.Join(",", actual.ToArray())}] with overflow "
                    + $"{actualOverflow}, brute force [{string.Join(",", expected.ToArray())}] with overflow "
                    + $"{expectedOverflow}");
        }
        // Same layout until the compact image lands, so the whole image must match too. B3 removes this line.
        Assert.True(oracle.Image.AsSpan().SequenceEqual(builder.Image), $"{context}: the images differ");
    }
}
```

`KhaozEngine.Render.Tests/Render3D/PointLightClusterEquivalenceTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Proves the range-limited cluster builder assigns every cluster exactly the lights, in exactly the order, that the
/// brute-force builder assigned (issue #1112). The brute-force builder is kept verbatim as
/// <see cref="PointLightClusterOracle"/>. Scenes are seeded, so a failure names a seed that reproduces it. One builder is
/// reused across a batch, so state left by the previous scene is exercised too.
/// </summary>
public sealed class PointLightClusterEquivalenceTests
{
    const int ScenesPerBatch = 32;
    static readonly Vector3 Forward = -Vector3.UnitZ;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void SeededScenesAssignEveryClusterExactlyAsTheBruteForceBuilder(int batch)
    {
        var builder = new PointLightClusterBuilder();
        var oracle = new PointLightClusterOracle();
        for (int index = 0; index < ScenesPerBatch; index++)
        {
            int seed = batch * ScenesPerBatch + index;
            ClusterScene scene = ClusterScene.Generate(seed);

            builder.Build(scene.Lights, scene.ViewProjection, scene.Eye, scene.Forward, scene.Projection,
                scene.RenderOrigin);
            oracle.Build(scene.Lights, scene.ViewProjection, scene.Eye, scene.Forward, scene.Projection,
                scene.RenderOrigin);

            PointLightClusterImage.AssertSameAssignment(oracle, builder, $"seed {seed}, {scene.Description}");
        }
    }

    [Fact]
    public void ASphereWhollyOutsideTheFrustumThatThePlaneTestAcceptsIsStillAssigned()
    {
        // Behind and left of a 90 degree camera, reaching the near plane's depth only 10 m off to the side. The sphere
        // lies wholly outside the frustum, yet the six-plane test accepts it for the rightmost column's first slice,
        // because behind the eye that column's side planes have crossed. A frustum cull would drop it and change the
        // assignment, which is why a light that reaches the near plane is never culled by a side plane.
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 100f);
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(-10.5f, 0f, 4.8f), 5f)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);
        builder.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);

        Assert.True(PointLightClusterImage.Contains(oracle, 15, 4, 0, 0u),
            "precondition: the brute-force plane test accepts this sphere in cluster (15, 4, 0)");
        PointLightClusterImage.AssertSameAssignment(oracle, builder, "a sphere behind and beside the camera");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACameraInsideALightSphereMatchesTheBruteForceBuilder(bool perspective)
    {
        Matrix4x4 projection = perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 16f / 9f, 0.1f, 300f)
            : Matrix4x4.CreateOrthographic(32f, 18f, -2f, 22f);
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(0.2f, -0.1f, 0.3f), 4f),
            Light(new Vector3(3f, 1f, -6f), 2f),
            Light(Vector3.Zero, 0.05f),
        ];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);
        builder.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);

        PointLightClusterImage.AssertSameAssignment(oracle, builder, $"camera inside a light, perspective {perspective}");
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };

    readonly record struct ClusterScene(ModelRenderer.PointLightData[] Lights, Matrix4x4 ViewProjection, Vector3 Eye,
        Vector3 Forward, Matrix4x4 Projection, Vector3 RenderOrigin, string Description)
    {
        internal static ClusterScene Generate(int seed)
        {
            var random = new Random(seed);
            Vector3 origin = random.Next(4) switch
            {
                0 => Vector3.Zero,
                1 => new Vector3(100_000f, -50_000f, 70_000f),
                _ => Scatter(random, 2_000f),
            };
            // A game that sets no render origin renders in world space, so the eye can sit hundreds of metres out.
            Vector3 eye = Scatter(random, random.Next(4) == 0 ? 200f : 50f);
            Vector3 direction = Direction(random);
            Vector3 up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            (Matrix4x4 projection, string kind) = RandomProjection(random);
            Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, eye + direction, up) * projection;
            bool flipped = random.Next(2) == 0;
            if (flipped) viewProjection *= Matrix4x4.CreateScale(1f, -1f, 1f);
            // The builder normalizes forward itself, so a scaled one must change nothing.
            Vector3 forward = direction * Between(random, 0.5f, 3f);
            bool invertible = Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);

            var lights = new List<ModelRenderer.PointLightData>();
            int count = random.Next(8) == 0 ? 0 : random.Next(1, 97);
            for (int i = 0; i < count; i++)
            {
                int placement = random.Next(20);
                lights.Add(placement switch
                {
                    < 10 => InView(random, origin, eye, direction, invertible, inverse),
                    < 13 => Light(origin + eye + Scatter(random, 3f), Between(random, 0.2f, 8f)),
                    < 16 => Light(origin + eye - direction * Between(random, 0.5f, 40f) + Scatter(random, 15f),
                        Between(random, 0.5f, 20f)),
                    < 18 => Light(origin + eye + Direction(random) * Between(random, 500f, 4_000f),
                        Between(random, 1f, 50f)),
                    18 when lights.Count > 0 => lights[random.Next(lights.Count)],
                    _ => Malformed(random, origin + eye + direction * 5f),
                });
            }
            int stack = random.Next(6);
            if (stack < 2)
            {
                // Exactly 64, or more than 64, lights on one sphere, which fills or overflows its clusters.
                ModelRenderer.PointLightData stacked = InView(random, origin, eye, direction, invertible, inverse);
                int size = stack == 0 ? 64 : random.Next(65, 81);
                for (int i = 0; i < size; i++) lights.Add(stacked);
            }
            string description = $"{kind}{(flipped ? ", clip-Y flipped" : "")}, {lights.Count} lights, eye {eye}, "
                + $"origin {origin}";
            return new ClusterScene(lights.ToArray(), viewProjection, eye, forward, projection, origin, description);
        }

        static (Matrix4x4 Projection, string Kind) RandomProjection(Random random)
        {
            int kind = random.Next(10);
            if (kind < 6)
            {
                float near = Between(random, 0.05f, 2f);
                float far = MathF.Min(near * Between(random, 20f, 5_000f), 2_000f);
                return (Matrix4x4.CreatePerspectiveFieldOfView(Between(random, 0.3f, 1.9f),
                    Between(random, 0.5f, 2.5f), near, far), "perspective");
            }
            if (kind < 7)
            {
                float near = Between(random, 0.1f, 1f);
                return (Matrix4x4.CreatePerspectiveOffCenter(-Between(random, 0.2f, 1.5f) * near,
                    Between(random, 0.2f, 1.5f) * near, -Between(random, 0.2f, 1.2f) * near,
                    Between(random, 0.2f, 1.2f) * near, near, Between(random, 50f, 800f)), "off-centre perspective");
            }
            float orthoNear = Between(random, -10f, 2f);
            return (Matrix4x4.CreateOrthographic(Between(random, 4f, 80f), Between(random, 4f, 60f), orthoNear,
                orthoNear + Between(random, 5f, 200f)), "orthographic");
        }

        // Anywhere between the near and far planes, biased toward the camera where clusters are smallest.
        static ModelRenderer.PointLightData InView(Random random, Vector3 origin, Vector3 eye, Vector3 direction,
            bool invertible, Matrix4x4 inverse)
        {
            float x = Between(random, -1.2f, 1.2f);
            float y = Between(random, -1.2f, 1.2f);
            float t = Between(random, 0f, 1f);
            float radius = Between(random, 0.05f, 12f);
            Vector3 position = eye + direction * (1f + 60f * t);
            if (invertible && TryUnproject(inverse, x, y, 0f, out Vector3 near)
                && TryUnproject(inverse, x, y, 1f, out Vector3 far))
                position = Vector3.Lerp(near, far, t * t);
            return Light(origin + position, radius);
        }

        static ModelRenderer.PointLightData Malformed(Random random, Vector3 near) => random.Next(5) switch
        {
            0 => Light(new Vector3(float.NaN, near.Y, near.Z), 3f),
            1 => Light(near, -2f),
            2 => Light(near, 0f),
            3 => Light(near, float.PositiveInfinity),
            _ => Light(new Vector3(1e30f, near.Y, near.Z), 5f),
        };

        static bool TryUnproject(Matrix4x4 inverse, float x, float y, float z, out Vector3 point)
        {
            Vector4 homogeneous = Vector4.Transform(new Vector4(x, y, z, 1f), inverse);
            point = new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
            return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
        }

        static float Between(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);

        static Vector3 Scatter(Random random, float extent) =>
            new(Between(random, -extent, extent), Between(random, -extent, extent), Between(random, -extent, extent));

        static Vector3 Direction(Random random)
        {
            Vector3 direction;
            do direction = Scatter(random, 1f);
            while (direction.LengthSquared() < 0.01f || direction.LengthSquared() > 1f);
            return Vector3.Normalize(direction);
        }
    }
}
```

Then `KhaozEngine.Render.Tests/Render3D/PointLightClusterRangeTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Pins that the cluster builder tests only the clusters a light's conservative range reaches (issue #1112).
/// <see cref="PointLightClusterEquivalenceTests"/> proves the result did not change. These prove the work did.</summary>
public sealed class PointLightClusterRangeTests
{
    static readonly Matrix4x4 Ortho = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);
    static readonly Matrix4x4 TinyNear = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 1e-5f, 256f);
    static readonly Vector3 Forward = -Vector3.UnitZ;

    [Fact]
    public void ASmallLightInViewBuildsAndTestsOnlyItsOwnCluster()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(1f, 0.5f, -1.5f), 0.1f)];

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        // The 32 by 18 by 24 ortho frustum makes each cell 2 by 2 by 1, and this sphere sits well inside (8, 4, 0).
        // The brute-force builder built 3,456 plane sets and ran 3,456 sphere tests for it.
        Assert.Equal(1, builder.PlaneSetsBuilt);
        Assert.Equal(1, builder.SphereTests);
        Assert.Equal(1, builder.LightReferenceCount);
    }

    [Theory]
    [InlineData(100f, 0f, -10f)]
    [InlineData(0f, 0f, 40f)]
    [InlineData(0f, 0f, -60f)]
    public void ALightWhollyOutsideTheFrustumIsCulledBeforeAnyClusterIsBuilt(float x, float y, float z)
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(x, y, z), 1f)];

        builder.Build(lights, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(0f, builder.Depth.W);
        Assert.Equal(0, builder.PlaneSetsBuilt);
        Assert.Equal(0, builder.SphereTests);
        Assert.Equal(0, builder.LightReferenceCount);
    }

    [Fact]
    public void ADegenerateClusterThatNoLightReachesNoLongerForcesTheFullListFallback()
    {
        // A 10 micrometre near plane makes every first-slice cluster too thin for a plane. The brute-force builder built
        // them all and fell back to the full list. The range-limited builder never builds them for a light 50 m away,
        // so the frame stays clustered. Lighting is the same either way, because the fallback walks every light and the
        // shader skips a light outside its radius. This is the documented spec conflict, pinned so it can be reviewed.
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 0f, -50f), 1f)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);
        builder.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);

        Assert.Equal(-1f, oracle.Depth.W);
        Assert.Equal(1f, builder.Depth.W);
        Assert.Equal(0, builder.OverflowedClusters);
        int slice = Math.Clamp((int)(MathF.Log(50f / builder.Depth.X) / builder.Depth.Z
            * PointLightClusterBuilder.ClusterCountZ), 0, PointLightClusterBuilder.ClusterCountZ - 1);
        Assert.True(PointLightClusterImage.Contains(builder, 8, 4, slice, 0u),
            $"cluster (8, 4, {slice}) did not contain the light 50 m ahead");
    }

    [Fact]
    public void ADegenerateClusterALightReachesStillForcesTheFullListFallback()
    {
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(0f, 0f, -1f), 2f)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);
        builder.Build(lights, TinyNear, Vector3.Zero, Forward, TinyNear, Vector3.Zero);

        Assert.Equal(-1f, oracle.Depth.W);
        PointLightClusterImage.AssertSameAssignment(oracle, builder, "a light reaching the degenerate first slice");
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run it in two parts. First, before `PointLightClusterRangeTests.cs` exists, check that the harness works:
```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightClusterEquivalenceTests"
```
Expected: PASS for all 11 cases. The oracle is the unchanged builder, which shows the harness itself is sound.

Then add `PointLightClusterRangeTests.cs` and run:
```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightClusterRangeTests"
```
Expected: build error `CS1061: 'PointLightClusterBuilder' does not contain a definition for 'PlaneSetsBuilt'` (and the same for `SphereTests`).

- [ ] **Step 3: Implement**

Create `KhaozEngine.Render3D/Internal/PointLightClusterRanges.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The conservative cluster range of every point light in one frame (issue #1112), gathered before any cluster is
/// visited. A range holds every cluster whose exact plane test in <see cref="PointLightClusterBuilder"/> could accept the
/// light, so testing only those clusters assigns exactly what testing all of them did. A light whose range is empty is
/// culled here, which is the frustum cull: no cluster could accept it.
/// </summary>
internal sealed class PointLightClusterRanges
{
    /// <summary>
    /// How far every range test is widened, as a fraction of the largest of one, the frame's geometry scale, the light's
    /// largest centre coordinate and its radius. That is ten times the builder's exact-test epsilon over the same scale,
    /// so the widening covers the epsilon plus the rounding between these boundary planes and the planes each cluster
    /// builds from its own corners.
    /// </summary>
    internal const float MarginScale = 1e-3f;

    const int CountX = PointLightClusterBuilder.ClusterCountX;
    const int CountY = PointLightClusterBuilder.ClusterCountY;
    const int CountZ = PointLightClusterBuilder.ClusterCountZ;

    readonly Plane[] _columnPlanes = new Plane[CountX + 1];
    readonly Plane[] _rowPlanes = new Plane[CountY + 1];
    int[] _lights = new int[16];
    ClusterRange[] _ranges = new ClusterRange[16];
    bool _tilePlanesValid;
    float _geometryScale;

    /// <summary>Submitted indices of the lights that survived the cull, in ascending order.</summary>
    internal ReadOnlySpan<int> Lights => new(_lights, 0, Count);

    /// <summary>The range of each surviving light, parallel to <see cref="Lights"/>.</summary>
    internal ReadOnlySpan<ClusterRange> Ranges => new(_ranges, 0, Count);

    internal int Count { get; private set; }

    /// <summary>The lowest slice any surviving range reaches, or the slice count when none survived.</summary>
    internal int MinSlice { get; private set; }

    /// <summary>The highest slice any surviving range reaches, or -1 when none survived.</summary>
    internal int MaxSlice { get; private set; }

    internal void Gather(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin, Vector3 eye,
        Vector3 cameraForward, ReadOnlySpan<Vector3> clipNear, ReadOnlySpan<Vector3> clipFar,
        ReadOnlySpan<float> sliceDepth, float geometryScale)
    {
        Count = 0;
        MinSlice = CountZ;
        MaxSlice = -1;
        _geometryScale = geometryScale;
        _tilePlanesValid = PrepareTilePlanes(clipNear, clipFar);
        EnsureCapacity(lights.Length);
        for (int light = 0; light < lights.Length; light++)
        {
            Vector4 posRadius = lights[light].PosRadius;
            var center = new Vector3(posRadius.X, posRadius.Y, posRadius.Z) - renderOrigin;
            float radius = posRadius.W;
            // The builder's own rejection. A light it would skip in every cluster never gets a range.
            if (!PointLightClusterBuilder.Finite(center) || !float.IsFinite(radius) || radius < 0f) continue;
            if (!TryGetRange(center, radius, eye, cameraForward, sliceDepth, out ClusterRange range)) continue;
            _lights[Count] = light;
            _ranges[Count] = range;
            Count++;
            MinSlice = Math.Min(MinSlice, range.MinZ);
            MaxSlice = Math.Max(MaxSlice, range.MaxZ);
        }
    }

    bool TryGetRange(Vector3 center, float radius, Vector3 eye, Vector3 cameraForward, ReadOnlySpan<float> sliceDepth,
        out ClusterRange range)
    {
        range = ClusterRange.Full;
        float margin = MarginScale * MathF.Max(1f,
            MathF.Max(_geometryScale, MathF.Max(PointLightClusterBuilder.MaxAbs(center), radius)));
        float reach = radius + margin;
        float depth = Vector3.Dot(center - eye, cameraForward);
        float nearReach = depth - reach;
        float farReach = depth + reach;
        // Arithmetic that overflowed bounds nothing. The light keeps every cluster and the exact test decides, as it did
        // for every light before ranges existed.
        if (!float.IsFinite(nearReach) || !float.IsFinite(farReach)) return true;

        // Every cluster's near and far faces lie at constant view depth, so this is those two faces' exact test widened
        // by the margin, and a light outside it can pass no cluster at all.
        if (farReach < sliceDepth[0] || nearReach > sliceDepth[CountZ]) return false;
        int minZ = 0;
        while (minZ < CountZ - 1 && sliceDepth[minZ + 1] < nearReach) minZ++;
        int maxZ = CountZ - 1;
        while (maxZ > minZ && sliceDepth[maxZ] > farReach) maxZ--;

        // A sphere that reaches the near plane takes every tile and is never culled by a side plane. Behind the eye a
        // column's side planes cross, and the six-plane test accepts spheres there that lie wholly outside the frustum,
        // so a side-plane cull would drop lights the exact test keeps.
        if (nearReach <= sliceDepth[0] || !_tilePlanesValid)
        {
            range = new ClusterRange(0, CountX - 1, 0, CountY - 1, minZ, maxZ);
            return true;
        }
        if (!TryGetTileSpan(_columnPlanes, center, reach, out int minX, out int maxX)
            || !TryGetTileSpan(_rowPlanes, center, reach, out int minY, out int maxY))
            return false;
        range = new ClusterRange(minX, maxX, minY, maxY, minZ, maxZ);
        return true;
    }

    // A tile column (or row) can hold the sphere only if it reaches the inner side of both boundary planes of that tile,
    // which are the two side planes every cluster in it tests. The span runs from the first such tile to the last.
    static bool TryGetTileSpan(Plane[] boundaries, Vector3 center, float reach, out int min, out int max)
    {
        int last = boundaries.Length - 2;
        min = -1;
        max = -1;
        float lower = Plane.DotCoordinate(boundaries[0], center);
        for (int tile = 0; tile <= last; tile++)
        {
            float upper = Plane.DotCoordinate(boundaries[tile + 1], center);
            if (!float.IsFinite(lower) || !float.IsFinite(upper))
            {
                min = 0;
                max = last;
                return true;
            }
            if (lower >= -reach && upper <= reach)
            {
                if (min < 0) min = tile;
                max = tile;
            }
            lower = upper;
        }
        return min >= 0;
    }

    // One plane per tile boundary, through the corner rays the builder unprojected for it, oriented so the next tile
    // along lies on its positive side. Every cluster's side plane on that boundary is built from points on the same
    // rays, so the two agree up to rounding, which the margin covers. A failure here gives every light the full tile
    // range, which is always safe.
    bool PrepareTilePlanes(ReadOnlySpan<Vector3> clipNear, ReadOnlySpan<Vector3> clipFar)
    {
        for (int x = 0; x <= CountX; x++)
        {
            int first = PointLightClusterBuilder.BoundaryIndex(x, 0);
            int last = PointLightClusterBuilder.BoundaryIndex(x, CountY);
            int beside = PointLightClusterBuilder.BoundaryIndex(x < CountX ? x + 1 : x - 1, 0);
            if (!TryBoundaryPlane(clipNear[first], clipFar[first], clipFar[last], clipFar[beside], x < CountX,
                    out _columnPlanes[x]))
                return false;
        }
        for (int y = 0; y <= CountY; y++)
        {
            int first = PointLightClusterBuilder.BoundaryIndex(0, y);
            int last = PointLightClusterBuilder.BoundaryIndex(CountX, y);
            int beside = PointLightClusterBuilder.BoundaryIndex(0, y < CountY ? y + 1 : y - 1);
            if (!TryBoundaryPlane(clipNear[first], clipFar[first], clipFar[last], clipFar[beside], y < CountY,
                    out _rowPlanes[y]))
                return false;
        }
        return true;
    }

    static bool TryBoundaryPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 beside, bool besideIsPositive,
        out Plane plane)
    {
        plane = default;
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float lengthSquared = normal.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-16f) return false;
        normal /= MathF.Sqrt(lengthSquared);
        float distance = -Vector3.Dot(normal, a);
        float side = Vector3.Dot(normal, beside) + distance;
        if (!float.IsFinite(distance) || !float.IsFinite(side) || side == 0f) return false;
        if (side < 0f == besideIsPositive)
        {
            normal = -normal;
            distance = -distance;
        }
        plane = new Plane(normal, distance);
        return true;
    }

    void EnsureCapacity(int count)
    {
        if (_lights.Length >= count) return;
        int capacity = Math.Max(count, _lights.Length * 2);
        _lights = new int[capacity];
        _ranges = new ClusterRange[capacity];
    }

    /// <summary>An inclusive range of tile columns, tile rows and depth slices.</summary>
    internal readonly struct ClusterRange(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        internal static ClusterRange Full => new(0, CountX - 1, 0, CountY - 1, 0, CountZ - 1);

        internal int MinX { get; } = minX;
        internal int MaxX { get; } = maxX;
        internal int MinY { get; } = minY;
        internal int MaxY { get; } = maxY;
        internal int MinZ { get; } = minZ;
        internal int MaxZ { get; } = maxZ;

        internal bool ContainsColumn(int x) => x >= MinX && x <= MaxX;
        internal bool ContainsRow(int y) => y >= MinY && y <= MaxY;
        internal bool ContainsSlice(int z) => z >= MinZ && z <= MaxZ;
    }
}
```

In `PointLightClusterBuilder.cs`, replace the summary (line 7), add the fields and counters after line 26, replace `Build` (lines 39-72) and replace `PackCluster` (lines 165-190). `TryPrepare`, `TryBuildPlanes`, `PointAtDepth`, `MarkInvalid`, `TryPlane`, `TryUnproject` and `ClusterPlanes` stay unchanged:
```csharp
/// <summary>Builds the fixed clustered point-light index image consumed by lit receiver shaders. Each light first gets
/// a conservative cluster range from <see cref="PointLightClusterRanges"/>, and each cluster then runs the exact plane
/// test on only the lights whose range holds it, in ascending submitted order (issue #1112).</summary>
internal sealed class PointLightClusterBuilder
{
    // ... constants and existing arrays unchanged ...
    readonly PointLightClusterRanges _ranges = new();
    int[] _sliceCandidates = new int[16];
    int[] _rowCandidates = new int[16];

    // ... constructor and existing properties unchanged ...

    /// <summary>Cluster plane sets the latest build made. Tests read it to prove a cluster no light's range reaches is
    /// never built.</summary>
    internal int PlaneSetsBuilt { get; private set; }

    /// <summary>Exact sphere tests the latest build ran, counted for the same reason.</summary>
    internal int SphereTests { get; private set; }

    internal void Build(ReadOnlySpan<ModelRenderer.PointLightData> lights,
        Matrix4x4 gpuCorrectedRenderViewProjection, Vector3 eyeRender, Vector3 forward,
        Matrix4x4 projection, Vector3 renderOrigin)
    {
        Array.Clear(Image);
        OverflowedClusters = 0;
        LightReferenceCount = 0;
        PlaneSetsBuilt = 0;
        SphereTests = 0;
        if (!TryPrepare(gpuCorrectedRenderViewProjection, eyeRender, forward, projection, renderOrigin,
                out Vector3 cameraForward))
        {
            MarkInvalid();
            return;
        }

        CameraForward = new Vector4(cameraForward, 0f);
        _ranges.Gather(lights, renderOrigin, eyeRender, cameraForward, _clipNear, _clipFar, _sliceDepth,
            FrameGeometryScale());
        if (!AssignClusters(lights, renderOrigin)) MarkInvalid();
    }

    // Visits clusters in index order, z then y then x, testing each against only the lights whose range holds it. The
    // candidate lists narrow per slice and then per row but never reorder, so every cluster still sees its lights in
    // ascending submitted order.
    bool AssignClusters(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin)
    {
        ReadOnlySpan<int> survivors = _ranges.Lights;
        ReadOnlySpan<PointLightClusterRanges.ClusterRange> ranges = _ranges.Ranges;
        EnsureCandidateCapacity(survivors.Length);
        for (int z = _ranges.MinSlice; z <= _ranges.MaxSlice; z++)
        {
            int sliceCount = 0;
            int minY = ClusterCountY;
            int maxY = -1;
            for (int entry = 0; entry < survivors.Length; entry++)
            {
                if (!ranges[entry].ContainsSlice(z)) continue;
                _sliceCandidates[sliceCount++] = entry;
                minY = Math.Min(minY, ranges[entry].MinY);
                maxY = Math.Max(maxY, ranges[entry].MaxY);
            }
            for (int y = minY; y <= maxY; y++)
            {
                int rowCount = 0;
                int minX = ClusterCountX;
                int maxX = -1;
                for (int i = 0; i < sliceCount; i++)
                {
                    int entry = _sliceCandidates[i];
                    if (!ranges[entry].ContainsRow(y)) continue;
                    _rowCandidates[rowCount++] = entry;
                    minX = Math.Min(minX, ranges[entry].MinX);
                    maxX = Math.Max(maxX, ranges[entry].MaxX);
                }
                for (int x = minX; x <= maxX; x++)
                    if (!PackCluster(lights, renderOrigin, survivors, ranges, rowCount, x, y, z)) return false;
            }
        }
        return true;
    }

    // The exact test is unchanged. Only its candidates changed: the lights whose range holds this cluster, still in
    // ascending submitted order, so the first 64 that pass and the overflow point are the same as testing all of them.
    // The planes are built at the first candidate, so a cluster no light's range reaches is never built.
    bool PackCluster(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin,
        ReadOnlySpan<int> survivors, ReadOnlySpan<PointLightClusterRanges.ClusterRange> ranges, int rowCount,
        int x, int y, int z)
    {
        int offset = ((z * ClusterCountY + y) * ClusterCountX + x) * ClusterStrideUInts;
        ClusterPlanes planes = default;
        float clusterScale = 0f;
        bool planesBuilt = false;
        int count = 0;
        for (int i = 0; i < rowCount; i++)
        {
            int entry = _rowCandidates[i];
            if (!ranges[entry].ContainsColumn(x)) continue;
            if (!planesBuilt)
            {
                if (!TryBuildPlanes(x, y, _sliceDepth[z], _sliceDepth[z + 1], out planes, out clusterScale))
                    return false;
                planesBuilt = true;
                PlaneSetsBuilt++;
            }
            int light = survivors[entry];
            Vector4 posRadius = lights[light].PosRadius;
            var center = new Vector3(posRadius.X, posRadius.Y, posRadius.Z) - renderOrigin;
            float radius = posRadius.W;
            float epsilon = GeometryEpsilonScale * MathF.Max(1f,
                MathF.Max(clusterScale, MathF.Max(MaxAbs(center), radius)));
            SphereTests++;
            if (!planes.IntersectsSphere(center, radius, epsilon)) continue;
            if (count == MaxLightsPerCluster)
            {
                Image[offset + 1] = 1u;
                OverflowedClusters++;
                break;
            }
            Image[offset + HeaderUInts + count] = (uint)light;
            count++;
        }
        Image[offset] = (uint)count;
        LightReferenceCount += count;
        return true;
    }

    // The largest coordinate any cluster corner can take this frame. Corners lie on the boundary rays at slice depths
    // inside [minNear, maxFar], and a coordinate is affine in depth along a ray, so its extremes sit at the two ends of
    // that interval. It bounds every cluster's own scale in the exact test's epsilon.
    float FrameGeometryScale()
    {
        float scale = 0f;
        for (int boundary = 0; boundary < BoundaryCount; boundary++)
        {
            scale = MathF.Max(scale, MaxAbs(PointAtDepth(boundary, _sliceDepth[0])));
            scale = MathF.Max(scale, MaxAbs(PointAtDepth(boundary, _sliceDepth[ClusterCountZ])));
        }
        return scale;
    }

    void EnsureCandidateCapacity(int count)
    {
        if (_sliceCandidates.Length >= count) return;
        int capacity = Math.Max(count, _sliceCandidates.Length * 2);
        _sliceCandidates = new int[capacity];
        _rowCandidates = new int[capacity];
    }
```
At lines 247-258, change `static int BoundaryIndex`, `static float MaxAbs(Vector3 value)` and `static bool Finite(Vector3 value)` to `internal static`. The bodies stay as they are.

- [ ] **Step 4: Run the tests and confirm they pass**
```bash
dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightCluster"
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightBufferTests|FullyQualifiedName~PointShadowAllocationTests"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~ManyPointLightsGpuTests|FullyQualifiedName~PointLightGpuTests"
sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree
```
Expected:
- Everything green, including the existing `PointLightClusterBuilderTests` (unchanged, `WarmBuildAllocatesNothing` among them).
- The GPU run shows zero skipped.
- If an equivalence seed fails, the range is not conservative enough. Fix the range (for example by raising `MarginScale`). Never edit the oracle or drop the seed.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Internal/PointLightClusterRanges.cs KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterOracle.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterImage.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterEquivalenceTests.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterRangeTests.cs
git commit -m "render3d(lighting): assign point light clusters from conservative per-light ranges"
```

---

### Task B2: GPU invariant for cluster offsets behind an in-view crowd

**Branch:** `feature/frame-cost-clusters`

**Files:**
- Modify: `KhaozEngine.Render.Tests/Gpu/ManyPointLightsGpuTests.cs`
  - After line 121: a new theory.
  - Lines 145-164: the aged-parity sequence.
  - Lines 228-238: the `LightQueue` enum.
  - Lines 349-351: `QueueLights`.

**Interfaces:**
- Consumes: `ManyPointLightsScene.Capture(Surface, float, LightQueue, bool, float)`, unchanged.
- Produces: `ManyPointLightsScene.LightQueue.InViewCrowdRelevantLast`.

This task lands a test that passes on purpose, on the fixed layout. It pins the invariant the compact layout in B3 must keep. With 48 in-view lights queued ahead of the contributing one, every receiver cluster reads its list from a non-zero offset and a non-zero component. With the crowd frame in the aged sequence, a later sparse frame runs over a buffer that still holds a longer prefix.

- [ ] **Step 1: Write the test**

Add to the `LightQueue` enum, after `ClusterOverflowWithInvalidGeometry`:
```csharp
        InViewCrowdRelevantLast,
```
Add to `QueueLights`, right after `if (lights == LightQueue.None) return;`:
```csharp
        if (lights == LightQueue.InViewCrowdRelevantLast)
        {
            // Forty-eight lights hanging 1.5 m over the receiver with a 1 m radius. They are in view, so the grid stores
            // their indices ahead of the contributing light's and the receiver's clusters read from non-zero offsets,
            // but none of them reaches the receiver, so the picture must match the contributing light alone.
            for (int i = 0; i < 48; i++)
                scene.AddLight(new Vector3(-3.5f + i % 8, 1.5f, -2.5f + i / 8), Color.White, radius: 1f,
                    intensity: 2f);
            AddRelevant(scene);
            return;
        }
```
Add the theory after `Invalid_light_geometry_cannot_change_illumination_when_a_cluster_overflows`:
```csharp
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Model_receiver_reads_its_light_from_a_non_zero_cluster_offset_behind_an_in_view_crowd(bool perspective)
    {
        ManyPointLightsScene.Shot alone = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.RelevantOnly, perspective: perspective);
        ManyPointLightsScene.Shot crowded = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.InViewCrowdRelevantLast, perspective: perspective);

        PointLightClusterProjection projection = perspective
            ? PointLightClusterProjection.Perspective
            : PointLightClusterProjection.Orthographic;
        AssertMatchesAlone(alone, crowded, $"model behind an in-view crowd, perspective {perspective}", projection);
        Assert.True(crowded.Clusters.LightReferenceCount >= alone.Clusters.LightReferenceCount + 48,
            $"the crowd added {crowded.Clusters.LightReferenceCount - alone.Clusters.LightReferenceCount} cluster "
            + "references, fewer than one per crowd light, so the receiver may never read past offset zero");
    }
```
In `Reused_many_light_scene_matches_a_fresh_scene_after_camera_and_order_changes`, add this after the `ClusterOverflow` capture and before the `aged` capture:
```csharp
        scene.Capture(ManyPointLightsScene.Surface.Model, RightAzimuth,
            ManyPointLightsScene.LightQueue.InViewCrowdRelevantLast, perspective: true);
```

- [ ] **Step 2: Run it and confirm it passes on the fixed layout**

Run: `KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~ManyPointLightsGpuTests"`

Expected: PASS with zero skipped. This is the baseline. A failure here means the scene is wrong, not the layout. For example, a crowd light reaching the receiver would show up as a brightness mismatch. Fix the scene before B3.

- [ ] **Step 3: Implement**

Nothing beyond Step 1. It is a test-only task.

- [ ] **Step 4: Run the tests and confirm they pass**

Run the Step 2 command, plus `sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree` (the file grows to about 545 lines and is not baselined).

D3D11 and Vulkan run this class only on the weekly sweep or on a dispatch (`docs/CROSS-PLATFORM.md:521-522, 575`). See B3 Step 4.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render.Tests/Gpu/ManyPointLightsGpuTests.cs
git commit -m "render3d(lighting): pin clustered light offsets behind an in-view crowd on the GPU"
```

---

### Task B3: Compact cluster layout, shader lookup and used-prefix upload

**Branch:** `feature/frame-cost-clusters`

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs`
  - Lines 7-17: summary and constants.
  - Lines 33-37: add `UsedUIntCount`.
  - The B1 `Build` and `PackCluster`.
  - Lines 199-208: `MarkInvalid`.
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs`
  - Lines 314-351: cluster lookup.
  - Lines 372-386: loop head.
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightClusters.cs:59`
- Modify (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterImage.cs`: the builder decoder and `AssertSameAssignment`.
- Modify (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterBuilderTests.cs`
  - Lines 113-136: floating origin.
  - Lines 161-211: invalid camera and overflow.
  - Lines 238-258: helpers.
- Modify (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterShaderTests.cs:31-71`
- Create (test): `KhaozEngine.Render.Tests/Render3D/PointLightClusterUploadTests.cs`
- Modify: the hash tables, rebaked (Step 3):
  - `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`
  - `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`
  - `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`

**Interfaces:**
- Consumes: B1's `PointLightClusterRanges`, `AssignClusters`, `PlaneSetsBuilt` and `SphereTests`, and B2's `InViewCrowdRelevantLast`.
- Produces:
  - `PointLightClusterBuilder` constants:
    - `ImageUIntCount` (the value stays 235,008, which is 940,032 bytes)
    - `HeaderRegionUInts = 3456`, `HeaderRegionUvec4s = 864`, `IndexRegionUInts = 231552`
    - `CountBits = 8`, `CountMask = 255u`, `OverflowCount = 255u`
  - `internal int UsedUIntCount { get; private set; }`
  - `HeaderUInts` and `ClusterStrideUInts` are removed. Only tests referenced them.
  - GLSL: `bool pointLightClusterForFragment(vec3 worldPos, out uint clusterIndex)`.

- [ ] **Step 1: Write the failing test**

In `PointLightClusterImage.cs`, replace the builder `Lights` overload and delete the last line of `AssertSameAssignment` (the whole-image comparison):
```csharp
    /// <summary>A cluster's stored indices and overflow flag in the compact layout: one header uint per cluster, the
    /// count in the low 8 bits and the offset into the index region in the high 24. It also asserts the run lies
    /// inside the uploaded prefix, which is what the shader can see.</summary>
    internal static ReadOnlySpan<uint> Lights(PointLightClusterBuilder builder, int cluster, out bool overflow)
    {
        uint header = builder.Image[cluster];
        uint count = header & PointLightClusterBuilder.CountMask;
        overflow = count == PointLightClusterBuilder.OverflowCount;
        if (overflow || count == 0u) return ReadOnlySpan<uint>.Empty;
        int first = PointLightClusterBuilder.HeaderRegionUInts + (int)(header >> PointLightClusterBuilder.CountBits);
        Assert.True(first + (int)count <= builder.UsedUIntCount,
            $"cluster {cluster} reads indices {first} to {first + (int)count} past the uploaded prefix of "
            + $"{builder.UsedUIntCount}");
        return builder.Image.AsSpan(first, (int)count);
    }
```

In `PointLightClusterBuilderTests.cs`:
- Replace the helpers at lines 238-258 with the block below.
- Replace lines 188-193 of the invalid-camera test.
- Replace the overflow test at lines 196-211.
- Replace line 135 of the floating-origin test.
- Add the new facts.

Every `AssertContains` and `ClusterContains` caller stays as written.
```csharp
    static void AssertContains(PointLightClusterBuilder builder, int x, int y, int z, uint light) =>
        Assert.True(ClusterContains(builder, x, y, z, light),
            $"cluster ({x},{y},{z}) did not contain submitted light {light}");

    static bool ClusterContains(PointLightClusterBuilder builder, int x, int y, int z, uint light) =>
        PointLightClusterImage.Contains(builder, x, y, z, light);

    static ModelRenderer.PointLightData[] Stack(int count, Vector3 position, float radius)
    {
        var lights = new ModelRenderer.PointLightData[count];
        for (int i = 0; i < count; i++) lights[i] = Light(position, radius);
        return lights;
    }
```
```csharp
        // (InvalidCameraDataMarksEveryClusterForFullListFallback, replacing its final loop)
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts, builder.UsedUIntCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
            Assert.Equal(PointLightClusterBuilder.OverflowCount, builder.Image[cluster]);
```
```csharp
        // (FloatingOriginShiftLeavesEveryPackedIndexUnchanged, replacing Assert.Equal(atZero.Image, shifted.Image))
        Assert.Equal(atZero.UsedUIntCount, shifted.UsedUIntCount);
        Assert.Equal(atZero.Image[..atZero.UsedUIntCount], shifted.Image[..shifted.UsedUIntCount]);
```
```csharp
    [Fact]
    public void SixtyFifthReferenceMarksOverflowAndStoresNoIndices()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(65, new Vector3(1f, 0f, -1.5f), 0.1f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(PointLightClusterBuilder.OverflowCount, builder.Image[PointLightClusterImage.ClusterIndex(8, 4, 0)]);
        Assert.Equal(1, builder.OverflowedClusters);
        // The diagnostic keeps its meaning: the 64 references the cluster accepted before it overflowed.
        Assert.Equal(64, builder.LightReferenceCount);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts, builder.UsedUIntCount);
    }

    [Fact]
    public void SixtyFourReferencesFillAClusterInAscendingOrderWithoutOverflow()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(64, new Vector3(1f, 0f, -1.5f), 0.1f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        ReadOnlySpan<uint> stored = PointLightClusterImage.Lights(builder, PointLightClusterImage.ClusterIndex(8, 4, 0),
            out bool overflow);
        Assert.False(overflow);
        Assert.Equal(64, stored.Length);
        for (int i = 0; i < stored.Length; i++) Assert.Equal((uint)i, stored[i]);
        Assert.Equal(0, builder.OverflowedClusters);
    }

    [Fact]
    public void TheCompactImageKeepsTheBufferSizeAndHoldsAFullGrid()
    {
        Assert.Equal(940_032, PointLightClusterBuilder.ImageUIntCount * sizeof(uint));
        Assert.Equal(864, PointLightClusterBuilder.HeaderRegionUvec4s);
        Assert.Equal(231_552, PointLightClusterBuilder.IndexRegionUInts);
        Assert.True(PointLightClusterBuilder.IndexRegionUInts
            >= PointLightClusterBuilder.ClusterCount * PointLightClusterBuilder.MaxLightsPerCluster);
        Assert.True(PointLightClusterBuilder.IndexRegionUInts < 1 << (32 - PointLightClusterBuilder.CountBits));
        Assert.True(PointLightClusterBuilder.MaxLightsPerCluster < PointLightClusterBuilder.OverflowCount);
    }

    [Fact]
    public void SixtyFourLightsReachingEveryClusterFillTheIndexRegionWithoutRunningOut()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(64, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(0, builder.OverflowedClusters);
        Assert.Equal(PointLightClusterBuilder.ClusterCount * 64, builder.LightReferenceCount);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts + PointLightClusterBuilder.ClusterCount * 64,
            builder.UsedUIntCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            Assert.Equal(((uint)(cluster * 64) << PointLightClusterBuilder.CountBits) | 64u, builder.Image[cluster]);
            ReadOnlySpan<uint> stored = PointLightClusterImage.Lights(builder, cluster, out _);
            for (int i = 0; i < 64; i++) Assert.Equal((uint)i, stored[i]);
        }
    }

    [Fact]
    public void SixtyFiveLightsReachingEveryClusterOverflowEveryHeaderAndStoreNoIndices()
    {
        var builder = new PointLightClusterBuilder();

        builder.Build(Stack(65, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(0f, builder.Depth.W);
        Assert.Equal(PointLightClusterBuilder.ClusterCount, builder.OverflowedClusters);
        Assert.Equal(PointLightClusterBuilder.ClusterCount * 64, builder.LightReferenceCount);
        Assert.Equal(PointLightClusterBuilder.HeaderRegionUInts, builder.UsedUIntCount);
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
            Assert.Equal(PointLightClusterBuilder.OverflowCount, builder.Image[cluster]);
    }

    [Fact]
    public void ClusterIndicesAreContiguousInClusterOrderAndThePaddingIsZero()
    {
        var builder = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(0f, 0f, -2f), 1f),
            Light(new Vector3(4f, 2f, -12f), 2f),
            Light(new Vector3(-3f, -1f, -6f), 1.5f),
        ];

        builder.Build(lights, Perspective, Vector3.Zero, Forward, Perspective, Vector3.Zero);

        int next = 0;
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            uint header = builder.Image[cluster];
            uint count = header & PointLightClusterBuilder.CountMask;
            if (count == 0u)
            {
                Assert.Equal(0u, header);
                continue;
            }
            Assert.NotEqual(PointLightClusterBuilder.OverflowCount, count);
            Assert.Equal((uint)next, header >> PointLightClusterBuilder.CountBits);
            ReadOnlySpan<uint> stored = PointLightClusterImage.Lights(builder, cluster, out _);
            for (int i = 1; i < stored.Length; i++) Assert.True(stored[i - 1] < stored[i]);
            next += (int)count;
        }
        int used = PointLightClusterBuilder.HeaderRegionUInts + next;
        Assert.True(next > 0);
        Assert.Equal((used + 3) & ~3, builder.UsedUIntCount);
        for (int i = used; i < builder.UsedUIntCount; i++) Assert.Equal(0u, builder.Image[i]);
    }

    [Fact]
    public void ASparseFrameAfterADenseOneMatchesAFreshBuilderOverItsUsedPrefix()
    {
        var reused = new PointLightClusterBuilder();
        var fresh = new PointLightClusterBuilder();
        ModelRenderer.PointLightData[] sparse =
        [
            Light(new Vector3(1f, 0.5f, -1.5f), 0.6f),
            Light(new Vector3(-6f, 3f, -12f), 3f),
        ];

        reused.Build(Stack(65, new Vector3(0f, 0f, -13f), 100f), Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        reused.Build(sparse, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);
        fresh.Build(sparse, Ortho, Vector3.Zero, Forward, Ortho, Vector3.Zero);

        Assert.Equal(fresh.UsedUIntCount, reused.UsedUIntCount);
        Assert.Equal(fresh.Image[..fresh.UsedUIntCount], reused.Image[..reused.UsedUIntCount]);
    }
```

In `PointLightClusterShaderTests.cs`, replace the last line of `ZeroLightsReturnBeforeReadingAStaleClusterAndFallbackWalksTheFullQueue` and its `candidateCount` assertion, replace `ShaderGridConstantsMatchTheCpuPackedImage`, and add one fact:
```csharp
        Assert.Contains("int candidateCount = fullPointLightFallback ? npl : int(clusterCount);",
            source, StringComparison.Ordinal);
        Assert.Contains("int lightIndex = fullPointLightFallback ? candidate : clusteredLightIndex",
            source, StringComparison.Ordinal);
        Assert.Contains(
            $"if (clusterCount > {PointLightClusterBuilder.MaxLightsPerCluster}u) fullPointLightFallback = true;",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderGridConstantsMatchTheCpuPackedImage()
    {
        string source = ShaderSources.LightingCommonGlsl;
        Assert.Contains($"* {PointLightClusterBuilder.ClusterCountX}.0", source, StringComparison.Ordinal);
        Assert.Contains($"* {PointLightClusterBuilder.ClusterCountY}.0", source, StringComparison.Ordinal);
        Assert.Contains($"* {PointLightClusterBuilder.ClusterCountZ}.0", source, StringComparison.Ordinal);
        Assert.Contains("clusterIndex = uint((tileZ * 9 + tileY) * 16 + tileX);", source, StringComparison.Ordinal);
        Assert.Contains("uvec4 packedHeaders = PointLightClusters[clusterIndex >> 2u];", source,
            StringComparison.Ordinal);
        Assert.Contains($"clusterCount = clusterHeader & {PointLightClusterBuilder.CountMask}u;", source,
            StringComparison.Ordinal);
        Assert.Contains($"clusterOffset = clusterHeader >> {PointLightClusterBuilder.CountBits}u;", source,
            StringComparison.Ordinal);
        Assert.Contains($"PointLightClusters[{PointLightClusterBuilder.HeaderRegionUvec4s}u + (indexSlot >> 2u)]",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClusterLoopKeepsItsVaryingCountFormForDirect3D()
    {
        // STABLE-POINT-LIGHTING-2026-09-19.md: the count varies per fragment and every atlas read inside the loop uses
        // explicit mip zero, so FXC never attempts an unbounded unroll.
        Assert.Contains("for (int candidate = 0; candidate < candidateCount; candidate++) {",
            ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
    }
```

Create `KhaozEngine.Render.Tests/Render3D/PointLightClusterUploadTests.cs`:
```csharp
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The cluster image is uploaded as its used prefix, the headers plus the indices this frame stored, never as
/// the whole 940,032-byte buffer (issue #1112).</summary>
public sealed class PointLightClusterUploadTests
{
    static readonly Matrix4x4 Ortho = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);

    static ModelRenderer NewRenderer(FakeGpuDevice device) => new(device,
        new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
            GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float),
        shadowMapResolution: 128, shadowCascadeCount: 1);

    [Fact]
    public void AFrameUploadsTheHeadersAndOnlyTheIndicesItStored()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(1f, 0.5f, -1.5f), 0.6f),
            Light(new Vector3(-6f, 3f, -12f), 3f),
        ];

        renderer.BuildAndUploadPointLightClusters(cl, lights, Ortho, Vector3.Zero, -Vector3.UnitZ, Ortho, Vector3.Zero);

        RecordingGpuCommandList.Upload upload =
            Assert.Single(cl.Uploads, u => ReferenceEquals(u.Buffer, renderer.PointLightClusterBuffer));
        uint[] words = MemoryMarshal.Cast<byte, uint>(upload.Data!.AsSpan()).ToArray();
        int stored = 0;
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            uint count = words[cluster] & PointLightClusterBuilder.CountMask;
            if (count != PointLightClusterBuilder.OverflowCount) stored += (int)count;
        }
        int expectedWords = (PointLightClusterBuilder.HeaderRegionUInts + stored + 3) & ~3;
        Assert.Equal(0u, upload.Offset);
        Assert.True(stored > 0, "the two lights stored no references");
        Assert.Equal((uint)(expectedWords * sizeof(uint)), upload.Bytes);
        Assert.True(upload.Bytes < renderer.PointLightClusterBuffer.SizeInBytes / 10);
    }

    [Fact]
    public void AnEmptyFrameUploadsNothing()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        var cl = new RecordingGpuCommandList(new NullGpuCommandList());

        renderer.BuildAndUploadPointLightClusters(cl, ReadOnlySpan<ModelRenderer.PointLightData>.Empty, Ortho,
            Vector3.Zero, -Vector3.UnitZ, Ortho, Vector3.Zero);

        Assert.DoesNotContain(cl.Uploads, u => ReferenceEquals(u.Buffer, renderer.PointLightClusterBuffer));
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightCluster"`

Expected: build errors `CS0117` (`'PointLightClusterBuilder' does not contain a definition for 'HeaderRegionUInts'`, and the same for `CountMask`, `CountBits`, `OverflowCount`, `HeaderRegionUvec4s` and `IndexRegionUInts`) and `CS1061` for `UsedUIntCount`.

- [ ] **Step 3: Implement**

`PointLightClusterBuilder.cs`: summary and constants, replacing lines 7-17:
```csharp
/// <summary>Builds the compact clustered point-light image consumed by lit receiver shaders: one header uint per
/// cluster, then each cluster's light indices, contiguous and in ascending submitted order. Each light first gets a
/// conservative cluster range from <see cref="PointLightClusterRanges"/>, and each cluster runs the exact plane test on
/// only the lights whose range holds it (issue #1112).</summary>
internal sealed class PointLightClusterBuilder
{
    internal const int ClusterCountX = 16;
    internal const int ClusterCountY = 9;
    internal const int ClusterCountZ = 24;
    internal const int MaxLightsPerCluster = 64;
    internal const int ClusterCount = ClusterCountX * ClusterCountY * ClusterCountZ;

    /// <summary>The image keeps the size of the fixed layout it replaced, seventeen uvec4 per cluster or 940,032 bytes,
    /// so the buffer, its binding, its stride and every size pin stay as they were.</summary>
    internal const int ImageUIntCount = ClusterCount * 17 * 4;

    /// <summary>One header uint per cluster at the start of the image, four to a uvec4.</summary>
    internal const int HeaderRegionUInts = ClusterCount;
    internal const int HeaderRegionUvec4s = HeaderRegionUInts / 4;

    /// <summary>Everything after the headers holds light indices, four to a uvec4. It is at least
    /// <see cref="MaxLightsPerCluster"/> for every cluster, so a full grid can never run it out.</summary>
    internal const int IndexRegionUInts = ImageUIntCount - HeaderRegionUInts;

    /// <summary>A header's low <see cref="CountBits"/> bits hold the cluster's light count and the high bits hold the
    /// offset of its first index in the index region.</summary>
    internal const int CountBits = 8;
    internal const uint CountMask = (1u << CountBits) - 1u;

    /// <summary>The count of a cluster that passed more than <see cref="MaxLightsPerCluster"/> lights. It stores no
    /// indices and its fragments walk the complete list.</summary>
    internal const uint OverflowCount = CountMask;
```
New state, next to the other properties:
```csharp
    /// <summary>How many uints of <see cref="Image"/> the latest build wrote, the headers plus the stored indices rounded
    /// up to a whole uvec4. Only this prefix is uploaded.</summary>
    internal int UsedUIntCount { get; private set; }

    int _indexCursor;
```
`Build`, replacing B1's:
```csharp
    internal void Build(ReadOnlySpan<ModelRenderer.PointLightData> lights,
        Matrix4x4 gpuCorrectedRenderViewProjection, Vector3 eyeRender, Vector3 forward,
        Matrix4x4 projection, Vector3 renderOrigin)
    {
        // Only the headers are cleared. Indices are written behind a cursor and the upload stops at it, so whatever an
        // earlier frame left past the cursor is never uploaded and never read.
        Array.Clear(Image, 0, HeaderRegionUInts);
        _indexCursor = 0;
        OverflowedClusters = 0;
        LightReferenceCount = 0;
        PlaneSetsBuilt = 0;
        SphereTests = 0;
        if (!TryPrepare(gpuCorrectedRenderViewProjection, eyeRender, forward, projection, renderOrigin,
                out Vector3 cameraForward))
        {
            MarkInvalid();
            return;
        }

        CameraForward = new Vector4(cameraForward, 0f);
        _ranges.Gather(lights, renderOrigin, eyeRender, cameraForward, _clipNear, _clipFar, _sliceDepth,
            FrameGeometryScale());
        if (!AssignClusters(lights, renderOrigin))
        {
            MarkInvalid();
            return;
        }

        // Round up to a whole uvec4 and zero the pad, so the upload is whole elements and the image is deterministic.
        int used = HeaderRegionUInts + _indexCursor;
        while ((used & 3) != 0) Image[used++] = 0u;
        UsedUIntCount = used;
    }
```
`PackCluster`, replacing B1's. `AssignClusters`, `FrameGeometryScale` and `EnsureCandidateCapacity` are unchanged:
```csharp
    // The exact test is unchanged. Only its candidates changed: the lights whose range holds this cluster, still in
    // ascending submitted order, so the first 64 that pass and the overflow point are the same as testing all of them.
    // The planes are built at the first candidate, so a cluster no light's range reaches is never built.
    bool PackCluster(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin,
        ReadOnlySpan<int> survivors, ReadOnlySpan<PointLightClusterRanges.ClusterRange> ranges, int rowCount,
        int x, int y, int z)
    {
        int cluster = (z * ClusterCountY + y) * ClusterCountX + x;
        int first = HeaderRegionUInts + _indexCursor;
        ClusterPlanes planes = default;
        float clusterScale = 0f;
        bool planesBuilt = false;
        bool overflow = false;
        int count = 0;
        for (int i = 0; i < rowCount; i++)
        {
            int entry = _rowCandidates[i];
            if (!ranges[entry].ContainsColumn(x)) continue;
            if (!planesBuilt)
            {
                if (!TryBuildPlanes(x, y, _sliceDepth[z], _sliceDepth[z + 1], out planes, out clusterScale))
                    return false;
                planesBuilt = true;
                PlaneSetsBuilt++;
            }
            int light = survivors[entry];
            Vector4 posRadius = lights[light].PosRadius;
            var center = new Vector3(posRadius.X, posRadius.Y, posRadius.Z) - renderOrigin;
            float radius = posRadius.W;
            float epsilon = GeometryEpsilonScale * MathF.Max(1f,
                MathF.Max(clusterScale, MathF.Max(MaxAbs(center), radius)));
            SphereTests++;
            if (!planes.IntersectsSphere(center, radius, epsilon)) continue;
            if (count == MaxLightsPerCluster)
            {
                overflow = true;
                break;
            }
            Image[first + count] = (uint)light;
            count++;
        }

        // The diagnostic keeps its meaning: an overflowed cluster still counts the 64 references it accepted first.
        LightReferenceCount += count;
        if (overflow)
        {
            // An overflowed cluster stores no indices. Its tentative ones sit past the cursor and the next cluster writes
            // over them.
            Image[cluster] = OverflowCount;
            OverflowedClusters++;
        }
        else if (count > 0)
        {
            Image[cluster] = ((uint)_indexCursor << CountBits) | (uint)count;
            _indexCursor += count;
        }
        return true;
    }
```
`MarkInvalid`, replacing lines 199-208:
```csharp
    void MarkInvalid()
    {
        Array.Fill(Image, OverflowCount, 0, HeaderRegionUInts);
        _indexCursor = 0;
        UsedUIntCount = HeaderRegionUInts;
        Depth = new Vector4(0f, 0f, 0f, -1f);
        CameraForward = Vector4.Zero;
        OverflowedClusters = ClusterCount;
        LightReferenceCount = 0;
    }
```
`ModelRenderer.PointLightClusters.cs`, replacing line 59:
```csharp
        // Only the used prefix, the headers plus this frame's indices, rather than the whole 940,032-byte image.
        cl.UpdateBuffer<uint>(_pointLightClusterBuffer, 0,
            new ReadOnlySpan<uint>(_pointLightClusters.Image, 0, _pointLightClusters.UsedUIntCount));
```
`ShaderSources.Lighting.cs`: the full new cluster lookup (lines 314-351). Every line between the depth checks and `tileZ` is kept verbatim:
```glsl
// pointAtlas/pointSamp are parameters for the same reason sampleKeyShadow's are: their set/binding differ per
// fragment and GLSL cannot reference a fragment's own bindings from a shared function.
//
// THE CLUSTER IMAGE (PointLightClusterBuilder, issue #1112) is one uvec4 buffer in two regions. The first 864 uvec4
// hold one header uint per cluster, four to an element: the low 8 bits are the cluster's light count, 255 meaning it
// overflowed and stores nothing, and the high 24 bits are where its first index sits in the index region. The index
// region follows at uvec4 864, four indices to an element, each cluster's run contiguous and in ascending submitted
// order, so a clustered fragment sums the same lights in the same order the full list would.
bool pointLightClusterForFragment(vec3 worldPos, out uint clusterIndex) {
    clusterIndex = 0u;
    if (ClusterDepth.w < -0.5) return false;

    vec4 clip = ViewProj * vec4(worldPos, 1.0);
    if (any(isnan(clip)) || any(isinf(clip)) || clip.w <= 0.0) return false;
    vec2 ndc = clip.xy / clip.w;
    if (any(isnan(ndc)) || any(isinf(ndc))) return false;
    const float ndcEpsilon = 1e-5;
    if (ndc.x < -1.0 - ndcEpsilon || ndc.x > 1.0 + ndcEpsilon ||
        ndc.y < -1.0 - ndcEpsilon || ndc.y > 1.0 + ndcEpsilon) return false;

    float depth = dot(worldPos - CameraPos.xyz, ClusterCamera.xyz);
    if (isnan(depth) || isinf(depth)) return false;
    float depthEpsilon = 1e-4 * max(1.0, max(abs(ClusterDepth.x), abs(ClusterDepth.y)));
    if (depth < ClusterDepth.x - depthEpsilon || depth > ClusterDepth.y + depthEpsilon) return false;
    float clampedDepth = clamp(depth, ClusterDepth.x, ClusterDepth.y);

    float zNorm;
    if (ClusterDepth.w > 0.5) {
        if (ClusterDepth.x <= 0.0 || ClusterDepth.z <= 0.0) return false;
        zNorm = log(max(clampedDepth, ClusterDepth.x) / ClusterDepth.x) / ClusterDepth.z;
    } else {
        float span = ClusterDepth.y - ClusterDepth.x;
        if (span <= 0.0) return false;
        zNorm = (clampedDepth - ClusterDepth.x) / span;
    }
    if (isnan(zNorm) || isinf(zNorm)) return false;

    int tileX = clamp(int(floor((ndc.x * 0.5 + 0.5) * 16.0)), 0, 15);
    int tileY = clamp(int(floor((ndc.y * 0.5 + 0.5) * 9.0)), 0, 8);
    int tileZ = clamp(int(floor(clamp(zNorm, 0.0, 1.0) * 24.0)), 0, 23);
    clusterIndex = uint((tileZ * 9 + tileY) * 16 + tileX);
    return true;
}
```
The full new loop head inside `computeLighting`, replacing lines 372-386. The body from `if (lightIndex < 0 || lightIndex >= npl) continue;` onward is unchanged, and the loop keeps its varying-count form:
```glsl
    uint clusterIndex;
    bool fullPointLightFallback = !pointLightClusterForFragment(worldPos, clusterIndex);
    uint clusterCount = 0u;
    uint clusterOffset = 0u;
    if (!fullPointLightFallback) {
        uvec4 packedHeaders = PointLightClusters[clusterIndex >> 2u];
        uint clusterHeader = packedHeaders[int(clusterIndex & 3u)];
        clusterCount = clusterHeader & 255u;
        clusterOffset = clusterHeader >> 8u;
        if (clusterCount > 64u) fullPointLightFallback = true;
    }
    int candidateCount = fullPointLightFallback ? npl : int(clusterCount);
    for (int candidate = 0; candidate < candidateCount; candidate++) {
        int clusteredLightIndex = 0;
        if (!fullPointLightFallback) {
            uint indexSlot = clusterOffset + uint(candidate);
            uvec4 packedIndices = PointLightClusters[864u + (indexSlot >> 2u)];
            clusteredLightIndex = int(packedIndices[int(indexSlot & 3u)]);
        }
        int lightIndex = fullPointLightFallback ? candidate : clusteredLightIndex;
```
Rebake the three shader hash tables. Each test's `IsUpdating()` reads its own environment variable and rewrites its table:
```bash
KE_UPDATE_SPIRV_HASHES=1 KE_UPDATE_MSL_HASHES=1 KE_UPDATE_HLSL_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanSpirvByteEquality|FullyQualifiedName~MetalMslByteEquality|FullyQualifiedName~D3D11HlslByteEquality"
git diff -U0 -- KhaozEngine.Render.Tests/Gpu/spirv-hashes KhaozEngine.Render.Tests/Gpu/msl-hashes KhaozEngine.Render.Tests/Gpu/hlsl-hashes
```
Read the diff before going on:
- Each table must change exactly seven rows: `Foliage.fragment`, `Model.fragment`, `ModelDissolve.fragment`, `SkinnedModel.fragment`, `SkinnedModelDissolve.fragment`, `Splat.fragment` and `TileGround.fragment`.
- No `.vertex` row may move, and no other program may move. If anything else moves, the pinned options moved, so stop.
- The header comment lines stay unchanged.

- [ ] **Step 4: Run the tests and confirm they pass**
```bash
dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightCluster"
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanSpirvByteEquality|FullyQualifiedName~MetalMslByteEquality|FullyQualifiedName~D3D11HlslByteEquality|FullyQualifiedName~PointLightBufferTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~D3D11RegisterNumberingTests|FullyQualifiedName~VulkanDescriptorLimitTests|FullyQualifiedName~PointShadowFilterShaderTests|FullyQualifiedName~LightingCompositionTests|FullyQualifiedName~ShadowShaderContractTests"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~ManyPointLightsGpuTests|FullyQualifiedName~PointLightGpuTests"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~Golden"
sh scripts/check-dashes.sh --tree && sh scripts/check-file-size.sh --tree
```
Expected:
- All green. The hash tests pass without the environment variables. `WarmBuildAllocatesNothing` and the B1 equivalence and range tests still pass on the new decoder.
- The Metal GPU runs show zero skipped, including B2's crowd theory and the aged parity test.
- Every Metal golden is unchanged, because lighting output is identical.

Backend coverage:
- The D3D11 and Vulkan push legs run golden tests only. Those goldens compile the lit fragments through FXC and SPIR-V, which proves the shaders compile.
- `ManyPointLightsGpuTests` and `PointLightGpuTests` run on those legs only on the weekly sweep or a dispatch. Once the round branch is pushed, run:
  - `gh workflow run cross-platform-gpu.yml --ref feature/frame-cost-round -f legs=direct3d11-native -f renderTestFilter="FullyQualifiedName~ManyPointLightsGpuTests|FullyQualifiedName~PointLightGpuTests"`
  - the same command with `-f legs=vulkan-native`

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightClusters.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterImage.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterBuilderTests.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterShaderTests.cs KhaozEngine.Render.Tests/Render3D/PointLightClusterUploadTests.cs KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt
git commit -m "render3d(lighting): pack point light clusters compactly and upload the used prefix"
```

---


### Decisions and risks


- **Frustum cull rationale.** Spec lines 218-219 against `KhaozEngine.Render3D/Internal/PointLightClusterBuilder.cs:280-290`. See conflict 1 at the top. The plan keeps the output identical by never side-culling a light that reaches the near plane.
- **Lazy plane building against identical output.** Spec lines 224-228 against `PointLightClusterBuilder.cs:62-67`. See conflict 2 at the top. Owner decision. It is pinned by `ADegenerateClusterThatNoLightReachesNoLongerForcesTheFullListFallback`.
- **Reference count under overflow.** Spec lines 211-212 against `PointLightClusterBuilder.cs:179-189` and `docs/USING-KHAOZENGINE.md:3226`. The plan keeps counting 64 per overflowed cluster so the diagnostic value does not change.
- **Tile span method.** Spec line 220 says "projected screen rectangle". The plan computes the tile span from signed distances to the 17 column and 10 row boundary planes. Those are the same planes the clusters test, so the result is the tile-quantised projected rectangle and is conservative by construction. This is a difference in method only.
- **Float margin.** `PointLightClusterRanges.MarginScale` (1e-3 of the frame scale) must exceed the exact-test epsilon (`PointLightClusterBuilder.cs:176-177`, 1e-4) plus the rounding between the planes. That rounding grows when the eye sits far from render-space zero, which happens when a game sets no `Scene3D.RenderOrigin` (`KhaozEngine.Render3D/Scene3D.RenderOrigin.cs:107-115`).
  - A miss could only drop a light from a cluster it touches by less than epsilon. That is invisible on screen but not byte-identical.
  - The seeded harness covers eyes up to 200 m out. If a seed fails, raise the margin. Never weaken the oracle.
- **CI coverage.** `docs/CROSS-PLATFORM.md:521-522, 575`. The D3D11 and Vulkan legs run `ManyPointLightsGpuTests` only on the weekly sweep or a dispatch, so the integration branch needs the dispatch given in B3 Step 4.
- **Hash table merge.** Spec lines 282-285. `feature/water-glint-foam` also rewrites all three hash tables. Whichever merges second rebakes with the B3 command.
- **Documentation sweep for the release commit.** `docs/design/STABLE-POINT-LIGHTING-2026-09-19.md:27-29` still describes the 17-record, 918 KiB layout. It is a history document, so the release sweep decides whether to annotate it. No living doc states the layout.
- **Upload size.** The plan rounds the upload up to a whole uvec4 (at most 12 extra bytes) so that every backend gets whole structured elements. The spec says "13.8 KB header plus the used index bytes".


---

## Item 4: Water flat quads, pre-pass uploads and culling (#1113)

The code contradicts the spec in four places. The plan handles them as follows, and the full list with file:line is at the end.

1. **Growing the bounds by the swell amplitude alone is not safe.** The Gerstner pinch moves points sideways by up to `abs(steepness) * wavelength / (2 pi)`, whatever the amplitude. The plan grows XZ by that amount and Y by the amplitude.
2. **FFT planes have no swell amplitude to grow by.** Their displacement comes from the ocean maps, and the CPU has no bound on it. The plan never culls them.
3. **The River golden is not the only committed golden that changes path.** `scene3d_sky_two_discs` also draws a zero-swell procedural plane in camera-focused mode.
4. **One existing test would break under culling.** `WaterFlatPlaneTests` passes `Matrix4x4.Identity` as the view-projection, and planes at x = -20 and x = 20 would then be culled. The plan moves it to a real camera. `GrownBufferRetirementTests` also uses the identity matrix but still passes, because the uniform buffer capacity stays keyed on the full plane count.

**Setup (once, before C1):**
```bash
git -C /Users/antonio/KhaozEngine worktree add /Users/antonio/KhaozEngine/.worktrees/frame-cost-water -b feature/frame-cost-water feature/frame-cost-round
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-water && mkdir -p local-feed
```

---

### Task C1: Widened `UsesFlatQuad`, flat quads in every grid mode, one upload before the pass, River rebake

**Branch:** `feature/frame-cost-water` (worktree created from `feature/frame-cost-round`)

**Files:**
- Create: `KhaozEngine.Render3D/Internal/WaterSwellReach.cs`
- Create: `KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs`
- Modify: `KhaozEngine.Render3D/Rendering/WaterRenderer.FlatPlane.cs:1-71` (rewrite)
- Modify: `KhaozEngine.Render3D/Rendering/WaterRenderer.cs:570-599` (Draw doc and head), `:644-683` (upload and draw tail). Lines 601-642 stay as they are.
- Modify: `KhaozEngine.Render3D/README.md:602-604`, `:634-636`
- Test: `KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.cs:33-40, 56-59, 83-91, 109-121, 125`
- Test: `KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.Draws.cs:8-27`
- Create test: `KhaozEngine.Render.Tests/Render3D/WaterTestFrames.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/WaterFlatPlaneTests.cs:1-106` (rewrite)
- Goldens: `KhaozEngine.Render.Tests/Gpu/goldens/tileworld_river.{metal-native,direct3d11-native,vulkan-native}.txt`, plus `scene3d_sky_two_discs.*.txt` only if its bake moves

KESIZE: none of these files is in `.filesize-baseline`. `WaterRenderer.cs` is at 763 lines against the 800 cap, and this task shrinks it.

**Interfaces:**
- Consumes: `WaterRenderer.Draw(IGpuCommandList, RenderResources, ReadOnlySpan<WaterPlane>, Matrix4x4, Vector3, Color, Vector3, WaterSettings, SkySettings, float, Vector3)`. The signature does not change, and the call site at `Scene3D.cs:1968` is untouched.
- Produces:
  - `internal static bool WaterSwellReach.Displaces(float amplitude, float wavelength)`
  - `internal static bool WaterRenderer.UsesFlatQuad(in WaterPlane, WaterSettings)` (widened)
  - `internal const int WaterRenderer.FlatQuadVertices = 4`
  - `internal const uint WaterRenderer.FlatQuadBytes = 48`
  - `internal enum WaterRenderer.PlaneRoute { Culled, FlatQuad, FocusedGrid, Clipmap }`
  - `internal WaterRenderer.PlaneRoute WaterRenderer.LastRoute(int index)`
- Test-side:
  - `RecordingGpuCommandList.Upload.FramebufferBindsBefore`
  - `RecordingGpuCommandList.FramebufferBinds`
  - `IndexedDraw.IndexStart`, `IndexedDraw.VertexOffset`
  - `WaterTestFrames.TopDown`, `TopDownAt(float)`, `Draw(...)`, `UploadsTo(...)`

- [ ] **Step 1: Write the failing test**

First, extend the recorder. The new tests need these fields to compile against the old renderer, so the red comes from assertions, not build errors.

`RecordingGpuCommandList.cs`:
```csharp
        internal readonly record struct Upload(IGpuBuffer Buffer, uint Offset, uint Bytes, byte[]? Data = null,
            int DrawsBefore = 0, int FramebufferBindsBefore = 0)
        {
            public bool IsWholeBuffer => Offset == 0 && Bytes == Buffer.SizeInBytes;
        }
```
(keep the two existing doc comments above `Upload` and `IsWholeBuffer`)
```csharp
        readonly List<Upload> _uploads = new();
        readonly List<Resolve> _resolves = new();
        readonly List<TextureCopy> _textureCopies = new();
        int _draws;
        int _framebufferBinds;

        /// <summary>Framebuffer binds recorded since the last <see cref="Clear"/>. Paired with
        /// <see cref="Upload.FramebufferBindsBefore"/> it tells a test whether an upload landed before the pass
        /// opened, which is what keeps a staging backend from ending and reopening the pass for it.</summary>
        public int FramebufferBinds => _framebufferBinds;
```
In `Clear()`, after `_draws = 0;`, add `_framebufferBinds = 0;`. Both `UpdateBuffer` overloads end their `new Upload(...)` with `..., _draws, _framebufferBinds)`. Replace the `SetFramebuffer` one-liner with:
```csharp
        public void SetFramebuffer(IGpuFramebuffer fb)
        {
            _framebufferBinds++;
            Inner.SetFramebuffer(fb);
        }
```
In `DrawIndexed`, replace `NoteIndexedDraw(indexCount);` with `NoteIndexedDraw(indexCount, indexStart, vertexOffset);`.

`RecordingGpuCommandList.Draws.cs`:
```csharp
        internal readonly record struct IndexedDraw(
            uint IndexCount, IGpuPipeline? Pipeline, IGpuBuffer? VertexBuffer, IGpuBuffer? IndexBuffer,
            uint IndexStart = 0, int VertexOffset = 0);
```
```csharp
        void NoteIndexedDraw(uint indexCount, uint indexStart, int vertexOffset)
            => _indexedDraws.Add(new IndexedDraw(indexCount, _currentPipeline, _currentVertexBuffer,
                _currentIndexBuffer, indexStart, vertexOffset));
```

New `KhaozEngine.Render.Tests/Render3D/WaterTestFrames.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Drives one headless water frame through <see cref="WaterRenderer"/> the way Scene3D does, prepare
    /// then draw, for the tests that read the frame's shape from a <see cref="RecordingGpuCommandList"/>. The
    /// cameras are real ones, never the identity matrix, because the renderer culls planes the view cannot see.</summary>
    internal static class WaterTestFrames
    {
        /// <summary>Never mutated. Read by PackUbo only.</summary>
        static readonly SkySettings Sky = new();

        /// <summary>An orthographic camera straight down at world x = <paramref name="x"/>, seeing x in
        /// [x - 80, x + 80], z in [-80, 80] and heights from -300 to 99.</summary>
        public static Matrix4x4 TopDownAt(float x)
            => Matrix4x4.CreateLookAt(new Vector3(x, 100f, 0f), new Vector3(x, 0f, 0f), -Vector3.UnitZ)
                * Matrix4x4.CreateOrthographic(160f, 160f, 1f, 400f);

        /// <summary><see cref="TopDownAt"/> over the origin.</summary>
        public static readonly Matrix4x4 TopDown = TopDownAt(0f);

        public static void Draw(WaterRenderer renderer, IGpuCommandList commands, RenderResources resources,
            ReadOnlySpan<WaterPlane> planes, WaterSettings settings, Vector3 eye)
            => Draw(renderer, commands, resources, planes, settings, eye, TopDown);

        public static void Draw(WaterRenderer renderer, IGpuCommandList commands, RenderResources resources,
            ReadOnlySpan<WaterPlane> planes, WaterSettings settings, Vector3 eye, Matrix4x4 viewProj)
        {
            if (commands is RecordingGpuCommandList recording) recording.Clear();
            renderer.PrepareFrame(new FramePrepare(settings, planes, 0f));
            renderer.Draw(commands, resources, planes, viewProj, -Vector3.UnitY, Color.White, eye, settings, Sky, 0f);
        }

        public static List<RecordingGpuCommandList.Upload> UploadsTo(RecordingGpuCommandList commands,
            IGpuBuffer buffer)
        {
            var hits = new List<RecordingGpuCommandList.Upload>();
            foreach (RecordingGpuCommandList.Upload upload in commands.Uploads)
                if (ReferenceEquals(upload.Buffer, buffer)) hits.Add(upload);
            return hits;
        }
    }
}
```

`KhaozEngine.Render.Tests/Render3D/WaterFlatPlaneTests.cs` (whole file):
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterFlatPlaneTests
    {
        [Theory]
        [InlineData(0f, 42f, true)]
        [InlineData(-0.5f, 42f, true)]
        [InlineData(0.45f, 0f, true)]
        [InlineData(0.45f, -10f, true)]
        [InlineData(-0.5f, -10f, true)]
        [InlineData(0.45f, 42f, false)]
        [InlineData(1e-30f, 42f, false)]
        public void EverySwellTheGerstnerGateSwitchesOffDrawsAsAFlatQuad(float amplitude, float wavelength, bool flat)
        {
            var scene = new WaterSettings
            {
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = amplitude,
                SwellWavelength = wavelength,
            };
            Assert.Equal(flat, WaterRenderer.UsesFlatQuad(new WaterPlane(0f, 0f, 0f, 8f), scene));

            // The same knobs arriving through a look, over a scene whose own swell displaces.
            var look = new WaterLook { SwellAmplitude = amplitude, SwellWavelength = wavelength };
            Assert.Equal(flat, WaterRenderer.UsesFlatQuad(new WaterPlane(0f, 0f, 0f, 8f, look: look),
                new WaterSettings { WaveSource = WaterWaveSource.Procedural }));

            // The CPU mirror of the swell agrees: these swells, and only these, build no component at all.
            Span<GerstnerWaves.Component> scratch = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            Assert.Equal(flat, GerstnerWaves.BuildComponents(amplitude, wavelength, 0f, 0f, 0.6f, 0.6f, 0f, 4,
                scratch) == 0);
        }

        [Fact]
        public void ClipmapFrameUsesSixIndicesOnlyForEffectiveProceduralZeroSwellPlanes()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings
            {
                GridMode = WaterGridMode.Clipmap,
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 1f,
                ClipmapLevels = 1,
                ClipmapRingCells = 8,
            };
            WaterPlane[] planes =
            [
                new WaterPlane(-20f, 0f, 0f, 8f, look: new WaterLook { SwellAmplitude = 0f }),
                new WaterPlane(0f, 0f, 0f, 8f),
                new WaterPlane(20f, 0f, 0f, 8f,
                    look: new WaterLook { WaveSource = WaterWaveSource.FftOcean, SwellAmplitude = 0f }),
            ];

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 5f, -5f));

            Assert.Equal(3, commands.IndexedDraws.Count);
            Assert.Equal(6u, commands.IndexedDraws[0].IndexCount);
            Assert.True(commands.IndexedDraws[1].IndexCount > 6);
            Assert.True(commands.IndexedDraws[2].IndexCount > 6);
            Assert.NotSame(commands.IndexedDraws[0].Pipeline, commands.IndexedDraws[1].Pipeline);
            Assert.Same(commands.IndexedDraws[1].Pipeline, commands.IndexedDraws[2].Pipeline);
            Assert.NotSame(commands.IndexedDraws[0].VertexBuffer, commands.IndexedDraws[1].VertexBuffer);
            Assert.NotSame(commands.IndexedDraws[0].IndexBuffer, commands.IndexedDraws[1].IndexBuffer);
            Assert.Equal(2, renderer.LastClipmapRebuilds);
            // The quad lands before the pass opens, as the clipmap slices always did.
            RecordingGpuCommandList.Upload quad = Assert.Single(
                WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[0].VertexBuffer!));
            Assert.Equal(0, quad.FramebufferBindsBefore);
            Assert.Equal(1, commands.FramebufferBinds);
        }

        [Fact]
        public void CameraFocusedFrameDrawsEveryFlatPlaneFromOneQuadUploadAheadOfThePass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 1f };
            WaterPlane[] planes =
            [
                new WaterPlane(-20f, 0f, 0f, 8f, look: new WaterLook { SwellAmplitude = 0f }),
                new WaterPlane(0f, 0f, 0f, 8f),
                new WaterPlane(20f, 1f, 0f, 8f, 4f, look: new WaterLook { SwellWavelength = -1f }),
                new WaterPlane(0f, 0f, 20f, 8f, look: new WaterLook { SwellAmplitude = -2f }),
            ];

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 5f, -5f));

            IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws = commands.IndexedDraws;
            Assert.Equal(4, draws.Count);
            Assert.Equal((uint)WaterMath.GridIndexCount, draws[1].IndexCount);
            IGpuBuffer quads = draws[0].VertexBuffer!;
            int[] quadOffsets = [0, -1, 4, 8];
            for (int i = 0; i < 4; i++)
            {
                if (i == 1) continue;
                Assert.Equal(WaterRenderer.FlatIndexCount, draws[i].IndexCount);
                Assert.Same(quads, draws[i].VertexBuffer);
                Assert.Equal(quadOffsets[i], draws[i].VertexOffset);
                Assert.Same(draws[1].Pipeline, draws[i].Pipeline);   // the regular water pipeline, as the grid uses
            }

            RecordingGpuCommandList.Upload upload = Assert.Single(WaterTestFrames.UploadsTo(commands, quads));
            Assert.Equal(0u, upload.Offset);
            Assert.Equal(3u * 48u, upload.Bytes);
            Assert.Equal(0, upload.FramebufferBindsBefore);
            Assert.Equal(1, commands.FramebufferBinds);

            // The third plane's quad is the second one written: its own rectangle at its own height.
            ReadOnlySpan<Vector3> corners = MemoryMarshal.Cast<byte, Vector3>(upload.Data!.AsSpan()).Slice(4, 4);
            Assert.Equal(new Vector3(12f, 1f, -4f), corners[0]);
            Assert.Equal(new Vector3(28f, 1f, -4f), corners[1]);
            Assert.Equal(new Vector3(12f, 1f, 4f), corners[2]);
            Assert.Equal(new Vector3(28f, 1f, 4f), corners[3]);
        }

        [Fact]
        public void ASteadyRiverOfThirtyFivePlanesUploadsTwiceAndBothLandBeforeThePass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings();   // the scene's own sea displaces, only the river look is flat
            WaterPlane[] planes = RiverPlanes(35);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 9f, 6f));
            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 9f, 6f));

            Assert.Equal(35, commands.IndexedDraws.Count);
            Assert.Equal(2, commands.Uploads.Count);   // the uniform slots and the quads, nothing per plane
            foreach (RecordingGpuCommandList.Upload upload in commands.Uploads)
                Assert.Equal(0, upload.FramebufferBindsBefore);
            Assert.Equal(1, commands.FramebufferBinds);
        }

        [Fact]
        public void TheQuadBufferGrowsOnlyAndRetiresTheBufferItReplaces()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
            var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs, retired);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings();
            var eye = new Vector3(0f, 9f, 6f);

            WaterTestFrames.Draw(renderer, commands, resources, RiverPlanes(3), settings, eye);
            var first = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            WaterTestFrames.Draw(renderer, commands, resources, RiverPlanes(9), settings, eye);
            var grown = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            Assert.NotSame(first, grown);
            Assert.True(grown.SizeInBytes >= 9u * 48u, $"the grown quad buffer holds only {grown.SizeInBytes} bytes");
            Assert.False(first.Disposed, "the replaced quad buffer was freed at the grow, while a prior frame may still read it");

            WaterTestFrames.Draw(renderer, commands, resources, RiverPlanes(2), settings, eye);
            Assert.Same(grown, commands.IndexedDraws[0].VertexBuffer);   // shrinking reallocates nothing

            renderer.Dispose();
            retired.BeginFrame();
            Assert.True(first.Disposed, "the safe retirement boundary must free the replaced quad buffer");
        }

        static WaterPlane[] RiverPlanes(int count)
        {
            var planes = new WaterPlane[count];
            for (int i = 0; i < count; i++)
                planes[i] = new WaterPlane(-68f + 4f * i, 0f, 0f, 1.5f, look: TileWaterLooks.River);
            return planes;
        }
    }

    public sealed class WaterFlatPlaneGoldenTests
    {
        const int Width = 320;
        const int Height = 240;

        // Positive, so the plane takes the tessellated grid, and far too small to move a vertex or tilt a normal by
        // a representable amount. Every component amplitude sits under the 1e-6 guard that zeroes the horizontal
        // pinch, and the vertical offset and the slope round away. That makes it the tessellated flat reference now
        // that a switched-off swell never reaches the grid.
        const float VanishingSwell = 1e-30f;

        [GpuFact]
        public void FlatQuadsMatchTheTessellatedFlatReferenceInEveryGridMode()
        {
            float[] reference = Capture(WaterGridMode.CameraFocused, VanishingSwell, 42f);
            AssertWithinFlatBound(reference, Capture(WaterGridMode.CameraFocused, 0f, 42f), "camera-focused, zero amplitude");
            AssertWithinFlatBound(reference, Capture(WaterGridMode.Clipmap, 0f, 42f), "clipmap, zero amplitude");
            AssertWithinFlatBound(reference, Capture(WaterGridMode.CameraFocused, -0.5f, 42f), "camera-focused, negative amplitude");
            AssertWithinFlatBound(reference, Capture(WaterGridMode.Clipmap, 0.45f, -10f), "clipmap, negative wavelength");
        }

        static void AssertWithinFlatBound(float[] reference, float[] quad, string what)
        {
            double sum = 0;
            float worst = 0f;
            float peak = 0f;
            for (int i = 0; i < quad.Length; i++)
            {
                float delta = MathF.Abs(quad[i] - reference[i]);
                sum += delta;
                worst = MathF.Max(worst, delta);
                peak = MathF.Max(peak, quad[i]);
            }

            float mean = (float)(sum / quad.Length);
            Assert.True(peak > 0.2f, $"{what}: the optimized frame is blank or too dark, with a peak channel of {peak}");
            Assert.True(mean < 0.002f && worst < 0.02f,
                $"{what}: the six-index quad differs from the tessellated flat reference by mean {mean} and worst {worst}");
        }

        static float[] Capture(WaterGridMode mode, float amplitude, float wavelength)
        {
            MeshHandle ground = default;
            byte[] rgba = Render3DSnapshot.Capture(Width, Height,
                setup: scene =>
                {
                    ground = scene.LoadMesh(MeshPrimitives.Tile(120f, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Water.WaveSource = WaterWaveSource.Procedural;
                    scene.Post.Water.SwellAmplitude = amplitude;
                    scene.Post.Water.SwellWavelength = wavelength;
                    scene.Post.Water.GridMode = mode;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(34f, 24f, 34f));
                    scene.EffectTimeSeconds = 2f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(ground, Matrix4x4.CreateTranslation(0f, -8f, 0f),
                        new Color(0.16f, 0.18f, 0.14f, 1f));
                    scene.DrawWater(new WaterPlane(0f, 0f, 0f, 50f));
                },
                frames: 2);
            return GoldenCompare.Downsample(rgba, Width, Height);
        }
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~KhaozEngine.Tests.Render3D.WaterFlatPlaneTests"
```
Expected: the build succeeds and 8 tests fail, all on assertions.
- Four `EverySwellTheGerstnerGateSwitchesOffDrawsAsAFlatQuad` rows fail with `Assert.Equal() Failure  Expected: True  Actual: False`: (-0.5, 42), (0.45, 0), (0.45, -10) and (-0.5, -10).
- `CameraFocusedFrameDrawsEveryFlatPlane...` fails with expected `6` and actual `55296`.
- `ASteadyRiverOfThirtyFivePlanes...` fails with expected `2` and actual `36`.
- `TheQuadBufferGrowsOnly...` fails on `Assert.NotSame()`.
- `ClipmapFrameUsesSixIndices...` fails on `FramebufferBindsBefore` with expected `0` and actual `1`.

Then record the golden baselines on the unchanged code:
```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~Golden3D_TileWorld_River|FullyQualifiedName~Golden3D_SkyTwoDiscs|FullyQualifiedName~WaterFlatPlaneGoldenTests"
tail -n 2 KhaozEngine.Render.Tests/Gpu/goldens-evidence/golden-deltas.metal-native.txt
```
Expected: all three pass, with zero skipped. `FlatQuadsMatch...` is an equivalence guard and holds before and after the change. The two `tileworld_river worst=` and `scene3d_sky_two_discs worst=` lines are the "before" numbers for the rebake commit.

- [ ] **Step 3: Implement**

`KhaozEngine.Render3D/Internal/WaterSwellReach.cs`:
```csharp
namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// How far the procedural Gerstner swell can move a still-water point, and whether it moves one at all. Pure, so
    /// the water renderer's flat-quad routing is headless-testable against <see cref="GerstnerWaves"/>, the CPU
    /// mirror of the swell both water stages evaluate.
    /// </summary>
    internal static class WaterSwellReach
    {
        /// <summary>
        /// Whether a swell displaces the surface at all. The exact complement of the gate at the top of
        /// <c>gerstnerEvaluate</c> (ShaderSources.WaterSwell.cs), which returns a zero offset unless the amplitude
        /// AND the wavelength are positive, and of the same gate in the fragment's swell attenuation. So a zero or
        /// negative amplitude and a zero or negative wavelength all leave the surface flat, which a test on a zero
        /// amplitude alone misses.
        /// </summary>
        public static bool Displaces(float amplitude, float wavelength) => amplitude > 0f && wavelength > 0f;
    }
}
```

`KhaozEngine.Render3D/Rendering/WaterRenderer.FlatPlane.cs` (whole file):
```csharp
using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// EVERY PLANE WITH NO VERTEX DISPLACEMENT, IN EITHER GRID MODE, IS ONE QUAD OF ONE SHARED VERTEX BUFFER. The
    /// frame writes all of its quads into a CPU mirror and uploads them in one write before the pass opens, so no
    /// quad costs an upload inside the pass. Each draw names its own quad by vertex offset over the one six-index
    /// buffer. The quad matches the zero-swell grid mathematically, since every varying the vertex stage writes is
    /// affine or constant there, but not byte for byte, and WaterFlatPlaneGoldenTests holds the difference.
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        internal const uint FlatIndexCount = 6;

        /// <summary>Vertices in one quad.</summary>
        internal const int FlatQuadVertices = 4;

        /// <summary>Bytes in one quad: four positions of 12 bytes.</summary>
        internal const uint FlatQuadBytes = (uint)FlatQuadVertices * 12u;

        IGpuBuffer? _flatVb;
        IGpuBuffer? _flatIb;
        int _flatCapacity;   // quads the vertex buffer holds
        Vector3[] _flatVertices = Array.Empty<Vector3>();

        /// <summary>
        /// Whether a plane draws as a flat quad: its effective source is procedural and its effective swell does not
        /// displace (<see cref="WaterSwellReach.Displaces"/>). FFT planes keep their grid even when the procedural
        /// swell knobs are off because their displacement comes from the ocean maps.
        /// </summary>
        internal static bool UsesFlatQuad(in WaterPlane plane, WaterSettings settings)
            => EffectiveWaveSource(plane, settings) == WaterWaveSource.Procedural
                && !WaterSwellReach.Displaces(plane.Look?.SwellAmplitude ?? settings.SwellAmplitude,
                    plane.Look?.SwellWavelength ?? settings.SwellWavelength);

        /// <summary>Grow the quad buffer to hold <paramref name="quads"/>, geometrically. The replaced buffer is
        /// RETIRED rather than disposed because a submitted frame may still read it, the rule
        /// <see cref="EnsureUboCapacity"/> follows. The index buffer never changes after its first write.</summary>
        void EnsureFlatBuffers(int quads)
        {
            if (_flatIb is null)
            {
                _flatIb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                    FlatIndexCount * sizeof(uint), GpuBufferUsage.IndexBuffer));
                _gd.UpdateBuffer<uint>(_flatIb, 0, [0, 2, 1, 1, 2, 3]);
            }
            if (_flatVb is not null && _flatCapacity >= quads) return;
            _flatCapacity = Math.Max(quads, _flatCapacity == 0 ? 4 : _flatCapacity * 2);
            if (_flatVb is not null) _retired.Retire(_flatVb);
            _flatVb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)_flatCapacity * FlatQuadBytes, GpuBufferUsage.VertexBuffer));
            _flatVertices = new Vector3[_flatCapacity * FlatQuadVertices];
        }

        /// <summary>Write every quad the frame routed into the mirror, in slot order, and upload them in one write.
        /// Runs before the pass's <c>SetFramebuffer</c>.</summary>
        void UploadFlatQuads(IGpuCommandList commands, ReadOnlySpan<WaterPlane> planes)
        {
            if (_flatCount == 0) return;
            for (int i = 0; i < planes.Length; i++)
            {
                if (_routes[i] != PlaneRoute.FlatQuad) continue;
                WaterPlane plane = planes[i];
                float minX = plane.CenterX - plane.HalfExtentX;
                float maxX = plane.CenterX + plane.HalfExtentX;
                float minZ = plane.CenterZ - plane.HalfExtentZ;
                float maxZ = plane.CenterZ + plane.HalfExtentZ;
                int v = _routeSlots[i] * FlatQuadVertices;
                _flatVertices[v] = new Vector3(minX, plane.SurfaceY, minZ);
                _flatVertices[v + 1] = new Vector3(maxX, plane.SurfaceY, minZ);
                _flatVertices[v + 2] = new Vector3(minX, plane.SurfaceY, maxZ);
                _flatVertices[v + 3] = new Vector3(maxX, plane.SurfaceY, maxZ);
            }
            commands.UpdateBuffer<Vector3>(_flatVb!, 0, _flatVertices.AsSpan(0, _flatCount * FlatQuadVertices));
        }

        void DisposeFlatBuffers()
        {
            _flatVb?.Dispose();
            _flatIb?.Dispose();
        }
    }
}
```
(`AnyFlatPlane` and `AnyDisplacedPlane` are deleted. The routing replaces them.)

`KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE FRAME'S PLANE ROUTING: which geometry each queued plane draws through, decided once before any geometry
    /// is built or uploaded, so the uploads can land before the pass's <c>SetFramebuffer</c>. A plane keeps its
    /// queue index for its uniform slot and, under the clipmap, for its cached slice. Flat quads and camera-focused
    /// grids take compact slots in their own shared buffers. Draws stay in queue order because the pass is alpha
    /// blended and overlapping planes composite in the order they were queued.
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        /// <summary>The geometry one queued plane draws through this frame.</summary>
        internal enum PlaneRoute : byte
        {
            /// <summary>Not drawn this frame.</summary>
            Culled,
            /// <summary>No displacement: one quad of the shared flat buffer.</summary>
            FlatQuad,
            /// <summary>The camera-focused grid.</summary>
            FocusedGrid,
            /// <summary>The world-locked clipmap, through the plane's own slice of the clip buffers.</summary>
            Clipmap,
        }

        PlaneRoute[] _routes = Array.Empty<PlaneRoute>();
        int[] _routeSlots = Array.Empty<int>();
        int _flatCount, _gridCount, _clipCount;

        /// <summary>How the last <see cref="Draw"/> routed queued plane <paramref name="index"/>. Internal, for the
        /// tests that pin the routing without reading pixels.</summary>
        internal PlaneRoute LastRoute(int index) => _routes[index];

        /// <summary>Route every queued plane and count each route. Returns how many planes will draw.</summary>
        int RoutePlanes(ReadOnlySpan<WaterPlane> planes, WaterSettings settings)
        {
            if (_routes.Length < planes.Length)
            {
                int capacity = Math.Max(planes.Length, _routes.Length * 2);
                _routes = new PlaneRoute[capacity];
                _routeSlots = new int[capacity];
            }
            bool clipmap = settings.GridMode == WaterGridMode.Clipmap;
            _flatCount = _gridCount = _clipCount = 0;
            for (int i = 0; i < planes.Length; i++)
            {
                if (UsesFlatQuad(planes[i], settings))
                {
                    _routes[i] = PlaneRoute.FlatQuad;
                    _routeSlots[i] = _flatCount++;
                }
                else if (clipmap)
                {
                    // The clipmap's slices and their cache are keyed by queue index, so the slot is the index.
                    _routes[i] = PlaneRoute.Clipmap;
                    _routeSlots[i] = i;
                    _clipCount++;
                }
                else
                {
                    _routes[i] = PlaneRoute.FocusedGrid;
                    _routeSlots[i] = _gridCount++;
                }
            }
            return _flatCount + _gridCount + _clipCount;
        }

        /// <summary>Bind what a route draws through. Called only when the route differs from the previous draw's,
        /// so a run of flat quads binds its buffers once.</summary>
        void BindRoute(IGpuCommandList cl, PlaneRoute route)
        {
            switch (route)
            {
                case PlaneRoute.FlatQuad:
                    cl.SetPipeline(_pipe);
                    cl.SetIndexBuffer(_flatIb!, GpuIndexFormat.UInt32);
                    cl.SetVertexBuffer(0, _flatVb!);
                    break;
                case PlaneRoute.FocusedGrid:
                    cl.SetPipeline(_pipe);
                    cl.SetIndexBuffer(_ib!, GpuIndexFormat.UInt32);
                    cl.SetVertexBuffer(0, _vb!);
                    break;
                case PlaneRoute.Clipmap:
                    cl.SetPipeline(_clipPipe!);
                    cl.SetIndexBuffer(_clipIb!, GpuIndexFormat.UInt32);
                    cl.SetVertexBuffer(0, _clipVb!);
                    break;
            }
        }

        /// <summary>One draw per routed plane, in queue order.</summary>
        void DrawRoutedPlanes(IGpuCommandList cl, ReadOnlySpan<WaterPlane> planes, Vector3 cameraPos, float focusBias)
        {
            PlaneRoute bound = PlaneRoute.Culled;
            for (int i = 0; i < planes.Length; i++)
            {
                PlaneRoute route = _routes[i];
                if (route == PlaneRoute.Culled) continue;
                if (route != bound)
                {
                    BindRoute(cl, route);
                    bound = route;
                }
                cl.SetGraphicsResourceSet(0, _set!, (uint)i * SlotBytes);
                switch (route)
                {
                    case PlaneRoute.FlatQuad:
                        cl.DrawIndexed(FlatIndexCount, 1, 0, _routeSlots[i] * FlatQuadVertices, 0);
                        break;
                    case PlaneRoute.Clipmap:
                        // Each plane reads its own slice: indexStart walks the index buffer, vertexOffset rebases
                        // the plane-local indices onto its own vertex block.
                        cl.DrawIndexed((uint)_clipSlots[i].IndexCount, 1,
                            (uint)(i * _clipSliceIndices), i * _clipSliceVerts, 0);
                        break;
                    default:
                        // Still built and uploaded inside the pass. Task C2 moves it ahead of SetFramebuffer.
                        int n = WaterMath.BuildGridPositions(planes[i], cameraPos.X, cameraPos.Z, focusBias,
                            _gridScratch, _axisScratch);
                        cl.UpdateBuffer<Vector3>(_vb!, 0, _gridScratch.AsSpan(0, n));
                        cl.SetVertexBuffer(0, _vb!);
                        cl.DrawIndexed((uint)WaterMath.GridIndexCount, 1, 0, 0, 0);
                        break;
                }
            }
        }
    }
}
```

`WaterRenderer.cs`. Replace the last `<para>` of the Draw doc (lines 574-579) with:
```csharp
        /// <para>
        /// <paramref name="settings"/> is the SCENE-wide look, and a plane carrying a <see cref="WaterLook"/>
        /// resolves its own copy of it for the UBO slot only. Everything outside that slot keeps reading the scene
        /// object on purpose: the grid mode and the <c>Clipmap*</c> group select the displaced geometry, the sea
        /// state drives one bake and the bathymetry one texture. A procedural plane whose effective swell does not
        /// displace draws one quad of the shared flat buffer in either grid mode, because its vertex stage has no
        /// geometry to displace (WaterRenderer.FlatPlane.cs).
        /// </para></summary>
```
Replace lines 584-599 with:
```csharp
            if (planes.Length == 0) return;
            LastClipmapRebuilds = 0;
            EnsureUboCapacity(planes.Length);
            RoutePlanes(planes, settings);
            if (_clipCount > 0)
            {
                EnsureClipPipeline();
                EnsureClipBuffers(planes, settings, renderOrigin);
            }
            if (_flatCount > 0) EnsureFlatBuffers(_flatCount);
            if (_gridCount > 0) EnsureGridBuffers();
```
Keep lines 601-642 (the ocean block, `BindTargets`, the pack loop and `UploadSlots`). Replace lines 644-683 with:
```csharp
            // Flat quads and clipmap slices upload HERE, before a single draw is recorded, so no plane's geometry
            // can be written over another's mid-pass.
            UploadFlatQuads(cl, planes);
            for (int i = 0; i < planes.Length; i++)
                if (_routes[i] == PlaneRoute.Clipmap)
                    RefreshClipmapPlane(cl, i, planes[i], cameraPos, settings, renderOrigin);

            cl.SetFramebuffer(res.ColorDepthFB);
            DrawRoutedPlanes(cl, planes, cameraPos, settings.GridFocusBias);
```

`KhaozEngine.Render3D/README.md`. At lines 602-604, replace "The first effective procedural zero-swell plane in clipmap mode lazily creates one shared four-vertex and six-index buffer pair, then every such plane reuses it." with:

> Every procedural plane whose effective swell does not displace (an amplitude or a wavelength that is zero or negative) draws one quad of a shared, grow-only buffer of 48 bytes per plane over one six-index buffer, in either grid mode, and the frame uploads every such quad in one write before the water pass opens.

At lines 634-636, replace "A plane whose effective look is procedural with `SwellAmplitude = 0` ... keep the clipmap." with:

> A plane whose effective look is procedural with a `SwellAmplitude` or `SwellWavelength` of zero or less has no vertex displacement, so it skips the lattice, as it skips the camera-focused grid, and draws one six-index quad through the regular water pipeline. FFT planes and procedural planes with any swell keep their grid.

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Water|FullyQualifiedName~GrownBufferRetirementTests|FullyQualifiedName~FrameUniformUploadShapeGpuTests|FullyQualifiedName~UboLayoutTests"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~WaterFlatPlaneGoldenTests|FullyQualifiedName~Golden3D_TileWorld_River|FullyQualifiedName~Golden3D_SkyTwoDiscs" \
  --logger "console;verbosity=normal"
tail -n 2 KhaozEngine.Render.Tests/Gpu/goldens-evidence/golden-deltas.metal-native.txt
```
Expected:
- All headless tests pass.
- `FlatQuadsMatch...` passes.
- The River grid, and possibly the two-disc grid, reports a new worst delta. Either may fail its 0.01 compare, which is what the rebake in Step 5 addresses.
- Zero skipped GPU tests.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Internal/WaterSwellReach.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.FlatPlane.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.cs \
  KhaozEngine.Render3D/README.md \
  KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.cs \
  KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.Draws.cs \
  KhaozEngine.Render.Tests/Render3D/WaterTestFrames.cs \
  KhaozEngine.Render.Tests/Render3D/WaterFlatPlaneTests.cs
git commit -m "render3d(water): draw every non-displacing plane as a pre-pass flat quad"
```
Then rebake on the three owning legs. This part is an ORCHESTRATOR step: the implementer stops after the commit above and reports, and the orchestrating session pushes the item branch and runs the bake, because implementers never push.
```bash
git push -u origin feature/frame-cost-water
gpu_branch=feature/frame-cost-water
gh workflow run cross-platform-gpu.yml --ref "$gpu_branch" -f bake=true -f legs=all -f tier=push \
  -f renderTestFilter='FullyQualifiedName~Golden3D_TileWorld_River|FullyQualifiedName~Golden3D_SkyTwoDiscs'
gpu_run_id=$(gh run list --workflow cross-platform-gpu.yml --branch "$gpu_branch" \
  --event workflow_dispatch --limit 1 --json databaseId --jq '.[0].databaseId')
gh run watch "$gpu_run_id" --exit-status
gpu_artifact_dir=$(mktemp -d)
gh run download "$gpu_run_id" --pattern 'goldens-*' --dir "$gpu_artifact_dir"
find "$gpu_artifact_dir" \( -name 'tileworld_river.*.txt' -o -name 'scene3d_sky_two_discs.*.txt' \) \
  -exec cp {} KhaozEngine.Render.Tests/Gpu/goldens/ \;
git diff --stat -- KhaozEngine.Render.Tests/Gpu/goldens/
for f in metal-native direct3d11-native vulkan-native; do
  g=KhaozEngine.Render.Tests/Gpu/goldens/tileworld_river.$f.txt
  paste <(git show HEAD:$g | tail -n +2) <(tail -n +2 $g) | awk -v f=$f \
    '{for(i=1;i<=3;i++){d=$i-$(i+3); if(d<0)d=-d; s+=d; if(d>w)w=d; n++}} END {printf "%s mean %.5f worst %.5f\n", f, s/n, w}'
done
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~Golden3D_TileWorld_River|FullyQualifiedName~Golden3D_SkyTwoDiscs"
```
Expected:
- The workflow exits 0.
- Every River family reports mean under 0.002 and worst under 0.02, the `WaterFlatPlaneTests` bound.
- The bake PNGs in the artifacts show the river unchanged to the eye.
- The local verify passes.

Stage the two-disc files only if `git diff` lists them.
```bash
git add KhaozEngine.Render.Tests/Gpu/goldens/tileworld_river.metal-native.txt \
  KhaozEngine.Render.Tests/Gpu/goldens/tileworld_river.direct3d11-native.txt \
  KhaozEngine.Render.Tests/Gpu/goldens/tileworld_river.vulkan-native.txt
git commit -m "render3d(water): rebake the river golden on the flat quad" \
  -m "Cause: zero-swell procedural planes now draw one quad instead of the 97 by 97 grid (#1113). Worst cell per family before and after, as printed by golden-deltas and the awk loop above, written into this message when the bake returns."
```

---

### Task C2: Camera-focused grid slices uploaded before the pass

**Branch:** `feature/frame-cost-water`

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/WaterRenderer.FocusedGrid.cs`
- Modify: `KhaozEngine.Render3D/Rendering/WaterRenderer.cs`:
  - delete the grid fields at `:134-141` and `EnsureGridBuffers()` at `:317-327`
  - edit the Draw head and upload tail from C1
- Modify: `KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs` (the `DrawRoutedPlanes` grid case and signature)
- Modify: `KhaozEngine.Render3D/README.md:624-625`
- Create test: `KhaozEngine.Render.Tests/Render3D/WaterFocusedGridTests.cs`

**Interfaces:**
- Produces:
  - `internal const int WaterRenderer.GridSliceVertices = 9409`
  - `internal const uint WaterRenderer.GridSliceBytes = 112908`
  - `internal int WaterRenderer.LastFocusedGridBuilds`
  - `void EnsureGridBuffers(int slices)`
  - `void UploadFocusedGrids(IGpuCommandList, ReadOnlySpan<WaterPlane>, Vector3, float)`
  - `void DrawRoutedPlanes(IGpuCommandList, int)`
- Consumes: `WaterMath.BuildGridPositions`, `WaterMath.BuildGridIndices` and `WaterMath.GridIndexCount` (`WaterMath.cs:491, 521, 526`). None of these is edited, which avoids the glint-foam branch.

- [ ] **Step 1: Write the failing test**
```csharp
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterFocusedGridTests
    {
        const int GridVertices = WaterMath.GridResolution * WaterMath.GridResolution;
        const uint GridBytes = GridVertices * 12u;

        [Fact]
        public void EachDisplacedPlaneGetsItsOwnGridSliceUploadedAheadOfThePass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            WaterPlane[] planes =
            [
                new WaterPlane(-30f, 0f, 0f, 10f),
                new WaterPlane(0f, 0f, 0f, 8f, look: new WaterLook { SwellAmplitude = 0f }),
                new WaterPlane(0f, 2f, 30f, 12f, 6f),
                new WaterPlane(30f, -1f, -30f, 9f),
            ];
            var eye = new Vector3(4f, 12f, -6f);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye);

            IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws = commands.IndexedDraws;
            Assert.Equal(4, draws.Count);
            Assert.Equal(6u, draws[1].IndexCount);   // the flat plane between them keeps its quad
            int[] grids = [0, 2, 3];
            IGpuBuffer slices = draws[0].VertexBuffer!;
            for (int k = 0; k < grids.Length; k++)
            {
                RecordingGpuCommandList.IndexedDraw draw = draws[grids[k]];
                Assert.Equal((uint)WaterMath.GridIndexCount, draw.IndexCount);
                Assert.Equal(0u, draw.IndexStart);
                Assert.Equal(k * GridVertices, draw.VertexOffset);
                Assert.Same(slices, draw.VertexBuffer);
            }

            List<RecordingGpuCommandList.Upload> uploads = WaterTestFrames.UploadsTo(commands, slices);
            Assert.Equal(3, uploads.Count);
            var expected = new Vector3[GridVertices];
            var axis = new float[2 * WaterMath.GridResolution];
            for (int k = 0; k < grids.Length; k++)
            {
                RecordingGpuCommandList.Upload upload = uploads[k];
                Assert.Equal((uint)k * GridBytes, upload.Offset);
                Assert.Equal(GridBytes, upload.Bytes);
                Assert.Equal(0, upload.FramebufferBindsBefore);
                WaterMath.BuildGridPositions(planes[grids[k]], eye.X, eye.Z, settings.GridFocusBias, expected, axis);
                Assert.True(MemoryMarshal.Cast<byte, Vector3>(upload.Data!.AsSpan()).SequenceEqual(expected),
                    $"slice {k} does not hold plane {grids[k]}'s grid");
            }
            Assert.Equal(1, commands.FramebufferBinds);
        }

        [Fact]
        public void TheGridBufferGrowsOnlyAndRetiresTheBufferItReplaces()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
            var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs, retired);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            var eye = new Vector3(0f, 12f, -6f);

            WaterTestFrames.Draw(renderer, commands, resources, Displaced(1), settings, eye);
            var first = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            Assert.Equal(GridBytes, first.SizeInBytes);   // one plane costs exactly what the old fixed buffer did

            WaterTestFrames.Draw(renderer, commands, resources, Displaced(3), settings, eye);
            var grown = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            Assert.NotSame(first, grown);
            Assert.True(grown.SizeInBytes >= 3u * GridBytes, $"the grown grid buffer holds only {grown.SizeInBytes} bytes");
            Assert.False(first.Disposed, "the replaced grid buffer was freed at the grow, while a prior frame may still read it");

            WaterTestFrames.Draw(renderer, commands, resources, Displaced(2), settings, eye);
            Assert.Same(grown, commands.IndexedDraws[0].VertexBuffer);

            renderer.Dispose();
            retired.BeginFrame();
            Assert.True(first.Disposed, "the safe retirement boundary must free the replaced grid buffer");
        }

        static WaterPlane[] Displaced(int count)
        {
            var planes = new WaterPlane[count];
            for (int i = 0; i < count; i++) planes[i] = new WaterPlane(-60f + 40f * i, 0f, 0f, 10f);
            return planes;
        }
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~WaterFocusedGridTests"
```
Expected: both tests fail.
- `EachDisplacedPlane...` fails with `Assert.Equal() Failure  Expected: 9409  Actual: 0` at k = 1, because every grid still draws from offset 0.
- `TheGridBufferGrowsOnly...` fails at `Assert.NotSame()`.

- [ ] **Step 3: Implement**

`KhaozEngine.Render3D/Rendering/WaterRenderer.FocusedGrid.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE CAMERA-FOCUSED GRID AS ONE SLICE PER PLANE of one shared vertex buffer, uploaded before the pass opens
    /// and drawn through the one static index buffer at the slice's vertex offset. That is the clipmap's shape
    /// (WaterRenderer.ClipmapBuffers.cs) without its cache: this grid follows the camera, so every slice is rebuilt
    /// every frame. What changes is where the upload lands. Writing every plane into offset 0 of one buffer forced
    /// each upload inside the pass, between one plane's draw and the next, and a backend that stages uploads
    /// through a blit ended and reopened the pass once per plane.
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        /// <summary>Vertices in one camera-focused grid slice.</summary>
        internal const int GridSliceVertices = WaterMath.GridResolution * WaterMath.GridResolution;

        /// <summary>Bytes in one slice: 9,409 positions of 12 bytes, the 113 KB one plane has always uploaded.</summary>
        internal const uint GridSliceBytes = (uint)GridSliceVertices * 12u;

        IGpuBuffer? _vb;
        IGpuBuffer? _ib;
        int _gridSlices;   // slices the vertex buffer holds
        // Heap-allocated once, not stackalloc'd per draw: at GridResolution 97 the position scratch is 113 KB and
        // the index scratch 216 KB, both far past what belongs on the stack. One plane's worth, reused per slice.
        readonly Vector3[] _gridScratch = new Vector3[GridSliceVertices];
        readonly float[] _axisScratch = new float[2 * WaterMath.GridResolution];

        /// <summary>Camera-focused grids built and uploaded by the last <see cref="Draw"/>. Internal, for tests.</summary>
        internal int LastFocusedGridBuilds { get; private set; }

        /// <summary>Hold at least <paramref name="slices"/> slices, growing geometrically and retiring the replaced
        /// buffer. The index buffer is plane-local and written once.</summary>
        void EnsureGridBuffers(int slices)
        {
            if (_ib is null)
            {
                const uint icount = WaterMath.GridIndexCount;
                _ib = _gd.Factory.CreateBuffer(new GpuBufferDescription(icount * sizeof(uint), GpuBufferUsage.IndexBuffer));
                uint[] indices = new uint[icount];   // built once, then thrown away: the index layout never changes
                WaterMath.BuildGridIndices(indices);
                _gd.UpdateBuffer(_ib, 0, indices);
            }
            if (_vb is not null && _gridSlices >= slices) return;
            _gridSlices = Math.Max(slices, _gridSlices * 2);
            if (_vb is not null) _retired.Retire(_vb);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)_gridSlices * GridSliceBytes, GpuBufferUsage.VertexBuffer));
        }

        /// <summary>Build every routed plane's grid into its own slice. Runs before the pass's
        /// <c>SetFramebuffer</c>, beside the flat quads and the clipmap slices.</summary>
        void UploadFocusedGrids(IGpuCommandList cl, ReadOnlySpan<WaterPlane> planes, Vector3 cameraPos, float focusBias)
        {
            for (int i = 0; i < planes.Length; i++)
            {
                if (_routes[i] != PlaneRoute.FocusedGrid) continue;
                // The grid concentrates its vertices around the camera's XZ (clamped inside the plane by
                // BuildGridPositions), so the fixed vertex budget lands where the displaced swell actually reads.
                int n = WaterMath.BuildGridPositions(planes[i], cameraPos.X, cameraPos.Z, focusBias,
                    _gridScratch, _axisScratch);
                cl.UpdateBuffer<Vector3>(_vb!, (uint)_routeSlots[i] * GridSliceBytes, _gridScratch.AsSpan(0, n));
                LastFocusedGridBuilds++;
            }
        }
    }
}
```

In `WaterRenderer.cs`:
- Delete lines 134-141 (the "Fixed-size grid buffers" comment, `_vb`, `_ib`, the scratch comment, `_gridScratch` and `_axisScratch`).
- Delete lines 317-327 (`EnsureGridBuffers()`).
- In the Draw head, add `LastFocusedGridBuilds = 0;` after `LastClipmapRebuilds = 0;`.
- Replace `if (_gridCount > 0) EnsureGridBuffers();` with `if (_gridCount > 0) EnsureGridBuffers(_gridCount);`.
- Replace the C1 tail with:
```csharp
            // Every geometry upload happens HERE, before a single draw is recorded, so no plane's geometry can be
            // written over another's mid-pass and the draw loop touches no buffer contents at all.
            UploadFlatQuads(cl, planes);
            UploadFocusedGrids(cl, planes, cameraPos, settings.GridFocusBias);
            for (int i = 0; i < planes.Length; i++)
                if (_routes[i] == PlaneRoute.Clipmap)
                    RefreshClipmapPlane(cl, i, planes[i], cameraPos, settings, renderOrigin);

            cl.SetFramebuffer(res.ColorDepthFB);
            DrawRoutedPlanes(cl, planes.Length);
```

In `WaterRenderer.Routing.cs`, change the signature to `void DrawRoutedPlanes(IGpuCommandList cl, int planeCount)`, the loop bound to `i < planeCount`, and replace the `default:` block with:
```csharp
                    case PlaneRoute.FocusedGrid:
                        cl.DrawIndexed((uint)WaterMath.GridIndexCount, 1, 0, _routeSlots[i] * GridSliceVertices, 0);
                        break;
```

In `README.md:624-625`, replace "warped toward the camera by `GridFocusBias`, unchanged." with:

> warped toward the camera by `GridFocusBias`. Each displaced plane gets its own slice of one shared, grow-only vertex buffer, and every slice uploads before the water pass opens.

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Water|FullyQualifiedName~GrownBufferRetirementTests|FullyQualifiedName~FrameUniformUploadShapeGpuTests"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Water|FullyQualifiedName~GoldenSnapshotTests|FullyQualifiedName~GoldenTileWorldTests|FullyQualifiedName~SkyWorldHorizonGoldenTests|FullyQualifiedName~HdrShowcaseGpuTests|FullyQualifiedName~UniformRewriteGuardGpuTests"
```
Expected:
- Everything passes with zero skipped.
- `scene3d_water`, `scene3d_water_grid_focus`, the swell facet tests and `PerPlaneWaterLookGpuTests` (several displaced planes, so non-zero vertex offsets on the GPU) keep their grids unchanged.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Rendering/WaterRenderer.FocusedGrid.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs \
  KhaozEngine.Render3D/README.md \
  KhaozEngine.Render.Tests/Render3D/WaterFocusedGridTests.cs
git commit -m "render3d(water): upload camera-focused grids as slices before the pass"
```

---

### Task C3: Frustum culling of water planes inside `WaterRenderer`

**Branch:** `feature/frame-cost-water`

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/WaterSwellReach.cs` (add `Of` and `MayBeVisible`)
- Modify: `KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs` (`RoutePlanes`, `MayBeVisible`, `LastCulledPlanes`)
- Modify: `KhaozEngine.Render3D/Rendering/WaterRenderer.cs`:
  - `:17-20`: class summary, "one draw per queued plane"
  - Draw head and pack loop
- Modify: `KhaozEngine.Render3D/README.md` (the grid bullet, after the sentence C1 wrote at `:634-636`)
- Create test: `KhaozEngine.Render.Tests/Render3D/WaterPlaneCullingTests.cs`

**Interfaces:**
- Consumes:
  - `FrustumPlanes.Extract(Matrix4x4)` at `Culling/FrustumPlanes.cs:52`
  - `FrustumPlanes.IntersectsAabb(Vector3, Vector3)` at `:76`
- Produces:
  - `internal static Vector2 WaterSwellReach.Of(float amplitude, float wavelength, float steepness)`
  - `internal static bool WaterSwellReach.MayBeVisible(in WaterPlane plane, Vector2 reach, in FrustumPlanes frustum)`
  - `internal int WaterRenderer.LastCulledPlanes`
  - `int RoutePlanes(ReadOnlySpan<WaterPlane>, WaterSettings, in FrustumPlanes)`

- [ ] **Step 1: Write the failing test**
```csharp
using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterPlaneCullingTests
    {
        const int GridVertices = WaterMath.GridResolution * WaterMath.GridResolution;

        static readonly Matrix4x4 Perspective =
            Matrix4x4.CreateLookAt(new Vector3(0f, 10f, 30f), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(1f, 4f / 3f, 0.5f, 500f);

        [Fact]
        public void TheReachBoundsEveryOffsetTheGerstnerMirrorProduces()
        {
            Span<GerstnerWaves.Component> scratch = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            (float Amplitude, float Wavelength, float Steepness, int Components, float Spread)[] swells =
            [
                (0.45f, 42f, 0.6f, 4, 55f),
                (0.45f, 42f, 1f, 1, 0f),
                (2f, 8f, 1f, 8, 180f),
                (0.1f, 120f, 0.3f, 8, 30f),
            ];
            foreach (var swell in swells)
            {
                int n = GerstnerWaves.BuildComponents(swell.Amplitude, swell.Wavelength, 0.3f,
                    GerstnerWaves.DegreesToRadians(swell.Spread), swell.Steepness, 0.6f, 0f, swell.Components, scratch);
                Vector2 reach = WaterSwellReach.Of(swell.Amplitude, swell.Wavelength, swell.Steepness);
                for (float x = -60f; x <= 60f; x += 1.7f)
                    for (float t = 0f; t < 20f; t += 1.3f)
                    {
                        GerstnerWaves.Sample s = GerstnerWaves.Evaluate(x, x * 0.37f, t, swell.Steepness,
                            scratch.Slice(0, n));
                        float horizontal = MathF.Sqrt(s.Offset.X * s.Offset.X + s.Offset.Z * s.Offset.Z);
                        Assert.True(horizontal <= reach.X * 1.0001f + 1e-6f,
                            $"a {swell} swell moved ({x}, {t}) sideways by {horizontal}, past its reach {reach.X}");
                        Assert.True(MathF.Abs(s.Offset.Y) <= reach.Y * 1.0001f + 1e-6f,
                            $"a {swell} swell moved ({x}, {t}) vertically by {s.Offset.Y}, past its reach {reach.Y}");
                    }
            }
        }

        [Fact]
        public void ASwellThatDoesNotDisplaceHasNoReach()
        {
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(0f, 42f, 0.6f));
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(-0.5f, 42f, 0.6f));
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(0.45f, 0f, 0.6f));
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(0.45f, -10f, 0.6f));
        }

        [Theory]
        [InlineData(100f, 0f, 8f, 0f, 0f, false)]     // wholly beside the view
        [InlineData(90f, 0f, 8f, 5f, 0f, true)]       // beside the view by less than its sideways reach
        [InlineData(85f, 0f, 8f, 0f, 0f, true)]       // straddling the view's edge
        [InlineData(-85f, 0f, -8f, 0f, 0f, true)]     // straddling, with a negative half extent
        [InlineData(0f, 0f, 1000f, 0f, 0f, true)]     // far larger than the view
        [InlineData(0f, 105f, 8f, 0f, 0f, false)]     // above the camera, behind its near plane
        [InlineData(0f, 105f, 8f, 0f, 10f, true)]     // above the camera, its troughs reaching in front of it
        [InlineData(0f, -310f, 8f, 0f, 0f, false)]    // past the far plane
        [InlineData(0f, -310f, 8f, 0f, 20f, true)]    // past the far plane, its crests reaching back inside
        public void AnOrthographicCameraKeepsExactlyThePlanesItCanReach(float centerX, float surfaceY, float half,
            float reachX, float reachY, bool visible)
        {
            FrustumPlanes frustum = FrustumPlanes.Extract(WaterTestFrames.TopDown);
            Assert.Equal(visible, WaterSwellReach.MayBeVisible(new WaterPlane(centerX, surfaceY, 0f, half),
                new Vector2(reachX, reachY), frustum));
        }

        [Theory]
        [InlineData(0f, 0f, 20f, true)]          // in front of the camera
        [InlineData(0f, 60f, 8f, false)]         // behind the camera
        [InlineData(0f, 30f, 1000f, true)]       // under the camera and far larger than the view
        [InlineData(400f, -100f, 20f, false)]    // far off to the side
        public void APerspectiveCameraKeepsExactlyThePlanesItCanReach(float centerX, float centerZ, float half,
            bool visible)
        {
            FrustumPlanes frustum = FrustumPlanes.Extract(Perspective);
            Assert.Equal(visible, WaterSwellReach.MayBeVisible(new WaterPlane(centerX, 0f, centerZ, half),
                Vector2.Zero, frustum));
        }

        [Fact]
        public void CulledPlanesAreNeitherBuiltUploadedNorDrawn()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            var flat = new WaterLook { SwellAmplitude = 0f };
            WaterPlane[] planes =
            [
                new WaterPlane(200f, 0f, 0f, 8f, look: flat),   // flat, outside
                new WaterPlane(-40f, 0f, 0f, 8f, look: flat),   // flat, inside
                new WaterPlane(0f, 0f, 300f, 8f),               // displaced, outside
                new WaterPlane(40f, 0f, 0f, 8f),                // displaced, inside
                new WaterPlane(90f, 0f, 0f, 8f),                // displaced, outside by less than its 4 m pinch
                new WaterPlane(0f, 0f, -500f, 8f,
                    look: new WaterLook { WaveSource = WaterWaveSource.FftOcean }),   // ocean: never culled
            ];

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 100f, 0f));

            Assert.Equal(2, renderer.LastCulledPlanes);
            Assert.Equal(WaterRenderer.PlaneRoute.Culled, renderer.LastRoute(0));
            Assert.Equal(WaterRenderer.PlaneRoute.FlatQuad, renderer.LastRoute(1));
            Assert.Equal(WaterRenderer.PlaneRoute.Culled, renderer.LastRoute(2));
            Assert.Equal(WaterRenderer.PlaneRoute.FocusedGrid, renderer.LastRoute(3));
            Assert.Equal(WaterRenderer.PlaneRoute.FocusedGrid, renderer.LastRoute(4));
            Assert.Equal(WaterRenderer.PlaneRoute.FocusedGrid, renderer.LastRoute(5));
            Assert.Equal(4, commands.IndexedDraws.Count);
            Assert.Equal(48u, Assert.Single(
                WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[0].VertexBuffer!)).Bytes);
            Assert.Equal(3, WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[1].VertexBuffer!).Count);
            Assert.Equal(3, renderer.LastFocusedGridBuilds);
        }

        [Fact]
        public void AFrameWithEveryPlaneOutsideOpensNoPass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            WaterPlane[] planes = [new WaterPlane(300f, 0f, 0f, 8f), new WaterPlane(0f, 0f, -300f, 8f)];

            WaterTestFrames.Draw(renderer, commands, resources, planes, new WaterSettings(), new Vector3(0f, 100f, 0f));

            Assert.Equal(2, renderer.LastCulledPlanes);
            Assert.Empty(commands.IndexedDraws);
            Assert.Empty(commands.Uploads);
            Assert.Equal(0, commands.FramebufferBinds);
        }

        [Fact]
        public void AQueueThatShrinksAndGrowsKeepsEveryDrawOnItsOwnGeometry()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            var flat = new WaterLook { SwellAmplitude = 0f };
            WaterPlane[] six =
            [
                new WaterPlane(-60f, 0f, 0f, 6f, look: flat),
                new WaterPlane(-30f, 0f, 0f, 6f),
                new WaterPlane(500f, 0f, 0f, 6f, look: flat),   // culled
                new WaterPlane(0f, 0f, 0f, 6f, look: flat),
                new WaterPlane(30f, 0f, 0f, 6f),
                new WaterPlane(60f, 0f, 0f, 6f, look: flat),
            ];
            WaterPlane[] two = [six[3], six[4]];

            int[] first = Offsets(renderer, commands, resources, six, settings);
            Assert.Equal(new[] { 0, 0, 4, GridVertices, 8 }, first);
            Assert.Equal(new[] { 0, 0 }, Offsets(renderer, commands, resources, two, settings));
            Assert.Equal(48u, Assert.Single(
                WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[0].VertexBuffer!)).Bytes);
            Assert.Equal(first, Offsets(renderer, commands, resources, six, settings));
        }

        [Fact]
        public void AClipmapPlaneCulledForAFrameComesBackWithoutARebuild()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings
            {
                GridMode = WaterGridMode.Clipmap,
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 0.5f,
                ClipmapLevels = 1,
                ClipmapRingCells = 8,
            };
            WaterPlane[] planes = [new WaterPlane(-40f, 0f, 0f, 8f), new WaterPlane(40f, 0f, 0f, 8f)];
            var eye = new Vector3(0f, 100f, 0f);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye, WaterTestFrames.TopDownAt(0f));
            Assert.Equal(2, renderer.LastClipmapRebuilds);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye, WaterTestFrames.TopDownAt(60f));
            Assert.Equal(WaterRenderer.PlaneRoute.Culled, renderer.LastRoute(0));
            Assert.Single(commands.IndexedDraws);
            Assert.Equal(0, renderer.LastClipmapRebuilds);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye, WaterTestFrames.TopDownAt(0f));
            Assert.Equal(2, commands.IndexedDraws.Count);
            Assert.Equal(0, renderer.LastClipmapRebuilds);
        }

        static int[] Offsets(WaterRenderer renderer, RecordingGpuCommandList commands, RenderResources resources,
            WaterPlane[] planes, WaterSettings settings)
        {
            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 100f, 0f));
            var offsets = new int[commands.IndexedDraws.Count];
            for (int i = 0; i < offsets.Length; i++) offsets[i] = commands.IndexedDraws[i].VertexOffset;
            return offsets;
        }
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~WaterPlaneCullingTests"
```
Expected: the build fails, with `error CS0117: 'WaterSwellReach' does not contain a definition for 'Of'`, the same for `MayBeVisible`, and `error CS1061: 'WaterRenderer' does not contain a definition for 'LastCulledPlanes'`.

- [ ] **Step 3: Implement**

`WaterSwellReach.cs`: add `using System;` and `using System.Numerics;` at the top. Inside the class, add:
```csharp
        const float InverseTwoPi = 0.15915494f;

        /// <summary>
        /// The largest offset the swell can give any point, as (horizontal, vertical) metres, and zero when it does
        /// not displace. Vertical: the component amplitudes are normalized to sum to <paramref name="amplitude"/>.
        /// Horizontal: each component's orbital radius is <c>steepness / (k_i * n)</c>, which is
        /// <c>|steepness| * lambda_i / (2 pi n)</c>, and no component is longer than <paramref name="wavelength"/>,
        /// so the n radii sum to at most <c>|steepness| * wavelength / (2 pi)</c>. That reach does not shrink with
        /// the amplitude, which is why a cull grown by the amplitude alone would drop crests the pinch carries into view.
        /// </summary>
        public static Vector2 Of(float amplitude, float wavelength, float steepness)
            => Displaces(amplitude, wavelength)
                ? new Vector2(MathF.Abs(steepness) * wavelength * InverseTwoPi, amplitude)
                : Vector2.Zero;

        /// <summary>
        /// Whether any part of <paramref name="plane"/>, grown by <paramref name="reach"/>, may lie inside
        /// <paramref name="frustum"/>. Conservative: false only when the grown box is provably outside one frustum
        /// plane. The plane and the frustum must be in the same space, which for the water pass is the render frame
        /// on both sides.
        /// </summary>
        public static bool MayBeVisible(in WaterPlane plane, Vector2 reach, in FrustumPlanes frustum)
        {
            float halfX = MathF.Abs(plane.HalfExtentX) + reach.X;
            float halfZ = MathF.Abs(plane.HalfExtentZ) + reach.X;
            return frustum.IntersectsAabb(
                new Vector3(plane.CenterX - halfX, plane.SurfaceY - reach.Y, plane.CenterZ - halfZ),
                new Vector3(plane.CenterX + halfX, plane.SurfaceY + reach.Y, plane.CenterZ + halfZ));
        }
```

`WaterRenderer.Routing.cs`. Add the property, then replace `RoutePlanes` and add `MayBeVisible`:
```csharp
        /// <summary>Planes the last <see cref="Draw"/> skipped because the view could not reach them. Internal, for tests.</summary>
        internal int LastCulledPlanes { get; private set; }

        /// <summary>Route every queued plane and count each route. Returns how many planes will draw.</summary>
        int RoutePlanes(ReadOnlySpan<WaterPlane> planes, WaterSettings settings, in FrustumPlanes frustum)
        {
            if (_routes.Length < planes.Length)
            {
                int capacity = Math.Max(planes.Length, _routes.Length * 2);
                _routes = new PlaneRoute[capacity];
                _routeSlots = new int[capacity];
            }
            bool clipmap = settings.GridMode == WaterGridMode.Clipmap;
            _flatCount = _gridCount = _clipCount = 0;
            LastCulledPlanes = 0;
            for (int i = 0; i < planes.Length; i++)
            {
                if (!MayBeVisible(planes[i], settings, frustum))
                {
                    _routes[i] = PlaneRoute.Culled;
                    _routeSlots[i] = -1;
                    LastCulledPlanes++;
                }
                else if (UsesFlatQuad(planes[i], settings))
                {
                    _routes[i] = PlaneRoute.FlatQuad;
                    _routeSlots[i] = _flatCount++;
                }
                else if (clipmap)
                {
                    // The clipmap's slices and their cache are keyed by queue index, so a culled plane keeps its
                    // slice and comes back without a rebuild when nothing it depends on moved.
                    _routes[i] = PlaneRoute.Clipmap;
                    _routeSlots[i] = i;
                    _clipCount++;
                }
                else
                {
                    _routes[i] = PlaneRoute.FocusedGrid;
                    _routeSlots[i] = _gridCount++;
                }
            }
            return _flatCount + _gridCount + _clipCount;
        }

        /// <summary>
        /// Whether a plane may reach the view: its rectangle grown by how far its surface can move. A procedural
        /// plane's reach is its effective swell's (<see cref="WaterSwellReach.Of"/>). An ocean plane is never culled,
        /// because its displacement comes from the cascade maps and the CPU holds no bound on it.
        /// </summary>
        static bool MayBeVisible(in WaterPlane plane, WaterSettings settings, in FrustumPlanes frustum)
        {
            if (EffectiveWaveSource(plane, settings) != WaterWaveSource.Procedural) return true;
            WaterLook? look = plane.Look;
            Vector2 reach = WaterSwellReach.Of(look?.SwellAmplitude ?? settings.SwellAmplitude,
                look?.SwellWavelength ?? settings.SwellWavelength, look?.SwellSteepness ?? settings.SwellSteepness);
            return WaterSwellReach.MayBeVisible(plane, reach, frustum);
        }
```

In `WaterRenderer.cs`, the Draw head becomes:
```csharp
            if (planes.Length == 0) return;
            LastClipmapRebuilds = 0;
            LastFocusedGridBuilds = 0;
            EnsureUboCapacity(planes.Length);
            // The planes arrive reduced by the render origin and so does viewProj, so both sides of the cull are
            // in the render frame, which is what FrustumPlanes asks of its caller.
            int drawn = RoutePlanes(planes, settings, FrustumPlanes.Extract(viewProj));
            if (_clipCount > 0)
            {
                EnsureClipPipeline();
                EnsureClipBuffers(planes, settings, renderOrigin);
            }
            if (_flatCount > 0) EnsureFlatBuffers(_flatCount);
            if (_gridCount > 0) EnsureGridBuffers(_gridCount);
```
Directly after `ShoreMaps shore = _bathymetry.Snapshot();` and before `BindTargets(res);`, add:
```csharp
            // Nothing the view can reach: no slots, no geometry and no pass. The ocean demand compare and its record
            // above still ran over the whole queue, because PrepareFrame planned against the whole queue.
            if (drawn == 0) return;
```
Make the first statement of the pack loop body:
```csharp
                if (_routes[i] == PlaneRoute.Culled) continue;   // its slot is never bound this frame
```
In the class summary (`:17-20`), change "One draw per queued plane" to "One draw per queued plane the view can reach".

In `README.md`, after the sentence C1 wrote at `:634-636`, add:

> Before any geometry is built, a procedural plane whose rectangle, grown by how far its swell can move the surface, lies wholly outside the view frustum is skipped. FFT planes are never culled.

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Water|FullyQualifiedName~GrownBufferRetirementTests|FullyQualifiedName~FrameUniformUploadShapeGpuTests|FullyQualifiedName~FrustumPlanes"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Water|FullyQualifiedName~Golden|FullyQualifiedName~Scene3DPassTimingsGpuTests"
```
Expected:
- Everything passes with zero skipped.
- `TwoPlanesShareAFrameWithoutDefeatingEachOthersCache` still sees 2 rebuilds, because its off-screen small plane is FFT.
- `GrownBufferRetirementTests.TheWaterSlotUbo...` passes unchanged, because the uniform buffer capacity stays keyed on the full count.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render3D/Internal/WaterSwellReach.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.Routing.cs \
  KhaozEngine.Render3D/Rendering/WaterRenderer.cs \
  KhaozEngine.Render3D/README.md \
  KhaozEngine.Render.Tests/Render3D/WaterPlaneCullingTests.cs
git commit -m "render3d(water): skip planes the view frustum cannot reach"
```

---

### Task C4: Allocation test for a steady 35-plane water frame

**Branch:** `feature/frame-cost-water`

**Files:**
- Create test: `KhaozEngine.Render.Tests/Render3D/WaterSteadyFrameAllocationTests.cs`

**Interfaces:**
- Consumes:
  - `AllocAssert.NoPerCallAllocation(string, Action)` from `KhaozEngine.Render.Tests/AllocAssert.cs`
  - `[Collection("AllocSensitive")]` from `AllocSensitiveCollection.cs`
  - `WaterTestFrames.Draw`
  - `WaterRenderer.LastCulledPlanes`, `LastFocusedGridBuilds`, `LastClipmapRebuilds`
- Produces: none.

- [ ] **Step 1: Write the failing test**
```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// A STEADY WATER FRAME OF A GRIMHOLLOW-SIZED QUEUE ALLOCATES NOTHING: 29 river planes on the flat quad, four
    /// displaced planes on the grid of the mode under test and two planes outside the view. Every buffer the routing
    /// grows is grow-only, so once the first frames have sized them nothing is left to allocate. Measured over the
    /// fake device, which allocates nothing of its own. Metal staging allocations are frame-cost item 5's and are
    /// not visible here.
    /// </summary>
    [Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
    public sealed class WaterSteadyFrameAllocationTests
    {
        [Theory]
        [InlineData(WaterGridMode.CameraFocused)]
        [InlineData(WaterGridMode.Clipmap)]
        public void ASteadyFrameOfThirtyFivePlanesAllocatesNothing(WaterGridMode mode)
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new NullGpuCommandList();
            var settings = new WaterSettings
            {
                GridMode = mode,
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 0.3f,
                ClipmapLevels = 1,
                ClipmapRingCells = 8,
            };
            var planes = new WaterPlane[35];
            for (int i = 0; i < 29; i++) planes[i] = new WaterPlane(-70f + 5f * i, 0f, -20f, 2f, look: TileWaterLooks.River);
            for (int i = 0; i < 4; i++) planes[29 + i] = new WaterPlane(-30f + 20f * i, 0f, 30f, 8f);
            planes[33] = new WaterPlane(400f, 0f, 0f, 8f, look: TileWaterLooks.River);
            planes[34] = new WaterPlane(0f, 0f, -400f, 8f);
            var eye = new Vector3(0f, 100f, 0f);

            void Frame() => WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye);

            for (int i = 0; i < 4; i++) Frame();   // size every grow-only buffer and warm every cached slice
            Assert.Equal(2, renderer.LastCulledPlanes);
            if (mode == WaterGridMode.Clipmap) Assert.Equal(0, renderer.LastClipmapRebuilds);
            else Assert.Equal(4, renderer.LastFocusedGridBuilds);

            AllocAssert.NoPerCallAllocation($"20 steady 35-plane {mode} water frames", () =>
            {
                for (int i = 0; i < 20; i++) Frame();
            });
        }
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~WaterSteadyFrameAllocationTests"
```
Expected: both cases PASS. This test is a regression guard, not a TDD red. On the fake device the old per-plane path allocated nothing either, because the measured 9.6 KiB per frame lived in Metal staging, which is item 5. The test holds C1 to C3's new grow-only arrays (`_routes`, `_routeSlots`, `_flatVertices`, the grid slices) to zero allocation in steady state. If it fails, the message names the bytes, and the fix is in the routing code, never in the test.

- [ ] **Step 3: Implement**

No product code. If Step 2 reports allocation, the usual cause is a routing array resized on every frame instead of only on growth. `RoutePlanes` must reallocate only when `_routes.Length < planes.Length`:
```csharp
            if (_routes.Length < planes.Length)
            {
                int capacity = Math.Max(planes.Length, _routes.Length * 2);
                _routes = new PlaneRoute[capacity];
                _routeSlots = new int[capacity];
            }
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Water|FullyQualifiedName~Golden" --logger "console;verbosity=normal"
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~SculptBrushOverlayGoldenGpuTests"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
```
Expected: all green with zero skipped GPU tests, and the three guards report clean.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render.Tests/Render3D/WaterSteadyFrameAllocationTests.cs
git commit -m "render3d(water): pin a steady 35-plane water frame at zero allocation"
```

---


### Decisions and risks


1. **Growing the bounds by the amplitude alone would cull visible water.** The spec says bounds are "grown by the swell amplitude". The horizontal orbital radius per component is `steepness / (k * n)`, which does not depend on the amplitude (`KhaozEngine.Render3D/Internal/GerstnerWaves.cs:164`, `KhaozEngine.Render3D/Internal/ShaderSources.WaterSwell.cs:63, 90-92`). At the defaults the pinch reaches about 2.5 m, and the plan's bound is `abs(steepness) * wavelength / (2 pi)`, about 4 m. The plan grows XZ by that bound and Y by the amplitude.
2. **FFT planes have no swell amplitude.** Their displacement is `oceanDisp` from the maps (`ShaderSources.Water.cs:185-186`), and the CPU has no cheap bound on it, so the plan never culls them. This also keeps `WaterClipmapAcceptanceTests.cs:392-406` valid: its small FFT plane is off-screen, and the test expects 2 rebuilds.
3. **A second committed golden changes path.** `Golden3D_SkyTwoDiscs_BothShowAndBothReflectInWater` (`KhaozEngine.Render.Tests/Gpu/SkyWorldHorizonGoldenTests.cs:100, 141, 172`, via `WaterSceneTuning.cs:97`) draws a zero-swell procedural camera-focused plane, so it also moves to the flat quad. The C1 bake includes it, and its grids are committed only if they move. `SculptBrushOverlayGoldenGpuTests` (`KhaozEngine.MapEditor.Tests/MapEditor/SculptBrushOverlayGoldenGpuTests.cs:102`) compares two captures within one session, so it moves in step and keeps its verdict.
4. **Existing tests pass `Matrix4x4.Identity` as the view-projection.** In `KhaozEngine.Render.Tests/Render3D/WaterFlatPlaneTests.cs:39` the planes at x = -20 and x = 20 would be culled, so C1 moves the test to a real camera. `KhaozEngine.Render.Tests/Render3D/GrownBufferRetirementTests.cs:210` still passes, because `EnsureUboCapacity(planes.Length)` stays on the full count.
5. **NaN amplitude or wavelength.** The shader gate treats NaN as off (`ShaderSources.WaterSwell.cs:78`, `ShaderSources.Water.cs:483`). The CPU mirror does not (`GerstnerWaves.cs:143`, `amplitude <= 0f`). `WaterSwellReach.Displaces` follows the shader, so NaN routes to the flat quad. This is not GPU-tested, and Metal fast-math gives no guarantee for NaN compares anyway.
6. **The water cull ignores `Scene3D.FrustumCulling`** (`KhaozEngine.Render3D/Scene3D.cs:201`). The spec puts the cull inside `WaterRenderer`, and the renderer has no view of that toggle, so the water cull is unconditional. Scene3D also still counts one draw call per water pass (`Scene3D.cs:1970`). No public stat shows culled planes, which fits the no-API-change rule.
7. **Grid slices cost memory and staging bytes.** They are one upload per displaced plane before the pass, which is the clipmap's shape. The grid buffer is grow-only and never shrinks: 35 displaced planes hold 3.95 MB and stage 3.95 MB per frame on Metal. Grimhollow's river is flat, so it pays none of this.
8. **Coordination with the glint-foam branch.** This plan leaves `WaterMath.cs`, `GerstnerWaves.cs` and all shaders alone. `WaterMath.cs:19-21` ("one 113 KB vertex upload per plane per frame") is still accurate. `KhaozEngine.Render3D/README.md:602-604, 624-625, 634-636` may conflict textually with that branch. The vanishing-swell reference in `WaterFlatPlaneGoldenTests` depends on the gates at `ShaderSources.WaterSwell.cs:78` and `ShaderSources.Water.cs:482-483`, so rerun it after the second merge.
9. **Growth policy differs slightly from the spec's comparison.** The spec says the flat buffer grows "like `EnsureClipBuffers`", which grows to the exact size (`WaterRenderer.ClipmapBuffers.cs:44-56`). The plan doubles, like `EnsureUboCapacity` (`WaterRenderer.cs:306-315`), to bound the retire queue. Both are grow-only and retire the old buffer.


---

## Item 5: Metal encoder overhead and staging allocations (#1114)


### Where the code contradicts the spec

1. **`MetalAutoreleaseArchitectureTests` cannot pass unchanged once the sink stops opening pools.** The spec (lines 303 to 304 and 311) says the sink members "are called only from inside the package, so the architecture test keeps its rule and gains no exclusion list". That is true in the source but false in the IL walk:
   - `EntryPoints()` (`MetalAutoreleaseArchitectureTests.cs:274-288`) treats a method as called only when some IL token names it. `IlCallGraph.TryResolve` (`IlCallGraph.cs:134-148`) turns a call through `IMetalEncoderSink`, `IMetalRenderApi` or a `constrained.` call into the abstract interface member, which has no body.
   - So every `MetalEncoderSink` and `MetalRenderApi` member is a computed entry point today. So is every generic definition such as `MetalCommandList.DrawWith<TSink>`, because callers name `DrawWith<MetalEncoderSink>`, which is not equal to the definition.
   - The code says so itself at `MetalEncoderSink.cs:145-149` and `MetalRenderApi.cs:168-174`: "an entry point that reaches the interop layer opens a pool, full stop".
   - If the sink pools are removed with the walk unchanged, all 17 seam members become violations.
   - **Resolution (Task D2):** the walk gains two kinds of edge. A call through a package interface leads to every package implementation of that member, and a constructed generic call leads to its definition. The rule text, the failure message and all four positive controls stay unchanged, and there is still no exclusion list. The file does change (two call sites plus new rows), so "passes unchanged" becomes "passes with its rule and controls unchanged, over a call graph that sees through the package's own seams". **This needs owner sign-off.**
2. **`MetalCommandList` runs off macOS.** `MetalRingHarness.NewList` (`MetalRingHarness.cs:138-150`) builds real lists over fakes on the Linux and Windows legs. `ObjCAutoreleasePool.Enter` (`ObjCAutoreleasePool.cs:43-45`) calls into libobjc with no platform check. Moving the pool into list members means `Enter` and `Dispose` have to become no-ops off macOS (Task D3). The spec is silent on this.
3. **Upload pool placement.** The spec puts the pool on the list's upload member. Only the staged half of `UpdateBuffer` reaches the sink, and every uniform write takes the ring half, which has never paid for a pool. So the pool goes in `MetalBufferUpload.StageAndCopy` (`MetalBufferUpload.cs:88`), not `UpdateBufferCore` (`MetalCommandList.Uploads.cs:41`).
4. **The ObjC folder forbids static selector fields.** `ObjCRuntime.cs:31-35` and `:57-62` rule them out. The nested-class pattern from `MetalCompletionHandler.cs:374-387` answers that rule's reason, so D4 amends both remarks.
5. **A per-pass allocation outside the spec's list.** `MTLRenderPassDescriptor.Create` (`MTLRenderPassDescriptor.cs:217-218`) calls `ObjCRuntime.ClassNamed`, which builds a fresh byte array on every call (`ObjCRuntime.cs:118-121, 141-146`). That is one managed allocation every time a render pass begins, including every pass reopened after a staged upload. D4 includes the fix because it sits on the same path as the hot selectors. The owner can drop it.

### Every path that reaches `MetalEncoderSink` or `MetalRenderApi` after D3

- **Only `MetalCommandList` can reach them.** `MetalEncoderScope` and `MetalRenderPassSchedule` are constructed only in the `MetalCommandList` constructor (`MetalCommandList.cs:145-146`). `MetalBufferUpload.Record` is called only from `UpdateBufferCore` (`MetalCommandList.Uploads.cs:63`).
- **None of the other device paths reach either seam:**
  - Setup batch: `MetalSetupCommands` goes through `IMetalSetupNative`, which opens a pool in every member, and the typed `MTLBlitCommandEncoder`.
  - Present boundary: `IMetalSwapchainApi`, pooled per member.
  - Device-level `UpdateBuffer` and `UpdateTexture`: ring, `MetalBuffer.Write` or the setup batch.
  - Frame capture: `MetalFrameCapture` in `KhaozEngine.Gpu`, which pushes its own pools and has its own rows.
  - Device submit touches only `SealedCommandBuffer`, `DiscardRecording` and `MarkSubmitted`. `DiscardRecording` ends in `objc_release`, not a message send.

| Computed root | Route to a seam | Pool after D3 |
|---|---|---|
| `Begin` | `MetalEncoderScope.BeginRecording`, then `EnsureNoEncoder`, then `EndEncoding` | `Begin` |
| `End` | `EndPass`: descriptor create and release, `BeginRenderEncoder`, `EndEncoding`. Then `EnsureNoEncoder` | `End` |
| `Dispose` | `EnsureNoEncoder`, then `EndEncoding` | `Dispose` |
| `SetFramebuffer` | `MetalRenderPassSchedule.SetFramebuffer`, then `EndPass` | `SetFramebuffer` |
| `ClearColorTarget`, `ClearDepthStencil` | `EndOpenPass`, then `EndEncoding` | each |
| `Draw(uint)` | delegates to `Draw(uint,uint,uint,uint)`: `PrepareDraw` (pass open, viewport, scissor), `SetGraphicsState`, the three flushes, `Draw` | `Draw(uint,uint,uint,uint)` |
| `DrawIndexed` | the same, then `DrawIndexed` | `DrawIndexed` |
| `Dispatch` | `EnsureComputeEncoder`, compute state (its own pool is kept), `FlushComputeBinds`, `Dispatch` | `Dispatch` |
| `UpdateBuffer<T>` (both overloads) | `UpdateBufferCore`, `Record`, `StageAndCopy`, `EnsureBlitEncoder` | `StageAndCopy` |
| `CopyBuffer`, `CopyTexture`, `GenerateMipmaps` | `EnsureBlitEncoder` | each |
| `CopyTextureSubresource` (6 parameters) | delegates to the 8-parameter overload | the 8-parameter overload |
| `ResolveTexture` | `StandaloneResolvePass`: `EnsureNoEncoder`, resolve descriptor, `EnsureRenderEncoder`, release | `StandaloneResolvePass` |
| `MetalRelayEncoderSink.*`, generic flush bodies | not roots after D2 (reached through the interface and definition edges) | covered by the rows above |

---

### Task D1: Allocation-free staging

**Branch:** `feature/frame-cost-metal` (worktree created from `feature/frame-cost-round` with `git -C /Users/antonio/KhaozEngine worktree add .worktrees/frame-cost-metal -b feature/frame-cost-metal feature/frame-cost-round`, then `mkdir -p local-feed` inside it)

**Files:**
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalBufferUpload.cs:71-83` (`CopyBytesFor`)
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalStagingArena.cs:1-3` (usings), `:230-245` (`Take`), `:430-453` (`OpenBlock`)
- Create / Test: `KhaozEngine.Render.Tests/Gpu/MetalStagingAllocationTests.cs`
- Create / Test: `KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs`

**Interfaces:**
- Consumes: `MetalBufferUpload.Record(MetalUniformRing?, int, IntPtr, uint, uint, ReadOnlySpan<byte>, MetalEncoderScope, MetalStagingArena, IMetalBlitApi)`, `MetalStagingArena.Take(ulong)`, `BeginSlot(int, ulong)`, `MetalCopyAlignment.IsAligned(ulong)`, `AllocAssert.NoPerCallAllocation(string, Action)`, `MetalRingHarness.NewArena(...)`
- Produces: no new members. `OpenBlock` becomes a private struct, and there is a new private `static bool TryBump(ref OpenBlock, ulong, out MetalStagingLease)`.

- [ ] **Step 1: Write the failing test**
```csharp
using System;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// STEADY STAGING ALLOCATES NOTHING (#1114), device-free: the arena's slot rotation, and a frame of staged
    /// record-time uploads through <see cref="MetalBufferUpload.Record"/>, measured with
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>.
    /// <para>
    /// <b>TWO ALLOCATIONS HID HERE AND NEITHER SHOWS IN A PICTURE.</b> The arena made one block record OBJECT every
    /// time a slot opened a block, which on a steady frame is every frame, and the upload built the alignment
    /// refusal's message on every call, refusal or not. Grimhollow's water path paid both per plane per frame.
    /// </para>
    /// <para>
    /// <b>THE SINK AND THE BLIT SEAM ARE LOCAL AND SILENT</b>, because the shared fakes log every call into a
    /// growing list, which is right for a routing test and exactly the allocation this one would then measure.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class MetalStagingAllocationTests : IDisposable
    {
        const int UploadsPerFrame = 8;
        const int MeasuredFrames = 20;
        const uint DestinationSize = 4096;
        const uint UploadStride = 512;

        static readonly IntPtr Destination = new(0xDEAD);
        static readonly IntPtr CommandBuffer = new(0x100);

        readonly MetalRingHarness _harness = new();
        readonly byte[] _payload = new byte[250];

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        [Fact]
        public void ASteadyRotationOfArenaSlotsAllocatesNothing()
        {
            using MetalStagingArena arena = _harness.NewArena();

            for (int frame = 0; frame < _harness.FramesInFlight * 2; frame++) LeaseFrame(arena, frame);

            AllocAssert.NoPerCallAllocation($"{MeasuredFrames} frames of arena slot rotation", () =>
            {
                for (int frame = 0; frame < MeasuredFrames; frame++) LeaseFrame(arena, frame);
            });

            // One block per slot, reused ever after, so the reading above is about the block RECORDS rather than
            // about blocks being created.
            Assert.Equal(_harness.FramesInFlight, arena.BlocksCreated);
        }

        [Fact]
        public void ASteadyFrameOfStagedRecordTimeUploadsAllocatesNothing()
        {
            using MetalStagingArena arena = _harness.NewArena();
            var blit = new CountingBlitApi();
            var encoders = new MetalEncoderScope(new SilentEncoderSink());

            for (int frame = 0; frame < _harness.FramesInFlight * 2; frame++)
                StagedFrame(arena, encoders, blit, frame);

            AllocAssert.NoPerCallAllocation($"{MeasuredFrames} frames of {UploadsPerFrame} staged uploads", () =>
            {
                for (int frame = 0; frame < MeasuredFrames; frame++) StagedFrame(arena, encoders, blit, frame);
            });

            Assert.Equal(_harness.FramesInFlight, arena.BlocksCreated);
            Assert.True(blit.BufferCopies > 0, "no staged copy was emitted, so the reading above measured nothing");
        }

        /// <summary>
        /// AND THE REFUSAL STILL SAYS WHAT IT REFUSED. The message moved behind the alignment check, so this pins
        /// that a misaligned staged upload still names its own size and the offset it was given.
        /// </summary>
        [Fact]
        public void AMisalignedStagedUploadStillNamesItsSizeInTheRefusal()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalBufferUpload.CopyBytesFor(offsetBytes: 6, lengthBytes: 16, DestinationSize));

            Assert.Contains("A record-time upload of 16 bytes to a non-uniform native Metal buffer",
                thrown.Message, StringComparison.Ordinal);
            Assert.Contains("destination offset of 6", thrown.Message, StringComparison.Ordinal);
        }

        static void LeaseFrame(MetalStagingArena arena, int frame)
        {
            arena.BeginSlot(frame % arena.Depth, 0);
            for (int i = 0; i < UploadsPerFrame; i++) arena.Take(MetalStagingArena.AlignedCopyBytes(250));
        }

        void StagedFrame(MetalStagingArena arena, MetalEncoderScope encoders, CountingBlitApi blit, int frame)
        {
            arena.BeginSlot(frame % arena.Depth, 0);
            encoders.BeginRecording(CommandBuffer);

            for (int i = 0; i < UploadsPerFrame; i++)
            {
                MetalBufferUpload.Record(ring: null, 0, Destination, DestinationSize, (uint)i * UploadStride,
                    _payload, encoders, arena, blit);
            }

            encoders.EnsureNoEncoder();
        }

        // AN ENCODER SINK THAT ALLOCATES NOTHING. Every handle is one fabricated number and every emission is
        // dropped, because what this class measures is the staging path above the seam.
        readonly struct SilentEncoderSink : IMetalEncoderSink
        {
            static readonly IntPtr Encoder = new(0xE0C0);

            public IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor) => Encoder;

            public IntPtr BeginBlitEncoder(IntPtr commandBuffer) => Encoder;

            public IntPtr BeginComputeEncoder(IntPtr commandBuffer) => Encoder;

            public void EndEncoding(MetalEncoderKind kind, IntPtr encoder) { }

            public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
                ReadOnlySpan<nuint> offsets, uint firstIndex) { }

            public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
                uint firstIndex) { }

            public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
                uint firstIndex) { }

            public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index) { }

            public void Draw(IntPtr encoder, MTLPrimitiveType topology, uint vertexStart, uint vertexCount,
                uint instanceCount, uint baseInstance) { }

            public void DrawIndexed(IntPtr encoder, MTLPrimitiveType topology, uint indexCount, IntPtr indexBuffer,
                nuint indexBufferOffset, bool sixteenBitIndices, uint instanceCount, int baseVertex,
                uint baseInstance) { }

            public void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
                uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ) { }
        }

        // THE BLIT SEAM, COUNTED RATHER THAN LOGGED. Only the buffer copy is on the staging path, so the other four
        // refuse by name if anything ever routes one here.
        sealed class CountingBlitApi : IMetalBlitApi
        {
            internal int BufferCopies { get; private set; }

            public void CopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffsetBytes,
                IntPtr destination, ulong destinationOffsetBytes, ulong sizeBytes) => BufferCopies++;

            public void CopyTextureToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
                in MetalTextureRegion region) => throw NotStaging();

            public void CopyTextureToBuffer(IntPtr encoder, IntPtr source, IntPtr destination,
                in MetalBufferImageRegion region) => throw NotStaging();

            public void CopyBufferToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
                in MetalBufferImageRegion region) => throw NotStaging();

            public void GenerateMipmaps(IntPtr encoder, IntPtr texture) => throw NotStaging();

            static InvalidOperationException NotStaging()
                => new("A staged record-time buffer upload emits one buffer-to-buffer copy and nothing else.");
        }
    }
}
```
```csharp
using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// STEADY-STATE RECORDING ON A REAL NATIVE METAL DEVICE ALLOCATES NO MANAGED MEMORY (#1114).
    /// <see cref="MetalStagingAllocationTests"/> proves the arena and the upload decision device-free. This proves
    /// the whole record path on hardware: the list, the encoder scope, the real sink, the real staging source and
    /// the real blit seam.
    /// <para>
    /// <b>ONLY THE RECORDING IS MEASURED.</b> Each frame reads the counter around <c>Begin</c> to <c>End</c> and
    /// submits and drains outside that window, because the submit path and the drain are not what #1114 changed,
    /// and a drain per frame is what makes the arena's recycling deterministic. The retry policy is
    /// <see cref="AllocAssert"/>'s, spelled out here because the window is split across frames.
    /// </para>
    /// <para>
    /// <b>ALLOCSENSITIVE, WHICH ALSO SERIALISES THE DEVICE IT BUILDS</b>: the collection disables parallelization,
    /// so the device built beside the suite's own never overlaps another device-building class either.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class MetalRecordingAllocationGpuTests
    {
        const int WarmFrames = 6;
        const int MeasuredFrames = 24;
        const int UploadsPerFrame = 8;
        const uint UploadStride = 512;
        const uint SmallBufferBytes = 4096;
        const int SmallPayloadBytes = 250;

        // Over MetalStagingArena.DefaultRetentionBytes, so the block it takes is released at every recycle.
        const uint OverCapBytes = 9u * 1024 * 1024;

        readonly ITestOutputHelper _output;

        public MetalRecordingAllocationGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void SteadyStagedUploadsUnderTheRetentionCapAllocateNothing()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(SmallBufferBytes, GpuBufferUsage.VertexBuffer));
            using MetalCommandList list = device.CreateCommandList();
            byte[] payload = Payload(SmallPayloadBytes);

            StagedFrames(device, list, buffer, payload, UploadsPerFrame, WarmFrames);

            long first = StagedFrames(device, list, buffer, payload, UploadsPerFrame, MeasuredFrames);
            long retry = first == 0 ? 0 : StagedFrames(device, list, buffer, payload, UploadsPerFrame, MeasuredFrames);

            Assert.True(retry == 0,
                $"{MeasuredFrames} frames of {UploadsPerFrame} staged uploads allocated {first} bytes while "
                + $"recording on the first pass and {retry} on the retry, expected zero on at least one");
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"{MeasuredFrames} steady frames allocated {first} managed bytes while recording, "
                + $"with {list.Arena.BlocksCreated} staging blocks created in total");
        }

        /// <summary>
        /// OVER THE CAP THE NATIVE BLOCK CHURNS BY DESIGN AND NOTHING MANAGED DOES. The retention cap releases
        /// the one enormous block at every recycle and the next frame takes a fresh one, which is M-M8's own
        /// shape. What #1114 owes is that the churn is native only.
        /// </summary>
        [GpuFact]
        public void AnUploadOverTheRetentionCapChurnsNativeBlocksButNoManagedMemory()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(OverCapBytes, GpuBufferUsage.VertexBuffer));
            using MetalCommandList list = device.CreateCommandList();
            byte[] payload = Payload((int)OverCapBytes);

            StagedFrames(device, list, buffer, payload, 1, WarmFrames);
            int createdBefore = list.Arena.BlocksCreated;

            long first = StagedFrames(device, list, buffer, payload, 1, MeasuredFrames);
            long retry = first == 0 ? 0 : StagedFrames(device, list, buffer, payload, 1, MeasuredFrames);

            Assert.True(retry == 0,
                $"{MeasuredFrames} frames of one over-cap staged upload allocated {first} managed bytes while "
                + $"recording on the first pass and {retry} on the retry, expected zero on at least one");
            Assert.True(list.Arena.BlocksCreated - createdBefore >= MeasuredFrames,
                "the over-cap upload was pooled, so this row measured the under-cap path instead");
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        // Sum of the recording windows only. The submit and the drain sit outside every window.
        static long StagedFrames(MetalGpuDevice device, MetalCommandList list, IGpuBuffer buffer, byte[] payload,
            int uploads, int frames)
        {
            long recorded = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();

                list.Begin();
                for (int i = 0; i < uploads; i++)
                    list.UpdateBuffer(buffer, (uint)i * UploadStride, (ReadOnlySpan<byte>)payload);
                list.End();

                recorded += GC.GetAllocatedBytesForCurrentThread() - before;

                device.Submit(list);
                device.WaitForIdle();
            }

            return recorded;
        }

        static byte[] Payload(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(i + 1);
            return bytes;
        }

        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (KhaozEngineMetal.IsPlatformSupported) return true;

            MetalDormancy.ThrowIfRequired("this is not macOS at all");
            _output.WriteLine("dormant: not macOS, so there is no Metal device to record against.");
            return false;
        }

        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
    }
}
```
- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalStagingAllocationTests" && KE_GPU_TESTS=1 KE_METAL_REQUIRED=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalRecordingAllocationGpuTests"`
Expected:
- `ASteadyRotationOfArenaSlotsAllocatesNothing` fails with "20 frames of arena slot rotation allocated N bytes on the first pass and N bytes on the retry". N is about 960, one `OpenBlock` object per frame.
- `ASteadyFrameOfStagedRecordTimeUploadsAllocatesNothing` fails the same way with a few kilobytes: the record plus two message strings per upload.
- `AMisalignedStagedUploadStillNamesItsSizeInTheRefusal` passes. It is a guard, not a red row.
- Both GPU rows fail with a non-zero first and retry count.
- [ ] **Step 3: Implement**

`MetalBufferUpload.cs`, replacing `CopyBytesFor` (lines 71 to 83):
```csharp
        internal static uint CopyBytesFor(uint offsetBytes, uint lengthBytes, uint destinationSizeBytes)
        {
            MetalBufferPolicy.RequireWriteFits(offsetBytes, lengthBytes, destinationSizeBytes);

            // THE RULE ITSELF MOVED TO MetalCopyAlignment AT ROW 14 and nothing about it changed: CopyBuffer and
            // the staging-to-staging arm of a texture copy need the identical refusal, and a second spelling of
            // section 9.3's ruling is the one that would drift.
            //
            // AND THE MESSAGE IS BUILT ON THE REFUSAL AND NOWHERE ELSE (#1114). It names the payload's size, which
            // is a number formatted and two strings joined, and building it as an argument ran both on every staged
            // upload that was about to succeed. The check asks the same predicate the refusal does, so the two
            // cannot disagree about what is aligned.
            if (!MetalCopyAlignment.IsAligned(offsetBytes))
            {
                MetalCopyAlignment.RequireAlignedOffset(offsetBytes, nameof(offsetBytes),
                    "A record-time upload of " + lengthBytes.ToString(CultureInfo.InvariantCulture)
                    + " bytes to a non-uniform native Metal buffer", "destination");
            }

            return MetalCopyAlignment.PaddedSize(lengthBytes);
        }
```

`MetalStagingArena.cs`: add `using System.Runtime.InteropServices;` after line 2. Then replace the body of `Take` from line 230 (`List<OpenBlock> open = _open[_slot];`) through the throw:
```csharp
            List<OpenBlock> open = _open[_slot];
            Span<OpenBlock> blocks = CollectionsMarshal.AsSpan(open);

            for (int i = blocks.Length - 1; i >= 0; i--)
            {
                if (TryBump(ref blocks[i], sizeBytes, out MetalStagingLease lease)) return lease;
            }

            open.Add(new OpenBlock(TakeBlock(sizeBytes)));

            // THE SPAN IS TAKEN AGAIN AFTER THE ADD, because the Add may have grown the list and moved its backing
            // array out from under the one read above.
            ref OpenBlock opened = ref CollectionsMarshal.AsSpan(open)[open.Count - 1];
            if (TryBump(ref opened, sizeBytes, out MetalStagingLease fresh)) return fresh;

            throw new InvalidOperationException(
                "A native Metal staging block of " + opened.Block.SizeBytes + " bytes could not hold a request "
                + "of " + sizeBytes + " bytes. A fresh block starts at offset 0, which is aligned to everything, "
                + "so this is arithmetic that cannot be reached rather than a machine that ran out of memory.");
```
Replace the `OpenBlock` class (lines 430 to 453) with:
```csharp
        // ONE BLOCK PLUS HOW FAR IT HAS BEEN BUMPED, held INLINE in the slot's list rather than as an object per
        // block (#1114). A class cost one allocation every time a slot opened a block, which on a steady frame is
        // every frame, and the list's backing array is already the storage a struct needs, reused across frames
        // because Clear keeps the capacity.
        struct OpenBlock
        {
            internal OpenBlock(MetalStagingBlock block)
            {
                Block = block;
                Used = 0;
            }

            internal readonly MetalStagingBlock Block;
            internal ulong Used;
        }

        // A STATIC TAKING THE RECORD BY REF, so a bump on a copy (a List indexer, a foreach variable) is a compile
        // error rather than a lease that silently overlaps the next one. A mutating instance method would compile
        // against either and only one of them would move the bump.
        static bool TryBump(ref OpenBlock block, ulong sizeBytes, out MetalStagingLease lease)
        {
            lease = default;

            ulong offset = (block.Used + (CopyAlignment - 1)) & ~(CopyAlignment - 1);
            if (offset < block.Used) return false;
            if (offset > block.Block.SizeBytes || sizeBytes > block.Block.SizeBytes - offset) return false;

            lease = new MetalStagingLease(
                block.Block.Buffer, offset, block.Block.Mapped + (nint)offset, sizeBytes);
            block.Used = offset + sizeBytes;
            return true;
        }
```
`BeginSlot` (line 286, `Retain(open[i].Block)`) and `Dispose` (line 336, `Destroy(open[i].Block)`) only read `Block`, so they compile unchanged.
- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalStagingAllocationTests|FullyQualifiedName~MetalStagingArenaTests|FullyQualifiedName~MetalBufferUploadTests" && KE_GPU_TESTS=1 KE_METAL_REQUIRED=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalRecordingAllocationGpuTests|FullyQualifiedName~MetalRingGpuTests|FullyQualifiedName~MetalRecordCostGpuTests"`
- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gpu.Metal/Internal/MetalBufferUpload.cs KhaozEngine.Gpu.Metal/Internal/MetalStagingArena.cs KhaozEngine.Render.Tests/Gpu/MetalStagingAllocationTests.cs KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs
git commit -m "gpu(metal): stage record-time uploads with no managed allocation" -- KhaozEngine.Gpu.Metal/Internal/MetalBufferUpload.cs KhaozEngine.Gpu.Metal/Internal/MetalStagingArena.cs KhaozEngine.Render.Tests/Gpu/MetalStagingAllocationTests.cs KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs
```

### Task D2: The autorelease walk sees through the package's own seams

**Branch:** `feature/frame-cost-metal` (worktree created from `feature/frame-cost-round`)

**Files:**
- Modify / Test: `KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs`: summary after line 38, `EntryPoints` at 274-288, `Reaches` at 319-340, a new section after line 364, and new rows after line 153.
- `IlCallGraph.cs` is untouched, so `MetalIndexTableNameBlindnessTests` keeps its raw reader.

**Interfaces:**
- Consumes: `IlCallGraph.Callees(MethodBase)`, `IlCallGraph.Describe(MethodBase)`, `Type.GetInterfaceMap(Type)`, `MethodInfo.GetGenericMethodDefinition()`
- Produces (private to the test class): `static MethodBase[] PackageCallees(MethodBase)`, `static MethodBase Definition(MethodBase)`, `static IEnumerable<MethodBase> Implementations(MethodBase)`

- [ ] **Step 1: Write the failing test**
```csharp
        /// <summary>
        /// THE WALK SEES THROUGH THE ENCODER SEAM, which is the positive control for the interface edge and the
        /// reason the seam's own members no longer need pools of their own (#1114). The encoder scope reaches
        /// <c>-endEncoding</c> only through <see cref="IMetalEncoderSink"/>, so a walk that stopped at the
        /// interface would find nothing here and report every caller of the scope clean for the wrong reason.
        /// </summary>
        [Fact]
        public void TheWalk_SeesThroughTheEncoderSinkToTheInteropLayer()
        {
            MethodBase ensure = typeof(MetalEncoderScope)
                .GetMethod(nameof(MetalEncoderScope.EnsureBlitEncoder),
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("MetalEncoderScope.EnsureBlitEncoder is gone.");

            var path = new List<MethodBase>();
            bool reaches = Reaches(ensure, new HashSet<MethodBase>(), path, stopAtPools: false);

            _output.WriteLine(string.Join(" -> ", path.Select(Describe)));
            Assert.True(reaches,
                "The IL walk found no route from MetalEncoderScope.EnsureBlitEncoder to ObjCMsgSend, which means "
                + "it stopped at IMetalEncoderSink rather than following the call to the package's implementation. "
                + "Every caller of the scope would then read as clean because the walk is blind, not because it "
                + "is pooled.");
            Assert.Contains(path, m => m.DeclaringType == typeof(MetalEncoderSink));
        }

        /// <summary>
        /// AND THE ENTRY-POINT SET NO LONGER HOLDS WHAT THE PACKAGE CALLS THROUGH A SEAM OR A GENERIC BODY
        /// (#1114). Before the two indirect edges, every member of both encoder seams and every generic flush body
        /// was a root, which is what forced a pool into each seam member however its caller was covered. The
        /// command list's own members stay roots, which is where the pools sit.
        /// </summary>
        [Fact]
        public void TheEntryPoints_ExcludeWhatThePackageCallsThroughASeamOrAGenericBody()
        {
            string[] roots = EntryPoints().Select(Describe).ToArray();

            Assert.DoesNotContain("MetalEncoderSink.SetBufferOffset", roots, StringComparer.Ordinal);
            Assert.DoesNotContain("MetalRenderApi.SetViewport", roots, StringComparer.Ordinal);
            Assert.DoesNotContain("MetalCommandList.DrawWith", roots, StringComparer.Ordinal);
            Assert.DoesNotContain("MetalBindRecords.Flush", roots, StringComparer.Ordinal);

            Assert.Contains("MetalCommandList.Begin", roots, StringComparer.Ordinal);
            Assert.Contains("MetalCommandList.DrawIndexed", roots, StringComparer.Ordinal);
        }
```
- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalAutoreleaseArchitectureTests"`
Expected:
- `TheWalk_SeesThroughTheEncoderSinkToTheInteropLayer` fails with "The IL walk found no route from MetalEncoderScope.EnsureBlitEncoder to ObjCMsgSend".
- `TheEntryPoints_Exclude...` fails at `Assert.DoesNotContain` for "MetalEncoderSink.SetBufferOffset".
- The other seven rows pass.
- [ ] **Step 3: Implement**
Add this paragraph to the class summary after line 38:
```csharp
    /// <para><b>AND THE CALL GRAPH SEES THROUGH THE PACKAGE'S OWN SEAMS (#1114).</b> The raw IL names an
    /// interface member at a call through <c>IMetalEncoderSink</c> or <c>IMetalRenderApi</c>, and a constructed
    /// method at a call into a generic body, so each seam implementation and each generic definition used to be a
    /// root nothing called and had to open a pool of its own. The walk now follows both: an interface call reaches
    /// every package implementation, and a generic call reaches its definition. That is what lets the pool sit on
    /// the command list member that caused an emission, with no exclusion list anywhere.</para>
```
In `EntryPoints()`, change line 281 to:
```csharp
                foreach (MethodBase callee in PackageCallees(method)) called.Add(callee);
```
In `Reaches`, change line 323 to:
```csharp
            foreach (MethodBase callee in PackageCallees(method))
```
`OpensAPool` stays on the raw `Callees`, because opening a pool is a fact about a method's own IL. Add this after line 364:
```csharp
        // ---- The package's own call graph ----------------------------------------------------------------

        static readonly Assembly Package = typeof(MetalSupportProbe).Assembly;

        static readonly Dictionary<MethodBase, MethodBase[]> _packageCallees = new();

        // THE IL'S EDGES PLUS THE TWO IT SPELLS INDIRECTLY, which is what the rule walks. A call through one of the
        // package's OWN interfaces is an edge to every package implementation of that member, the conservative
        // answer: the walk cannot know which one a field holds, so it follows all of them. A call to a constructed
        // generic method is an edge to its DEFINITION, the method the package declares and the entry-point set
        // holds. Interfaces from another assembly add nothing, because their implementations here are exactly the
        // consumer-facing members the entry-point set is meant to hold.
        static MethodBase[] PackageCallees(MethodBase method)
        {
            lock (_packageCallees)
            {
                if (_packageCallees.TryGetValue(method, out MethodBase[]? cached)) return cached;
            }

            var found = new List<MethodBase>();
            foreach (MethodBase callee in IlCallGraph.Callees(method))
            {
                MethodBase target = Definition(callee);
                found.Add(target);
                found.AddRange(Implementations(target));
            }

            MethodBase[] result = found.ToArray();
            lock (_packageCallees) { _packageCallees[method] = result; }
            return result;
        }

        static MethodBase Definition(MethodBase method)
            => method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } constructed
                && constructed.DeclaringType?.Assembly == Package
                    ? constructed.GetGenericMethodDefinition()
                    : method;

        static IEnumerable<MethodBase> Implementations(MethodBase member)
        {
            Type? contract = member.DeclaringType;
            if (contract is not { IsInterface: true } || contract.Assembly != Package) yield break;

            foreach (Type type in Package.GetTypes())
            {
                if (type.IsInterface || !contract.IsAssignableFrom(type)) continue;

                InterfaceMapping map = type.GetInterfaceMap(contract);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Equals(member)) yield return map.TargetMethods[i];
                }
            }
        }
```
- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalAutoreleaseArchitectureTests|FullyQualifiedName~MetalIndexTableNameBlindnessTests"`
All nine rows must pass. `NoEntryPointReachesAMessageSendWithoutAPool` staying green here proves the stronger walk turns up no hidden gap before any pool moves. By inspection, every package implementation of a package interface either pushes its own pool or never reaches `ObjCMsgSend`.
- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs
git commit -m "gpu(metal): walk the autorelease rule through the package's own seams" -- KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs
```

### Task D3: One autorelease pool per command list member

**Branch:** `feature/frame-cost-metal` (worktree created from `feature/frame-cost-round`)

**Files:**
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCAutoreleasePool.cs:7-58`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs:31` (one sentence)
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalEncoderSink.cs`: summary after line 48, members at 53-225
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalRenderApi.cs`: summary at 26-30, members at 43-192
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.cs`: using at line 2, summary after 46, `Begin` at 254, `End` at 358, `Dispose` at 473
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Passes.cs`: using at line 2, members at 39, 55 and 66
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Draws.cs`: members at 143, 173 and 228
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Transfers.cs`: using at line 2, members at 57, 96, 129, 153 and 235
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalBufferUpload.cs`: using at line 2, `StageAndCopy` at 88
- Test: `KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs` (new row)

**Interfaces:**
- Consumes: `ObjCAutoreleasePool.Enter()` and `Dispose()`. Their signatures are unchanged. They lose `[SupportedOSPlatform("macos")]` and become no-ops off macOS.
- Produces:
  - **Lose their pool (17):**
    - `MetalEncoderSink`: `BeginRenderEncoder`, `BeginBlitEncoder`, `BeginComputeEncoder`, `EndEncoding`, `SetBuffers`, `SetTextures`, `SetSamplerStates`, `SetBufferOffset`, `Draw`, `DrawIndexed`, `Dispatch`
    - `MetalRenderApi`: `CreateRenderPassDescriptor`, `CreateResolveDescriptor`, `SetGraphicsState`, `ReleaseRenderPassDescriptor`, `SetViewport`, `SetScissorRect`
  - **Gain a pool (15):**
    - `MetalCommandList`: `Begin`, `End`, `Dispose`, `SetFramebuffer`, `ClearColorTarget`, `ClearDepthStencil`, `Draw(uint, uint, uint, uint)`, `DrawIndexed`, `Dispatch`, `CopyBuffer`, `CopyTexture`, `CopyTextureSubresource(IGpuTexture, uint, uint, IGpuTexture, uint, uint, uint, uint)`, `GenerateMipmaps`, `StandaloneResolvePass`
    - `MetalBufferUpload.StageAndCopy`
  - **Keep their pool:** `MetalBlitApi` (5 members), `MetalComputeApi.SetComputePipelineState` and every other package pool.

- [ ] **Step 1: Write the failing test**
```csharp
        /// <summary>
        /// THE COST HALF OF #1114: neither encoder seam opens a pool of its own. The rule above proves every caller
        /// is covered, and it would stay green if a pool came back into a seam member, because a nested pool is
        /// correct. What it would cost is the push and pop per argument-table write and per draw that #600 measured
        /// at 21 ns and Grimhollow's town frame spent 13% of its recording on. So the placement is pinned here,
        /// over every member either type declares, with no list of names to fall behind.
        /// </summary>
        [Fact]
        public void TheEncoderSeamsOpenNoPoolOfTheirOwn()
        {
            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            MethodBase[] members = typeof(MetalEncoderSink).GetMethods(declared)
                .Concat(typeof(MetalRenderApi).GetMethods(declared))
                .Cast<MethodBase>()
                .ToArray();

            Assert.Contains(members, m => m.Name == nameof(MetalEncoderSink.SetBufferOffset));

            string[] pooled = members.Where(OpensAPool).Select(Describe).ToArray();

            Assert.True(pooled.Length == 0,
                "These encoder-seam members open an autorelease pool of their own, which puts a push and a pop "
                + "back on every argument-table write or draw. Since #1114 the caller holds the pool: the "
                + "MetalCommandList member that caused the emission opens one, and "
                + "NoEntryPointReachesAMessageSendWithoutAPool proves every caller does. Remove the pool here.\n"
                + string.Join("\n", pooled));
        }
```
- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalAutoreleaseArchitectureTests"`
Expected: `TheEncoderSeamsOpenNoPoolOfTheirOwn` fails with "These encoder-seam members open an autorelease pool of their own", listing the 17 members named under Interfaces.
- [ ] **Step 3: Implement**

`ObjCAutoreleasePool.cs`, replacing lines 38 to 57 and adding a summary paragraph before `</summary>` at line 31:
```csharp
    /// <para>
    /// IT IS A NO-OP OFF macOS SINCE #1114, the one member of this folder that answers on another platform. The
    /// pool moved up to the <c>MetalCommandList</c> members that reach the encoder seams, and the device-free
    /// tests on the Linux and Windows legs drive those members through fake sinks. There <see cref="Enter"/> hands
    /// back a scope that pushed nothing and <see cref="Dispose"/> pops nothing, so neither loads libobjc. On macOS
    /// it is the same push and pop as before.
    /// </para>
```
```csharp
        /// <summary>
        /// Push a pool and hand back the scope that pops it. Always used as
        /// <c>using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();</c> at the TOP of the body, so the pop
        /// happens on every exit including a throw. Off macOS the scope is empty and costs a branch.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ObjCAutoreleasePool Enter()
        {
            if (!OperatingSystem.IsMacOS()) return default;
            return new(ObjCRuntime.AutoreleasePoolPush());
        }

        /// <summary>
        /// Pop the pool, releasing everything autoreleased inside the scope.
        /// <para>
        /// Popping a pool pops every pool pushed after it too, which is the runtime's own behaviour and is why
        /// the <c>ref struct</c> above matters: a scope that escaped its frame and popped late would drain
        /// objects a caller further up is still using. An empty scope pops nothing, which is also what keeps a
        /// <c>default</c> scope from ever reaching <c>objc_autoreleasePoolPop</c> with nil.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (_pool == IntPtr.Zero) return;

            ObjCRuntime.AutoreleasePoolPop(_pool);
        }
```
`ObjCRuntime.cs:31`: change "NOTHING HERE RUNS OFF macOS, and nothing here runs at type load either." to "NOTHING HERE RUNS OFF macOS apart from <see cref=\"ObjCAutoreleasePool\"/>'s empty scope (#1114), and nothing here runs at type load either."

`MetalEncoderSink.cs`: add this paragraph before `</summary>` at line 49:
```csharp
    /// <para><b>NO MEMBER OPENS AN AUTORELEASE POOL, AND THE CALLER ALWAYS HOLDS ONE (M-N5, #1114).</b> The pool
    /// sits on the <c>MetalCommandList</c> member that caused the emission (the draw, the dispatch, the staged
    /// upload, the framebuffer change, a clear, a transfer, <c>Begin</c>, <c>End</c> or <c>Dispose</c>), so one push
    /// and one pop cover a pass opening, a whole bind flush and the draw after it. The encoders these factories
    /// hand back are retained before that pool drains, which is the paragraph above and unchanged.
    /// <c>MetalAutoreleaseArchitectureTests</c> walks through this seam to prove every caller is pooled, and
    /// <c>TheEncoderSeamsOpenNoPoolOfTheirOwn</c> keeps a per-call pool from coming back.</para>
```
Then replace lines 53 to 225 (every doc comment is unchanged except `SetBufferOffset`'s remarks):
```csharp
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor)
            => Retained(new MTLCommandBuffer(commandBuffer).RenderCommandEncoder(descriptor));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr BeginBlitEncoder(IntPtr commandBuffer)
        {
            // .Handle because a list holds its encoder across calls and every transition here is by raw pointer.
            // The typed MTLBlitCommandEncoder exists for the setup batch, which opens, copies and ends in one go.
            return Retained(new MTLCommandBuffer(commandBuffer).BlitCommandEncoder().Handle);
        }

        /// <inheritdoc/>
        /// <remarks>SERIAL (M-H4), which is what makes a dependent dispatch inside one encoder ordered with no
        /// barrier machinery behind it.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr BeginComputeEncoder(IntPtr commandBuffer)
            => Retained(new MTLCommandBuffer(commandBuffer).ComputeCommandEncoder(MTLDispatchType.Serial));

        /// <inheritdoc/>
        /// <remarks>The end AND the release, in that order: releasing an encoder that has not been ended would
        /// leave the command buffer holding one it can never be told about, and the buffer could then never be
        /// committed.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EndEncoding(MetalEncoderKind kind, IntPtr encoder)
        {
            if (encoder == IntPtr.Zero) return;

            new MTLCommandEncoder(encoder).EndEncoding();
            ObjCRuntime.ObjcRelease(encoder);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
        {
            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetBuffers(buffers, offsets, firstIndex);
            else
                new MTLRenderCommandEncoder(encoder).SetBuffers(stage, buffers, offsets, firstIndex);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
            uint firstIndex)
        {
            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetTextures(textures, firstIndex);
            else
                new MTLRenderCommandEncoder(encoder).SetTextures(stage, textures, firstIndex);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
        {
            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetSamplerStates(samplers, firstIndex);
            else
                new MTLRenderCommandEncoder(encoder).SetSamplerStates(stage, samplers, firstIndex);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE HOTTEST MEMBER IN THIS TYPE, and since #1114 it opens no pool of its own.
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/600 measured it on 2026-08-11 (M2 Max, Release, debug
        /// layer off, best of seven) at about 61 ns with a pool around the send and about 40 ns without, so the
        /// pool was 21 ns of it, and recorded that the answer if that ever mattered was structural, one pool per
        /// flush, never an exclusion list. Grimhollow's town frame is where it mattered: 13% of
        /// <c>Scene3D.RenderInternal</c> was the pool pair. The pool now sits on the <c>MetalCommandList</c> member
        /// that caused the flush, so a draw pays one pair however many argument-table writes precede it.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index)
        {
            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetBufferOffset(offset, index);
            else
                new MTLRenderCommandEncoder(encoder).SetBufferOffset(stage, offset, index);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Draw(IntPtr encoder, MTLPrimitiveType topology, uint vertexStart, uint vertexCount,
            uint instanceCount, uint baseInstance)
            => new MTLRenderCommandEncoder(encoder).DrawPrimitives(
                topology, vertexStart, vertexCount, instanceCount, baseInstance);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void DrawIndexed(IntPtr encoder, MTLPrimitiveType topology, uint indexCount, IntPtr indexBuffer,
            nuint indexBufferOffset, bool sixteenBitIndices, uint instanceCount, int baseVertex,
            uint baseInstance)
            => new MTLRenderCommandEncoder(encoder).DrawIndexedPrimitives(
                topology, indexCount, sixteenBitIndices ? MTLIndexType.UInt16 : MTLIndexType.UInt32,
                new MTLBuffer(indexBuffer), indexBufferOffset, instanceCount, baseVertex, baseInstance);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
            uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ)
            => new MTLComputeCommandEncoder(encoder).DispatchThreadgroups(
                new MTLSize(groupCountX, groupCountY, groupCountZ),
                new MTLSize(threadsPerGroupX, threadsPerGroupY, threadsPerGroupZ));
```

`MetalRenderApi.cs`: replace the summary paragraph at lines 26 to 30 with:
```csharp
    /// <para><b>NO BODY OPENS AN AUTORELEASE POOL, AND EVERY CALLER HOLDS ONE (M-N5, #1114).</b>
    /// <c>+renderPassDescriptor</c> and every attachment slot it vends are autoreleased objects, and they are
    /// drained by the pool of the <c>MetalCommandList</c> member that opened the pass (a draw, a clear-only
    /// <c>End</c>, a framebuffer change or a resolve). The descriptor's OWN lifetime is still not what that pool
    /// covers: it stays retained explicitly, so its lifetime does not depend on where the caller's pool sits.
    /// </para>
```
Delete the `using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();` line and the blank line after it from the six members at lines 47, 91, 131, 162, 180 and 190. `ReleaseRenderPassDescriptor` becomes:
```csharp
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReleaseRenderPassDescriptor(IntPtr descriptor)
        {
            if (descriptor == IntPtr.Zero) return;

            new MTLRenderPassDescriptor(descriptor).Release();
        }
```
Replace the `SetViewport` remarks (lines 166 to 175) and the `SetScissorRect` remark (line 186):
```csharp
        /// <inheritdoc/>
        /// <remarks>Once per framebuffer change or encoder boundary, under the caller's pool.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetViewport(IntPtr encoder, float x, float y, float width, float height,
            float minDepth, float maxDepth)
            => new MTLRenderCommandEncoder(encoder).SetViewport(
                new MTLViewport(x, y, width, height, minDepth, maxDepth));

        /// <inheritdoc/>
        /// <remarks>Under the caller's pool, like <see cref="SetViewport"/>.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetScissorRect(IntPtr encoder, uint x, uint y, uint width, uint height)
            => new MTLRenderCommandEncoder(encoder).SetScissorRect(new MTLScissorRect(x, y, width, height));
```

`MetalCommandList.cs`: add `using KhaozEngine.Gpu.Metal.Internal.ObjC;` after line 2, and add this paragraph before `</summary>` at line 47:
```csharp
    /// <para><b>EVERY MEMBER THAT CAN REACH OBJECTIVE-C OPENS ONE AUTORELEASE POOL, AND NOTHING BELOW IT OPENS
    /// ANOTHER ON THE ENCODER PATH (M-N5, #1114).</b> <c>Begin</c>, <c>End</c>, <c>Dispose</c>, the framebuffer
    /// bind, both clears, both draws, the dispatch and the transfers each push one pool as their first statement,
    /// and <see cref="MetalEncoderSink"/> and <see cref="MetalRenderApi"/> push none, so one push and one pop cover
    /// a pass opening, a whole bind flush and the draw after it. The staged half of <c>UpdateBuffer</c> pushes its
    /// own in <see cref="MetalBufferUpload"/>, because the ring half reaches no Objective-C at all and every uniform
    /// write in the engine takes it. A pool opened here is popped here, on this list's one recording thread.
    /// <c>MetalAutoreleaseArchitectureTests</c> walks through both seams, so a member added here that reaches
    /// either one without a pool fails that walk.</para>
```
Insert as the first statement of `Begin` (line 255), `End` (line 359) and `Dispose` (line 474). Here is `Begin`:
```csharp
        public void Begin()
        {
            // M-N5's one pool for this member. See the type remarks.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ObjectDisposedException.ThrowIf(_disposed, this);
```
`End`:
```csharp
        public void End()
        {
            // M-N5's one pool for this member. See the type remarks.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ObjectDisposedException.ThrowIf(_disposed, this);
```
`Dispose`:
```csharp
        public void Dispose()
        {
            // M-N5's one pool for this member, which the encoder ended below needs. See the type remarks.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (_disposed) return;
```
Add `using KhaozEngine.Gpu.Metal.Internal.ObjC;` to `MetalCommandList.Passes.cs` and `MetalCommandList.Transfers.cs`. `Draws.cs` already has it. Insert the same two lines as the first statement of `SetFramebuffer`, `ClearColorTarget`, `ClearDepthStencil`, `Draw(uint, uint, uint, uint)`, `DrawIndexed`, `Dispatch`, `CopyBuffer`, `CopyTexture`, the 8-parameter `CopyTextureSubresource`, `GenerateMipmaps` and `StandaloneResolvePass`. For example:
```csharp
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            // M-N5's one pool for the pass opening, the bind flush and the draw. See the type remarks.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MetalGraphicsPipeline pipeline = BeginDraw("Drawing", out IntPtr encoder);
```
```csharp
        internal void StandaloneResolvePass(IntPtr sourceTexture, IntPtr destinationTexture)
        {
            // M-N5's one pool, here rather than on ResolveTexture so the device-free row that drives this member
            // directly takes the same shape. See the type remarks.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // END WHATEVER IS OPEN BEFORE THE DESCRIPTOR IS EVEN BUILT, which is the incumbent's own order and the
```
`MetalBufferUpload.cs`: add `using KhaozEngine.Gpu.Metal.Internal.ObjC;` and make this the first statement of `StageAndCopy`:
```csharp
            // THE POOL FOR THIS UPLOAD (M-N5, #1114), here rather than on MetalCommandList.UpdateBuffer because this
            // is the half that reaches Objective-C: a staging block, the blit encoder boundary and the copy. The
            // ring half is a memcpy and every uniform write in the engine takes it, so a pool there would be a push
            // and a pop bought for nothing, which the ring path has never paid.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
```
- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet build KhaozEngine.slnx -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.Metal" && MTL_DEBUG_LAYER=1 KE_GPU_TESTS=1 KE_METAL_REQUIRED=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "Category!=LiveSocket"`
- All ten architecture rows pass. Between dropping the seam pools and adding the list pools, `NoEntryPointReachesAMessageSendWithoutAPool` lists the members above, which is the intended midpoint.
- The full native Metal Render suite passes with 0 failed and 0 skipped. That is about 25 s locally, according to the workflow note at `cross-platform-gpu.yml:614-618`.
- Then run `dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, and `sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree`.
- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCAutoreleasePool.cs KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs KhaozEngine.Gpu.Metal/Internal/MetalEncoderSink.cs KhaozEngine.Gpu.Metal/Internal/MetalRenderApi.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Passes.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Draws.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Transfers.cs KhaozEngine.Gpu.Metal/Internal/MetalBufferUpload.cs KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs
git commit -m "gpu(metal): hold one autorelease pool per command list member" -- KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCAutoreleasePool.cs KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs KhaozEngine.Gpu.Metal/Internal/MetalEncoderSink.cs KhaozEngine.Gpu.Metal/Internal/MetalRenderApi.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Passes.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Draws.cs KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Transfers.cs KhaozEngine.Gpu.Metal/Internal/MetalBufferUpload.cs KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs
```

### Task D4: Cached hot selectors and the pass descriptor class

**Branch:** `feature/frame-cost-metal` (worktree created from `feature/frame-cost-round`)

**Files:**
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderCommandEncoder.cs:76-294`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLComputeCommandEncoder.cs:52-126`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLCommandEncoder.cs:54`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLCommandBuffer.cs:181-221`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLBlitCommandEncoder.cs:76-82`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderPassDescriptor.cs:209-222`
- Modify: `KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs:31-35, 57-62`
- Create / Test: `KhaozEngine.Render.Tests/Gpu/MetalHotSelectorTests.cs`
- Test: `KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs` (one new row)

**Interfaces:**
- Consumes: `ObjCRuntime.Sel(string)`, `ObjCRuntime.ClassNamed(string)`, `IlCallGraph.Callees`, `IlCallGraph.DeclaredMethods`
- Produces: a private nested `static class Selectors` of `internal static readonly IntPtr` fields in six handle types. `MTLRenderCommandEncoder.Stage` changes to `static IntPtr Stage(MetalShaderStage, IntPtr, IntPtr)`. New private `static IntPtr DescriptorClass()` on `MTLRenderPassDescriptor`.

- [ ] **Step 1: Write the failing test**
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE HOT SELECTORS ARE RESOLVED ONCE PER PROCESS (#1114). <c>ObjCRuntime.Sel</c> is a string hash and a
    /// dictionary probe per call, and <c>ObjCRuntime.ClassNamed</c> encodes its name into a fresh array per call,
    /// so on the per-draw, per-bind and per-pass members both are replaced by fields in a nested
    /// <c>Selectors</c> type, <c>MetalCompletionHandler</c>'s shape. The IL rows run everywhere. The value row
    /// needs libobjc and goes dormant off macOS.
    /// </summary>
    public sealed class MetalHotSelectorTests
    {
        static readonly Type[] SelectorOwners =
        {
            typeof(MTLRenderCommandEncoder), typeof(MTLComputeCommandEncoder), typeof(MTLCommandEncoder),
            typeof(MTLCommandBuffer), typeof(MTLBlitCommandEncoder), typeof(MTLRenderPassDescriptor),
        };

        readonly ITestOutputHelper _output;

        public MetalHotSelectorTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void NoHotMemberLooksASelectorOrAClassUpPerCall()
        {
            string[] lookups = HotMembers()
                .Where(m => IlCallGraph.Callees(m).Any(IsPerCallLookup))
                .Select(IlCallGraph.Describe)
                .ToArray();

            Assert.True(lookups.Length == 0,
                "These per-draw, per-bind or per-pass members still resolve a selector or a class on every call. "
                + "Read it from the type's nested Selectors class instead, which resolves once per process.\n"
                + string.Join("\n", lookups));
        }

        /// <summary>The positive control: the reader does see a per-call lookup where one is deliberately left, so
        /// the row above is green because the hot members are clean rather than because the IL read came back
        /// empty.</summary>
        [Fact]
        public void TheWalk_SeesAPerCallLookupWhereOneIsLeft()
            => Assert.Contains(IlCallGraph.Callees(Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.Commit))),
                IsPerCallLookup);

        [Fact]
        public void EveryCachedSelectorResolvesToADistinctSelector()
        {
            if (!OperatingSystem.IsMacOS())
            {
                _output.WriteLine("dormant: not macOS, so there is no libobjc to register selectors with.");
                return;
            }

            var seen = new HashSet<IntPtr>();
            foreach (Type owner in SelectorOwners)
            {
                Type selectors = owner.GetNestedType("Selectors", BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(owner.Name + " has no nested Selectors type.");

                foreach (FieldInfo field in selectors.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var value = (IntPtr)field.GetValue(null)!;
                    Assert.NotEqual(IntPtr.Zero, value);
                    Assert.True(seen.Add(value),
                        owner.Name + "." + field.Name + " resolved to a selector another cached field already holds");
                }
            }

            _output.WriteLine($"{seen.Count} cached selectors resolved, all distinct");
        }

        static IEnumerable<MethodBase> HotMembers()
        {
            foreach (Type type in new[] { typeof(MTLRenderCommandEncoder), typeof(MTLComputeCommandEncoder),
                typeof(MTLCommandEncoder) })
            {
                foreach (MethodBase member in IlCallGraph.DeclaredMethods(type)) yield return member;
            }

            yield return Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.RenderCommandEncoder));
            yield return Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.BlitCommandEncoder));
            yield return Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.ComputeCommandEncoder));
            yield return Member(typeof(MTLBlitCommandEncoder), nameof(MTLBlitCommandEncoder.CopyFromBufferToBuffer));
            yield return Member(typeof(MTLRenderPassDescriptor), nameof(MTLRenderPassDescriptor.Create));
        }

        static MethodBase Member(Type type, string name)
            => IlCallGraph.DeclaredMethods(type).Single(m => m.Name == name);

        static bool IsPerCallLookup(MethodBase callee)
            => callee.DeclaringType == typeof(ObjCRuntime)
                && callee.Name is nameof(ObjCRuntime.Sel) or nameof(ObjCRuntime.ClassNamed);
    }
}
```
Add this row to `MetalRecordingAllocationGpuTests`, together with `using KhaozEngine.Primitives;` and `static readonly Color Blue = new(0f, 0f, 1f, 1f);`:
```csharp
        /// <summary>
        /// A STEADY CLEAR-ONLY PASS ALLOCATES NOTHING, which is the per-pass half of #1114: every pass begin built
        /// <c>MTLRenderPassDescriptor</c>'s class name into a fresh array, and a frame reopens passes after every
        /// staged upload as well as at every framebuffer change.
        /// </summary>
        [GpuFact]
        public void ASteadyClearOnlyPassAllocatesNothing()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuTexture colour = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer framebuffer = device.Factory.CreateFramebuffer(null, colour);
            using MetalCommandList list = device.CreateCommandList();

            ClearFrames(device, list, framebuffer, WarmFrames);

            long first = ClearFrames(device, list, framebuffer, MeasuredFrames);
            long retry = first == 0 ? 0 : ClearFrames(device, list, framebuffer, MeasuredFrames);

            Assert.True(retry == 0,
                $"{MeasuredFrames} clear-only passes allocated {first} bytes while recording on the first pass and "
                + $"{retry} on the retry, expected zero on at least one");
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        static long ClearFrames(MetalGpuDevice device, MetalCommandList list, IGpuFramebuffer framebuffer,
            int frames)
        {
            long recorded = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();

                list.Begin();
                list.SetFramebuffer(framebuffer);
                list.ClearColorTarget(0, Blue);
                list.End();

                recorded += GC.GetAllocatedBytesForCurrentThread() - before;

                device.Submit(list);
                device.WaitForIdle();
            }

            return recorded;
        }
```
- [ ] **Step 2: Run it and confirm it fails**
Run: `dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalHotSelectorTests" && KE_GPU_TESTS=1 KE_METAL_REQUIRED=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalRecordingAllocationGpuTests"`
Expected:
- `NoHotMemberLooksASelectorOrAClassUpPerCall` fails, listing every encoder member plus the five named members.
- On macOS, `EveryCachedSelectorResolvesToADistinctSelector` fails with "MTLRenderCommandEncoder has no nested Selectors type."
- `ASteadyClearOnlyPassAllocatesNothing` fails with about 40 bytes per frame on both passes.
- The control row passes.
- [ ] **Step 3: Implement**

In `MTLRenderCommandEncoder.cs`, every doc comment is unchanged. Replace the member bodies and `Stage`, and append `Selectors`:
```csharp
        internal unsafe void SetViewport(in MTLViewport viewport)
        {
            MTLViewport value = viewport;
            ObjCMsgSend.SendVoidPtrNUInt(Handle, Selectors.SetViewports, &value, 1);
        }

        internal unsafe void SetScissorRect(in MTLScissorRect rect)
        {
            MTLScissorRect value = rect;
            ObjCMsgSend.SendVoidPtrNUInt(Handle, Selectors.SetScissorRects, &value, 1);
        }

        internal unsafe void SetBuffers(MetalShaderStage stage, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
        {
            IntPtr selector = Stage(stage, Selectors.SetVertexBuffers, Selectors.SetFragmentBuffers);

            fixed (IntPtr* objects = buffers)
            fixed (nuint* offsetValues = offsets)
            {
                ObjCMsgSend.SendVoidBuffersRange(Handle, selector, objects, offsetValues,
                    new NSRange(firstIndex, (nuint)buffers.Length));
            }
        }

        internal unsafe void SetTextures(MetalShaderStage stage, ReadOnlySpan<IntPtr> textures, uint firstIndex)
        {
            IntPtr selector = Stage(stage, Selectors.SetVertexTextures, Selectors.SetFragmentTextures);

            fixed (IntPtr* objects = textures)
            {
                ObjCMsgSend.SendVoidObjectsRange(Handle, selector, objects,
                    new NSRange(firstIndex, (nuint)textures.Length));
            }
        }

        internal unsafe void SetSamplerStates(MetalShaderStage stage, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
        {
            IntPtr selector = Stage(stage, Selectors.SetVertexSamplerStates, Selectors.SetFragmentSamplerStates);

            fixed (IntPtr* objects = samplers)
            {
                ObjCMsgSend.SendVoidObjectsRange(Handle, selector, objects,
                    new NSRange(firstIndex, (nuint)samplers.Length));
            }
        }

        internal void SetBufferOffset(MetalShaderStage stage, nuint offset, uint index)
            => ObjCMsgSend.SendVoidNUIntNUInt(Handle,
                Stage(stage, Selectors.SetVertexBufferOffset, Selectors.SetFragmentBufferOffset), offset, index);

        internal void SetRenderPipelineState(MTLRenderPipelineState state)
            => ObjCMsgSend.SendVoidPtr(Handle, Selectors.SetRenderPipelineState, state.Handle);

        internal void SetCullMode(MTLCullMode mode)
            => ObjCMsgSend.SendVoidNUInt(Handle, Selectors.SetCullMode, (nuint)(ulong)mode);

        internal void SetFrontFacingWinding(MTLWinding winding)
            => ObjCMsgSend.SendVoidNUInt(Handle, Selectors.SetFrontFacingWinding, (nuint)(ulong)winding);

        internal void SetTriangleFillMode(MTLTriangleFillMode mode)
            => ObjCMsgSend.SendVoidNUInt(Handle, Selectors.SetTriangleFillMode, (nuint)(ulong)mode);

        internal void SetBlendColour(float red, float green, float blue, float alpha)
            => ObjCMsgSend.SendVoidFloat4(Handle, Selectors.SetBlendColor, red, green, blue, alpha);

        internal void SetDepthStencilState(MTLDepthStencilState state)
            => ObjCMsgSend.SendVoidPtr(Handle, Selectors.SetDepthStencilState, state.Handle);

        internal void SetDepthClipMode(MTLDepthClipMode mode)
            => ObjCMsgSend.SendVoidNUInt(Handle, Selectors.SetDepthClipMode, (nuint)(ulong)mode);

        internal void SetStencilReferenceValue(uint reference)
            => ObjCMsgSend.SendVoidUInt(Handle, Selectors.SetStencilReferenceValue, reference);

        internal void DrawPrimitives(MTLPrimitiveType type, uint vertexStart, uint vertexCount,
            uint instanceCount, uint baseInstance)
            => ObjCMsgSend.SendVoidDrawPrimitives(Handle, Selectors.DrawPrimitives,
                (nuint)(ulong)type, vertexStart, vertexCount, instanceCount, baseInstance);

        internal void DrawIndexedPrimitives(MTLPrimitiveType type, uint indexCount, MTLIndexType indexType,
            MTLBuffer indexBuffer, nuint indexBufferOffset, uint instanceCount, int baseVertex, uint baseInstance)
            => ObjCMsgSend.SendVoidDrawIndexedPrimitives(Handle, Selectors.DrawIndexedPrimitives,
                (nuint)(ulong)type, indexCount, (nuint)(ulong)indexType, indexBuffer.Handle, indexBufferOffset,
                instanceCount, baseVertex, baseInstance);

        // THE ONE PLACE THE STAGE BECOMES A SELECTOR, so a new setter added later cannot spell the fork a second
        // way. Compute is refused rather than folded into the vertex arm: a compute encoder is a different
        // protocol with unprefixed selectors, and sending a vertex selector to one is an unrecognised-selector
        // crash at best and a bind onto the wrong table at worst.
        static IntPtr Stage(MetalShaderStage stage, IntPtr vertex, IntPtr fragment) => stage switch
        {
            MetalShaderStage.Vertex => vertex,
            MetalShaderStage.Fragment => fragment,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "An MTLRenderCommandEncoder has a vertex stage and a fragment stage and nothing else. The "
                + "compute argument-table setters are unprefixed selectors on MTLComputeCommandEncoder, so this "
                + "is a bind routed to the wrong encoder kind rather than a stage this type could serve."),
        };

        /// <summary>
        /// EVERY SELECTOR THIS TYPE SENDS, RESOLVED ONCE PER PROCESS (#1114), because every one of them is on the
        /// per-draw, per-bind or per-pass path. A NESTED type for <c>MetalCompletionHandler</c>'s reason: the CLR
        /// runs a type's initializer on first access to THAT type, and only the macOS-only members above touch
        /// it, so nothing here reaches libobjc on the legs where this assembly's device-free tests run.
        /// </summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr SetViewports = ObjCRuntime.Sel("setViewports:count:");
            internal static readonly IntPtr SetScissorRects = ObjCRuntime.Sel("setScissorRects:count:");
            internal static readonly IntPtr SetVertexBuffers =
                ObjCRuntime.Sel("setVertexBuffers:offsets:withRange:");
            internal static readonly IntPtr SetFragmentBuffers =
                ObjCRuntime.Sel("setFragmentBuffers:offsets:withRange:");
            internal static readonly IntPtr SetVertexTextures = ObjCRuntime.Sel("setVertexTextures:withRange:");
            internal static readonly IntPtr SetFragmentTextures = ObjCRuntime.Sel("setFragmentTextures:withRange:");
            internal static readonly IntPtr SetVertexSamplerStates =
                ObjCRuntime.Sel("setVertexSamplerStates:withRange:");
            internal static readonly IntPtr SetFragmentSamplerStates =
                ObjCRuntime.Sel("setFragmentSamplerStates:withRange:");
            internal static readonly IntPtr SetVertexBufferOffset = ObjCRuntime.Sel("setVertexBufferOffset:atIndex:");
            internal static readonly IntPtr SetFragmentBufferOffset =
                ObjCRuntime.Sel("setFragmentBufferOffset:atIndex:");
            internal static readonly IntPtr SetRenderPipelineState = ObjCRuntime.Sel("setRenderPipelineState:");
            internal static readonly IntPtr SetCullMode = ObjCRuntime.Sel("setCullMode:");
            internal static readonly IntPtr SetFrontFacingWinding = ObjCRuntime.Sel("setFrontFacingWinding:");
            internal static readonly IntPtr SetTriangleFillMode = ObjCRuntime.Sel("setTriangleFillMode:");
            internal static readonly IntPtr SetBlendColor = ObjCRuntime.Sel("setBlendColorRed:green:blue:alpha:");
            internal static readonly IntPtr SetDepthStencilState = ObjCRuntime.Sel("setDepthStencilState:");
            internal static readonly IntPtr SetDepthClipMode = ObjCRuntime.Sel("setDepthClipMode:");
            internal static readonly IntPtr SetStencilReferenceValue = ObjCRuntime.Sel("setStencilReferenceValue:");
            internal static readonly IntPtr DrawPrimitives =
                ObjCRuntime.Sel("drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:");
            internal static readonly IntPtr DrawIndexedPrimitives =
                ObjCRuntime.Sel("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:"
                    + "instanceCount:baseVertex:baseInstance:");
        }
```
`MTLComputeCommandEncoder.cs`: the six bodies read `Selectors.SetBuffers`, `Selectors.SetTextures`, `Selectors.SetSamplerStates`, `Selectors.SetBufferOffset`, `Selectors.SetComputePipelineState` and `Selectors.DispatchThreadgroups` where each calls `ObjCRuntime.Sel("...")` today. Then append:
```csharp
        /// <summary>The compute encoder's selectors, resolved once per process (#1114), in a nested type for the
        /// reason <see cref="MTLRenderCommandEncoder"/>'s own gives.</summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr SetBuffers = ObjCRuntime.Sel("setBuffers:offsets:withRange:");
            internal static readonly IntPtr SetTextures = ObjCRuntime.Sel("setTextures:withRange:");
            internal static readonly IntPtr SetSamplerStates = ObjCRuntime.Sel("setSamplerStates:withRange:");
            internal static readonly IntPtr SetBufferOffset = ObjCRuntime.Sel("setBufferOffset:atIndex:");
            internal static readonly IntPtr SetComputePipelineState = ObjCRuntime.Sel("setComputePipelineState:");
            internal static readonly IntPtr DispatchThreadgroups =
                ObjCRuntime.Sel("dispatchThreadgroups:threadsPerThreadgroup:");
        }
```
`MTLCommandEncoder.cs:54`:
```csharp
        internal void EndEncoding() => ObjCMsgSend.SendVoid(Handle, Selectors.EndEncoding);

        /// <summary>The shared protocol's one selector, resolved once per process (#1114).</summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr EndEncoding = ObjCRuntime.Sel("endEncoding");
        }
```
`MTLCommandBuffer.cs`: the three factories read `Selectors.RenderCommandEncoder`, `Selectors.BlitCommandEncoder` and `Selectors.ComputeCommandEncoder`. Append:
```csharp
        /// <summary>The three encoder factories' selectors, resolved once per process (#1114) because every encoder
        /// boundary sends one. The buffer's other selectors are once per submit and stay on
        /// <see cref="ObjCRuntime.Sel"/>.</summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr RenderCommandEncoder =
                ObjCRuntime.Sel("renderCommandEncoderWithDescriptor:");
            internal static readonly IntPtr BlitCommandEncoder = ObjCRuntime.Sel("blitCommandEncoder");
            internal static readonly IntPtr ComputeCommandEncoder =
                ObjCRuntime.Sel("computeCommandEncoderWithDispatchType:");
        }
```
`MTLBlitCommandEncoder.cs`: `CopyFromBufferToBuffer` reads `Selectors.CopyBufferToBuffer`. Append:
```csharp
        /// <summary>The staged upload's one copy selector, resolved once per process (#1114). The texture copies
        /// are per transfer and stay on <see cref="ObjCRuntime.Sel"/>.</summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr CopyBufferToBuffer =
                ObjCRuntime.Sel("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:");
        }
```
`MTLRenderPassDescriptor.cs`: add `using System.Threading;` and replace `Create` (lines 213 to 222):
```csharp
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLRenderPassDescriptor Create()
        {
            IntPtr descriptor = ObjCMsgSend.Send(DescriptorClass(), Selectors.RenderPassDescriptor);

            return new MTLRenderPassDescriptor(
                descriptor == IntPtr.Zero ? IntPtr.Zero : ObjCRuntime.ObjcRetain(descriptor));
        }

        // THE CLASS, CACHED ONLY ONCE IT IS REAL (#1114). ClassNamed encodes its name into a fresh array per call,
        // and this runs at every pass begin. objc_getClass answers nil for a class whose framework is not loaded
        // yet, so a nil answer is never kept: caching it would make every later pass in the process build nothing.
        static IntPtr _class;

        [SupportedOSPlatform("macos")]
        static IntPtr DescriptorClass()
        {
            IntPtr cached = Volatile.Read(ref _class);
            if (cached != IntPtr.Zero) return cached;

            IntPtr resolved = ObjCRuntime.ClassNamed("MTLRenderPassDescriptor");
            if (resolved != IntPtr.Zero) Volatile.Write(ref _class, resolved);
            return resolved;
        }

        /// <summary>The factory selector, resolved once per process (#1114).</summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr RenderPassDescriptor = ObjCRuntime.Sel("renderPassDescriptor");
        }
```
`ObjCRuntime.cs`: replace lines 31 to 35 with:
```csharp
    /// <para>
    /// NOTHING HERE RUNS OFF macOS apart from <see cref="ObjCAutoreleasePool"/>'s empty scope (#1114), and nothing
    /// here runs at type load either. No handle type carries a <c>static readonly</c> selector field of its OWN: a
    /// static initializer would call into libobjc the moment the type was touched, including on the Linux and
    /// Windows legs where this assembly is referenced and its device-free tests run. The hot types keep theirs in a
    /// NESTED <c>Selectors</c> class instead (#1114), the shape <c>MetalCompletionHandler</c> already took, because
    /// the CLR runs a type's initializer on first access to THAT type and only macOS-only members touch it.
    /// Everything else goes through <see cref="Sel"/>, reached only from a body already behind the platform guard.
    /// </para>
```
Replace the last two sentences of the cache comment at lines 60 to 62 with: "Sel(...) at a cold site stays honest rather than needing a field, and the per-draw sites read nested Selectors fields instead (see the class remarks)."
- [ ] **Step 4: Run the tests and confirm they pass**
Run: `dotnet build KhaozEngine.slnx -c Release && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MetalHotSelectorTests|FullyQualifiedName~MetalAutoreleaseArchitectureTests" && MTL_DEBUG_LAYER=1 KE_GPU_TESTS=1 KE_METAL_REQUIRED=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "Category!=LiveSocket"`
- `MetalDrawPathGpuTests` and `MetalBindFlushGpuTests` send every cached selector to a real encoder. A wrong string would abort the host, so the suite must finish with 0 failed and 0 skipped.
- Then run `sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree`.
- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLComputeCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLCommandBuffer.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLBlitCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderPassDescriptor.cs KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs KhaozEngine.Render.Tests/Gpu/MetalHotSelectorTests.cs KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs
git commit -m "gpu(metal): cache the hot selectors and the pass descriptor class" -- KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLComputeCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLCommandBuffer.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLBlitCommandEncoder.cs KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderPassDescriptor.cs KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs KhaozEngine.Render.Tests/Gpu/MetalHotSelectorTests.cs KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs
```

**Release commit sweep terms** (for the round's release commit, not these commits): `ObjCAutoreleasePool`, "opens its own pool", "OpenBlock", "Sel(", and the #600 conclusion. The CHANGELOG history at `CHANGELOG.md:8455-8460` stays as history.


### Decisions and risks


- `docs/design/FRAME-COST-ROUND-DESIGN-2026-09-23.md:303-304` and `:311` against `KhaozEngine.Render.Tests/Gpu/MetalAutoreleaseArchitectureTests.cs:274-288, 362`, `KhaozEngine.Render.Tests/Gpu/IlCallGraph.cs:134-148`, `KhaozEngine.Gpu.Metal/Internal/MetalEncoderSink.cs:145-149` and `KhaozEngine.Gpu.Metal/Internal/MetalRenderApi.cs:168-174`. The walk treats every seam member and every generic definition as a root, so "passes unchanged" is impossible. D2 changes the edges, not the rule. **Owner sign-off needed.**
- `KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCAutoreleasePool.cs:43-57` against `KhaozEngine.Render.Tests/Gpu/MetalRingHarness.cs:138-150`. The command list is driven off macOS, so the pool must become a no-op there. The spec is silent.
- `docs/design/FRAME-COST-ROUND-DESIGN-2026-09-23.md:301-303` ("uploads") against `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Uploads.cs:41-64`. The pool goes in `MetalBufferUpload.StageAndCopy` (`MetalBufferUpload.cs:88`) so the ring path stays pool-free.
- `KhaozEngine.Gpu.Metal/Internal/ObjC/ObjCRuntime.cs:31-35, 57-62` forbids static selector fields in the folder. D4 amends it using the `MetalCompletionHandler.cs:369-387` pattern. That pattern relies on the CoreCLR initializer running at first access, which is safe because only macOS-only members touch the nested types.
- `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLRenderPassDescriptor.cs:217-218` with `ObjCRuntime.cs:118-121, 141-146` is a per-pass managed allocation the spec does not name. It is fixed in D4 and can be dropped by the owner. If dropped, the town-path 5 KiB allocation target in the spec's Acceptance section still pays for it on every pass.
- `KhaozEngine.Gpu.Metal/Internal/IMetalBlitApi.cs:109-170` and `KhaozEngine.Gpu.Metal/Internal/IMetalComputeApi.cs:54-64` keep a per-call pool nested inside the hoisted ones. That is correct and cheap (per copy and per compute state bind), but not free. Removing them is a follow-up the D2 walk already supports.
- Tests that drive scope internals on a device now run unpooled on the test thread:
  - `MetalBindFlushGpuTests.cs:132-156, 317-332`
  - `MetalCommandListGpuTests.cs:74-246`
  - `MetalRingGpuTests.cs:100, 299, 352`
  - `MetalRenderPassGpuTests.cs:218, 321`
  - `MetalCountersAndHeaderGpuTests.cs:210`
  - `FakeMetalEncoderSink.cs:96-181`

  The runtime holds their autoreleased encoders until the worker thread exits. That is a bounded, test-only leak. Wrapping those sites in `ObjCAutoreleasePool.Enter()` is optional.
- `KhaozEngine.Render.Tests/Gpu/MetalRecordingAllocationGpuTests.cs` (new) joins `AllocSensitive`, not `NativeDeviceLifecycle`, even though it builds its own device. Both definitions disable parallelization (`HeadlessNamedBackendTests.cs:147`, `AllocSensitiveCollection.cs:16`), so the device work is still serialized.


---

## Item 6: D3D11 high-performance adapter (#1115)


**Spec vs code:** the spec's "Today" paragraph matches the code. The unset request returns `DefaultEnumeration` at `D3D11AdapterSelection.cs:183-184`. The device then uses a null adapter and `DriverType.Hardware` at `D3D11GpuDevice.Create.cs:365-369`.

**Vortice check:** `Vortice.DXGI` 2.3.0 comes in through `Vortice.Direct3D11` 2.3.0 (`Directory.Packages.props:39`). Disassembling the dll confirms two public methods on `IDXGIFactory6`:
- `Result EnumAdapterByGpuPreference<T>(int index, GpuPreference gpuPreference, out T? adapter) where T : IDXGIAdapter`
- `T EnumAdapterByGpuPreference<T>(int, GpuPreference)`, which throws on failure.

The raw `(int, GpuPreference, Guid, out IntPtr)` overload is internal. `GpuPreference.HighPerformance` exists. `ComObject.QueryInterfaceOrNull<T>()` and `SharpGen.Runtime.SharpGenException` exist in SharpGen.Runtime 2.0.0-beta.13. Microsoft Learn gives Windows 10 1803 as the minimum for `EnumAdapterByGpuPreference`.

### Task E1: The adapter policy gains a high-performance default   (item 6, branch `feature/frame-cost-d3d11-adapter`)

**Branch:** `feature/frame-cost-d3d11-adapter` (worktree created from `feature/frame-cost-round`)
Setup:
```bash
git -C /Users/antonio/KhaozEngine worktree add .worktrees/frame-cost-d3d11-adapter -b feature/frame-cost-d3d11-adapter feature/frame-cost-round
mkdir -p /Users/antonio/KhaozEngine/.worktrees/frame-cost-d3d11-adapter/local-feed
```

**Files:**
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterSelection.cs`
  - enum doc at lines 10-11
  - `D3D11AdapterChoiceKind` at 26-40
  - choice statics at 100-101
  - class summary at 104-128
  - `EnvVarName` doc at 131-135
  - `Choose` at 170-223
  - `IsSoftwareChoice` doc at 225-235
  - `Describe` at 245-268
  - new warnings after 281
- Test: `KhaozEngine.Render.Tests/Gpu/D3D11AdapterSelectionTests.cs`
  - class doc at 11-16
  - `Choose_DefaultLetsDxgiPickAndWarnsAboutNothing` at 97-105 is replaced
  - every `Choose` call at 114, 124, 135, 147, 161, 175, 192, 211
  - new facts appended before line 250
- Test: `KhaozEngine.Render.Tests/Gpu/D3D11DiagnosticsBoundaryTests.cs` lines 58-64

KESIZE: none of these files is in `.filesize-baseline`, and all stay well under the 800-line cap. The largest ends up around 330 lines.

**Interfaces:**
- Produces:
  - `internal static D3D11AdapterChoice Choose(in D3D11AdapterRequest request, IReadOnlyList<D3D11AdapterInfo> adapters, bool gpuPreferenceAvailable, out string? warning)`. This replaces the 3-argument overload. It's internal, so there's no public API change.
  - `D3D11AdapterChoiceKind.HighPerformance = 3`
  - `internal static D3D11AdapterChoice D3D11AdapterChoice.HighPerformance`
  - `internal static string D3D11AdapterSelection.HighPerformanceUnavailableWarning { get; }`
  - `internal static string D3D11AdapterSelection.HighPerformanceCreateFailedWarning(string reason)`
- Consumes: nothing new. The policy stays free of Vortice types.

- [ ] **Step 1: Write the failing test**

In `D3D11AdapterSelectionTests.cs`, replace lines 97-105 (`Choose_DefaultLetsDxgiPickAndWarnsAboutNothing`) with:
```csharp
        /// <summary>
        /// THE DEFAULT PREFERS THE HIGH-PERFORMANCE ADAPTER (https://github.com/APKiwiOrg/KhaozEngine/issues/1115).
        /// DXGI lists the adapter driving the display first, which on a hybrid laptop is the integrated GPU, so
        /// letting DXGI pick ran the engine on the slower of two GPUs. The choice names no index, because the
        /// ranking is IDXGIFactory6's and not the enumeration's.
        /// </summary>
        [Fact]
        public void Choose_UnsetPrefersTheHighPerformanceAdapterWhenIDXGIFactory6IsOffered()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(null), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.HighPerformance, choice.Kind);
            Assert.True(choice.Index < 0);
            Assert.Null(warning);
        }

        /// <summary>The missing-interface fallback. IDXGIFactory6 arrived in Windows 10 1803, and without it the
        /// engine lets DXGI pick as it always did. That is not a warning: nothing was asked for that could not be
        /// given.</summary>
        [Fact]
        public void Choose_UnsetWithoutIDXGIFactory6LetsDxgiPickAndWarnsAboutNothing()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(null), TwoAdapters, gpuPreferenceAvailable: false, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.Null(warning);
        }
```

At every other `Choose(` call in the file (lines 114, 124, 135, 147, 161, 175, 192, 211), insert `gpuPreferenceAvailable: true, ` before `out string? warning`. Each explicit request then proves it wins while the preference is on offer. For example, lines 124-125 become:
```csharp
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse("hardware"), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);
```

Append these facts before the `FromEnvironment_IsReadableOnAnyOperatingSystem` fact at line 243:
```csharp
        /// <summary>The preference does not depend on the enumeration. A machine with one adapter, only a software
        /// adapter, or an enumeration that failed still asks IDXGIFactory6, which answers with whatever it ranks
        /// first.</summary>
        [Fact]
        public void Choose_UnsetPrefersHighPerformanceWhateverTheEnumerationHolds()
        {
            var one = new[] { new D3D11AdapterInfo("Intel(R) UHD Graphics", isSoftware: false) };

            foreach (IReadOnlyList<D3D11AdapterInfo> adapters in new[] { one, SoftwareOnly, None })
            {
                D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                    D3D11AdapterSelection.Parse(null), adapters, gpuPreferenceAvailable: true, out string? warning);

                Assert.Equal(D3D11AdapterChoiceKind.HighPerformance, choice.Kind);
                Assert.Null(warning);
            }
        }

        /// <summary>
        /// EVERY EXPLICIT VALUE KEEPS WINNING, with the preference offered or not, and warp above all, because it is
        /// the value CI pins so the committed goldens keep their rasterizer. The expected kind travels as an int
        /// because the enum is internal and a public test method cannot take it as a parameter.
        /// </summary>
        [Theory]
        [InlineData("warp", (int)D3D11AdapterChoiceKind.WarpDriver, -1)]
        [InlineData("hardware", (int)D3D11AdapterChoiceKind.Enumerated, 0)]
        [InlineData("1", (int)D3D11AdapterChoiceKind.Enumerated, 1)]
        [InlineData("GeForce", (int)D3D11AdapterChoiceKind.Enumerated, 0)]
        [InlineData("Basic Render", (int)D3D11AdapterChoiceKind.Enumerated, 1)]
        public void Choose_EveryExplicitValueWinsOverTheHighPerformanceDefault(string value, int expectedKind,
            int expectedIndex)
        {
            foreach (bool available in new[] { true, false })
            {
                D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                    D3D11AdapterSelection.Parse(value), TwoAdapters, available, out string? warning);

                Assert.Equal((D3D11AdapterChoiceKind)expectedKind, choice.Kind);
                Assert.Equal(expectedIndex, choice.Index);
                Assert.Null(warning);
            }
        }

        /// <summary>An explicit value that cannot be honoured still lets DXGI pick, as it did before the
        /// preference existed, and never lands on the high-performance default: a pin that failed says so and lands
        /// where it always has.</summary>
        [Theory]
        [InlineData("2")]
        [InlineData("Radeon")]
        public void Choose_AnUnhonourableExplicitValueStillLetsDxgiPick(string value)
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(value), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.NotNull(warning);
        }

        /// <summary>The high-performance choice names no enumerated adapter, so like DXGI's own pick it answers
        /// false and leaves the flag to the created device, which is the authority on which adapter ran.</summary>
        [Fact]
        public void IsSoftwareChoice_LeavesTheHighPerformanceAdapterToTheCreatedDevice()
        {
            Assert.False(D3D11AdapterSelection.IsSoftwareChoice(D3D11AdapterChoice.HighPerformance, SoftwareOnly));
        }

        [Fact]
        public void Describe_NamesTheHighPerformanceDefaultAndTheLeverThatOverridesIt()
        {
            string line = D3D11AdapterSelection.Describe(D3D11AdapterChoice.HighPerformance, TwoAdapters);

            Assert.Contains("high-performance", line, StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, line, StringComparison.Ordinal);
        }

        /// <summary>The two ways the default can fail on a real factory both WARN in the same shape as every
        /// unhonourable request above, naming the lever and saying DXGI is picking. The glue that raises them is
        /// Windows-only, so the wording is pinned here.</summary>
        [Fact]
        public void HighPerformanceFallbacks_WarnNamingTheLeverAndThatDxgiPicks()
        {
            string unavailable = D3D11AdapterSelection.HighPerformanceUnavailableWarning;
            string refused = D3D11AdapterSelection.HighPerformanceCreateFailedWarning("DXGI_ERROR_UNSUPPORTED");

            Assert.Contains("Letting DXGI pick", unavailable, StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, unavailable, StringComparison.Ordinal);
            Assert.Contains("Letting DXGI pick", refused, StringComparison.Ordinal);
            Assert.Contains("DXGI_ERROR_UNSUPPORTED", refused, StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, refused, StringComparison.Ordinal);
        }
```

In the class doc at lines 13-15, replace "and the only untested-here piece is `D3D11DxgiQueries.DescribeAdaptersWindows`, which reads a description and a flag off each enumerated adapter and decides nothing." with "and the untested-here pieces are the Windows glue in `D3D11DxgiQueries` and `D3D11AdapterResolution`, which read descriptions, flags and the IDXGIFactory6 ranking and decide nothing."

In `D3D11DiagnosticsBoundaryTests.cs`, replace lines 58-64 with:
```csharp
            // Adapter selection (G2), including the high-performance default (#1115).
            var adapters = new List<D3D11AdapterInfo> { new("WARP", isSoftware: true) };
            D3D11AdapterRequest request = D3D11AdapterSelection.Parse("warp");
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(request, adapters,
                gpuPreferenceAvailable: true, out _);
            _ = D3D11AdapterSelection.Describe(choice, adapters);
            _ = D3D11AdapterSelection.IsSoftwareChoice(choice, adapters);
            D3D11AdapterChoice preferred = D3D11AdapterSelection.Choose(D3D11AdapterSelection.Parse(null), adapters,
                gpuPreferenceAvailable: true, out _);
            _ = D3D11AdapterSelection.Describe(preferred, adapters);
            _ = D3D11AdapterSelection.HighPerformanceUnavailableWarning;
            _ = D3D11AdapterSelection.HighPerformanceCreateFailedWarning("off-windows");
            _ = D3D11AdapterSelection.FromEnvironment();
```

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-d3d11-adapter && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.D3D11AdapterSelectionTests|FullyQualifiedName~KhaozEngine.Tests.Gpu.D3D11DiagnosticsBoundaryTests"
```
Expected: the test project fails to build with:
- `error CS1739: The best overload for 'Choose' does not have a parameter named 'gpuPreferenceAvailable'`
- `error CS1501: No overload for method 'Choose' takes 4 arguments` (the positional call in the theory)
- `error CS0117: 'D3D11AdapterChoiceKind' does not contain a definition for 'HighPerformance'`
- the same CS0117 for `D3D11AdapterChoice.HighPerformance`, `HighPerformanceUnavailableWarning` and `HighPerformanceCreateFailedWarning`

- [ ] **Step 3: Implement**

In `D3D11AdapterSelection.cs`, replace the `Default` member doc (lines 10-11):
```csharp
        /// <summary>Unset or blank: the high-performance adapter where the factory offers <c>IDXGIFactory6</c>,
        /// otherwise DXGI's own pick, which is what the engine did before the preference existed.</summary>
        Default = 0,
```

Replace the `DefaultEnumeration` doc (lines 30-31), and append `HighPerformance` after `Enumerated = 2,` (line 39):
```csharp
        /// <summary>Create with a null adapter and <c>DriverType.Hardware</c>, letting DXGI pick. The unset
        /// request on a runtime without <c>IDXGIFactory6</c> and every request that could not be honoured land
        /// here. The glue takes the same path, without changing the choice, when a high-performance adapter cannot
        /// be fetched or refuses the device.</summary>
        DefaultEnumeration = 0,
```
```csharp
        /// <summary>Create against the adapter <c>IDXGIFactory6.EnumAdapterByGpuPreference(0, HighPerformance)</c>
        /// ranks first, with <c>DriverType.Unknown</c>. What the unset request resolves to whenever the factory
        /// offers <c>IDXGIFactory6</c>, so a hybrid laptop runs on its discrete GPU rather than on the integrated
        /// one DXGI lists first (https://github.com/APKiwiOrg/KhaozEngine/issues/1115). It names no index, because
        /// the ranking is the factory's and not the enumeration's.</summary>
        HighPerformance = 3,
```

After line 101, add:
```csharp
        internal static D3D11AdapterChoice HighPerformance => new(D3D11AdapterChoiceKind.HighPerformance, -1);
```

In the class summary, insert this paragraph before the closing `/// </summary>` at line 128:
```csharp
    /// <para>
    /// UNSET PREFERS THE HIGH-PERFORMANCE ADAPTER (https://github.com/APKiwiOrg/KhaozEngine/issues/1115). DXGI lists
    /// the adapter driving the display first, which on a hybrid laptop is the integrated GPU, so leaving the pick
    /// to DXGI ran the engine on the slower of two GPUs. Whether the factory offers <c>IDXGIFactory6</c> is passed
    /// in as a fact, so the rule is decided and tested off Windows like every other one here. An explicit value
    /// still wins, and one that cannot be honoured still lets DXGI pick, exactly as before.
    /// </para>
```

In the `EnvVarName` doc at line 133, change "Unset or blank leaves DXGI to pick." to "Unset or blank prefers the high-performance adapter."

Replace the `Choose` summary and signature (lines 170-176), and the `Default` case (lines 183-184):
```csharp
        /// <summary>
        /// Which adapter <paramref name="request"/> names in <paramref name="adapters"/>, with
        /// <paramref name="warning"/> set when the request could not be honoured and the default enumeration is
        /// being used instead. <paramref name="gpuPreferenceAvailable"/> is whether the factory offers
        /// <c>IDXGIFactory6</c>, and it decides the unset request and nothing else. Never throws for a bad
        /// request, by design.
        /// </summary>
        internal static D3D11AdapterChoice Choose(in D3D11AdapterRequest request,
            IReadOnlyList<D3D11AdapterInfo> adapters, bool gpuPreferenceAvailable, out string? warning)
```
```csharp
                case D3D11AdapterRequestKind.Default:
                    // IDXGIFactory6 arrived in Windows 10 1803. Without it the engine lets DXGI pick, as it always
                    // did, and that is not a warning: nothing was asked for that could not be given.
                    return gpuPreferenceAvailable ? D3D11AdapterChoice.HighPerformance : D3D11AdapterChoice.Default;
```

In the `IsSoftwareChoice` doc (lines 230-231), change "It answers false for `<see cref="D3D11AdapterChoiceKind.DefaultEnumeration"/>`, which is NOT a claim the adapter is hardware: nothing here knows which adapter DXGI picked." to "It answers false for `DefaultEnumeration` and `HighPerformance`, which is NOT a claim the adapter is hardware: nothing here knows which adapter DXGI picked or ranked first." The body is unchanged, since `HighPerformance` already falls to `return false` at line 241.

Replace the `Describe` doc and switch (lines 245-268). This also fixes the existing doc comment, which says the default line doesn't mention the variable when it does:
```csharp
        /// <summary>
        /// The INFO line naming which adapter the session ran on and why, logged through the existing
        /// <c>GPU adapter:</c> line's neighbourhood rather than replacing it. The unpinned cases name the lever,
        /// because they are the ones a reader chasing the wrong GPU needs to learn it from.
        /// </summary>
        internal static string Describe(in D3D11AdapterChoice choice, IReadOnlyList<D3D11AdapterInfo> adapters)
        {
            ArgumentNullException.ThrowIfNull(adapters);

            switch (choice.Kind)
            {
                case D3D11AdapterChoiceKind.WarpDriver:
                    return $"D3D11 adapter selection: WARP, the software rasterizer, from {EnvVarName}=warp. This "
                        + "is the rasterizer the committed Direct3D 11 goldens are baked on.";
                case D3D11AdapterChoiceKind.Enumerated:
                    string name = choice.Index >= 0 && choice.Index < adapters.Count
                        ? adapters[choice.Index].Description
                        : "an adapter that is no longer enumerated";
                    return $"D3D11 adapter selection: adapter {choice.Index} ('{name}'), from {EnvVarName}.";
                case D3D11AdapterChoiceKind.HighPerformance:
                    return "D3D11 adapter selection: the high-performance adapter IDXGIFactory6 ranks first, which "
                        + $"is the default. Set {EnvVarName}=warp|hardware|<index>|<name substring> to pin one.";
                default:
                    return "D3D11 adapter selection: DXGI's own choice, taken when this runtime offers no "
                        + "IDXGIFactory6 or a pinned request could not be honoured. Set "
                        + $"{EnvVarName}=warp|hardware|<index>|<name substring> to pin one.";
            }
        }

        /// <summary>The WARN when <c>IDXGIFactory6</c> is offered but hands back no high-performance adapter. The
        /// session then lets DXGI pick, as the engine did before the preference existed.</summary>
        internal static string HighPerformanceUnavailableWarning
            => "IDXGIFactory6 is offered but handed back no high-performance adapter. Letting DXGI pick, as the "
                + $"engine did before the preference existed. Set {EnvVarName} to pin an adapter.";

        /// <summary>The WARN when the high-performance adapter refuses a device, with the refusal's own message.
        /// The device is then created again the way the engine created it before the preference existed.</summary>
        internal static string HighPerformanceCreateFailedWarning(string reason)
            => $"The high-performance adapter refused a feature level 11_0 device ({reason}). Letting DXGI pick, "
                + $"as the engine did before the preference existed. Set {EnvVarName} to pin an adapter.";
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-d3d11-adapter && dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.D3D11AdapterSelectionTests|FullyQualifiedName~KhaozEngine.Tests.Gpu.D3D11DiagnosticsBoundaryTests|FullyQualifiedName~KhaozEngine.Tests.Gpu.D3D11ResourceModelTests"
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
```
Expected: all pass, including the two off-Windows "interop not loaded" facts.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterSelection.cs KhaozEngine.Render.Tests/Gpu/D3D11AdapterSelectionTests.cs KhaozEngine.Render.Tests/Gpu/D3D11DiagnosticsBoundaryTests.cs
git commit -m "gpu(d3d11): prefer the high-performance adapter in the selection policy" -- KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterSelection.cs KhaozEngine.Render.Tests/Gpu/D3D11AdapterSelectionTests.cs KhaozEngine.Render.Tests/Gpu/D3D11DiagnosticsBoundaryTests.cs
```

### Task E2: Windows glue, feature probe and docs   (item 6, branch `feature/frame-cost-d3d11-adapter`)

**Branch:** `feature/frame-cost-d3d11-adapter` (same worktree, after E1)

**Files:**
- Create: `KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterResolution.cs`. The choice-to-adapter resolution moves out of `D3D11GpuDevice` so the device and the probe share one copy.
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11DxgiQueries.cs` (summary at 11-15, two new members after 225)
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.Create.cs`
  - `CreateWindows` head at 66-84
  - remove `ResolveAdapterWindows` and `DriverTypeFor` at 343-369, add `CreateOnChoiceWindows` there
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11FeatureProbe.cs` (message at 55-56, 82-114)
- Docs:
  - `KhaozEngine.Gpu.D3D11/README.md` (71-74, 96-99, 1021-1022, 1028-1029, 1034-1036)
  - `docs/USING-KHAOZENGINE.md` (14374-14375)
  - `README.md` (line 19, two fragments)

**Interfaces:**
- Consumes: from E1, `D3D11AdapterSelection.Choose(..., bool gpuPreferenceAvailable, out string?)`, `D3D11AdapterChoiceKind.HighPerformance` and both warnings. From Vortice, `IDXGIFactory6.EnumAdapterByGpuPreference<IDXGIAdapter1>(int, GpuPreference, out IDXGIAdapter1?)` and `ComObject.QueryInterfaceOrNull<IDXGIFactory6>()`.
- Produces (all internal and `[SupportedOSPlatform("windows")]`):
  - `D3D11DxgiQueries.SupportsGpuPreferenceWindows(IDXGIFactory1) : bool`
  - `D3D11DxgiQueries.HighPerformanceAdapterWindows(IDXGIFactory1) : IDXGIAdapter1?`
  - `D3D11AdapterResolution.ChooseWindows(IDXGIFactory1, out IReadOnlyList<D3D11AdapterInfo>, out string?) : D3D11AdapterChoice`
  - `D3D11AdapterResolution.AdapterForWindows(IDXGIFactory1, in D3D11AdapterChoice, out string?) : IDXGIAdapter1?`
  - `D3D11AdapterResolution.DriverTypeFor(in D3D11AdapterChoice, IDXGIAdapter1?) : DriverType`

- [ ] **Step 1: Write the failing test**

None runs on this Mac (CI-only). E1's policy tests carry every rule. This task is Windows glue, and the only automated checks here are compilation and the off-Windows interop-load facts:
- CA1416 platform analysis runs at build time.
- `D3D11ResourceModelTests.OffWindows_LoadingEveryTypeInTheBackend_PullsInNoInterop` checks the new type holds no Vortice value-type field.

- [ ] **Step 2: Run it and confirm it fails**

Not applicable (CI-only). The `direct3d11-native` leg is the runtime check, and it pins WARP. See the risks list.

- [ ] **Step 3: Implement**

Create `KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterResolution.cs`:
```csharp
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION G2 ON A LIVE FACTORY: the policy's choice, and the adapter object it names. Shared by
    /// <see cref="D3D11GpuDevice"/> and <see cref="D3D11FeatureProbe"/>, because the probe exists to answer for the
    /// adapter the device will actually run on, and two copies of this resolution are how those two would drift.
    /// <para>
    /// Everything here is Windows glue over <see cref="D3D11AdapterSelection"/>, which holds every rule and runs on
    /// macOS. The one thing the policy cannot see is decided here: a high-performance adapter DXGI will not hand
    /// over falls back to DXGI's own pick.
    /// </para>
    /// <para>
    /// NOTHING HERE LOGS. Each member returns its warning, so the device logs it once and the probe, which runs
    /// before the device and answers a settings screen, does not put every adapter warning in the log twice.
    /// There are no fields, so loading this type off Windows resolves nothing from the interop.
    /// </para>
    /// </summary>
    internal static class D3D11AdapterResolution
    {
        /// <summary>
        /// The choice for this machine: <c>KE_D3D11_ADAPTER</c> from the environment, decided over the factory's
        /// enumeration and over whether it offers <c>IDXGIFactory6</c>. <paramref name="warning"/> is the policy's
        /// own, set when an explicit request could not be honoured.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static D3D11AdapterChoice ChooseWindows(IDXGIFactory1 factory,
            out IReadOnlyList<D3D11AdapterInfo> adapters, out string? warning)
        {
            adapters = D3D11DxgiQueries.DescribeAdaptersWindows(factory);
            return D3D11AdapterSelection.Choose(D3D11AdapterSelection.FromEnvironment(), adapters,
                D3D11DxgiQueries.SupportsGpuPreferenceWindows(factory), out warning);
        }

        /// <summary>
        /// The adapter object <paramref name="choice"/> names, or null for DXGI's own pick and for WARP, which are
        /// reached through the driver type rather than through an adapter. Null with <paramref name="warning"/>
        /// set when the named adapter could not be fetched, which falls back to DXGI's own pick exactly as every
        /// unsatisfiable request does. The caller owns the adapter and releases it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static IDXGIAdapter1? AdapterForWindows(IDXGIFactory1 factory, in D3D11AdapterChoice choice,
            out string? warning)
        {
            warning = null;
            switch (choice.Kind)
            {
                case D3D11AdapterChoiceKind.Enumerated:
                {
                    // Re-fetched at its index because the enumeration handed the policy plain descriptions and
                    // released its own objects. An adapter can be removed between the two, which is not a fault.
                    SharpGen.Runtime.Result result = factory.EnumAdapters1(choice.Index, out IDXGIAdapter1? adapter);
                    if (result.Success && adapter is not null) return adapter;

                    adapter?.Dispose();
                    warning = $"Adapter {choice.Index} was enumerated a moment ago and is no longer there, so "
                        + $"{D3D11AdapterSelection.EnvVarName} could not be honoured after all. Letting DXGI pick.";
                    return null;
                }

                case D3D11AdapterChoiceKind.HighPerformance:
                {
                    IDXGIAdapter1? preferred = D3D11DxgiQueries.HighPerformanceAdapterWindows(factory);
                    if (preferred is null) warning = D3D11AdapterSelection.HighPerformanceUnavailableWarning;
                    return preferred;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// The driver type that goes with <paramref name="adapter"/>. Direct3D requires <c>DriverType.Unknown</c>
        /// when an adapter is supplied and refuses one alongside <c>Hardware</c> or <c>Warp</c>, so the two halves
        /// are one decision rather than two arguments a caller could pair up wrongly. A choice whose adapter could
        /// not be fetched lands on <c>Hardware</c> with a null adapter, which is DXGI's own pick.
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static DriverType DriverTypeFor(in D3D11AdapterChoice choice, IDXGIAdapter1? adapter)
        {
            if (adapter is not null) return DriverType.Unknown;
            return choice.Kind == D3D11AdapterChoiceKind.WarpDriver ? DriverType.Warp : DriverType.Hardware;
        }
    }
}
```

In `D3D11DxgiQueries.cs`, change "plus the adapter enumeration." (line 13) to "plus the adapter enumeration and the two `IDXGIFactory6` questions the high-performance default asks." Then insert after `DescribeAdaptersWindows` (after line 225):
```csharp
        /// <summary>
        /// Whether the factory offers <c>IDXGIFactory6</c>, the fact <see cref="D3D11AdapterSelection.Choose"/>
        /// takes for the unset request. Windows 10 1803 and later answer yes. A failed query answers no, which keeps
        /// the engine on DXGI's own pick, the behaviour it had before the preference existed.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static bool SupportsGpuPreferenceWindows(IDXGIFactory1 factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            try
            {
                using IDXGIFactory6? factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
                return factory6 is not null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The adapter <c>IDXGIFactory6.EnumAdapterByGpuPreference(0, HighPerformance)</c> ranks first, external
        /// then discrete then integrated, or null when the factory has no <c>IDXGIFactory6</c> or the call fails.
        /// The caller owns the adapter and releases it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static IDXGIAdapter1? HighPerformanceAdapterWindows(IDXGIFactory1 factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            try
            {
                using IDXGIFactory6? factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
                if (factory6 is null) return null;

                SharpGen.Runtime.Result result = factory6.EnumAdapterByGpuPreference(0,
                    GpuPreference.HighPerformance, out IDXGIAdapter1? adapter);
                if (result.Success && adapter is not null) return adapter;

                adapter?.Dispose();
                return null;
            }
            catch
            {
                // A preference is an optimisation, so an unanswerable query falls back rather than failing device
                // creation over which GPU would have been faster.
                return null;
            }
        }
```

In `D3D11GpuDevice.Create.cs`, replace lines 66-84 (from `uint creationFlags` through the `CreateDeviceWindows(...)` call inside `try`) with:
```csharp
            uint creationFlags = ResolveCreationFlags();

            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            D3D11AdapterChoice choice = D3D11AdapterResolution.ChooseWindows(factory,
                out IReadOnlyList<D3D11AdapterInfo> adapters, out string? adapterWarning);
            if (adapterWarning != null) log.Warn(adapterWarning);
            log.Info(D3D11AdapterSelection.Describe(choice, adapters));

            IDXGIAdapter1? requested = D3D11AdapterResolution.AdapterForWindows(factory, choice,
                out string? resolveWarning);
            if (resolveWarning != null) log.Warn(resolveWarning);
            IDXGIAdapter? deviceAdapter = null;
            ID3D11Device? device = null;
            ID3D11DeviceContext? immediate = null;
            ID3D11DeviceContext1? context = null;

            try
            {
                device = CreateOnChoiceWindows(choice, ref requested, ref creationFlags, out immediate);
```

Replace lines 343-369 (`ResolveAdapterWindows` and `DriverTypeFor`, both moved to `D3D11AdapterResolution`) with:
```csharp
        // THE HIGH-PERFORMANCE DEFAULT NEVER COSTS A SESSION. When the adapter IDXGIFactory6 ranked first refuses a
        // device, the device is created again with a null adapter and DriverType.Hardware, which is DXGI's own pick
        // and what the engine did before the preference existed. Only the default takes this retry. An adapter
        // pinned through KE_D3D11_ADAPTER fails loudly as it always has, because a pin that silently moved would
        // defeat the reason it was set.
        //
        // The adapter travels BY REF so the retry can release it and null the caller's reference, which keeps the
        // caller's finally from releasing it a second time.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static ID3D11Device CreateOnChoiceWindows(in D3D11AdapterChoice choice, ref IDXGIAdapter1? adapter,
            ref uint flags, out ID3D11DeviceContext immediateContext)
        {
            DriverType driverType = D3D11AdapterResolution.DriverTypeFor(choice, adapter);
            if (choice.Kind != D3D11AdapterChoiceKind.HighPerformance || adapter is null)
                return CreateDeviceWindows(adapter, driverType, ref flags, out immediateContext);

            try
            {
                return CreateDeviceWindows(adapter, driverType, ref flags, out immediateContext);
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException)
            {
                log.Warn(D3D11AdapterSelection.HighPerformanceCreateFailedWarning(ex.Message));
                adapter.Dispose();
                adapter = null;
                return CreateDeviceWindows(null, DriverType.Hardware, ref flags, out immediateContext);
            }
        }
```

In `D3D11FeatureProbe.cs`, change the message at lines 55-56 to:
```csharp
                return "no Direct3D 11 feature level 11_0 device could be created, on the adapter the device "
                    + "would use, on the default hardware adapter or on WARP";
```

Replace lines 82-114 (`CreateProbeDeviceWindows` and `TryCreate`) with:
```csharp
        /// <summary>
        /// A throwaway device on the adapter the device itself will be created on, then on the default hardware
        /// adapter, then on WARP, or null when none answers. The first attempt resolves the adapter through
        /// <see cref="D3D11AdapterResolution"/>, the policy and glue <see cref="D3D11GpuDevice"/> uses, so the probe
        /// answers for the adapter the session will run on, <c>KE_D3D11_ADAPTER</c> and the high-performance
        /// default included. WARP counts as a yes deliberately: it is the rasterizer the committed Direct3D 11
        /// goldens are baked on and the one CI pins, so a Windows machine with no usable GPU can still run and
        /// verify this backend. The two later attempts can repeat the first when the choice was DXGI's own pick or
        /// WARP, which costs one failed creation on a machine that is already failing.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static Vortice.Direct3D11.ID3D11Device? CreateProbeDeviceWindows()
        {
            if (TryCreateOnChosenAdapterWindows(out Vortice.Direct3D11.ID3D11Device? chosen)) return chosen;
            if (TryCreate(IntPtr.Zero, Vortice.Direct3D.DriverType.Hardware, out Vortice.Direct3D11.ID3D11Device? hardware))
                return hardware;
            return TryCreate(IntPtr.Zero, Vortice.Direct3D.DriverType.Warp, out Vortice.Direct3D11.ID3D11Device? warp)
                ? warp
                : null;
        }

        // The first attempt, on the adapter the device will use. Its warnings are dropped on purpose, because the
        // device logs the same ones when it is created. Any failure here, including a factory that cannot be
        // created, answers false and leaves the two attempts after it to run.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static bool TryCreateOnChosenAdapterWindows(out Vortice.Direct3D11.ID3D11Device? device)
        {
            device = null;
            try
            {
                using Vortice.DXGI.IDXGIFactory1 factory =
                    Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                D3D11AdapterChoice choice = D3D11AdapterResolution.ChooseWindows(factory, out _, out _);
                using Vortice.DXGI.IDXGIAdapter1? adapter =
                    D3D11AdapterResolution.AdapterForWindows(factory, choice, out _);
                return TryCreate(adapter?.NativePointer ?? IntPtr.Zero,
                    D3D11AdapterResolution.DriverTypeFor(choice, adapter), out device);
            }
            catch
            {
                device?.Dispose();
                device = null;
                return false;
            }
        }

        // One creation attempt. DeviceCreationFlags.None on purpose: the debug layer is a separate, env-gated
        // diagnostic and requiring it here would answer "unsupported" on every machine without the Windows
        // graphics tools installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static bool TryCreate(IntPtr adapter, Vortice.Direct3D.DriverType driverType,
            out Vortice.Direct3D11.ID3D11Device? device)
        {
            SharpGen.Runtime.Result result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                adapter, driverType, Vortice.Direct3D11.DeviceCreationFlags.None, _featureLevels, out device);

            if (result.Success && device is not null) return true;

            // A partial success would leak the device: the call can hand one back on a non-Success HRESULT
            // (S_FALSE is the documented case), and nothing else here will ever release it.
            device?.Dispose();
            device = null;
            return false;
        }
```

**Docs:**

`KhaozEngine.Gpu.D3D11/README.md` lines 72-74, from "The probe" onward, become:
```markdown
The probe creates a throwaway feature level 11_0 device on the adapter the device itself would be created on,
resolved through the same `KE_D3D11_ADAPTER` policy described below, then falls back to the default hardware
adapter and to WARP, and reads `D3D11_FEATURE_D3D11_OPTIONS` off it. Two features are hard requirements:
```

Lines 96-99 (step 1) become:
```markdown
1. **The adapter.** `KE_D3D11_ADAPTER` is parsed, the DXGI enumeration is described, and the choice is logged as
   an INFO line naming which adapter ran and why. Unset prefers the adapter
   `IDXGIFactory6.EnumAdapterByGpuPreference` ranks first for high performance, so a hybrid laptop runs on its
   discrete GPU rather than on the integrated one DXGI lists first. `warp` resolves through `DriverType.Warp`
   rather than through the enumeration, so the one value CI pins is the one value that cannot fail to resolve. A
   request that cannot be honoured WARNs and falls back to letting DXGI pick, and so does a high-performance adapter
   that cannot be fetched or refuses the device.
```

Lines 1021-1022 become the following, with line 1023 onward kept as is:
```markdown
**`KE_D3D11_ADAPTER=warp|hardware|<index>|<name substring>` pins the adapter (G2).** Unset prefers the
high-performance adapter, the one `IDXGIFactory6.EnumAdapterByGpuPreference(0, HighPerformance)` ranks first, and
creates the device on it with `DriverType.Unknown`. A runtime without `IDXGIFactory6`, which is Windows 10 before
version 1803, lets DXGI pick as the engine always did, and a preferred adapter that cannot be fetched or refuses
the device WARNs and lets DXGI pick too. `hardware` keeps its enumeration-order meaning, so on a hybrid laptop it
can name the integrated GPU while unset names the discrete one. A request that cannot be honoured WARNs and falls
back to letting DXGI pick, never fails, and the warning
```

At lines 1028-1029, change "and only the enumeration itself is Windows-only." to "and only the enumeration and the `IDXGIFactory6` query are Windows-only."

At lines 1035-1036, change "so it is right on the default path where nothing in the engine picked the adapter at all." to "so it is right on every path, including DXGI's own pick where nothing in the engine chose the adapter."

`docs/USING-KHAOZENGINE.md` lines 14374-14375 become:
```markdown
Unset prefers the high-performance adapter, the one `IDXGIFactory6.EnumAdapterByGpuPreference` ranks first, so a
hybrid laptop runs on its discrete GPU rather than on the integrated one DXGI lists first. On Windows 10 before
version 1803, which has no `IDXGIFactory6`, unset lets DXGI pick as the engine always did, and a preferred adapter
that cannot be fetched or refuses the device WARNs and lets DXGI pick too. `hardware` keeps its enumeration-order
meaning, so on a hybrid laptop it can name the integrated GPU. **A request that cannot be honoured WARNs
and falls back to letting DXGI pick, and never fails the run.** The warning names what was typed AND lists the
```

`README.md` line 19 has two replacements:
- "creates a throwaway feature level 11_0 device (hardware, then WARP) and checks" becomes "creates a throwaway feature level 11_0 device (on the adapter the device will use, then the default hardware adapter, then WARP) and checks".
- "pins which adapter the device is created on (an unhonourable request WARNs" becomes "pins which adapter the device is created on, and unset prefers the high-performance adapter `IDXGIFactory6` ranks first (an unhonourable request WARNs".

- [ ] **Step 4: Run the tests and confirm they pass**

On the Mac:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-d3d11-adapter && dotnet build KhaozEngine.Gpu.D3D11/KhaozEngine.Gpu.D3D11.csproj -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gpu.D3D11"
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree && bash scripts/check-doc-versions.sh
dotnet build KhaozEngine.slnx -c Release
```
Expected:
- The build is clean with warnings as errors, including CA1416.
- Every D3D11 fact passes, including `OffWindows_LoadingEveryTypeInTheBackend_PullsInNoInterop`.
- All guards pass.

CI-only: the `direct3d11-native` leg of `.github/workflows/cross-platform-gpu.yml` runs with `KE_D3D11_ADAPTER=warp`. The device path stays `WarpDriver` and the probe now tries WARP first. The golden family must stay unchanged.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterResolution.cs KhaozEngine.Gpu.D3D11/Internal/D3D11DxgiQueries.cs KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.Create.cs KhaozEngine.Gpu.D3D11/Internal/D3D11FeatureProbe.cs KhaozEngine.Gpu.D3D11/README.md docs/USING-KHAOZENGINE.md README.md
git commit -m "gpu(d3d11): create on the high-performance adapter by default" -- KhaozEngine.Gpu.D3D11/Internal/D3D11AdapterResolution.cs KhaozEngine.Gpu.D3D11/Internal/D3D11DxgiQueries.cs KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.Create.cs KhaozEngine.Gpu.D3D11/Internal/D3D11FeatureProbe.cs KhaozEngine.Gpu.D3D11/README.md docs/USING-KHAOZENGINE.md README.md
```


### Decisions and risks

- **The new default path never runs in CI.** The only Windows leg pins `d3d11Adapter: "warp"` (`.github/workflows/cross-platform-gpu.yml:512`, `:980`) and `ci.yml` is Linux-only (`ci.yml:29`, `:150`). So `EnumAdapterByGpuPreference` and the create-failure retry are compiled but never run anywhere automated. The spec's "The D3D11 CI leg" test therefore covers compilation and the probe's WARP path only. You should decide between a manual hybrid-laptop check and a future Windows fact with the variable unset, which is outside the spec.
- **A missing `IDXGIFactory6` gets INFO, not WARN.** The spec says "any failure falls back ... with a logged warning". I read an older runtime as an input, not a failure, so it goes through `Describe`. Failures on a factory that has the interface do WARN.
- **Unhonourable explicit values keep today's fallback.** They still get `DefaultEnumeration` (`D3D11AdapterSelection.cs:198-214`), not the new default. The spec is silent here.
- **Only the default gets the create-failure retry.** An explicitly pinned adapter that refuses a device still throws (`D3D11GpuDevice.Create.cs:404-412`), as today.
- **The probe now honours explicit `KE_D3D11_ADAPTER` values.** Today it ignores them and tries hardware then WARP (`D3D11FeatureProbe.cs:90-95`). "Resolves the adapter the same way" implies the change. On the CI leg the probe now tries WARP first.
- **`hardware` can now pick a slower GPU than unset.** It keeps its enumeration-order meaning (`D3D11AdapterSelection.cs:193-199`), so on a hybrid laptop it can name the integrated GPU. This is documented in the doc edits above.
- **Unverified: Windows per-app graphics settings.** I haven't confirmed how a user's per-app "Power saving" setting interacts with an explicit `HighPerformance` preference.
- **A stale doc comment gets fixed.** The `Describe` comment (`D3D11AdapterSelection.cs:247-249`) says the default line doesn't name the variable, but the code at 265-266 does. E1 corrects it.


---

## Item 7: ChatBox visible rows (#1116)


**Spec vs code, stated up front.**
- The spec's "Today" matches the code: `Draw` loops every row at `ChatBox.cs:176-177`, and `DrawRow` slices and measures at 196-201.
- **Deviation 1: the draw test goes through an internal hook.** Gui tests have no headless sprite batch. `SpriteBatch` is built only from an `IGpuDevice` (`KhaozEngine.Render2D/SpriteBatch.cs:167`), and `TabBarFlatDrawTests.cs:13-17` records this limit. The plan therefore adds an internal `IChatRowSink` that the real `Draw` also uses, and the tests record through it. `Draw` itself (frame, scissor, composer) is still not headless-testable.
- **Deviation 2: one extra row either side.** The plan draws one row past each end of the viewport, not strictly the rows whose bounds meet it.
  - Glyphs are placed at `baseline + g.YOff * k` (`SpriteBatch.cs:577`), so an accent above the ascent or a descender below the line can leave the row box.
  - The scissor rounds to whole pixels (`SpriteBatch.Scissor.cs:38-39`).
  - An exact cut would add a third pixel difference to a round that promises two. `VisibleRowMargin = 0` gives the spec's exact wording.

### Task F1: Route history rows through an internal row sink   (item 7, branch `feature/frame-cost-chatbox`)

**Branch:** `feature/frame-cost-chatbox` (worktree created from `feature/frame-cost-round`)
Setup:
```bash
git -C /Users/antonio/KhaozEngine worktree add .worktrees/frame-cost-chatbox -b feature/frame-cost-chatbox feature/frame-cost-round
mkdir -p /Users/antonio/KhaozEngine/.worktrees/frame-cost-chatbox/local-feed
```

**Files:**
- Create: `KhaozEngine.Gui/Chat/ChatRowSink.cs`
- Modify: `KhaozEngine.Gui/Chat/ChatBox.cs` (`Draw` at 161-182, `DrawRow` at 184-203). This is a behaviour-preserving refactor.
- Test: `KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs` (new, no collection, raw text only)

**Interfaces:**
- Produces:
  - `internal interface IChatRowSink { void DrawText(string text, Vector2 position, Color color); }`
  - `internal readonly struct SpriteBatchChatRowSink : IChatRowSink`
  - `internal void ChatBox.DrawHistoryRows<TSink>(ref TSink sink) where TSink : IChatRowSink`
- Consumes: `SpriteBatch.DrawString(SpriteFont, string, Vector2, Color)`, `ITextMeasurer`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui.Chat;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// What the chat history sends to the sprite batch, run by run. A <see cref="SpriteBatch"/> needs a GPU device, so
/// the rows reach it through the internal <see cref="IChatRowSink"/> the real draw also uses, and a recording sink
/// stands in for the batch here. Raw text only, so no ambient catalog is read or written.
/// </summary>
public sealed class ChatBoxRowDrawTests
{
    const string Stamp = "[11:07] ";

    static readonly Rect BoxBounds = new(100f, 100f, 300f, 180f);
    static readonly TimeZoneInfo Sydney = TimeZoneInfo.CreateCustomTimeZone(
        "Australia/Sydney",
        TimeSpan.FromHours(10),
        "Australian Eastern Standard Time",
        "Australian Eastern Standard Time");
    static readonly ChatBoxTheme Theme = new()
    {
        OrdinaryText = new Vector4(0.7f, 0.7f, 0.7f, 1f),
        OwnText = new Vector4(0.9f, 0.7f, 0.4f, 1f),
        SystemText = new Vector4(0.5f, 0.8f, 0.9f, 1f),
        TimestampText = new Vector4(0.5f, 0.5f, 0.5f, 1f),
    };
    static readonly FixedMeasurer Narrow = new(8f);

    [Fact]
    public void A_timestamped_row_draws_the_stamp_then_the_message_at_the_measured_offset()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a"));
        history.Add(Entry(At(8), "b", isOwn: true));
        history.Add(Entry(At(9), "c", kind: ChatEntryKind.System));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // Rows start at the viewport's top left (108, 108) and step by the 16 px line plus the 2 px spacing. The
        // message starts after the eight-character stamp, 64 px at 8 px a character.
        Assert.Equal(new[]
        {
            new Drawn("[11:07] ", new Vector2(108f, 108f), (Color)Theme.TimestampText),
            new Drawn("Alice: hello", new Vector2(172f, 108f), (Color)Theme.OrdinaryText),
            new Drawn("[11:08] ", new Vector2(108f, 126f), (Color)Theme.TimestampText),
            new Drawn("Alice: hello", new Vector2(172f, 126f), (Color)Theme.OwnText),
            new Drawn("[11:09] ", new Vector2(108f, 144f), (Color)Theme.TimestampText),
            new Drawn("Alice: hello", new Vector2(172f, 144f), (Color)Theme.SystemText),
        }, sink.Runs);
    }

    [Fact]
    public void A_row_without_a_stamp_draws_its_whole_line_in_the_message_colour()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a", isOwn: true));
        ChatBox box = Box(history);
        box.ShowTimestamps = false;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(new[] { new Drawn("Alice: hello", new Vector2(108f, 108f), (Color)Theme.OwnText) }, sink.Runs);
    }

    [Fact]
    public void A_wrapped_entry_draws_the_stamp_once_and_its_other_lines_whole()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a", content: new string('x', 40), author: null));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.True(box.CachedLines.Count > 1);
        Assert.Equal(ExpectedRuns(box), Texts(sink));
    }

    // The runs one entry's cached lines should produce: its first line split after the stamp when it starts with
    // one, every other line whole, and empty runs skipped because drawing them paints nothing.
    static List<string> ExpectedRuns(ChatBox box)
    {
        var runs = new List<string>();
        for (int i = 0; i < box.CachedLines.Count; i++)
        {
            string line = box.CachedLines[i];
            if (i == 0 && line.StartsWith(Stamp, StringComparison.Ordinal))
            {
                runs.Add(Stamp);
                if (line.Length > Stamp.Length) runs.Add(line[Stamp.Length..]);
            }
            else if (line.Length > 0)
            {
                runs.Add(line);
            }
        }
        return runs;
    }

    static List<string> Texts(RecordingSink sink) => sink.Runs.ConvertAll(run => run.Text);

    static DateTimeOffset At(int minute) => new(2026, 9, 6, 1, minute, 0, TimeSpan.Zero);

    static ChatBox Box(ChatHistory history, Rect? bounds = null) => new(history, bounds ?? BoxBounds)
    {
        Theme = Theme,
    };

    static ChatEntry Entry(
        DateTimeOffset timestamp,
        string sourceKey,
        ChatEntryKind kind = ChatEntryKind.Ordinary,
        bool isOwn = false,
        string content = "hello",
        string? author = "Alice") => new(
            timestamp,
            sourceKey,
            author is null ? null : LocalizedText.Raw(author),
            LocalizedText.Raw(content),
            content,
            kind,
            isOwn);

    readonly record struct Drawn(string Text, Vector2 Position, Color Color);

    sealed class RecordingSink : IChatRowSink
    {
        public List<Drawn> Runs { get; } = new();

        public void DrawText(string text, Vector2 position, Color color) => Runs.Add(new Drawn(text, position, color));
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        readonly float _advance;

        public FixedMeasurer(float advance) => _advance = advance;

        public float LineHeight => 16f;

        public Vector2 Measure(string text) => new(text.Length * _advance, LineHeight);
    }
}
```
The `KhaozEngine.Windowing` using is there for F2's scroll helper. If it's unused in F1 and triggers IDE0005 as an error, add it in F2 instead.

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-chatbox && dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gui.ChatBox"
```
Expected: the build fails with `error CS0246: The type or namespace name 'IChatRowSink' could not be found` and `error CS1061: 'ChatBox' does not contain a definition for 'DrawHistoryRows'`.

- [ ] **Step 3: Implement**

Create `KhaozEngine.Gui/Chat/ChatRowSink.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui.Chat;

/// <summary>
/// Where <see cref="ChatBox"/> sends each run of history text. The frame draw passes
/// <see cref="SpriteBatchChatRowSink"/>. Tests pass a recording or counting sink, because a
/// <see cref="SpriteBatch"/> needs a GPU device and which rows a frame draws is the thing they prove.
/// </summary>
internal interface IChatRowSink
{
    /// <summary>Draw <paramref name="text"/> with its top-left at <paramref name="position"/>.</summary>
    void DrawText(string text, Vector2 position, Color color);
}

/// <summary>The sink the frame draw uses, a pass-through to <c>SpriteBatch.DrawString</c>. A struct, so the
/// generic row loop runs without boxing it.</summary>
internal readonly struct SpriteBatchChatRowSink : IChatRowSink
{
    readonly SpriteBatch _batch;
    readonly SpriteFont _font;

    internal SpriteBatchChatRowSink(SpriteBatch batch, SpriteFont font)
    {
        _batch = batch;
        _font = font;
    }

    public void DrawText(string text, Vector2 position, Color color) => _batch.DrawString(_font, text, position, color);
}
```

In `ChatBox.cs`, replace lines 171-203 (the font block of `Draw`, plus `DrawRow`):
```csharp
        SpriteFont? font = Font;
        if (font != null)
        {
            RefreshLayout(font, TimeZoneInfo.Local);
            _scroll.BeginClip(batch);
            var sink = new SpriteBatchChatRowSink(batch, font);
            DrawHistoryRows(ref sink);
            _scroll.EndClip(batch);
        }

        Composer.Draw(batch, white);
    }

    // The history rows of this frame, run by run, into sink. Draw passes the sprite batch and tests pass a
    // recorder, because a sprite batch needs a GPU device. The layout must already be refreshed with the draw font.
    internal void DrawHistoryRows<TSink>(ref TSink sink) where TSink : IChatRowSink
    {
        for (int i = 0; i < _rows.Count; i++)
            DrawRow(ref sink, i, _rows[i]);
    }

    void DrawRow<TSink>(ref TSink sink, int index, CachedRow row) where TSink : IChatRowSink
    {
        Rect bounds = RowBounds(index);
        var position = new Vector2(MathF.Floor(bounds.X), MathF.Floor(bounds.Y));
        var messageColor = (Color)SelectColor(row.Entry, Theme);

        if (row.TimestampLength <= 0)
        {
            sink.DrawText(row.Text, position, messageColor);
            return;
        }

        string timestamp = row.Text[..row.TimestampLength];
        sink.DrawText(timestamp, position, (Color)Theme.TimestampText);
        if (row.TimestampLength == row.Text.Length) return;

        // Rows exist only after RefreshLayout, and Draw refreshes with the draw font first, so the cached
        // measurer is the font the stamp is drawn in.
        position.X += _cachedMeasurer!.Measure(timestamp).X;
        sink.DrawText(row.Text[row.TimestampLength..], position, messageColor);
    }
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-chatbox && dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gui.ChatBox"
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
```
Expected: all `ChatBoxTests` and `ChatBoxRowDrawTests` pass.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gui/Chat/ChatRowSink.cs KhaozEngine.Gui/Chat/ChatBox.cs KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs
git commit -m "gui(chat): route history rows through an internal row sink" -- KhaozEngine.Gui/Chat/ChatRowSink.cs KhaozEngine.Gui/Chat/ChatBox.cs KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs
```

### Task F2: Draw only visible rows from cached runs   (item 7, branch `feature/frame-cost-chatbox`)

**Branch:** `feature/frame-cost-chatbox` (same worktree, after F1)

**Files:**
- Modify: `KhaozEngine.Gui/Chat/ChatBox.cs`
  - constants at 18-21
  - the F1 `DrawHistoryRows` and `DrawRow`
  - `RowBounds` at 205-215
  - the `RefreshLayout` row loop at 274-283
  - `CachedRow` at 326
  - ends at about 370 lines, not baselined, under the 800 cap
- Test: `KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs` (append)
- Test: `KhaozEngine.Gui.Tests/Gui/ChatBoxDrawAllocationTests.cs` (new, `[Collection("AllocSensitive")]`). It's a separate class because `ChatBoxTests` sits in `AmbientLocalization` (`ChatBoxTests.cs:14`), and a class can't join two collections.
- Docs: none. `KhaozEngine.Gui/README.md:348-355` and `docs/USING-KHAOZENGINE.md:1528-1560` describe behaviour, which doesn't change. The release commit's sweep covers #1116.

**Interfaces:**
- Consumes: `IChatRowSink` and `DrawHistoryRows` from F1. From `ScrollablePanel`: `Stride`, `ItemHeight`, `ItemSpacing`, `ScrollOffset`, `ContentBounds`, `ItemBounds`.
- Produces:
  - `readonly record struct CachedRow(ChatEntry Entry, string TimestampText, string MessageText, float MessageX)`
  - private `void VisibleRows(out int first, out int end)`
  - private `float SparseSpace()`
  - `const int VisibleRowMargin = 1`

- [ ] **Step 1: Write the failing test**

Append to `ChatBoxRowDrawTests` after the F1 facts. `Viewport` is a new static field:
```csharp
    // The history viewport of BoxBounds: inset by the 8 px padding, less the 30 px composer and its 6 px gap.
    static readonly Rect Viewport = new(108f, 108f, 284f, 128f);
    static readonly FixedMeasurer Wide = new(10f);

    [Fact]
    public void A_full_history_draws_only_the_rows_on_screen()
    {
        ChatBox box = Box(Numbered(100));
        box.ShowTimestamps = false;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // A new layout starts scrolled to the newest row, so the viewport shows rows 93 to 99 and the one-row
        // margin adds 92. Row 92 ends exactly at the viewport top, which counts as outside.
        Assert.Equal(ExpectedVisible(box, 100), Texts(sink));
        Assert.Equal("message 92", sink.Runs[0].Text);
        Assert.Equal("message 99", sink.Runs[^1].Text);
    }

    [Theory]
    [InlineData(ChatHistoryAlignment.Top, 1f)]
    [InlineData(ChatHistoryAlignment.Top, 10f)]
    [InlineData(ChatHistoryAlignment.Top, 55f)]
    [InlineData(ChatHistoryAlignment.Top, 200f)]
    [InlineData(ChatHistoryAlignment.Bottom, 1f)]
    [InlineData(ChatHistoryAlignment.Bottom, 55f)]
    public void Scrolling_mid_history_draws_only_the_rows_meeting_the_viewport(ChatHistoryAlignment alignment,
        float notches)
    {
        ChatBox box = Box(Numbered(100));
        box.ShowTimestamps = false;
        box.HistoryAlignment = alignment;
        box.RefreshLayout(Narrow, Sydney);
        ScrollUp(box, notches);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(ExpectedVisible(box, 100), Texts(sink));
        Assert.InRange(sink.Runs.Count, 7, 10);
    }

    [Fact]
    public void A_partly_visible_row_at_either_edge_is_drawn()
    {
        ChatBox box = Box(Numbered(100));
        box.ShowTimestamps = false;
        box.RefreshLayout(Narrow, Sydney);
        ScrollUp(box, 1f);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // One 30 px notch up from the bottom leaves row 91 cut by the viewport top (104 to 120) and row 98 cut by
        // the viewport bottom (230 to 246).
        Assert.Equal(104f, box.RowBounds(91).Y);
        Assert.Equal(230f, box.RowBounds(98).Y);
        List<string> texts = Texts(sink);
        Assert.Contains("message 91", texts);
        Assert.Contains("message 98", texts);
    }

    [Theory]
    [InlineData(0, ChatHistoryAlignment.Top)]
    [InlineData(0, ChatHistoryAlignment.Bottom)]
    [InlineData(1, ChatHistoryAlignment.Top)]
    [InlineData(1, ChatHistoryAlignment.Bottom)]
    [InlineData(3, ChatHistoryAlignment.Top)]
    [InlineData(3, ChatHistoryAlignment.Bottom)]
    public void A_history_shorter_than_the_viewport_draws_every_row(int rows, ChatHistoryAlignment alignment)
    {
        ChatBox box = Box(Numbered(rows));
        box.ShowTimestamps = false;
        box.HistoryAlignment = alignment;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(Names(0, rows), Texts(sink));
    }

    [Fact]
    public void A_nearly_full_bottom_aligned_history_draws_every_row()
    {
        ChatBox box = Box(Numbered(8), BoxBounds with { Height = 195f });
        box.ShowTimestamps = false;
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(Names(0, 8), Texts(sink));
    }

    [Fact]
    public void A_font_change_remeasures_the_message_offset()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a"));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);
        var before = new RecordingSink();
        box.DrawHistoryRows(ref before);

        box.RefreshLayout(Wide, Sydney);
        var after = new RecordingSink();
        box.DrawHistoryRows(ref after);

        // The eight-character stamp is 64 px at 8 px a character and 80 px at 10.
        Assert.Equal(new Vector2(172f, 108f), before.Runs[1].Position);
        Assert.Equal(new Vector2(188f, 108f), after.Runs[1].Position);
    }

    [Fact]
    public void A_width_change_resplits_the_rows_the_draw_sends()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a", content: "the quick brown fox jumps over the lazy dog"));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);
        int wideLines = box.CachedLines.Count;

        box.Bounds = BoxBounds with { Width = 136f };   // 120 px of text, fifteen characters a row
        box.RefreshLayout(Narrow, Sydney);
        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.True(box.CachedLines.Count > wideLines);
        Assert.Equal(ExpectedRuns(box), Texts(sink));
    }

    // The rows whose bounds meet the viewport, grown by one row at each end and clamped to the history, which is
    // the range the draw promises. Computed from RowBounds, so it carries the same sparse-space offset.
    static List<string> ExpectedVisible(ChatBox box, int rows)
    {
        int first = -1, last = -1;
        for (int i = 0; i < rows; i++)
        {
            Rect row = box.RowBounds(i);
            if (row.Bottom <= Viewport.Y || row.Y >= Viewport.Bottom) continue;
            if (first < 0) first = i;
            last = i;
        }
        return first < 0 ? new List<string>() : Names(Math.Max(0, first - 1), Math.Min(rows, last + 2));
    }

    static List<string> Names(int first, int end)
    {
        var names = new List<string>();
        for (int i = first; i < end; i++) names.Add($"message {i}");
        return names;
    }

    static ChatHistory Numbered(int count)
    {
        var history = new ChatHistory(Math.Max(1, count));
        for (int i = 0; i < count; i++)
            history.Add(Entry(DateTimeOffset.UnixEpoch, $"source-{i}", content: $"message {i}", author: null));
        return history;
    }

    // One wheel frame over the history. A positive delta scrolls up by 30 px a notch.
    static void ScrollUp(ChatBox box, float notches)
    {
        var pointer = new Pointer();
        var input = new InputState(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            new Vector2(120f, 120f), Vector2.Zero, notches, 960, 540);
        pointer.Update(input);
        box.Update(pointer, input, 0.016f);
    }
```

Create `KhaozEngine.Gui.Tests/Gui/ChatBoxDrawAllocationTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui.Chat;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// A chat box with a full scrollback used to slice every timestamped row into two new strings and measure the
/// stamp on every frame, for every row the scissor then discarded
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1116). A steady frame now sends the cached runs of the rows on
/// screen and allocates nothing. The rows go through the internal sink the real draw uses, because a sprite batch
/// needs a GPU device.
/// </summary>
[Collection("AllocSensitive")]
public sealed class ChatBoxDrawAllocationTests
{
    static readonly FixedMeasurer Font = new();

    [Fact]
    public void Drawing_a_steady_full_history_allocates_nothing()
    {
        var history = new ChatHistory(100);
        for (int i = 0; i < 100; i++)
        {
            string content = $"message {i}";
            history.Add(new ChatEntry(DateTimeOffset.UnixEpoch.AddMinutes(i), $"source-{i}",
                LocalizedText.Raw("Alice"), LocalizedText.Raw(content), content,
                i % 10 == 0 ? ChatEntryKind.System : ChatEntryKind.Ordinary, IsOwn: i % 3 == 0));
        }
        var box = new ChatBox(history, new Rect(100f, 100f, 300f, 180f));
        box.RefreshLayout(Font, TimeZoneInfo.Utc);

        // The first pass JITs the generic row loop for this sink. Measuring it would charge one-off runtime bytes
        // to the per-frame cost this test is about.
        var sink = new CountingSink();
        box.DrawHistoryRows(ref sink);
        Assert.True(sink.Runs > 0);

        AllocAssert.NoPerCallAllocation("ChatBox history rows on a steady frame", () =>
        {
            for (int frame = 0; frame < 60; frame++)
            {
                box.RefreshLayout(Font, TimeZoneInfo.Utc);
                box.DrawHistoryRows(ref sink);
            }
        });
    }

    struct CountingSink : IChatRowSink
    {
        public int Runs;

        public void DrawText(string text, Vector2 position, Color color) => Runs++;
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 16f;

        public Vector2 Measure(string text) => new(text.Length * 8f, LineHeight);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-chatbox && dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gui.ChatBox"
```
Expected failures:
- `A_full_history_draws_only_the_rows_on_screen` and all six `Scrolling_mid_history_...` cases fail on `Assert.Equal` (expected 8 or 10 items, actual 100 starting at "message 0").
- `Drawing_a_steady_full_history_allocates_nothing` fails with "ChatBox history rows on a steady frame allocated N bytes on the first pass and N bytes on the retry, expected zero on at least one". This comes from the per-row substring slices.

These pass already and stay as guards: the partly-visible, shorter-history, nearly-full, font-change and width-change tests.

- [ ] **Step 3: Implement**

In `ChatBox.cs`, after `const float RowSpacing = 2f;` (line 21), add:
```csharp

    // Rows drawn past each end of the history viewport. VisibleRows says why the draw keeps one.
    const int VisibleRowMargin = 1;
```

Replace F1's `DrawHistoryRows` and `DrawRow`, and the `RowBounds` below them, with:
```csharp
    // The history rows of this frame, run by run, into sink. Only rows whose bounds meet the history viewport are
    // sent, plus VisibleRowMargin rows past each end, so a full scrollback costs the rows on screen rather than
    // every row the scissor would discard. Draw passes the sprite batch and tests pass a recorder, because a
    // sprite batch needs a GPU device. The layout must already be refreshed with the draw font.
    internal void DrawHistoryRows<TSink>(ref TSink sink) where TSink : IChatRowSink
    {
        VisibleRows(out int first, out int end);
        for (int i = first; i < end; i++)
            DrawRow(ref sink, i, _rows[i]);
    }

    void DrawRow<TSink>(ref TSink sink, int index, CachedRow row) where TSink : IChatRowSink
    {
        Rect bounds = RowBounds(index);
        var position = new Vector2(MathF.Floor(bounds.X), MathF.Floor(bounds.Y));

        // Both runs were split and measured when the layout was refreshed, so a steady frame slices and measures
        // nothing. The colours are read here because a theme change takes effect on the next draw. An empty run
        // is skipped, which paints exactly what drawing it did, nothing.
        if (row.TimestampText.Length > 0)
            sink.DrawText(row.TimestampText, position, (Color)Theme.TimestampText);
        if (row.MessageText.Length == 0) return;

        position.X += row.MessageX;
        sink.DrawText(row.MessageText, position, (Color)SelectColor(row.Entry, Theme));
    }

    // The half-open range of rows whose bounds meet the history viewport, grown by VisibleRowMargin at each end
    // and clamped to the history. The margin keeps the frame identical to drawing every row: a glyph can reach
    // past its row box (an accent above the ascent, a descender below the line) and the scissor rounds to whole
    // pixels, so the row just outside the viewport can still put pixels inside it.
    void VisibleRows(out int first, out int end)
    {
        int count = _rows.Count;
        float stride = _scroll.Stride;
        if (count == 0 || !(stride > 0f))
        {
            // No rows, or no stride to divide by. A zero stride stacks every row on one line, so every row is
            // drawn, which is what the draw did before it culled.
            first = 0;
            end = count;
            return;
        }

        // Row i's top is originY + i * stride, with the sparse-space offset RowBounds applies. A bottom-aligned
        // sparse history cancels the scroll offset there, so it is cancelled here too.
        Rect content = _scroll.ContentBounds;
        float sparseSpace = SparseSpace();
        float originY = sparseSpace > 0f ? content.Y + sparseSpace : content.Y - _scroll.ScrollOffset;

        // Row i meets the viewport when its bottom is below the viewport top and its top is above the viewport
        // bottom. The first such row is the smallest i past the top bound, and the end is the first i at or past
        // the bottom bound.
        float firstMeeting = MathF.Floor((content.Y - originY - _scroll.ItemHeight) / stride) + 1f;
        float endMeeting = MathF.Ceiling((content.Bottom - originY) / stride);

        first = (int)Math.Clamp(firstMeeting - VisibleRowMargin, 0f, count);
        end = (int)Math.Clamp(endMeeting + VisibleRowMargin, first, count);
    }

    internal Rect RowBounds(int index)
    {
        Rect bounds = _scroll.ItemBounds(index);
        float sparseSpace = SparseSpace();
        if (sparseSpace <= 0f) return bounds;
        return bounds with { Y = bounds.Y + sparseSpace + _scroll.ScrollOffset };
    }

    // The space above a bottom-aligned history too short to fill the viewport, zero or less when there is none.
    // RowBounds and VisibleRows share it so the cull and the placement cannot disagree.
    float SparseSpace()
    {
        if (HistoryAlignment != ChatHistoryAlignment.Bottom || _rows.Count == 0) return 0f;

        float rowsHeight = _rows.Count * _scroll.Stride - _scroll.ItemSpacing;
        return _scroll.ContentBounds.Height - rowsHeight;
    }
```

In `RefreshLayout`, replace the inner loop at lines 274-283:
```csharp
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                // The stamp is split off and measured once here rather than on every draw. A first line the wrap
                // broke inside the stamp does not start with it, so it draws whole in the message colour, as before.
                _rows.Add(i == 0 && prefix.Length > 0 && line.StartsWith(prefix, StringComparison.Ordinal)
                    ? new CachedRow(entry, prefix, line[prefix.Length..], measurer.Measure(prefix).X)
                    : new CachedRow(entry, "", line, 0f));
                _cachedLines.Add(line);
            }
```

Replace line 326:
```csharp
    // One wrapped line, split and measured at layout time: the stamp run (empty when the line carries none), the
    // message run (empty when the line is only the stamp), and where the message run starts after the stamp.
    readonly record struct CachedRow(ChatEntry Entry, string TimestampText, string MessageText, float MessageX);
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run:
```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-chatbox && dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Gui.ChatBox"
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree
```
Expected: every `ChatBox*` fact passes, including all existing `ChatBoxTests`, which pin `RowBounds`. The full Gui suite passes.

- [ ] **Step 5: Commit**
```bash
git add KhaozEngine.Gui/Chat/ChatBox.cs KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs KhaozEngine.Gui.Tests/Gui/ChatBoxDrawAllocationTests.cs
git commit -m "gui(chat): draw only the visible history rows" -- KhaozEngine.Gui/Chat/ChatBox.cs KhaozEngine.Gui.Tests/Gui/ChatBoxRowDrawTests.cs KhaozEngine.Gui.Tests/Gui/ChatBoxDrawAllocationTests.cs
```


### Decisions and risks

- **The headless draw proof goes through a hook, not `Draw`.** The spec's "headless test ... only the visible rows are drawn" can't drive `Draw`, because `SpriteBatch` needs an `IGpuDevice` (`KhaozEngine.Render2D/SpriteBatch.cs:167`, `KhaozEngine.Gui.Tests/Gui/TabBarFlatDrawTests.cs:13-17`). The proof goes through `IChatRowSink`, which `Draw` uses. `_frame.Draw`, `BeginClip` and `Composer.Draw` stay outside the allocation proof.
- **One-row margin instead of the exact cut.** The reason is glyph overhang (`SpriteBatch.cs:577`, `SpriteFont.cs` vertical metrics) and scissor rounding (`SpriteBatch.Scissor.cs:38-39`). You decide whether to keep it. `VisibleRowMargin = 0` gives the spec's literal behaviour.
- **Empty runs are now skipped.** A wrapped empty line, or a message that's only the stamp, no longer calls `DrawString`. The output is identical, because `DrawString("")` places no glyphs (`SpriteBatch.cs:568`).
- **The allocation test lives in its own class.** It needs `AllocSensitive`, and `ChatBoxTests` is pinned to `AmbientLocalization` (`ChatBoxTests.cs:14`).
- **Refresh still allocates when the history changes.** `TextLayout.Wrap` and the per-entry prefix strings still allocate in `RefreshLayout` (`ChatBox.cs:265-272`). This is by design and outside the spec's steady-frame claim.
- **F1 briefly relies on the cached measurer.** In F1, `DrawRow` uses `_cachedMeasurer!` as the draw font. That's the same object `Draw` just refreshed with, because the cache compares by reference (`ChatBox.cs:251`). F2 removes that dependency.


---
## Integration, release and adoption

These tasks run in the orchestrating session after every item branch has passed its own review. They are not
delegated as one unit, because each gate needs the previous one's evidence.

### Task G1: Integrate the item branches

**Branch:** `feature/frame-cost-round` in the worktree `/Users/antonio/KhaozEngine/.worktrees/frame-cost-round`

**Files:**
- Modify: whatever the item branches changed. No new code is written in this task except conflict resolution.

**Interfaces:**
- Consumes: the reviewed tips of `feature/frame-cost-point-shadows`, `feature/frame-cost-clusters`,
  `feature/frame-cost-water`, `feature/frame-cost-metal`, `feature/frame-cost-d3d11-adapter` and
  `feature/frame-cost-chatbox`.
- Produces: one integration branch holding every item, green on the full Release suite and the local Metal GPU
  suite.

- [ ] **Step 1: Bring the integration branch up to date with main**

```bash
cd /Users/antonio/KhaozEngine/.worktrees/frame-cost-round
git fetch origin
git merge --no-edit origin/main
```

Expected: a clean merge or a fast-forward. If `main` gained a #1054 implementation meanwhile, stop and rebase the
point shadow item onto it before continuing, because the spec says this round lands first and the ordering note on
#1054 promised it.

- [ ] **Step 2: Merge each item branch in spec order**

```bash
for b in feature/frame-cost-point-shadows feature/frame-cost-clusters feature/frame-cost-water \
         feature/frame-cost-metal feature/frame-cost-d3d11-adapter feature/frame-cost-chatbox; do
  git merge --no-ff --no-edit "$b" || { echo "CONFLICT in $b"; break; }
done
```

Expected: no conflicts, since the items own disjoint files. The known overlap is the three shader hash tables if a
concurrent water change landed on `main`. Resolve a hash table conflict by rebaking, never by hand editing (Step 4).

- [ ] **Step 3: Build and run the full Release suite**

```bash
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release 2>&1 | tail -3
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket" 2>&1 | grep -E "Passed!|Failed!|error" | tail -40
```

Expected: `0 Warning(s)`, `0 Error(s)`, and every test project reports `Passed!`.

- [ ] **Step 4: Run the local Metal GPU suite**

```bash
KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build 2>&1 | grep -E "Passed!|Failed!|golden" | tail -20
```

Expected: `Passed!`. The only permitted golden movements are `tileworld_river` and, if its bake moved it,
`scene3d_sky_two_discs`, both rebaked on all three legs in Task C1. Any other moved grid is a regression until
attributed.

If the shader hash tables conflicted in Step 2, rebake them with the command the clusters item documents, then rerun
this step.

- [ ] **Step 5: Run the repository guards**

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: every script exits 0.

- [ ] **Step 6: Push the integration branch**

```bash
git push origin feature/frame-cost-round
```

### Task G2: Cross-backend verification

**Branch:** `feature/frame-cost-round`

**Files:**
- None. This task verifies. A moved grid found here is investigated and attributed, not rebaked.

**Interfaces:**
- Consumes: the pushed integration branch from G1.
- Produces: green Metal, D3D11 and Vulkan legs on the integration branch.

- [ ] **Step 1: Dispatch the full GPU matrix on the branch**

```bash
gh workflow run cross-platform-gpu.yml --repo APKiwiOrg/KhaozEngine --ref feature/frame-cost-round -f legs=all -f bake=false
gh run list --repo APKiwiOrg/KhaozEngine --workflow cross-platform-gpu.yml --branch feature/frame-cost-round \
  --event workflow_dispatch --limit 1 --json databaseId --jq '.[0].databaseId'
```

- [ ] **Step 2: Wait for the run and read every leg**

```bash
gh run watch <run-id> --repo APKiwiOrg/KhaozEngine --exit-status
gh run view <run-id> --repo APKiwiOrg/KhaozEngine --log-failed | tail -80
```

Expected: every leg green. The River and two-disc grids were baked on all three legs in Task C1, and the
cluster change is byte-identical by construction.

- [ ] **Step 3: If a leg fails**

A failing golden on any leg is a regression until attributed. Follow `docs/CROSS-PLATFORM.md` "Tolerance and
rebakes": find which item moved it by rerunning the leg with `-f legs=<leg> -f renderTestFilter=<scene>` on each item
branch, fix the cause on that item branch, merge it again and rerun G1 Steps 3 to 6. Rebake only a grid whose move
is an intended consequence the spec names, with the cause and worst-cell delta in the commit body.

### Task G3: Release the engine

**Branch:** `feature/frame-cost-round`, then `main`

**Files:**
- Modify: `CHANGELOG.md` (newest entry), `Directory.Build.props` only if the staged version was taken,
  `docs/design/FRAME-COST-ROUND-DESIGN-2026-09-23.md` (status line), `docs/INDEX.md` (status cell), and every
  Markdown file the documentation sweep finds stale.

**Interfaces:**
- Consumes: the green integration branch from G2.
- Produces: a tagged engine release in the local feed and on origin that Grimhollow can pin.

- [ ] **Step 1: Re-read the version and tags**

```bash
git fetch origin --tags
grep KhaozEngineVersion Directory.Build.props
git tag -l 'v20.2*' ; git ls-remote --tags origin 'v20.2*'
```

Expected: `20.2.0` in `Directory.Build.props` and no `v20.2.0` tag. Then the round rides 20.2.0. If `v20.2.0`
already exists, take the next free patch, `20.2.1`, set it in `Directory.Build.props`, and start a new CHANGELOG
entry for it in Step 2 instead of extending 20.2.0's.

- [ ] **Step 2: Extend the CHANGELOG entry**

Add one paragraph per item under the newest entry, player-neutral and consumer-facing, in the file's existing
voice. Each paragraph names what changed, the measured effect on the Grimhollow town path, and the issue. The two
pixel differences (quantized dissolve in point shadow signatures, flat zero-swell water quads) and the D3D11
adapter default are stated explicitly, since a consumer can observe them.

- [ ] **Step 3: Documentation sweep**

```bash
git grep -n -w -e PointLightClusters -e UsesFlatQuad -e KE_D3D11_ADAPTER -e PointCasterSignature -e CachedRow -e ObjCAutoreleasePool -- '*.md'
git grep -n -i -e "918 KiB" -e "camera-focused" -e "first adapter" -e "null adapter" -- '*.md'
```

Correct every stale description the matches show. At minimum `docs/USING-KHAOZENGINE.md` must describe the
D3D11 default adapter and the override, and any description of the cluster image layout or the water grid mode
choice must match the new behavior. Set the design doc status line to
`Status: implemented in 20.2.0.` (or the version from Step 1) and the INDEX row status cell to
`**Implemented in 20.2.0.**` with the same issue links.

- [ ] **Step 4: Guards, pack and commit**

```bash
sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree \
  && sh scripts/check-agent-instructions.sh --tree && bash scripts/check-doc-versions.sh
dotnet build KhaozEngine.slnx -c Release 2>&1 | tail -2
scripts/pack-local-feed.sh
scripts/check-local-feed.sh
git add CHANGELOG.md docs/design/FRAME-COST-ROUND-DESIGN-2026-09-23.md docs/INDEX.md <each swept doc>
git commit -m "release(20.2.0): the frame cost round rides 20.2.0"
```

- [ ] **Step 5: Merge to main and push**

```bash
git fetch origin
git merge --no-edit origin/main            # must be clean, rerun G1 Steps 3 to 5 if it brought anything
cd /Users/antonio/KhaozEngine
git checkout main && git pull --ff-only
git merge --ff-only feature/frame-cost-round
git push origin main
```

- [ ] **Step 6: Tag under the waiting-consumer exception and push the tag**

Grimhollow #325 is pinned and waiting on this change, which is the contributor rules' one automatic tag exception.

```bash
scripts/tag-release.sh
git push origin v20.2.0
gh run list --repo APKiwiOrg/KhaozEngine --limit 3
```

Expected: the tag push starts the full CI sequence. Watch it to green before G4 vendors the packages.

- [ ] **Step 7: Close the engine issues**

Close #1110 to #1116 with a comment naming the release and the measured before and after numbers from G5 once
those exist. Until G5 runs, close them with the release commit and leave the numbers to the #325 closing comment.

### Task G4: Grimhollow adopts the release

**Branch:** `fix/engine-frame-cost` in a new Grimhollow worktree from local `main`

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`), `.config/dotnet-tools.json` (`ke-tileedit`,
  `ke-sfxbake`), `vendor/khaozengine/*` (refreshed packages), `docs/ENGINE-INTEGRATION.md` (new sweep entry),
  `docs/PLAY_CHANGELOG.md` (the staged entry).

**Interfaces:**
- Consumes: the tagged engine release in `~/KhaozEngine/local-feed`.
- Produces: Grimhollow on the new pin, closing #325.

- [ ] **Step 1: Create the worktree and re-read the current pin**

```bash
cd /Users/antonio/Grimhollow
git fetch origin
git worktree add .worktrees/engine-frame-cost -b fix/engine-frame-cost main
cd .worktrees/engine-frame-cost
grep -E "KhaozEngineVersion|<Version>|<BuildName>" Directory.Build.props
git tag --sort=-v:refname | head -1
```

The pin was 20.0.0 when the round was designed and 20.1.0 on `main` by the time the plan was written. The sweep
range in Step 4 starts from whatever this prints.

- [ ] **Step 2: Move the pin and both tool pins**

Set `<KhaozEngineVersion>` to the released version, and set `ke-tileedit` and `ke-sfxbake` in
`.config/dotnet-tools.json` to the same version.

- [ ] **Step 3: Refresh the vendored packages and build**

```bash
scripts/refresh-engine.sh
dotnet tool restore
dotnet build Grimhollow.slnx -c Release 2>&1 | tail -3
```

Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 4: Write the integration ledger entry**

Add a new top entry to `docs/ENGINE-INTEGRATION.md` for the swept range from the Step 1 pin exclusive to the new
version inclusive. Give every relevant capability in that range a disposition in the ledger's existing style
(ADOPTED, TRANSITIVE, AVAILABLE, NOT WIRED). The frame cost items are TRANSITIVE through the existing viewer and
scene calls with no game code change, and each line cites its engine issue. Record the before and after probe
numbers from G5 in the entry once G5 has run.

- [ ] **Step 5: Ride the staged player changelog entry**

`<Version>` is 0.10.2 against the newest tag v0.10.1 at the time of writing, so it is staged and this rides it. If
Step 1 shows a version equal to the newest tag instead, make the one patch bump the AGENTS rules describe. Append one
player-facing line to the newest `docs/PLAY_CHANGELOG.md` entry, in its existing voice, saying the game runs
smoother in town and around lit buildings, and on Windows laptops with two graphics chips now uses the faster one.
Roll the entry date to the day of the change.

- [ ] **Step 6: Merge main, verify and land**

```bash
git fetch origin && git merge --no-edit origin/main
dotnet build Grimhollow.slnx -c Release
dotnet test Grimhollow.Tests/Grimhollow.Tests.csproj -c Release
dotnet format Grimhollow.slnx --verify-no-changes --no-restore
git add Directory.Build.props .config/dotnet-tools.json vendor/khaozengine docs/ENGINE-INTEGRATION.md docs/PLAY_CHANGELOG.md
git commit -m "engine(pin): adopt the frame cost round"
cd /Users/antonio/Grimhollow && git merge --ff-only fix/engine-frame-cost && git push origin main
```

Expected: every command exits 0 and the push is a fast-forward.

### Task G5: Acceptance measurement

**Branch:** `feature/perf-probe` in `/Users/antonio/Grimhollow/.worktrees/perf-probe` (never merged)

**Files:**
- Modify: `tools/SnapshotTool/perf-results/FINDINGS.md` (an "After the frame cost round" section) and new result
  directories under `tools/SnapshotTool/perf-results/`.

**Interfaces:**
- Consumes: the adopted pin from G4.
- Produces: the before and after numbers the spec's Acceptance section asks for.

- [ ] **Step 1: Keep a build of the old probe**

```bash
cd /Users/antonio/Grimhollow/.worktrees/perf-probe
dotnet build tools/SnapshotTool -c Release 2>&1 | tail -2
rm -rf /tmp/probe-old && cp -R tools/SnapshotTool/bin/Release /tmp/probe-old
```

- [ ] **Step 2: Move the probe branch onto the new pin**

```bash
git merge --no-edit main
dotnet build tools/SnapshotTool -c Release 2>&1 | tail -2
```

Expected: the probe builds against the new engine. If a reflection seam in `PerfShots.cs` (`Scene3D` constructor,
`RenderInternal`) moved, update the lookup there.

- [ ] **Step 3: Interleaved runs, old then new, twice**

```bash
for i in 1 2; do
  for which in old new; do
    if [ $which = old ]; then run="dotnet /tmp/probe-old/net10.0/SnapshotTool.dll"; else run="dotnet run --no-build --project tools/SnapshotTool -c Release --"; fi
    $run perf /tmp/frame-cost-ab/$which-$i --paths town,forest,meadow,night,spawn --res 1920x1080
    $run perf /tmp/frame-cost-ab/$which-$i-nosession --paths forest,meadow --res 1920x1080 --no-session
  done
done
```

The old build's entry point is `/tmp/probe-old/net10.0/SnapshotTool.dll`, the Release output of `tools/SnapshotTool`.

- [ ] **Step 4: Compare against the acceptance lines**

Read each `summary.md` and check:
- Town with a session, `RenderInternal record` median at or under 4 ms.
- Forest and meadow with no session, point static rebuilds on at most 5% of the 600 frames (at most 30).
- Town, mean allocation at or under 5 KiB per frame and gen0 count 0.
- GPU wait median no worse than the old build's on every path.

- [ ] **Step 5: Record and push the evidence**

Copy the summaries into `tools/SnapshotTool/perf-results/after-frame-cost/`, add the comparison table to
`FINDINGS.md`, commit on `feature/perf-probe` and push. Add the same table to the G4 ledger entry on Grimhollow
`main` in a follow-up commit, comment it on #325 and close #325.

```bash
git add tools/SnapshotTool/perf-results
git commit -m "tools(perf): measure the frame cost round"
git push origin feature/perf-probe
```

If any acceptance line fails, do not close #325. Report the failing line with its numbers and open an engine issue
for the gap before anything else.
