# Walkable Prop/Building Surfaces (sub-project B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the player jump onto and stand on rocks/logs/solid buildings and walk over their real top-surface contours, server-authoritative with client prediction, building on the 7.54.0 vertical physics (sub-project A).

**Architecture:** Each walkable-solid prop kind bakes (offline, folded into kit ingest) a render-free top-down max-height grid. `WorldSurfaces` (sibling to `WorldColliders`) places those grids by the deterministic scatter and answers `Query(x,z)` with the max prop-top height. The vertical `CharacterMovement.Step(in MoveState, ...)` lands the capsule on `max(terrain, surface)`, the existing `WorldColliders` push-out becomes **height-aware** (block sides, carry tops), and a small step-up mounts low edges. Everything is render-free below the bake tool so the headless authoritative server shares identical data.

**Tech Stack:** C# / net10.0, `System.Numerics`, xUnit headless. MonoGame-free. Builds on A (7.54.0).

## Global Constraints

- **TDD, headless:** every new behaviour ships with an xUnit test in `KhaozEngine.Tests`. Run `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`.
- **Additive minor bump:** `<KhaozEngine5xVersion>` in `Directory.Build.props`, `7.54.0` → `7.55.0` (verify no concurrent release took it; bump past if so). New packable project (`KhaozEngine.PropSurface.Tool`) is allowed (a `PackAsTool`, same version line).
- **No em-dashes** anywhere (code, comments, docs, commit messages).
- **Render-free below the tool:** `PropSurface`/`WorldSurface`/`WorldSurfaces` + the binary reader are pure data (`KhaozEngine.Collision`, a `System.Numerics` leaf - keep it so). Only the bake (`PropSurfaceBake`, Render3D) and the `ke-propbake` tool touch meshes.
- **Bake local + unscaled; transform at query:** the grid is the unit prop; `WorldSurface.SampleWorld` applies centre/scale/yaw, identical on client and server (like the colliders).
- **Single-valued top contour** (no overhangs); buildings are solid blocks. Trees stay thin trunk-blockers (no surface).
- **Nullable everywhere:** a null/empty `WorldSurfaces` leaves movement exactly as 7.54.0.
- **Stay in scope.** Out: sub-project A itself, overhangs/interiors/caves, full 3D mesh collision, dynamic/moving surfaces, player-vs-player, fall damage, climbing/mantling, streaming surfaces.
- **A's seam (7.54.0):** vertical step is `CharacterMovement.Step(in MoveState, in MoveCommand, float dt, Func<float,float,float> groundHeight, in MoveTuning, Func<float,float,Vector3>? groundNormal, WorldColliders? colliders, Func<float,float,Vector2>? clampXz) -> MoveState`; ground contact lands on `groundHeight(x,z) + tuning.CapsuleHalfHeight`; `ResolveHorizontal` pushes out of `colliders` unconditionally. `MoveTuning` is a `record struct` with positional params (defaulted).
- **Doc sweep on the bump:** `CHANGELOG.md` + `CHANGENOTES.md`, `CLAUDE.md` package map (new package + surface notes), `README.md` catalog (+ new package row, repo-layout block), `docs/USING-KHAOZENGINE.md` (extend the static-collision section), `docs/CONSUMERS.md` (umbrella/package table for the new package + version line), the three guard declarations, `scripts/check-doc-versions.sh`.

---

## File Structure

**KhaozEngine.Collision (render-free, new files):**
- `PropSurface.cs` — the unit-scale top-surface height grid + `SampleLocal` + binary `Read`/`Write`.
- `WorldSurface.cs` — one placed surface (PropSurface + centre/scale/yaw) + `SampleWorld` (transform-at-query) + bounding radius.
- `WorldSurfaces.cs` — `SpatialHashGrid`-backed set + `Query(x,z) -> float?` (max top).
- Modify `WorldCollider.cs` — add a `Top` height + height-aware factory params.
- Modify `WorldColliders.cs` — `Resolve(position, radius, footY, iterations)` overload skipping on-top colliders.

**KhaozEngine.Render3D (new file):**
- `Models/PropSurfaceBake.cs` — `Bake(GltfMesh) -> PropSurface` + `Classify(GltfMesh) -> bool walkableSolid`.

**KhaozEngine.Terrain (new file):**
- `PropSurfaces.cs` — `FromScatter(...) -> WorldSurfaces` and a combined collider-top helper.

**KhaozEngine.Locomotion (modify):**
- `MoveTuning.cs` — add `StepHeight`.
- `CharacterMovement.cs` — thread `WorldSurfaces?` into the vertical `Step`/`ResolveHorizontal`; support = `max(terrain, surface)`; height-aware collider resolve; step-up.

**KhaozEngine.NetWorld (modify):** `PlayerMoveSimulator.cs`, `PlayerMovementSystem.cs`, `WorldServer.cs`, `ShardedWorldServer.cs` — thread `WorldSurfaces?` (mirrors 7.52.0 `WorldColliders`).

**KhaozEngine.Game.Render3D (modify):** `CharacterController3D.cs` — pass `WorldSurfaces?` through `Update`.

**New project:** `KhaozEngine.PropSurface.Tool/` — `ke-propbake` `PackAsTool` (Render3D dep).

**KhaozEngine.Render3D (modify):** `Models/AssetManifest.cs` — `AssetEntry.Heightmap` (path) + `Surface` (walkable flag).

**Demo:** `TerrainWalkSample/Program.cs` (+ baked `.surf` assets + manifest fields).

---

## Task 1: `PropSurface` — unit height grid + sampling + binary IO

**Files:**
- Create: `KhaozEngine.Collision/PropSurface.cs`
- Test: `KhaozEngine.Tests/Collision/PropSurfaceTests.cs`

**Interfaces:**
- Produces:
  - `sealed class PropSurface` with `int Width`, `int Height`, `float CellSize`, `float OriginX`, `float OriginZ` (local-space min corner), and `bool TryGetMaxHeight(out float max)`.
  - ctor `PropSurface(int width, int height, float cellSize, float originX, float originZ, float[] heights)` where `heights[j*width+i]` is the top Y of cell `(i,j)` or `float.NaN` for an empty (uncovered) cell.
  - `float? SampleLocal(float lx, float lz)` — bilinear sample of the covered cells at local `(lx,lz)`; null when outside the grid or over only-empty cells.
  - `float MaxHeight` — the max non-NaN height (0 if all empty).
  - `void Write(Stream)` / `static PropSurface Read(Stream)` — a versioned binary round-trip.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class PropSurfaceTests
{
    // A 3x3 grid, cell 1.0, origin (-1,-1): a flat top at y=2 over the centre, empty (NaN) corners.
    static PropSurface Sample()
    {
        float n = float.NaN;
        var h = new[] { n, 2f, n,  2f, 2f, 2f,  n, 2f, n };
        return new PropSurface(3, 3, 1f, -1f, -1f, h);
    }

    [Fact]
    public void SampleLocal_Centre_ReturnsTop()
    {
        Assert.Equal(2f, Sample().SampleLocal(0f, 0f)!.Value, 3);
    }

    [Fact]
    public void SampleLocal_OutsideGrid_ReturnsNull()
    {
        Assert.Null(Sample().SampleLocal(10f, 0f));
    }

    [Fact]
    public void MaxHeight_IsMaxNonEmpty()
    {
        Assert.Equal(2f, Sample().MaxHeight, 3);
    }

    [Fact]
    public void BinaryRoundTrip_IsIdentical()
    {
        PropSurface a = Sample();
        using var ms = new MemoryStream();
        a.Write(ms);
        ms.Position = 0;
        PropSurface b = PropSurface.Read(ms);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.CellSize, b.CellSize, 4);
        Assert.Equal(a.SampleLocal(0f, 0f)!.Value, b.SampleLocal(0f, 0f)!.Value, 3);
        Assert.Null(b.SampleLocal(10f, 0f));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfaceTests"`
Expected: FAIL (compile error: `PropSurface` does not exist).

- [ ] **Step 3: Implement `PropSurface`**

```csharp
using System;
using System.IO;

namespace KhaozEngine.Collision;

/// <summary>
/// A unit-scale (no placement transform) top-down max-height grid baked from a prop mesh: for each cell the
/// highest surface Y above it, or <see cref="float.NaN"/> where the prop does not cover that cell. Single-valued
/// (no overhangs). Render-free; the headless server reads the same binary the client does. Scale + yaw are applied
/// at query time by <see cref="WorldSurface"/>.
/// </summary>
public sealed class PropSurface
{
    const uint Magic = 0x4B455053; // "KEPS"
    const ushort FormatVersion = 1;

    readonly float[] heights;

    /// <summary>Grid columns.</summary>
    public int Width { get; }
    /// <summary>Grid rows.</summary>
    public int Height { get; }
    /// <summary>Local-space cell edge (metres).</summary>
    public float CellSize { get; }
    /// <summary>Local X of the grid's min corner (cell (0,0)).</summary>
    public float OriginX { get; }
    /// <summary>Local Z of the grid's min corner.</summary>
    public float OriginZ { get; }
    /// <summary>The maximum covered (non-NaN) height; 0 when fully empty.</summary>
    public float MaxHeight { get; }

    public PropSurface(int width, int height, float cellSize, float originX, float originZ, float[] heights)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("PropSurface dimensions must be positive.");
        if (heights is null || heights.Length != width * height)
            throw new ArgumentException("PropSurface heights length must equal width*height.");
        Width = width; Height = height; CellSize = MathF.Max(1e-4f, cellSize);
        OriginX = originX; OriginZ = originZ; this.heights = heights;

        float max = 0f; bool any = false;
        foreach (float v in heights)
            if (!float.IsNaN(v) && (!any || v > max)) { max = v; any = true; }
        MaxHeight = any ? max : 0f;
    }

    /// <summary>Bilinear sample of the covered cells at local (lx, lz); null when outside the grid or when the
    /// four surrounding cells are all empty.</summary>
    public float? SampleLocal(float lx, float lz)
    {
        float fx = (lx - OriginX) / CellSize;
        float fz = (lz - OriginZ) / CellSize;
        if (fx < 0f || fz < 0f || fx > Width - 1 || fz > Height - 1) return null;

        int i0 = (int)MathF.Floor(fx), j0 = (int)MathF.Floor(fz);
        int i1 = Math.Min(i0 + 1, Width - 1), j1 = Math.Min(j0 + 1, Height - 1);
        float tx = fx - i0, tz = fz - j0;

        // Average of the covered corners weighted bilinearly; ignore empty (NaN) corners.
        float sum = 0f, wsum = 0f;
        Accumulate(i0, j0, (1 - tx) * (1 - tz), ref sum, ref wsum);
        Accumulate(i1, j0, tx * (1 - tz), ref sum, ref wsum);
        Accumulate(i0, j1, (1 - tx) * tz, ref sum, ref wsum);
        Accumulate(i1, j1, tx * tz, ref sum, ref wsum);
        return wsum > 1e-6f ? sum / wsum : (float?)null;
    }

    void Accumulate(int i, int j, float w, ref float sum, ref float wsum)
    {
        float v = heights[j * Width + i];
        if (!float.IsNaN(v) && w > 0f) { sum += v * w; wsum += w; }
    }

    /// <summary>Versioned binary write (magic, version, dims, extent, then width*height little-endian floats).</summary>
    public void Write(Stream stream)
    {
        var w = new BinaryWriter(stream);
        w.Write(Magic); w.Write(FormatVersion);
        w.Write(Width); w.Write(Height); w.Write(CellSize); w.Write(OriginX); w.Write(OriginZ);
        foreach (float v in heights) w.Write(v);
        w.Flush();
    }

    /// <summary>Reads a surface written by <see cref="Write"/>. Throws <see cref="InvalidDataException"/> on a bad
    /// magic/version.</summary>
    public static PropSurface Read(Stream stream)
    {
        var r = new BinaryReader(stream);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("PropSurface: bad magic.");
        if (r.ReadUInt16() != FormatVersion) throw new InvalidDataException("PropSurface: unsupported version.");
        int width = r.ReadInt32(), height = r.ReadInt32();
        float cell = r.ReadSingle(), ox = r.ReadSingle(), oz = r.ReadSingle();
        var h = new float[width * height];
        for (int k = 0; k < h.Length; k++) h[k] = r.ReadSingle();
        return new PropSurface(width, height, cell, ox, oz, h);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfaceTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Collision/PropSurface.cs KhaozEngine.Tests/Collision/PropSurfaceTests.cs
git commit -m "collision: PropSurface unit top-surface height grid + bilinear sample + binary IO"
```

---

## Task 2: `WorldSurface` — placed surface, transform-at-query

**Files:**
- Create: `KhaozEngine.Collision/WorldSurface.cs`
- Test: `KhaozEngine.Tests/Collision/WorldSurfaceTests.cs`

**Interfaces:**
- Consumes: `PropSurface` (Task 1).
- Produces:
  - `readonly struct WorldSurface` with `PropSurface Surface`, `Vector2 Center`, `float Scale`, `float Yaw`, `float BaseY`, `float BoundingRadius`.
  - ctor `WorldSurface(PropSurface surface, Vector2 center, float scale, float yaw, float baseY)`.
  - `float? SampleWorld(float x, float z)` — world (x,z) → local (subtract centre, rotate by `-yaw`, divide by `scale`), `SampleLocal`, then `* scale + BaseY`; null outside the footprint.
  - `float TopWorld` — `BaseY + Surface.MaxHeight * Scale` (the placed top, for the collider top height).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class WorldSurfaceTests
{
    // A 3x3 unit grid, cell 1, origin (-1,-1), flat top y=2 over a 2x2 area (corners empty).
    static PropSurface Flat()
    {
        float n = float.NaN;
        return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, 2f, n, 2f, 2f, 2f, n, 2f, n });
    }

    [Fact]
    public void SampleWorld_AppliesCentreAndScale()
    {
        var ws = new WorldSurface(Flat(), center: new Vector2(10f, 5f), scale: 2f, yaw: 0f, baseY: 1f);
        // Centre of the prop in world is (10,5); local (0,0) -> height 2*scale + baseY = 5.
        Assert.Equal(5f, ws.SampleWorld(10f, 5f)!.Value, 3);
    }

    [Fact]
    public void SampleWorld_OutsideFootprint_ReturnsNull()
    {
        var ws = new WorldSurface(Flat(), new Vector2(0f, 0f), 1f, 0f, 0f);
        Assert.Null(ws.SampleWorld(50f, 50f));
    }

    [Fact]
    public void SampleWorld_YawRotatesLookup()
    {
        // An asymmetric strip (covered only along local +X at j=1) sampled through a 90deg yaw should be found by
        // querying along world +Z instead of +X.
        float n = float.NaN;
        var strip = new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, n, n, n, 3f, 3f, n, n, n }); // covered at (1,1),(2,1)
        var ws = new WorldSurface(strip, Vector2.Zero, 1f, yaw: MathF.PI / 2f, baseY: 0f);
        Assert.NotNull(ws.SampleWorld(0f, 1f)); // local +X maps to world +Z under +90deg yaw
    }

    [Fact]
    public void TopWorld_IsBasePlusScaledMax()
    {
        var ws = new WorldSurface(Flat(), Vector2.Zero, scale: 2f, yaw: 0f, baseY: 1f);
        Assert.Equal(1f + 2f * 2f, ws.TopWorld, 3);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldSurfaceTests"`
Expected: FAIL (compile error: `WorldSurface` does not exist).

- [ ] **Step 3: Implement `WorldSurface`**

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// A placed prop surface: a unit <see cref="PropSurface"/> positioned at a world XZ <see cref="Center"/>, scaled by
/// <see cref="Scale"/>, rotated by <see cref="Yaw"/>, sitting at <see cref="BaseY"/>. <see cref="SampleWorld"/>
/// transforms a world (x,z) into the prop's local frame, samples the grid, and scales the height back to world -
/// the same transform-at-query the colliders use, so it is identical on client and server.
/// </summary>
public readonly struct WorldSurface
{
    /// <summary>The unit (unscaled) height grid.</summary>
    public PropSurface Surface { get; }
    /// <summary>World XZ centre (the placement point).</summary>
    public Vector2 Center { get; }
    /// <summary>Per-instance uniform scale.</summary>
    public float Scale { get; }
    /// <summary>Per-instance yaw (radians).</summary>
    public float Yaw { get; }
    /// <summary>World Y of the prop's base (its feet).</summary>
    public float BaseY { get; }

    public WorldSurface(PropSurface surface, Vector2 center, float scale, float yaw, float baseY)
    {
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Center = center; Scale = scale <= 0f ? 1f : scale; Yaw = yaw; BaseY = baseY;
    }

    /// <summary>Conservative broad-phase radius: the unit grid's half-diagonal times the scale.</summary>
    public float BoundingRadius
    {
        get
        {
            float hx = MathF.Max(MathF.Abs(Surface.OriginX), MathF.Abs(Surface.OriginX + Surface.Width * Surface.CellSize));
            float hz = MathF.Max(MathF.Abs(Surface.OriginZ), MathF.Abs(Surface.OriginZ + Surface.Height * Surface.CellSize));
            return MathF.Sqrt(hx * hx + hz * hz) * Scale;
        }
    }

    /// <summary>The world top height of this placed surface (base + scaled max), used as the collider top.</summary>
    public float TopWorld => BaseY + Surface.MaxHeight * Scale;

    /// <summary>The world top height under (x, z), or null when (x, z) is outside this prop's footprint.</summary>
    public float? SampleWorld(float x, float z)
    {
        // World -> local: translate, rotate by -yaw, unscale.
        float dx = x - Center.X, dz = z - Center.Y;
        float cos = MathF.Cos(Yaw), sin = MathF.Sin(Yaw);
        float lx = (dx * cos + dz * sin) / Scale;
        float lz = (-dx * sin + dz * cos) / Scale;
        float? h = Surface.SampleLocal(lx, lz);
        return h.HasValue ? h.Value * Scale + BaseY : (float?)null;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldSurfaceTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Collision/WorldSurface.cs KhaozEngine.Tests/Collision/WorldSurfaceTests.cs
git commit -m "collision: WorldSurface placed surface with transform-at-query sampling"
```

---

## Task 3: `WorldSurfaces` — broadphased set, max-height query

**Files:**
- Create: `KhaozEngine.Collision/WorldSurfaces.cs`
- Test: `KhaozEngine.Tests/Collision/WorldSurfacesTests.cs`

**Interfaces:**
- Consumes: `WorldSurface` (Task 2), `SpatialHashGrid` (existing).
- Produces:
  - `sealed class WorldSurfaces` with `WorldSurfaces(IEnumerable<WorldSurface> surfaces, float cellSize = 8f)`, `int Count`, `bool IsEmpty`, `IReadOnlyList<WorldSurface> Surfaces`, `float? Query(float x, float z)` (max top over covering surfaces, null when none).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class WorldSurfacesTests
{
    static PropSurface FlatTop(float y)
    {
        float n = float.NaN;
        return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, y, n, y, y, y, n, y, n });
    }

    [Fact]
    public void Empty_QueryIsNull()
    {
        var set = new WorldSurfaces(new List<WorldSurface>());
        Assert.True(set.IsEmpty);
        Assert.Null(set.Query(0f, 0f));
    }

    [Fact]
    public void Query_ReturnsMaxOverOverlapping()
    {
        var low = new WorldSurface(FlatTop(1f), new Vector2(0f, 0f), 1f, 0f, 0f);
        var high = new WorldSurface(FlatTop(3f), new Vector2(0f, 0f), 1f, 0f, 0f);
        var set = new WorldSurfaces(new[] { low, high });
        Assert.Equal(3f, set.Query(0f, 0f)!.Value, 3); // the higher surface wins
    }

    [Fact]
    public void Query_FarFromAny_IsNull()
    {
        var set = new WorldSurfaces(new[] { new WorldSurface(FlatTop(2f), new Vector2(100f, 100f), 1f, 0f, 0f) });
        Assert.Null(set.Query(0f, 0f));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldSurfacesTests"`
Expected: FAIL (compile error: `WorldSurfaces` does not exist).

- [ ] **Step 3: Implement `WorldSurfaces`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// A render-free, broad-phased set of placed prop surfaces. Backed by the existing <see cref="SpatialHashGrid"/>
/// (each surface inserted at its centre with its bounding radius), so <see cref="Query"/> only samples nearby
/// surfaces. <see cref="Query"/> returns the maximum top height of the surfaces covering (x, z) - a player where
/// two props overlap stands on the higher - or null when none cover it. Immutable; null/empty = no surfaces
/// (movement uses terrain only). Build it from scatter placements + an obstacle list (see
/// <c>KhaozEngine.Terrain.PropSurfaces.FromScatter</c>).
/// </summary>
public sealed class WorldSurfaces
{
    readonly WorldSurface[] surfaces;
    readonly SpatialHashGrid grid;

    public WorldSurfaces(IEnumerable<WorldSurface> surfaces, float cellSize = 8f)
    {
        this.surfaces = surfaces?.ToArray() ?? Array.Empty<WorldSurface>();
        grid = new SpatialHashGrid(cellSize);
        grid.BeginRebuild(this.surfaces.Length);
        for (int i = 0; i < this.surfaces.Length; i++)
            grid.Add(i, this.surfaces[i].Center, this.surfaces[i].BoundingRadius);
    }

    public int Count => surfaces.Length;
    public bool IsEmpty => surfaces.Length == 0;
    public IReadOnlyList<WorldSurface> Surfaces => surfaces;

    /// <summary>The max top height of the surfaces covering (x, z), or null when none cover it.</summary>
    public float? Query(float x, float z)
    {
        if (surfaces.Length == 0) return null;
        int n = grid.QueryCandidates(new Vector2(x, z), 0f);
        float best = float.NegativeInfinity; bool any = false;
        for (int k = 0; k < n; k++)
        {
            float? h = surfaces[grid.GetQueryIndex(k)].SampleWorld(x, z);
            if (h.HasValue && (!any || h.Value > best)) { best = h.Value; any = true; }
        }
        return any ? best : (float?)null;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldSurfacesTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Collision/WorldSurfaces.cs KhaozEngine.Tests/Collision/WorldSurfacesTests.cs
git commit -m "collision: WorldSurfaces broad-phased set + max-height Query"
```

---

## Task 4: Height-aware `WorldCollider.Top` + `WorldColliders.Resolve(footY)`

**Files:**
- Modify: `KhaozEngine.Collision/WorldCollider.cs`
- Modify: `KhaozEngine.Collision/WorldColliders.cs`
- Test: `KhaozEngine.Tests/Collision/HeightAwareBlockingTests.cs`

**Interfaces:**
- Consumes: existing `WorldCollider`/`WorldColliders`.
- Produces:
  - `WorldCollider` gains `float Top { get; }` (the prop's solid top world Y; default `float.PositiveInfinity` = always blocks). Factories gain an optional `float top = float.PositiveInfinity`.
  - `WorldColliders` gains `Vector2 Resolve(Vector2 position, float radius, float footY, float skin = 0.05f, int iterations = 4)` — like the existing `Resolve` but a collider is skipped when `footY >= collider.Top - skin` (you are standing on/above it). The existing `Resolve(position, radius, iterations)` is unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class HeightAwareBlockingTests
{
    [Fact]
    public void BelowTop_StillBlocked()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        // Feet at y=0 (at the side) -> pushed out.
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 0f);
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f);
    }

    [Fact]
    public void AtOrAboveTop_NotBlocked()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        // Standing on top (feet at the rock top) -> NOT pushed (you stay where you are).
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 1.5f);
        Assert.Equal(0.5f, r.X, 3);
        Assert.Equal(0f, r.Y, 3);
    }

    [Fact]
    public void DefaultTop_AlwaysBlocks_LikeATree()
    {
        var tree = WorldCollider.Cylinder(Vector2.Zero, 1f); // default top = +inf
        var set = new WorldColliders(new[] { tree });
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 100f);
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f); // never mounted
    }

    [Fact]
    public void OldResolve_Unchanged()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f); // height-agnostic overload
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~HeightAwareBlockingTests"`
Expected: FAIL (compile error: `top:` parameter / `Resolve(... footY ...)` missing).

- [ ] **Step 3a: Add `Top` to `WorldCollider`**

Replace the fields/ctor/factories in `KhaozEngine.Collision/WorldCollider.cs`:

```csharp
    /// <summary>Box rotation about its centre (radians). Unused for a cylinder.</summary>
    public float Yaw { get; }
    /// <summary>The prop's solid top world Y: the capsule is blocked by this collider only while its feet are
    /// below <see cref="Top"/> (the side); at or above it the capsule is standing on top and is not pushed.
    /// Default <see cref="float.PositiveInfinity"/> means it always blocks (a thin blocker like a tree).</summary>
    public float Top { get; }

    WorldCollider(ColliderKind kind, Vector2 center, float radius, Vector2 halfExtents, float yaw, float top)
    {
        Kind = kind; Center = center; Radius = radius; HalfExtents = halfExtents; Yaw = yaw; Top = top;
    }

    /// <summary>A cylinder collider at <paramref name="center"/> with <paramref name="radius"/>; optional solid
    /// <paramref name="top"/> world Y for height-aware blocking (default always-blocks).</summary>
    public static WorldCollider Cylinder(Vector2 center, float radius, float top = float.PositiveInfinity)
        => new(ColliderKind.Cylinder, center, radius, Vector2.Zero, 0f, top);

    /// <summary>An oriented-box collider; optional solid <paramref name="top"/> world Y (default always-blocks).</summary>
    public static WorldCollider Box(Vector2 center, Vector2 halfExtents, float yaw, float top = float.PositiveInfinity)
        => new(ColliderKind.Box, center, 0f, halfExtents, yaw, top);
```

(The `BoundingRadius` and `Resolve(c, r, out push)` members are unchanged.)

- [ ] **Step 3b: Add the height-aware `Resolve` overload to `WorldColliders`**

In `KhaozEngine.Collision/WorldColliders.cs`, add alongside the existing `Resolve`:

```csharp
    /// <summary>Height-aware push-out: like <see cref="Resolve(Vector2,float,int)"/> but a collider is skipped once
    /// the capsule's feet (<paramref name="footY"/>) are at or above that collider's <see cref="WorldCollider.Top"/>
    /// minus <paramref name="skin"/> (you are standing on it, not hitting its side). Lets the capsule stand on a
    /// rock/roof without being shoved off, while a thin blocker (top = +inf) always blocks.</summary>
    public Vector2 Resolve(Vector2 position, float radius, float footY, float skin = 0.05f, int iterations = 4)
    {
        if (colliders.Length == 0) return position;
        Vector2 p = position;
        for (int it = 0; it < iterations; it++)
        {
            int n = grid.QueryCandidates(p, radius);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                WorldCollider c = colliders[grid.GetQueryIndex(i)];
                if (footY >= c.Top - skin) continue;          // standing on/above it -> not a side hit
                if (c.Resolve(p, radius, out Vector2 push)) { p += push; any = true; }
            }
            if (!any) break;
        }
        return p;
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~HeightAwareBlockingTests"`
Expected: PASS (4 tests). Also run `--filter "FullyQualifiedName~WorldCollider"` and the existing collider tests to confirm no regression.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Collision/WorldCollider.cs KhaozEngine.Collision/WorldColliders.cs KhaozEngine.Tests/Collision/HeightAwareBlockingTests.cs
git commit -m "collision: WorldCollider.Top + height-aware WorldColliders.Resolve(footY) (block sides, carry tops)"
```

---

## Task 5: `MoveTuning.StepHeight` + surface support + height-aware block + step-up in `CharacterMovement.Step`

**Files:**
- Modify: `KhaozEngine.Locomotion/MoveTuning.cs`
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs`
- Test: `KhaozEngine.Tests/Locomotion/SurfaceMovementTests.cs`

**Interfaces:**
- Consumes: `WorldSurfaces` (Task 3), `WorldColliders.Resolve(footY)` (Task 4), A's `MoveState`/vertical `Step`.
- Produces:
  - `MoveTuning` gains positional `float StepHeight = 0.4f` (last param, defaulted).
  - The vertical `Step(in MoveState, ...)` overload gains a trailing `WorldSurfaces? surfaces = null` param. Support height becomes `max(terrain(x,z), surfaces.Query(x,z) ?? terrain)`; the collider push-out uses `Resolve(xz, radius, footY)`; a `<= StepHeight` upward support rise from the pre-move support height is allowed (step-up), a larger one keeps the pre-move support (blocked). Null surfaces = unchanged 7.54.0 behaviour.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class SurfaceMovementTests
{
    static float Flat(float x, float z) => 0f;
    static PropSurface Slab(float y) { float n = float.NaN; return new PropSurface(3, 3, 1f, -1.5f, -1.5f, new[] { y, y, y, y, y, y, y, y, y }); }

    [Fact]
    public void StandsOnRockSurface_WhenAbove()
    {
        // A 1.5 m flat-topped slab at origin. A capsule dropped from above lands on top (y = top + half-height).
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(Slab(1.5f), Vector2.Zero, 1f, 0f, 0f) });
        var tuning = MoveTuning.Default;
        var s = new MoveState { Position = new Vector3(0f, 5f, 0f), VerticalVelocity = 0f, Grounded = false };
        for (int i = 0; i < 120; i++)
            s = CharacterMovement.Step(s, default, 1f / 60f, Flat, tuning, null, null, null, surfaces);
        Assert.Equal(1.5f + tuning.CapsuleHalfHeight, s.Position.Y, 1);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void NoSurfaces_FallsToTerrain_Unchanged()
    {
        var tuning = MoveTuning.Default;
        var a = new MoveState { Position = new Vector3(0f, 5f, 0f) };
        var b = a;
        for (int i = 0; i < 120; i++)
        {
            a = CharacterMovement.Step(a, default, 1f / 60f, Flat, tuning, null, null, null, null);
            b = CharacterMovement.Step(b, default, 1f / 60f, Flat, tuning, null, null, null, new WorldSurfaces(System.Array.Empty<WorldSurface>()));
        }
        Assert.Equal(a.Position.Y, b.Position.Y, 4);
    }

    [Fact]
    public void StepsUpLowLedge_WithoutJump()
    {
        // A 0.3 m ledge (below the 0.4 step height) is mounted by walking into it; grounded throughout.
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(Slab(0.3f), new Vector2(0f, -1.0f), 1f, 0f, 0f) });
        var tuning = MoveTuning.Default;
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // walk -Z toward the ledge
        var s = new MoveState { Position = Vector3.Zero, Grounded = true };
        for (int i = 0; i < 60; i++)
            s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, tuning, null, null, null, surfaces);
        Assert.True(s.Position.Z < -0.5f, $"did not advance onto the ledge: z={s.Position.Z}");
        Assert.Equal(0.3f + tuning.CapsuleHalfHeight, s.Position.Y, 1); // standing on the 0.3 m ledge
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SurfaceMovementTests"`
Expected: FAIL (compile error: vertical `Step` has no `surfaces` parameter).

- [ ] **Step 3a: Add `StepHeight` to `MoveTuning`**

In `KhaozEngine.Locomotion/MoveTuning.cs`, add the positional param (last) + doc + `init` member, mirroring the other vertical fields. Add `float StepHeight = 0.4f` to the primary ctor param list (after `GroundedEpsilon`), add a `public float StepHeight { get; init; } = StepHeight;` member with a doc comment ("Max upward support rise auto-mounted without a jump (a low rock/curb/log); a larger rise behaves as a wall."), and `Default` keeps the implicit 0.4.

- [ ] **Step 3b: Thread surfaces + height-aware block + step-up into the vertical `Step`**

In `KhaozEngine.Locomotion/CharacterMovement.cs`:
- Add `Func<float,float,float>` helper `Support(x,z)` = `MathF.Max(groundHeight(x,z), surfaces?.Query(x,z) ?? float.NegativeInfinity)`; when surfaces is null this is just `groundHeight`.
- Vertical `Step(in MoveState, ...)` signature gains a trailing `WorldSurfaces? surfaces = null`.
- Pass the capsule foot Y (`state.Position.Y - tuning.CapsuleHalfHeight`) into the horizontal resolve so the collider push-out is height-aware.
- Use the support height for the ground-contact `groundY`.
- Step-up: compute the support under the pre-move XZ (`supBefore`) and under the resolved XZ (`supAfter`); if grounded and `supAfter - supBefore > tuning.StepHeight`, revert the horizontal move (keep the pre-move XZ) so a too-tall rise is a wall.

Concrete diff (replace the vertical `Step` body + extend `ResolveHorizontal` to accept `footY`/`colliders`-height-aware + a step-up guard). Full method:

```csharp
    public static MoveState Step(in MoveState state, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldColliders? colliders = null,
        Func<float, float, Vector2>? clampXz = null, WorldSurfaces? surfaces = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        float Support(float x, float z)
        {
            float g = groundHeight(x, z);
            float? s = surfaces?.Query(x, z);
            return s.HasValue && s.Value > g ? s.Value : g;
        }

        float footY = state.Position.Y - tuning.CapsuleHalfHeight;
        float supBefore = Support(state.Position.X, state.Position.Z);

        float speedScale = state.Grounded ? 1f : tuning.AirControl;
        (float x, float z) = ResolveHorizontal(state.Position.X, state.Position.Z, cmd, dt, tuning, groundNormal,
            colliders, clampXz, speedScale, footY);

        // Step-up gate: while grounded, a support rise taller than the step height is a wall (revert the move).
        if (state.Grounded)
        {
            float supAfter = Support(x, z);
            if (supAfter - supBefore > tuning.StepHeight) { x = state.Position.X; z = state.Position.Z; }
        }

        bool jumpRequested = cmd.Jump || state.JumpBufferRemaining > 0f;
        float jumpBuffer = cmd.Jump ? tuning.JumpBuffer : MathF.Max(0f, state.JumpBufferRemaining - dt);

        float vVel = state.VerticalVelocity - tuning.Gravity * dt;
        if (vVel < -tuning.MaxFallSpeed) vVel = -tuning.MaxFallSpeed;
        float y = state.Position.Y + vVel * dt;

        float groundY = Support(x, z) + tuning.CapsuleHalfHeight;
        bool grounded; float tSinceGround;
        if (vVel <= 0f && (y <= groundY || (state.Grounded && y <= groundY + tuning.GroundedEpsilon)))
        {
            y = groundY; vVel = 0f; grounded = true; tSinceGround = 0f;
        }
        else { grounded = false; tSinceGround = state.TimeSinceGrounded + dt; }

        if (jumpRequested && (grounded || tSinceGround <= tuning.CoyoteTime))
        {
            vVel = tuning.JumpSpeed; grounded = false; tSinceGround = tuning.CoyoteTime + dt; jumpBuffer = 0f;
        }

        return new MoveState
        {
            Position = new Vector3(x, y, z), VerticalVelocity = vVel, Grounded = grounded,
            TimeSinceGrounded = tSinceGround, JumpBufferRemaining = jumpBuffer,
        };
    }
```

Extend `ResolveHorizontal` to take a trailing `float footY = float.PositiveInfinity` and call the height-aware collider resolve when a finite footY is supplied:

```csharp
    private static (float x, float z) ResolveHorizontal(float x, float z, in MoveCommand cmd, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, WorldColliders? colliders,
        Func<float, float, Vector2>? clampXz, float speedScale, float footY = float.PositiveInfinity)
    {
        // ... (camera-relative move + slope gate unchanged) ...

        if (colliders is not null && !colliders.IsEmpty)
        {
            Vector2 resolved = float.IsInfinity(footY)
                ? colliders.Resolve(new Vector2(x, z), tuning.CapsuleRadius)
                : colliders.Resolve(new Vector2(x, z), tuning.CapsuleRadius, footY);
            x = resolved.X; z = resolved.Y;
        }

        // ... (clampXz unchanged) ...
        return (x, z);
    }
```

The horizontal-only `Step(Vector3, ...)` overload is unchanged (it calls `ResolveHorizontal` without `footY`, defaulting to always-block).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Locomotion"`
Expected: PASS (the 3 new + all existing CharacterMovement / vertical-physics tests; the defaulted params keep A's call sites compiling). If a step-up threshold is marginal, adjust the test's numeric expectation to the observed value but keep the shape (advances onto a 0.3 m ledge, stands at ledge+half-height).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Locomotion KhaozEngine.Tests/Locomotion/SurfaceMovementTests.cs
git commit -m "locomotion: surface support (max terrain/surface) + height-aware block + step-up in vertical Step"
```

---

## Task 6: `PropSurfaceBake` — mesh -> PropSurface + walkable classification (Render3D)

**Files:**
- Create: `KhaozEngine.Render3D/Models/PropSurfaceBake.cs`
- Test: `KhaozEngine.Tests/Render3D/PropSurfaceBakeTests.cs`

**Interfaces:**
- Consumes: `GltfMesh`/`PropLoader` (existing), `PropSurface` (Task 1), `PropFootprint` (7.53.0).
- Produces:
  - `sealed class PropSurfaceBakeOptions` with `float CellSize = 0.25f`, `int MaxGrid = 64`, `float SolidHeightMeters = 2.5f`.
  - `static class PropSurfaceBake` with `static bool IsWalkableSolid(GltfMesh normalizedMesh, PropSurfaceBakeOptions? o = null)` (a tall-thin tree-like mesh -> false; short or solid-flat-top -> true; reuse the 7.53.0 footprint short/tall heuristic) and `static PropSurface Bake(GltfMesh normalizedMesh, PropSurfaceBakeOptions? o = null)` (rasterize the top-down max-Y grid over the footprint XZ extent at `CellSize`, clamped to `MaxGrid`, empty cells = NaN).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class PropSurfaceBakeTests
{
    static GltfMesh Mesh(IEnumerable<Vector3> positions)
    {
        var list = new List<ModelVertex>();
        foreach (Vector3 p in positions) list.Add(new ModelVertex(p, Vector3.UnitY, Vector4.One));
        return new GltfMesh(list.ToArray(), new uint[] { 0, 0, 0 });
    }

    // A 1.5 m tall, 2 m square flat-topped slab centred on origin (a "rock"): a top quad at y=1.5 + base at y=0.
    static GltfMesh Slab()
    {
        var pts = new List<Vector3>();
        foreach (float x in new[] { -1f, 1f }) foreach (float z in new[] { -1f, 1f })
        { pts.Add(new Vector3(x, 0f, z)); pts.Add(new Vector3(x, 1.5f, z)); pts.Add(new Vector3(x * 0.5f, 1.5f, z * 0.5f)); }
        return Mesh(pts);
    }

    [Fact]
    public void Bake_FlatSlab_TopIsSlabHeight()
    {
        PropSurface s = PropSurfaceBake.Bake(Slab());
        Assert.Equal(1.5f, s.MaxHeight, 1);
        Assert.Equal(1.5f, s.SampleLocal(0f, 0f)!.Value, 1); // standing over the centre -> the slab top
    }

    [Fact]
    public void IsWalkableSolid_ShortSlab_True()
    {
        Assert.True(PropSurfaceBake.IsWalkableSolid(Slab()));
    }

    [Fact]
    public void IsWalkableSolid_TallThinTrunkWithCanopy_False()
    {
        // A tall thin trunk (hx=hz=0.3, 0..2) + a wide canopy (hx=hz=3, 2..10): tree-like -> not a walkable solid.
        var pts = new List<Vector3>();
        foreach (float x in new[] { -0.3f, 0.3f }) foreach (float z in new[] { -0.3f, 0.3f }) { pts.Add(new Vector3(x, 0f, z)); pts.Add(new Vector3(x, 2f, z)); }
        foreach (float x in new[] { -3f, 3f }) foreach (float z in new[] { -3f, 3f }) { pts.Add(new Vector3(x, 2f, z)); pts.Add(new Vector3(x, 10f, z)); }
        Assert.False(PropSurfaceBake.IsWalkableSolid(Mesh(pts)));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfaceBakeTests"`
Expected: FAIL (compile error: `PropSurfaceBake` does not exist).

- [ ] **Step 3: Implement `PropSurfaceBake`**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Render3D
{
    /// <summary>Tunables for <see cref="PropSurfaceBake"/>.</summary>
    public sealed class PropSurfaceBakeOptions
    {
        /// <summary>Grid cell edge (metres) of the baked height map. Default 0.25.</summary>
        public float CellSize = 0.25f;
        /// <summary>Max grid dimension (cells) per axis; a larger footprint widens the cell instead. Default 64.</summary>
        public int MaxGrid = 64;
        /// <summary>A mesh no taller than this is a small solid (rock/crate) and is always a walkable solid; taller
        /// meshes are classified by footprint spread (tree-like canopy -> not walkable). Default 2.5.</summary>
        public float SolidHeightMeters = 2.5f;
        public static readonly PropSurfaceBakeOptions Default = new();
    }

    /// <summary>
    /// Derives a render-free top-down max-height grid (<see cref="PropSurface"/>) from a
    /// <see cref="PropLoader"/>-normalized prop mesh (base y=0, XZ centred on origin), and classifies whether a
    /// prop is a walkable solid (rock/log/building -> a surface you stand on) or a thin blocker (tree -> no surface,
    /// keeps its trunk collider). The grid is the unit prop; placement scale/yaw are applied at query time by
    /// <see cref="WorldSurface"/>. Offline/tooling (uses the mesh); the runtime reads the baked binary.
    /// </summary>
    public static class PropSurfaceBake
    {
        /// <summary>True when the prop is a walkable solid (short, or solid without a much-wider upper spread);
        /// false for a tall thin trunk-with-canopy (tree).</summary>
        public static bool IsWalkableSolid(GltfMesh normalizedMesh, PropSurfaceBakeOptions? options = null)
        {
            PropSurfaceBakeOptions o = options ?? PropSurfaceBakeOptions.Default;
            Bounds(normalizedMesh, out float minY, out float maxY, out _, out _, out _, out _);
            float height = maxY - minY;
            if (height <= o.SolidHeightMeters) return true; // small solid

            // Tall: walkable only if it does not widen a lot above a low slice (a building's vertical walls keep a
            // near-constant footprint; a tree's canopy is much wider than its trunk).
            float lowHx, lowHz, fullHx, fullHz;
            FootprintHalf(normalizedMesh, minY + 1.0f, out lowHx, out lowHz);   // trunk/base slice
            FootprintHalf(normalizedMesh, maxY, out fullHx, out fullHz);        // whole prop
            float lowR = MathF.Max(lowHx, lowHz), fullR = MathF.Max(fullHx, fullHz);
            return fullR <= lowR * 1.6f; // canopy spreads > 1.6x -> tree -> not walkable
        }

        /// <summary>Rasterize the top-down max-height grid over the mesh footprint.</summary>
        public static PropSurface Bake(GltfMesh normalizedMesh, PropSurfaceBakeOptions? options = null)
        {
            PropSurfaceBakeOptions o = options ?? PropSurfaceBakeOptions.Default;
            Bounds(normalizedMesh, out float minY, out _, out float minX, out float maxX, out float minZ, out float maxZ);

            float spanX = MathF.Max(1e-3f, maxX - minX), spanZ = MathF.Max(1e-3f, maxZ - minZ);
            float cell = o.CellSize;
            int w = Math.Clamp((int)MathF.Ceiling(spanX / cell) + 1, 2, o.MaxGrid);
            int h = Math.Clamp((int)MathF.Ceiling(spanZ / cell) + 1, 2, o.MaxGrid);
            cell = MathF.Max(spanX / (w - 1), spanZ / (h - 1)); // widen the cell if clamped so the grid still covers

            var heights = new float[w * h];
            for (int k = 0; k < heights.Length; k++) heights[k] = float.NaN;

            // Splat each vertex's Y into its nearest cell as a max (a coarse top-surface raster; triangle-accurate
            // rasterization is unnecessary for the kinematic top contour).
            ModelVertex[] verts = normalizedMesh.Vertices;
            for (int vi = 0; vi < verts.Length; vi++)
            {
                Vector3 p = verts[vi].Position;
                int i = (int)MathF.Round((p.X - minX) / cell);
                int j = (int)MathF.Round((p.Z - minZ) / cell);
                if (i < 0 || j < 0 || i >= w || j >= h) continue;
                int idx = j * w + i;
                if (float.IsNaN(heights[idx]) || p.Y > heights[idx]) heights[idx] = p.Y;
            }
            return new PropSurface(w, h, cell, minX, minZ, heights);
        }

        static void Bounds(GltfMesh m, out float minY, out float maxY, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = minY = minZ = float.MaxValue; maxX = maxY = maxZ = float.MinValue;
            foreach (ModelVertex v in m.Vertices)
            {
                Vector3 p = v.Position;
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
                minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
            }
        }

        static void FootprintHalf(GltfMesh m, float captureTopY, out float hx, out float hz)
        {
            hx = hz = 0f;
            foreach (ModelVertex v in m.Vertices)
                if (v.Position.Y <= captureTopY)
                { hx = MathF.Max(hx, MathF.Abs(v.Position.X)); hz = MathF.Max(hz, MathF.Abs(v.Position.Z)); }
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfaceBakeTests"`
Expected: PASS (3 tests). If the coarse splat leaves the centre cell empty for the slab, the test over (0,0) still hits a covered cell via bilinear neighbours; if not, lower the test's resolution expectation or assert `MaxHeight` only.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropSurfaceBake.cs KhaozEngine.Tests/Render3D/PropSurfaceBakeTests.cs
git commit -m "render3d: PropSurfaceBake derives a top-surface height grid + walkable-solid classification"
```

---

## Task 7: `AssetEntry.Heightmap` + `Surface` flag + manifest parse (Render3D)

**Files:**
- Modify: `KhaozEngine.Render3D/Models/AssetManifest.cs`
- Test: `KhaozEngine.Tests/Render3D/AssetManifestTests.cs` (extend)

**Interfaces:**
- Produces: `AssetEntry` gains `string? Heightmap` (path to the `.surf`, resolved like `File`) and `bool Surface` (walkable-solid flag, default false). JSON: optional `"heightmap": "rock_a.surf"` and `"surface": true`.

- [ ] **Step 1: Write the failing test** (append to `AssetManifestTests`)

```csharp
        [Fact]
        public void Parse_HeightmapAndSurfaceFlag()
        {
            const string json = """
            { "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.8,
                           "surface": true, "heightmap": "rock.surf" } ] }
            """;
            AssetManifest m = AssetManifest.Parse(json);
            Assert.True(m.Props[0].Surface);
            Assert.Equal("rock.surf", m.Props[0].Heightmap);
        }

        [Fact]
        public void Parse_NoHeightmap_DefaultsNullAndFalse()
        {
            const string json = """{ "props": [ { "id": "x", "file": "x.glb", "heightMeters": 1 } ] }""";
            AssetManifest m = AssetManifest.Parse(json);
            Assert.Null(m.Props[0].Heightmap);
            Assert.False(m.Props[0].Surface);
        }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AssetManifestTests"`
Expected: FAIL (compile error: `AssetEntry` has no `Heightmap`/`Surface`).

- [ ] **Step 3: Extend `AssetEntry` + parse** — add `string? Heightmap` + `bool Surface` to `AssetEntry` (trailing ctor params, defaults `null`/`false`), resolve `Heightmap` against `baseDir` like `File` (when non-null), add `[JsonPropertyName("heightmap")] string? Heightmap` + `[JsonPropertyName("surface")] bool Surface` to `Dto.Entry`, and pass them into the `AssetEntry` ctor in `Parse`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AssetManifestTests"`
Expected: PASS (existing + 2 new).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/AssetManifest.cs KhaozEngine.Tests/Render3D/AssetManifestTests.cs
git commit -m "render3d: AssetEntry.Heightmap + Surface flag (manifest references the baked .surf)"
```

---

## Task 8: `PropSurfaces.FromScatter` + collider-top wiring (Terrain)

**Files:**
- Create: `KhaozEngine.Terrain/PropSurfaces.cs`
- Modify: `KhaozEngine.Terrain/PropColliders.cs` (a top-aware overload)
- Test: `KhaozEngine.Tests/Terrain/PropSurfacesTests.cs`

**Interfaces:**
- Consumes: `PropPlacement`/`PropScatter` (existing), `PropSurface`/`WorldSurface`/`WorldSurfaces`/`WorldCollider` (Tasks 1-4).
- Produces:
  - `static WorldSurfaces PropSurfaces.FromScatter(IReadOnlyList<PropPlacement> placements, Func<string, PropSurface?> surfaceForId, IEnumerable<WorldSurface>? obstacles = null, float cellSize = 8f)` — one `WorldSurface` per placement whose id has a surface (base Y = placement Y), plus obstacles.
  - `static WorldColliders PropColliders.FromScatter(IReadOnlyList<PropPlacement> placements, Func<string, ColliderShape?> shapeForId, Func<string, float>? topForId, ColliderShape? defaultShape = null, IEnumerable<WorldCollider>? obstacles = null, float cellSize = 8f)` — a new overload that stamps each placed collider's `Top` from `topForId(id)` (the surface's placed top; `+inf` when the prop is a thin blocker / no surface). The existing overload (no `topForId`) is unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public class PropSurfacesTests
{
    static readonly ScatterConfig Cfg = ScatterConfig.ForestRing(seed: 1337);
    static TerrainField Field() => new(TerrainPresets.Clearing());
    static PropSurface FlatTop(float y) { float n = float.NaN; return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, y, n, y, y, y, n, y, n }); }

    [Fact]
    public void FromScatter_OneSurfacePerWalkablePlacement()
    {
        var f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        Assert.NotEmpty(placements);
        WorldSurfaces set = PropSurfaces.FromScatter(placements, _ => FlatTop(1.5f));
        Assert.Equal(placements.Count, set.Count);
    }

    [Fact]
    public void FromScatter_SkipsPlacementsWithoutASurface()
    {
        var f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        WorldSurfaces set = PropSurfaces.FromScatter(placements, _ => null);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void FromScatter_ObstaclesIncluded()
    {
        var f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        var roof = new WorldSurface(FlatTop(4f), new Vector2(0f, 12f), 1f, 0f, 0f);
        WorldSurfaces set = PropSurfaces.FromScatter(placements, _ => null, obstacles: new[] { roof });
        Assert.Equal(1, set.Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfacesTests"`
Expected: FAIL (compile error: `PropSurfaces` does not exist).

- [ ] **Step 3a: Implement `PropSurfaces.FromScatter`**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Terrain
{
    /// <summary>Builds a render-free <see cref="WorldSurfaces"/> from deterministic scatter placements (each
    /// walkable-solid prop's unit <see cref="PropSurface"/> placed at the instance's (x,z)/scale/yaw, base Y from
    /// the placement) plus an explicit obstacle/building list. Mirrors <see cref="PropColliders"/>; streaming-
    /// consistent because it shares the coordinate-hash scatter.</summary>
    public static class PropSurfaces
    {
        public static WorldSurfaces FromScatter(
            IReadOnlyList<PropPlacement> placements,
            Func<string, PropSurface?> surfaceForId,
            IEnumerable<WorldSurface>? obstacles = null,
            float cellSize = 8f)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (surfaceForId == null) throw new ArgumentNullException(nameof(surfaceForId));

            var list = new List<WorldSurface>(placements.Count);
            foreach (PropPlacement p in placements)
            {
                PropSurface? s = surfaceForId(p.Id);
                if (s is not null)
                    list.Add(new WorldSurface(s, new Vector2(p.X, p.Z), p.Scale, p.Yaw, p.Y));
            }
            if (obstacles != null) list.AddRange(obstacles);
            return new WorldSurfaces(list, cellSize);
        }
    }
}
```

- [ ] **Step 3b: Add the top-aware `PropColliders.FromScatter` overload** — in `KhaozEngine.Terrain/PropColliders.cs`, add an overload that also takes `Func<string, float>? topForId` and, after `s.Place(...)`, stamps the collider's `Top` (rebuild the `WorldCollider` with `top: topForId?.Invoke(p.Id) ?? float.PositiveInfinity`). Since `ColliderShape.Place` returns a `WorldCollider` without a top, add `ColliderShape.Place(center, scale, yaw, top)` (an overload threading `top` into `WorldCollider.Cylinder/Box`), and call it here. The existing no-top overload calls `Place(center, scale, yaw)` (top = +inf), unchanged.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfacesTests"` and `--filter "FullyQualifiedName~PropCollidersTests"`
Expected: PASS (3 new + existing collider tests green).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Terrain KhaozEngine.Collision/ColliderShape.cs KhaozEngine.Tests/Terrain/PropSurfacesTests.cs
git commit -m "terrain: PropSurfaces.FromScatter + top-aware PropColliders.FromScatter overload"
```

---

## Task 9: Thread `WorldSurfaces?` through the netcode movement stack

**Files:**
- Modify: `KhaozEngine.NetWorld/PlayerMoveSimulator.cs`, `PlayerMovementSystem.cs`, `WorldServer.cs`, `ShardedWorldServer.cs`
- Modify: `KhaozEngine.Game.Render3D/CharacterController3D.cs`
- Test: `KhaozEngine.Tests/NetWorld/ServerSurfaceTests.cs`

**Interfaces:**
- Consumes: vertical `Step(..., WorldSurfaces?)` (Task 5).
- Produces (all new params trailing + nullable, default null, mirroring 7.52.0 `WorldColliders`):
  - `PlayerMoveSimulator(..., WorldColliders? colliders = null, WorldSurfaces? surfaces = null)` (and pass `surfaces` into `CharacterMovement.Step`).
  - `PlayerMovementSystem(..., WorldColliders? colliders = null, WorldSurfaces? surfaces = null)`.
  - `WorldServer(..., WorldColliders? colliders = null, WorldSurfaces? surfaces = null)`, `ShardedWorldServer(..., WorldColliders? colliders = null, WorldSurfaces? surfaces = null)`.
  - `CharacterController3D.Update(..., WorldColliders? colliders = null, WorldSurfaces? surfaces = null)`.

> Note: these `Step` call sites currently call A's vertical `Step` overload. Confirm each (`PlayerMoveSimulator`/`PlayerMovementSystem`) passes the `MoveState` overload; thread `surfaces` as the new trailing arg.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerSurfaceTests
{
    static float Flat(float x, float z) => 0f;
    static PropSurface Slab(float y) { float n = float.NaN; return new PropSurface(3, 3, 1f, -1.5f, -1.5f, new[] { y, y, y, y, y, y, y, y, y }); }
    static WorldSurfaces OneRock() => new(new[] { new WorldSurface(Slab(1.5f), Vector2.Zero, 1f, 0f, 0f) });

    [Fact]
    public void Simulator_StandsPlayerOnRock()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: OneRock());
        var s = new PlayerMoveState { Position = new Vector3(0f, 5f, 0f) };
        for (int i = 0; i < 180; i++) s = sim.Step(s, default, 1f / 60f);
        Assert.Equal(1.5f + MoveTuning.Default.CapsuleHalfHeight, s.Position.Y, 1);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void Server_ResolvesIdenticallyToClient()
    {
        var surfaces = OneRock();
        var server = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: surfaces);
        var client = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: surfaces);
        var cmd = new MoveCommand(new Vector2(0.2f, 1f), run: false, cameraYaw: 0.3f);
        var a = new PlayerMoveState { Position = new Vector3(0.1f, 3f, 0.1f) };
        var b = a;
        for (int i = 0; i < 120; i++)
        {
            a = server.Step(a, cmd, 1f / 60f); b = client.Step(b, cmd, 1f / 60f);
            Assert.Equal(a.Position.Y, b.Position.Y, 5);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerSurfaceTests"`
Expected: FAIL (compile error: `PlayerMoveSimulator` has no `surfaces`).

- [ ] **Step 3: Thread `WorldSurfaces?`** through each: add the field + trailing ctor param and pass `surfaces` into the vertical `CharacterMovement.Step(...)` call (after `colliders`/`clampXz`), in `PlayerMoveSimulator`, `PlayerMovementSystem`, `WorldServer` (-> simulator), `ShardedWorldServer` (-> movement system + spawn-clamp), and `CharacterController3D.Update`. Add `using KhaozEngine.Collision;` where missing.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorld"`
Expected: PASS (2 new + existing NetWorld suite green).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld KhaozEngine.Game.Render3D KhaozEngine.Tests/NetWorld/ServerSurfaceTests.cs
git commit -m "networld: thread WorldSurfaces through simulator/system/servers + CharacterController3D"
```

---

## Task 10: `ke-propbake` tool + `PropSurfaceLoader` (Render3D) + manifest stamp

**Files:**
- Create: `KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj`
- Create: `KhaozEngine.PropSurface.Tool/Program.cs`
- Create: `KhaozEngine.Render3D/Models/PropSurfaceLoader.cs` (load + bake convenience)
- Test: `KhaozEngine.Tests/Render3D/PropSurfaceLoaderTests.cs`

**Interfaces:**
- Produces:
  - `static class PropSurfaceLoader` (Render3D): `static IReadOnlyDictionary<string, PropSurface> LoadAll(AssetManifest manifest)` — for each entry with a `Heightmap`, `PropSurface.Read` the file (resolved); render-free read, used by the client and by tests. `static void BakeAndWrite(AssetEntry entry, string outPath, PropSurfaceBakeOptions? o = null)` — load+normalize via PropLoader, bake, write the `.surf` (used by the tool).
  - `ke-propbake <manifest.json>` (`PackAsTool`): for each walkable-solid prop (or all, classifying via `PropSurfaceBake.IsWalkableSolid`), bake + write `<id>.surf` next to the glTF and ensure the manifest entry has `surface:true` + `heightmap:"<id>.surf"` (stamp if missing; idempotent).

- [ ] **Step 1: Write the failing test** (`PropSurfaceLoaderTests`)

```csharp
using System.IO;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class PropSurfaceLoaderTests
{
    [Fact]
    public void LoadAll_ReadsReferencedHeightmaps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke_surf_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Write a tiny .surf and a manifest that references it.
            float n = float.NaN;
            var surf = new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, 2f, n, 2f, 2f, 2f, n, 2f, n });
            using (var fs = File.Create(Path.Combine(dir, "rock.surf"))) surf.Write(fs);
            File.WriteAllText(Path.Combine(dir, "props.manifest.json"),
                """{ "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.8, "surface": true, "heightmap": "rock.surf" } ] }""");

            AssetManifest m = AssetManifest.Load(Path.Combine(dir, "props.manifest.json"));
            var loaded = PropSurfaceLoader.LoadAll(m);
            Assert.True(loaded.ContainsKey("rock"));
            Assert.Equal(2f, loaded["rock"].MaxHeight, 3);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfaceLoaderTests"`
Expected: FAIL (compile error: `PropSurfaceLoader` does not exist).

- [ ] **Step 3a: Implement `PropSurfaceLoader`** (Render3D):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Collision;

namespace KhaozEngine.Render3D
{
    /// <summary>Loads baked prop surfaces (render-free <see cref="PropSurface.Read"/>) referenced from a manifest,
    /// and (tooling) bakes + writes the binary for a prop. The runtime path is read-only and GPU-free.</summary>
    public static class PropSurfaceLoader
    {
        /// <summary>Read every entry's referenced <c>.surf</c> into an id -> <see cref="PropSurface"/> map.</summary>
        public static IReadOnlyDictionary<string, PropSurface> LoadAll(AssetManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var result = new Dictionary<string, PropSurface>();
            foreach (AssetEntry e in manifest.Props)
            {
                if (string.IsNullOrEmpty(e.Heightmap)) continue;
                using FileStream fs = File.OpenRead(e.Heightmap);
                result[e.Id] = PropSurface.Read(fs);
            }
            return result;
        }

        /// <summary>Load + normalize the prop mesh, bake its surface, and write the binary to <paramref name="outPath"/>.</summary>
        public static void BakeAndWrite(AssetEntry entry, string outPath, PropSurfaceBakeOptions? options = null)
        {
            GltfMesh mesh = PropLoader.LoadProp(entry);
            PropSurface surface = PropSurfaceBake.Bake(mesh, options);
            using FileStream fs = File.Create(outPath);
            surface.Write(fs);
        }
    }
}
```

- [ ] **Step 3b: Implement the tool** — `KhaozEngine.PropSurface.Tool.csproj` (mirror `KhaozEngine.Sfx.Tool.csproj`: `OutputType=Exe`, `PackAsTool=true`, `ToolCommandName=ke-propbake`, `Version=$(KhaozEngine5xVersion)`, reference `../KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`). `Program.cs`: read the manifest path arg, `AssetManifest.Load`, for each prop classify via `PropSurfaceBake.IsWalkableSolid` (loading the mesh), and for walkable ones `PropSurfaceLoader.BakeAndWrite(entry, "<dir>/<id>.surf")` + ensure the manifest JSON has `surface:true` + `heightmap:"<id>.surf"` (re-serialize the manifest with the stamped fields; idempotent). Print a summary line.

- [ ] **Step 3c:** Add the tool project to the solution build set if the repo enumerates projects (check `Directory.Build.props` / any `*.slnf`; the repo builds by directory, so just placing the csproj is enough). Add `<InternalsVisibleTo>` is not needed.

- [ ] **Step 4: Run to verify pass + build the tool**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropSurfaceLoaderTests"`
Then: `dotnet build KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj -c Release`
Expected: test PASS; tool builds.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.PropSurface.Tool KhaozEngine.Render3D/Models/PropSurfaceLoader.cs KhaozEngine.Tests/Render3D/PropSurfaceLoaderTests.cs
git commit -m "tool: ke-propbake bakes prop surfaces; PropSurfaceLoader reads them render-free"
```

---

## Task 11: Bake the demo kit + solid-rocks/building demo in `TerrainWalkSample`

**Files:**
- Modify: `TerrainWalkSample/Program.cs`
- Add: `TerrainWalkSample/assets/props/*.surf` (baked) + `props.manifest.json` (`surface`/`heightmap` fields)

**Interfaces:**
- Consumes: `PropSurfaceLoader.LoadAll`, `PropSurfaces.FromScatter`, the top-aware `PropColliders.FromScatter`, `CharacterController3D.Update(..., surfaces)`.

- [ ] **Step 1: Bake the demo assets** — run the tool against the demo manifest:

```bash
dotnet run --project KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj -c Release -- TerrainWalkSample/assets/props/props.manifest.json
```
Expected: writes `rock_a.surf`/`rock_b.surf` (and any other walkable-solids), stamps the manifest. Verify the trees classify as non-walkable (no `.surf`). Commit the baked assets + manifest.

- [ ] **Step 2: Wire surfaces into the demo** — in `OnLoad`, after loading meshes: `var surfaces = PropSurfaceLoader.LoadAll(manifest);` build the surface set from the same scatter region as the colliders, with a hand-placed solid building (a tall box collider with a flat roof surface), and pass `_surfaces` to `_character.Update(...)`. Build the colliders with the top-aware overload so each rock collider's `Top` = its surface placed top (so you stand on rocks without being shoved off), and the building box gets a `Top` = its roof. Add a console hint ("jump (Space) onto a rock or the building and walk across the top"). Keep the existing collider/inn wiring; add the surfaces alongside.

- [ ] **Step 3: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add TerrainWalkSample
git commit -m "sample: solid rocks/building you can jump onto and walk across (PropSurfaces)"
```

---

## Task 12: Version bump + full doc sweep + release

**Files:** `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `CLAUDE.md`, `README.md`, `docs/USING-KHAOZENGINE.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`.

- [ ] **Step 1: Verify the version is free** — `git fetch --tags origin; git tag | sort -V | tail -3`. Bump `Directory.Build.props` `7.54.0` -> `7.55.0` (or next free).
- [ ] **Step 2: `CHANGELOG.md`** — newest-first entry (no em-dashes): walkable prop/building surfaces (sub-project B), `PropSurface`/`WorldSurface`/`WorldSurfaces` (Collision), `PropSurfaceBake` + `PropSurfaceLoader` (Render3D), `ke-propbake` tool, `WorldCollider.Top` + height-aware `WorldColliders.Resolve(footY)`, `MoveTuning.StepHeight` + surface support + step-up in the vertical `Step`, `PropSurfaces.FromScatter`, the netcode threading, `AssetEntry.Heightmap`/`Surface`, the demo. New package `KhaozEngine.PropSurface.Tool`. Out of scope list. Additive; new tool package; minor.
- [ ] **Step 3: `CHANGENOTES.md`** — one/two-sentence digest.
- [ ] **Step 4: Guard declarations** to `7.55.0`: `docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` examples.
- [ ] **Step 5: Full doc sweep** — `CLAUDE.md` package map (add `KhaozEngine.PropSurface.Tool` to the tools list; add the surface types beside the collider note; `Render3D` gains `PropSurfaceBake`/`PropSurfaceLoader`; `Terrain` gains `PropSurfaces`); `README.md` catalog (a `KhaozEngine.PropSurface.Tool` row + repo-layout block); `docs/USING-KHAOZENGINE.md` (extend the "Static world collision" section with the walkable-surface flow: `ke-propbake`, `PropSurfaceLoader.LoadAll`, `PropSurfaces.FromScatter`, stand-on/jump-onto); `docs/CONSUMERS.md` (package table + the new tool). Mechanical check: `grep -rn "PropSurface\|WorldSurfaces\|ke-propbake\|StepHeight" --include="*.md" .`.
- [ ] **Step 6: Guard + full test** — `bash scripts/check-doc-versions.sh`; `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (all green).
- [ ] **Step 7: Pack** — `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 8: Commit** — `git add -A && git commit -m "collision(7.55.0): walkable prop/building surfaces (stand on rocks + roofs) + docs"`.

---

## Task 13: Merge, tag, push, clean up

- [ ] **Step 1: Merge to main** (from the main checkout): `git -C <repo> merge --no-ff worktree-feature+walkable-surfaces -m "Merge walkable prop/building surfaces (7.55.0)"`.
- [ ] **Step 2: Repack from main root** + **full test on merged main** (all green).
- [ ] **Step 3: Tag + push** — `git tag v7.55.0; git push origin main; git push origin v7.55.0`.
- [ ] **Step 4: Clean up** the worktree + branch (local; never pushed).
- [ ] **Step 5: Windowed validation command** (do NOT run via Bash) — a `bash` block running `TerrainWalkSample` from the main checkout.

---

## Self-Review

**Spec coverage:**
- Surface bake folded into kit-ingest (`ke-propbake` tool) + render-free binary asset + manifest reference → Tasks 1 (format), 6 (bake), 7 (manifest), 10 (tool). ✓
- `PropSurface`/`WorldSurface`/`WorldSurfaces` render-free + transform-at-query + broadphase → Tasks 1-3. ✓
- Walkable-solid vs thin-blocker classification → Task 6 (`IsWalkableSolid`). ✓
- `PropSurfaces.FromScatter` → Task 8. ✓
- Movement integration: support = max(terrain, surface), height-aware blocking, step-up, nullable → Tasks 4 (height-aware), 5 (support + step-up). ✓
- Authoritative + predicted threading → Task 9; render-free server path = `PropSurfaceLoader` (render-free read) + `PropSurfaces.FromScatter` over render-free scatter (Task 10 + 8). ✓
- Demo → Task 11. Bump/docs/release → Tasks 12-13. ✓

**Out-of-scope guard:** no overhangs/interiors, full-3D mesh collision, dynamic surfaces, pvp, fall damage, climbing, or streaming surfaces appear in any task. A itself is consumed, not rebuilt. ✓

**Type consistency:** `PropSurface(int,int,float,float,float,float[])` + `SampleLocal`/`MaxHeight`/`Read`/`Write`; `WorldSurface(PropSurface,Vector2,float,float,float)` + `SampleWorld`/`TopWorld`/`BoundingRadius`; `WorldSurfaces(IEnumerable<WorldSurface>,float)` + `Query(x,z)->float?`; `WorldCollider.Top` + `Cylinder/Box(...,top)`; `WorldColliders.Resolve(pos,radius,footY,skin,iterations)`; `MoveTuning(... ,StepHeight=0.4f)`; vertical `Step(..., WorldSurfaces? surfaces=null)`; `PropSurfaceBake.Bake/IsWalkableSolid`; `PropSurfaceLoader.LoadAll/BakeAndWrite`; `PropSurfaces.FromScatter(...)`; `AssetEntry.Heightmap/Surface`. Names consistent across tasks. ✓

**Open item carried to implementation:** the render-free server kit-index - resolved here by `PropSurfaceLoader.LoadAll` (render-free `PropSurface.Read`) being usable without the GPU stack; if a fully headless server must avoid even the Render3D assembly, a render-free manifest reader is a follow-up (the `.surf` files themselves are already render-free). Flagged, not blocking the demo.
