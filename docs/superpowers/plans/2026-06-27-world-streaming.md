# Client World Streaming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the overworld endless by streaming terrain chunks + props in/out of a ring around the player (`TerrainStreamer` in `KhaozEngine.Terrain.Render3D`), and drive `TerrainWalkSample` from it.

**Architecture:** A render-free-testable bookkeeping object (`TerrainStreamer`) maintains a loaded set of chunks keyed by integer `ChunkCoord`. Each `Update(playerPos, dt)` (1) unloads chunks beyond `UnloadRadius` immediately, (2) computes the desired load disk within `LoadRadius` plus any re-LOD work for loaded chunks whose `TerrainLod.PickLod(distance)` tier changed, (3) processes at most `MaxLoadsPerFrame` of those load/re-LOD ops nearest-first via an injected `IChunkSink`. The real GPU work (mesh build + prop scatter + draw) lives behind the sink, so the streamer is headless-tested with a fake sink and the production `Scene3DChunkSink` reuses the already-tested `TerrainChunkBuilder` + `PropScatter` + `Scene3D` paths.

**Tech Stack:** C# / net10.0, xUnit (headless tests, no GPU), `KhaozEngine.Terrain` (`TerrainField`, `PropScatter`, `RectArea`), `KhaozEngine.Terrain.Render3D` (`TerrainChunkBuilder`, `TerrainLod`, `TerrainScene3D`, `PropRenderer`), `KhaozEngine.Render3D` (`Scene3D`, `MeshHandle`).

## Global Constraints

- **Package placement:** all new types go in the EXISTING `KhaozEngine.Terrain.Render3D` package (namespace `KhaozEngine.Terrain`). No new package, no catalog churn.
- **One minor version bump** (additive API in an existing package): `7.47.0` → `7.48.0`. Update `Directory.Build.props` `<KhaozEngine5xVersion>`, `CHANGELOG.md` (newest-first detailed entry), `CHANGENOTES.md` (newest-first one-line digest), and the 3 guard declarations (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example), plus `docs/USING-KHAOZENGINE.md` streaming usage section.
- **No em-dashes** anywhere (use periods/commas/parentheses).
- **TDD, no GPU in tests.** Every new behaviour ships with a headless xUnit test in `KhaozEngine.Tests`. The streamer is tested only through a fake `IChunkSink`; no real device.
- **Stay in scope.** Do NOT build: threaded/background chunk build (main-thread amortized only), multi-cell server sharding (6b), server-side paging/`WorldStore`, distant-chunk impostors, prop-as-entity.
- **Determinism:** terrain + props are computed per-area from the seed (`TerrainField.SampleHeight`, `PropScatter.Generate`), identical regardless of load order, so streaming composes with the networked client unchanged.
- `scripts/check-doc-versions.sh` must pass; `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` all green; `dotnet pack -c Release -o ./local-feed`.

## File Structure

New files (all in `KhaozEngine.Terrain.Render3D/`, namespace `KhaozEngine.Terrain`):
- `ChunkCoord.cs` — `readonly record struct ChunkCoord(int X, int Z)`. Integer chunk index.
- `ChunkGrid.cs` — static helpers mapping `ChunkCoord` <-> world (coord-of-pos, center, region, RectArea) for a given chunk size. Single source of truth shared by streamer, sink, and tests.
- `IChunkSink.cs` — the `Load`/`ReLod`/`Unload` callback seam (exact spec signature).
- `StreamerConfig.cs` — `readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize)` + `Default`.
- `TerrainStreamer.cs` — the ring bookkeeping object.
- `Scene3DChunkSink.cs` — the production sink (builds mesh + scatters props on Load, unloads on Unload, rebuilds on ReLod, draws the loaded set + in-range props).

New test files (in `KhaozEngine.Tests/Terrain/`):
- `ChunkGridTests.cs`
- `TerrainStreamerTests.cs` — covers the spec's whole Testing list via a fake sink.

Modified:
- `TerrainWalkSample/Program.cs` — replace fixed 7x7 grid with `TerrainStreamer` + `Scene3DChunkSink`.
- `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

## Design notes (read before Task 1)

- **Two distance metrics, each the right tool:**
  - Load/unload ring: **Euclidean distance in chunk-index units** between the player's chunk and a chunk coord. Load disk = `dx*dx + dz*dz <= LoadRadius*LoadRadius`. Unload when `> UnloadRadius*UnloadRadius`. Hysteresis: a one-chunk player move changes any chunk's chunk-distance by at most 1, so `UnloadRadius >= LoadRadius + 1` guarantees no churn when oscillating across a boundary.
  - LOD: **world-metre distance** from `playerPos` (XZ) to the chunk **center**, fed straight to `TerrainLod.PickLod`. So "requested LOD == PickLod(distance)" holds exactly. No configurable tier distances (PickLod is canonical).
- **Opaque handle is a mutable holder.** `IChunkSink.ReLod` returns void, so the production sink returns a mutable `ChunkLoad` object from `Load` and mutates its `MeshHandle` in place on `ReLod`; the streamer keeps the same reference and only tracks the lod tier itself. The fake test sink returns any token (it records calls).
- **Unload is immediate and unbudgeted** (cheap GPU free; bounded by the unload pass). Only Load + ReLod count against `MaxLoadsPerFrame`.
- **Process nearest-first** so the area around the player fills before the far ring.
- **Defaults:** `LoadRadius=4`, `UnloadRadius=6`, `MaxLoadsPerFrame=3`, `ChunkSize=TerrainChunkRegion.DefaultSize` (60 m). At run speed 6 m/s (0.1 m/frame) a 60 m chunk takes ~10 s to cross; the leading edge exposes a handful of chunks per crossing, far under a 3/frame budget, and far chunks are cheap LOD2.

---

### Task 1: `ChunkCoord` + `ChunkGrid` (coord <-> world mapping)

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/ChunkCoord.cs`
- Create: `KhaozEngine.Terrain.Render3D/ChunkGrid.cs`
- Test: `KhaozEngine.Tests/Terrain/ChunkGridTests.cs`

**Interfaces:**
- Consumes: `TerrainChunkRegion`, `RectArea` (from `KhaozEngine.Terrain`), `System.Numerics.Vector2`.
- Produces:
  - `public readonly record struct ChunkCoord(int X, int Z)`
  - `ChunkGrid.CoordOf(float worldX, float worldZ, float chunkSize) -> ChunkCoord`
  - `ChunkGrid.CenterOf(ChunkCoord c, float chunkSize) -> Vector2` (world XZ center)
  - `ChunkGrid.RegionOf(ChunkCoord c, float chunkSize) -> TerrainChunkRegion`
  - `ChunkGrid.AreaOf(ChunkCoord c, float chunkSize) -> RectArea` (half-open `[origin, origin+size)`, tiling-invariant for `PropScatter`)

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class ChunkGridTests
    {
        const float Size = 60f;

        [Fact]
        public void CoordOf_floors_toward_negative_infinity()
        {
            Assert.Equal(new ChunkCoord(0, 0), ChunkGrid.CoordOf(0f, 0f, Size));
            Assert.Equal(new ChunkCoord(0, 0), ChunkGrid.CoordOf(59.9f, 0.1f, Size));
            Assert.Equal(new ChunkCoord(1, 2), ChunkGrid.CoordOf(60f, 120f, Size));
            Assert.Equal(new ChunkCoord(-1, -1), ChunkGrid.CoordOf(-0.1f, -1f, Size));   // floors down, not toward zero
            Assert.Equal(new ChunkCoord(-1, -2), ChunkGrid.CoordOf(-60f, -61f, Size));
        }

        [Fact]
        public void RegionOf_and_AreaOf_cover_the_chunk_with_half_open_tiling()
        {
            var c = new ChunkCoord(2, -3);
            TerrainChunkRegion region = ChunkGrid.RegionOf(c, Size);
            Assert.Equal(120f, region.OriginX);
            Assert.Equal(-180f, region.OriginZ);
            Assert.Equal(Size, region.Size);

            RectArea area = ChunkGrid.AreaOf(c, Size);
            Assert.Equal(120f, area.MinX);
            Assert.Equal(-180f, area.MinZ);
            Assert.Equal(180f, area.MaxX);
            Assert.Equal(-120f, area.MaxZ);

            // Adjacent chunk's area starts exactly where this one ends (no gap, no overlap).
            RectArea next = ChunkGrid.AreaOf(new ChunkCoord(3, -3), Size);
            Assert.Equal(area.MaxX, next.MinX);
        }

        [Fact]
        public void CenterOf_is_the_chunk_midpoint()
        {
            Vector2 center = ChunkGrid.CenterOf(new ChunkCoord(0, 0), Size);
            Assert.Equal(30f, center.X, 3);
            Assert.Equal(30f, center.Y, 3);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ChunkGridTests`
Expected: FAIL (build error: `ChunkCoord` / `ChunkGrid` not defined).

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Terrain.Render3D/ChunkCoord.cs`:

```csharp
namespace KhaozEngine.Terrain
{
    /// <summary>Integer index of a square terrain chunk in the streaming grid. (X, Z) maps to the world region
    /// whose -X/-Z corner is (X*chunkSize, Z*chunkSize). Value equality, so it is a dictionary key for the
    /// streamer's loaded set. Aligned with Sharding's CellCoord convention (floor(world / size)); a Sharding cell
    /// is a whole number of these chunks (the 6b ratio).</summary>
    public readonly record struct ChunkCoord(int X, int Z);
}
```

`KhaozEngine.Terrain.Render3D/ChunkGrid.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Maps a <see cref="ChunkCoord"/> to and from world space for a given chunk size. One source of truth
    /// shared by <see cref="TerrainStreamer"/>, <see cref="Scene3DChunkSink"/>, and the tests, so the grid math
    /// never drifts. <see cref="AreaOf"/> returns a half-open rect so adjacent chunks tile <see cref="PropScatter"/>
    /// exactly once (streaming-invariant).</summary>
    public static class ChunkGrid
    {
        /// <summary>The chunk containing the world point. Floors toward negative infinity (matches CellCoord), so a
        /// point on a chunk's lower edge belongs to that chunk and negatives floor downward, not toward zero.</summary>
        public static ChunkCoord CoordOf(float worldX, float worldZ, float chunkSize) =>
            new((int)MathF.Floor(worldX / chunkSize), (int)MathF.Floor(worldZ / chunkSize));

        /// <summary>World XZ midpoint of the chunk (used for distance-to-LOD).</summary>
        public static Vector2 CenterOf(ChunkCoord c, float chunkSize) =>
            new((c.X + 0.5f) * chunkSize, (c.Z + 0.5f) * chunkSize);

        /// <summary>The meshing region for the chunk (its -X/-Z corner + size).</summary>
        public static TerrainChunkRegion RegionOf(ChunkCoord c, float chunkSize) =>
            new() { OriginX = c.X * chunkSize, OriginZ = c.Z * chunkSize, Size = chunkSize };

        /// <summary>The half-open [origin, origin+size) prop-scatter window for the chunk.</summary>
        public static RectArea AreaOf(ChunkCoord c, float chunkSize) =>
            new(c.X * chunkSize, c.Z * chunkSize, (c.X + 1) * chunkSize, (c.Z + 1) * chunkSize);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ChunkGridTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Terrain.Render3D/ChunkCoord.cs KhaozEngine.Terrain.Render3D/ChunkGrid.cs KhaozEngine.Tests/Terrain/ChunkGridTests.cs
git commit -m "feat(terrain-render3d): ChunkCoord + ChunkGrid coord<->world mapping for streaming"
```

---

### Task 2: `IChunkSink` + `StreamerConfig`

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/IChunkSink.cs`
- Create: `KhaozEngine.Terrain.Render3D/StreamerConfig.cs`
- Test: none yet (these are pure declarations exercised by Task 3's tests; no behaviour to test alone).

**Interfaces:**
- Consumes: `ChunkCoord` (Task 1), `TerrainChunkRegion.DefaultSize`.
- Produces:
  - `public interface IChunkSink { object Load(ChunkCoord coord, int lod); void ReLod(ChunkCoord coord, object handle, int lod); void Unload(ChunkCoord coord, object handle); }`
  - `public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize)` with `public static StreamerConfig Default`.

- [ ] **Step 1: Write the implementation**

`KhaozEngine.Terrain.Render3D/IChunkSink.cs`:

```csharp
namespace KhaozEngine.Terrain
{
    /// <summary>The load/unload callback seam the <see cref="TerrainStreamer"/> drives. The streamer owns only the
    /// bookkeeping (which chunks are loaded, at what LOD); all GPU work (mesh build + prop scatter + draw) lives
    /// behind this interface, so the streamer is headless-testable with a fake sink. <see cref="Load"/> returns an
    /// opaque handle the streamer hands back to <see cref="ReLod"/> and <see cref="Unload"/>; the production sink
    /// uses a mutable holder it rebuilds in place on <see cref="ReLod"/> (ReLod returns void by design).</summary>
    public interface IChunkSink
    {
        /// <summary>Build the chunk at this LOD (mesh + props) and return an opaque handle for it.</summary>
        object Load(ChunkCoord coord, int lod);

        /// <summary>Rebuild an already-loaded chunk at a new LOD tier (the mesh resolution changed). The handle is
        /// the one returned by <see cref="Load"/>; the sink may mutate it in place.</summary>
        void ReLod(ChunkCoord coord, object handle, int lod);

        /// <summary>Free a chunk that has left the ring.</summary>
        void Unload(ChunkCoord coord, object handle);
    }
}
```

`KhaozEngine.Terrain.Render3D/StreamerConfig.cs`:

```csharp
namespace KhaozEngine.Terrain
{
    /// <summary>Tuning for <see cref="TerrainStreamer"/>. <see cref="LoadRadius"/> / <see cref="UnloadRadius"/> are in
    /// CHUNK units (Euclidean chunk-distance); <see cref="UnloadRadius"/> must exceed <see cref="LoadRadius"/> so the
    /// hysteresis band stops churn when the player oscillates across a chunk boundary. <see cref="MaxLoadsPerFrame"/>
    /// caps load + re-LOD ops per <c>Update</c> (unloads are immediate) so a build burst never hitches. LOD tiers come
    /// from <see cref="TerrainLod.PickLod"/> (metre distance to chunk center), not configured here.</summary>
    public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize)
    {
        /// <summary>LoadRadius 4 (~240 m view), UnloadRadius 6 (2-chunk hysteresis band), 3 builds/frame,
        /// 60 m chunks. A brisk run (6 m/s) crosses a chunk in ~10 s, far under the per-frame load budget.</summary>
        public static StreamerConfig Default => new(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: TerrainChunkRegion.DefaultSize);
    }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Terrain.Render3D/IChunkSink.cs KhaozEngine.Terrain.Render3D/StreamerConfig.cs
git commit -m "feat(terrain-render3d): IChunkSink seam + StreamerConfig tuning"
```

---

### Task 3: `TerrainStreamer` ring bookkeeping (the core, fully TDD'd via a fake sink)

This is the heart of the feature and covers the spec's entire Testing list. Build it test-by-test.

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/TerrainStreamer.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainStreamerTests.cs`

**Interfaces:**
- Consumes: `ChunkCoord`, `ChunkGrid` (Task 1), `IChunkSink`, `StreamerConfig` (Task 2), `TerrainLod.PickLod`, `System.Numerics.Vector3`.
- Produces:
  - `public sealed class TerrainStreamer`
  - ctor `TerrainStreamer(StreamerConfig config, IChunkSink sink)`
  - `void Update(Vector3 playerPos, float dt)`
  - `IReadOnlyCollection<ChunkCoord> Loaded { get; }`
  - `int LodOf(ChunkCoord coord)` (returns the currently-loaded LOD tier, or -1 if not loaded; lets tests assert requested LOD without reaching into the sink)

> Note on the spec's ctor: the spec sketch lists `TerrainStreamer(TerrainField, ScatterConfig, StreamerConfig, IChunkSink)`. The field + scatter config are only needed by the *sink* (to build meshes/props), not by the streamer's bookkeeping, so they live on `Scene3DChunkSink` (Task 4). The streamer stays field-free and purely positional, which is what makes it GPU-free testable. The fake sink in these tests takes no field.

**Fake sink for tests (define once at the top of `TerrainStreamerTests.cs`):**

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Records every sink op so tests can assert load/unload/relod behaviour with no GPU.
    sealed class FakeChunkSink : IChunkSink
    {
        public readonly List<(ChunkCoord coord, int lod)> Loads = new();
        public readonly List<(ChunkCoord coord, int lod)> ReLods = new();
        public readonly List<ChunkCoord> Unloads = new();
        // Per-Update op counts (load + relod), reset by the test harness between Updates.
        public int OpsThisFrame;

        public object Load(ChunkCoord coord, int lod) { Loads.Add((coord, lod)); OpsThisFrame++; return new Box(coord); }
        public void ReLod(ChunkCoord coord, object handle, int lod) { ReLods.Add((coord, lod)); OpsThisFrame++; }
        public void Unload(ChunkCoord coord, object handle) { Unloads.Add(coord); }

        public void ResetFrame() => OpsThisFrame = 0;
        sealed class Box { public Box(ChunkCoord c) { Coord = c; } public ChunkCoord Coord; }
    }
}
```

Implement these tests one at a time (write test -> run/fail -> implement -> run/pass -> commit). Group the commit at the end of the task, but run each test as you add it.

- [ ] **Step 1: Test — after draining, `Loaded` equals the expected load disk**

```csharp
        static TerrainStreamer Pump(TerrainStreamer s, FakeChunkSink sink, Vector3 pos, int frames)
        {
            for (int i = 0; i < frames; i++) { sink.ResetFrame(); s.Update(pos, 1f / 60f); }
            return s;
        }

        static HashSet<ChunkCoord> ExpectedDisk(ChunkCoord center, int radius)
        {
            var set = new HashSet<ChunkCoord>();
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dz * dz <= radius * radius)
                        set.Add(new ChunkCoord(center.X + dx, center.Z + dz));
            return set;
        }

        [Fact]
        public void Loaded_fills_the_expected_disk_after_draining()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 2);   // player at center of chunk (0,0)

            var expected = ExpectedDisk(new ChunkCoord(0, 0), 3);
            Assert.Equal(expected, new HashSet<ChunkCoord>(s.Loaded));
        }
```

- [ ] **Step 2: Run -> fail (type missing). Then implement the minimal streamer to pass.**

`KhaozEngine.Terrain.Render3D/TerrainStreamer.cs` (full implementation; later steps only add assertions, no rewrites):

```csharp
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Keeps the world loaded in a ring around the player. Each <see cref="Update"/>: unloads chunks beyond
    /// <c>UnloadRadius</c> (immediate), enqueues loads for chunks inside the <c>LoadRadius</c> disk that are not yet
    /// loaded and re-LODs for loaded chunks whose <see cref="TerrainLod.PickLod"/> tier changed, then processes at
    /// most <c>MaxLoadsPerFrame</c> of those (nearest first) through the injected <see cref="IChunkSink"/>. Pure
    /// bookkeeping (no GPU, no field), so it is fully headless-testable; the sink does the real work. Load/unload
    /// use Euclidean chunk-distance; the hysteresis band (UnloadRadius &gt; LoadRadius) stops churn at boundaries.</summary>
    public sealed class TerrainStreamer
    {
        readonly StreamerConfig _config;
        readonly IChunkSink _sink;
        readonly Dictionary<ChunkCoord, Entry> _loaded = new();

        sealed class Entry { public object Handle = null!; public int Lod; }

        public TerrainStreamer(StreamerConfig config, IChunkSink sink)
        {
            _config = config;
            _sink = sink;
        }

        /// <summary>The chunks currently loaded (after this frame's ops).</summary>
        public IReadOnlyCollection<ChunkCoord> Loaded => _loaded.Keys;

        /// <summary>The LOD tier a loaded chunk is currently built at, or -1 if not loaded.</summary>
        public int LodOf(ChunkCoord coord) => _loaded.TryGetValue(coord, out Entry? e) ? e.Lod : -1;

        public void Update(Vector3 playerPos, float dt)
        {
            float cs = _config.ChunkSize;
            ChunkCoord pc = ChunkGrid.CoordOf(playerPos.X, playerPos.Z, cs);

            // 1. Unload everything past the hysteresis radius (immediate, unbudgeted).
            float unloadSq = _config.UnloadRadius * (float)_config.UnloadRadius;
            // Snapshot keys so we can mutate the dictionary while iterating.
            if (_loaded.Count > 0)
            {
                var far = new List<ChunkCoord>();
                foreach (KeyValuePair<ChunkCoord, Entry> kv in _loaded)
                {
                    int dx = kv.Key.X - pc.X, dz = kv.Key.Z - pc.Z;
                    if (dx * dx + dz * dz > unloadSq) far.Add(kv.Key);
                }
                foreach (ChunkCoord c in far)
                {
                    _sink.Unload(c, _loaded[c].Handle);
                    _loaded.Remove(c);
                }
            }

            // 2. Gather pending load + re-LOD ops over the load disk, each with a metre distance for nearest-first.
            int r = _config.LoadRadius;
            float loadSq = r * (float)r;
            var pending = new List<Pending>();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dz * dz > loadSq) continue;
                var c = new ChunkCoord(pc.X + dx, pc.Z + dz);

                Vector2 center = ChunkGrid.CenterOf(c, cs);
                float mdx = center.X - playerPos.X, mdz = center.Y - playerPos.Z;
                float metreDist = MathF.Sqrt(mdx * mdx + mdz * mdz);
                int lod = TerrainLod.PickLod(metreDist);

                if (!_loaded.TryGetValue(c, out Entry? e))
                    pending.Add(new Pending(c, lod, metreDist, isLoad: true));
                else if (e.Lod != lod)
                    pending.Add(new Pending(c, lod, metreDist, isLoad: false));
            }

            // 3. Process nearest-first, capped at MaxLoadsPerFrame.
            pending.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
            int budget = _config.MaxLoadsPerFrame;
            for (int i = 0; i < pending.Count && i < budget; i++)
            {
                Pending p = pending[i];
                if (p.IsLoad)
                {
                    object handle = _sink.Load(p.Coord, p.Lod);
                    _loaded[p.Coord] = new Entry { Handle = handle, Lod = p.Lod };
                }
                else
                {
                    Entry e = _loaded[p.Coord];
                    _sink.ReLod(p.Coord, e.Handle, p.Lod);
                    e.Lod = p.Lod;
                }
            }
        }

        readonly struct Pending
        {
            public readonly ChunkCoord Coord;
            public readonly int Lod;
            public readonly float Dist;
            public readonly bool IsLoad;
            public Pending(ChunkCoord coord, int lod, float dist, bool isLoad)
            { Coord = coord; Lod = lod; Dist = dist; IsLoad = isLoad; }
        }
    }
}
```

Add `using System;` at the top for `MathF` (or use `System.MathF`). Use `using System;`.

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter TerrainStreamerTests`
Expected: PASS (the disk test).

- [ ] **Step 3: Test — moving the player loads newly-in-range and unloads newly-out-of-range chunks**

```csharp
        [Fact]
        public void Moving_loads_new_and_unloads_old()
        {
            var cfg = new StreamerConfig(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, new Vector3(30f, 0f, 30f), 2);      // centered on chunk (0,0)
            var before = new HashSet<ChunkCoord>(s.Loaded);

            // Walk +X by several chunks so the disk center moves to chunk (5,0).
            Pump(s, sink, new Vector3(5 * 60f + 30f, 0f, 30f), 4);
            var after = new HashSet<ChunkCoord>(s.Loaded);

            Assert.Equal(ExpectedDisk(new ChunkCoord(5, 0), 2), after);
            Assert.Contains(new ChunkCoord(5, 0), after);     // newly in range
            Assert.DoesNotContain(new ChunkCoord(0, 0), after); // old center beyond UnloadRadius -> gone
            Assert.True(sink.Unloads.Count > 0);
        }
```

Run -> PASS (no impl change).

- [ ] **Step 4: Test — hysteresis: oscillating across a boundary does NOT churn**

```csharp
        [Fact]
        public void Oscillating_across_a_boundary_does_not_churn()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            // Settle just inside chunk (0,0), near its +X edge.
            Pump(s, sink, new Vector3(59f, 0f, 30f), 3);
            int loadsAfterSettle = sink.Loads.Count;
            int unloadsAfterSettle = sink.Unloads.Count;

            // Oscillate across the x=60 boundary (chunk 0 <-> chunk 1) many times.
            for (int i = 0; i < 20; i++)
            {
                Pump(s, sink, new Vector3(61f, 0f, 30f), 1);   // now in chunk (1,0)
                Pump(s, sink, new Vector3(59f, 0f, 30f), 1);   // back in chunk (0,0)
            }

            // No new loads and no unloads occurred during the oscillation (hysteresis absorbed it).
            Assert.Equal(loadsAfterSettle, sink.Loads.Count);
            Assert.Equal(unloadsAfterSettle, sink.Unloads.Count);
        }
```

Run -> PASS. (If it fails, the hysteresis margin is wrong; `UnloadRadius=5` vs `LoadRadius=3` gives a 2-chunk band, comfortably absorbing the 1-chunk oscillation.)

- [ ] **Step 5: Test — requested LOD equals `PickLod(distance)`, and a tier crossing yields a ReLod**

```csharp
        [Fact]
        public void Requested_lod_matches_PickLod_of_center_distance()
        {
            var cfg = new StreamerConfig(LoadRadius: 5, UnloadRadius: 7, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var pos = new Vector3(30f, 0f, 30f);

            Pump(s, sink, pos, 3);

            foreach (ChunkCoord c in s.Loaded)
            {
                Vector2 center = ChunkGrid.CenterOf(c, 60f);
                float dist = Vector2.Distance(new Vector2(pos.X, pos.Z), center);
                Assert.Equal(TerrainLod.PickLod(dist), s.LodOf(c));
            }
        }

        [Fact]
        public void Approaching_a_far_chunk_triggers_a_ReLod_to_a_finer_tier()
        {
            var cfg = new StreamerConfig(LoadRadius: 6, UnloadRadius: 8, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            // Pick a target chunk and stand far enough that it loads at a coarse tier (LOD 2, dist > 200 m).
            var target = new ChunkCoord(5, 0);
            // Player at chunk (-1,0): target center is at x=330, player near x=-30 -> ~360 m -> LOD 2.
            Pump(s, sink, new Vector3(-30f, 0f, 30f), 4);
            Assert.Equal(2, s.LodOf(target));

            // Walk toward the target so its center distance drops into a finer tier, expect a ReLod for it.
            sink.ReLods.Clear();
            Pump(s, sink, new Vector3(5 * 60f + 30f, 0f, 30f), 6);   // stand on the target chunk -> LOD 0
            Assert.Equal(0, s.LodOf(target));
            Assert.Contains(sink.ReLods, r => r.coord == target);
        }
```

Run -> PASS.

- [ ] **Step 6: Test — amortization: at most `MaxLoadsPerFrame` sink ops per Update, backlog drains over frames**

```csharp
        [Fact]
        public void At_most_MaxLoadsPerFrame_ops_per_update_and_backlog_drains()
        {
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var pos = new Vector3(30f, 0f, 30f);

            int fullDisk = ExpectedDisk(new ChunkCoord(0, 0), 4).Count;

            // First Update from empty: only MaxLoadsPerFrame ops happen.
            sink.ResetFrame();
            s.Update(pos, 1f / 60f);
            Assert.True(sink.OpsThisFrame <= cfg.MaxLoadsPerFrame);
            Assert.Equal(cfg.MaxLoadsPerFrame, s.Loaded.Count);  // only the budget loaded so far

            // Keep pumping; every frame stays within budget and the disk eventually fills.
            for (int i = 0; i < 50 && s.Loaded.Count < fullDisk; i++)
            {
                sink.ResetFrame();
                s.Update(pos, 1f / 60f);
                Assert.True(sink.OpsThisFrame <= cfg.MaxLoadsPerFrame, $"frame {i} exceeded budget: {sink.OpsThisFrame}");
            }
            Assert.Equal(fullDisk, s.Loaded.Count);              // backlog drained
        }
```

Run -> PASS.

- [ ] **Step 7: Test — nearest-first ordering (player's own chunk loads on frame 1)**

```csharp
        [Fact]
        public void Nearest_chunk_loads_first()
        {
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 1, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            sink.ResetFrame();
            s.Update(new Vector3(30f, 0f, 30f), 1f / 60f);   // standing on chunk (0,0)

            Assert.Single(sink.Loads);
            Assert.Equal(new ChunkCoord(0, 0), sink.Loads[0].coord);   // the player's own chunk first
        }
```

Run -> PASS.

- [ ] **Step 8: Run the whole streamer test file**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter TerrainStreamerTests`
Expected: PASS (all 7).

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.Terrain.Render3D/TerrainStreamer.cs KhaozEngine.Tests/Terrain/TerrainStreamerTests.cs
git commit -m "feat(terrain-render3d): TerrainStreamer ring load/unload/re-LOD with hysteresis + amortized budget"
```

---

### Task 4: `Scene3DChunkSink` (production sink — mesh + props + draw)

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainStreamerTests.cs` (add one headless test that the per-chunk prop placements the sink would scatter match `PropScatter.Generate` for that chunk's area; this needs no GPU).

**Interfaces:**
- Consumes: `Scene3D`, `MeshHandle` (`KhaozEngine.Render3D`), `TerrainField`, `ScatterConfig`, `PropScatter`, `PropPlacement`, `RectArea` (`KhaozEngine.Terrain`), `TerrainChunkBuilder`, `TerrainScene3D` (`LoadTerrainChunk`/`DrawTerrainChunk`), `PropRenderer.DrawProps`, `ChunkGrid` (Task 1), `IChunkSink` (Task 2).
- Produces:
  - `public sealed class Scene3DChunkSink : IChunkSink`
  - ctor `Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter, IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius)`
  - `object Load(ChunkCoord, int)` / `void ReLod(...)` / `void Unload(...)` (interface)
  - `void Draw(Vector3 focus)` — draws every loaded chunk's mesh + its in-range props (XZ-culled to `propDrawRadius` around `focus`).
  - internal `IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord)` — exposed (InternalsVisibleTo) so the prop-placement test can assert parity with `PropScatter.Generate` without a GPU.

**Why a test here at all:** the GPU bits (`LoadTerrainChunk`, draw) can't run headless, but the *prop scatter per chunk* is pure data and the spec's Testing list requires "prop placements requested for a loaded chunk match `PropScatter.Generate` for that chunk's area." Expose that one pure method and test it.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void Sink_scatters_props_matching_PropScatter_for_the_chunk_area()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig scatter = ScatterConfig.ForestRing();
            float size = 60f;
            var sink = new Scene3DChunkSink(scene: null!, field, scatter,
                propMeshes: new Dictionary<string, MeshHandle>(), chunkSize: size, propDrawRadius: 90f);

            var coord = new ChunkCoord(-2, -2);   // a meadow chunk with props
            var expected = PropScatter.Generate(field, scatter, ChunkGrid.AreaOf(coord, size));
            IReadOnlyList<PropPlacement> got = sink.ScatterFor(coord);

            Assert.Equal(expected.Count, got.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Id, got[i].Id);
                Assert.Equal(expected[i].X, got[i].X, 3);
                Assert.Equal(expected[i].Z, got[i].Z, 3);
            }
        }
```

(Add `using KhaozEngine.Render3D;` to the test file for `MeshHandle`.)

- [ ] **Step 2: Run -> fail (`Scene3DChunkSink` missing).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter TerrainStreamerTests`
Expected: FAIL (type not found).

- [ ] **Step 3: Implement `Scene3DChunkSink`**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The production <see cref="IChunkSink"/>: turns the streamer's load/unload/re-LOD calls into real
    /// <see cref="Scene3D"/> work. <c>Load</c> builds the chunk mesh at the requested LOD (<see cref="TerrainChunkBuilder"/>)
    /// + scatters the chunk's props (<see cref="PropScatter"/> over the chunk's half-open <see cref="RectArea"/>),
    /// uploads the mesh, and returns a mutable <see cref="ChunkLoad"/> holder. <c>ReLod</c> rebuilds the mesh at the
    /// new tier in place (props are LOD-independent, so they are kept). <c>Unload</c> frees the mesh. <c>Draw</c>
    /// queues every loaded chunk + its props within <c>propDrawRadius</c> (XZ) of the focus each frame. Ships in the
    /// package so every game gets streaming for free.</summary>
    public sealed class Scene3DChunkSink : IChunkSink
    {
        readonly Scene3D _scene;
        readonly TerrainField _field;
        readonly ScatterConfig _scatter;
        readonly IReadOnlyDictionary<string, MeshHandle> _propMeshes;
        readonly float _chunkSize;
        readonly float _propDrawRadius;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();

        public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                                IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius)
        {
            _scene = scene;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _scatter = scatter ?? throw new ArgumentNullException(nameof(scatter));
            _propMeshes = propMeshes ?? throw new ArgumentNullException(nameof(propMeshes));
            _chunkSize = chunkSize;
            _propDrawRadius = propDrawRadius;
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            public IReadOnlyList<PropPlacement> Props = Array.Empty<PropPlacement>();
            public int Lod;
        }

        /// <summary>The deterministic prop placements for a chunk's area (pure; headless-testable).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) =>
            PropScatter.Generate(_field, _scatter, ChunkGrid.AreaOf(coord, _chunkSize));

        public object Load(ChunkCoord coord, int lod)
        {
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            var load = new ChunkLoad
            {
                Mesh = _scene.LoadTerrainChunk(mesh),
                Props = ScatterFor(coord),
                Lod = lod,
            };
            _loaded[coord] = load;
            return load;
        }

        public void ReLod(ChunkCoord coord, object handle, int lod)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            load.Mesh = _scene.LoadTerrainChunk(mesh);
            load.Lod = lod;
            // Props are LOD-independent; keep load.Props.
        }

        public void Unload(ChunkCoord coord, object handle)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            _loaded.Remove(coord);
        }

        /// <summary>Draw every loaded chunk mesh and its in-range props (XZ-culled to propDrawRadius of focus).</summary>
        public void Draw(Vector3 focus)
        {
            foreach (ChunkLoad load in _loaded.Values)
            {
                _scene.DrawTerrainChunk(load.Mesh);
                _scene.DrawProps(load.Props, _propMeshes, focus, _propDrawRadius);
            }
        }
    }
}
```

- [ ] **Step 4: Run -> pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter TerrainStreamerTests`
Expected: PASS (prop-parity test green; the GPU `Load`/`Draw` paths are exercised by the sample, not unit-tested, per "no real device in unit tests").

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs KhaozEngine.Tests/Terrain/TerrainStreamerTests.cs
git commit -m "feat(terrain-render3d): Scene3DChunkSink (build mesh + scatter props + draw the loaded ring)"
```

---

### Task 5: Integrate the streamer into `TerrainWalkSample`

Replace the fixed 7x7 grid with the streamer so the world streams forever.

**Files:**
- Modify: `TerrainWalkSample/Program.cs`

**Interfaces:**
- Consumes: `TerrainStreamer`, `StreamerConfig`, `Scene3DChunkSink` (Tasks 2-4), existing `CharacterController3D`, `FollowCamera3D`, `PropLoader`/`AssetManifest`, `TerrainCollision`.

- [ ] **Step 1: Edit `TerrainWalkApp`**

Replace the chunk-grid fields and load loop with the streamer. Concretely:

Remove `const int GridRadius = 3;` and `readonly List<MeshHandle> _chunks = new();`.
Add fields:

```csharp
    TerrainStreamer _streamer = null!;
    Scene3DChunkSink _chunkSink = null!;
```

In `OnLoad`, replace the fixed grid block:

```csharp
        // Fixed NxN grid of chunks around the origin, meshed at the densest LOD (no streaming here).
        float size = TerrainChunkRegion.DefaultSize;
        for (int gz = -GridRadius; gz <= GridRadius; gz++)
            for (int gx = -GridRadius; gx <= GridRadius; gx++)
            {
                var region = new TerrainChunkRegion { OriginX = gx * size, OriginZ = gz * size, Size = size };
                var chunk = TerrainChunkBuilder.Build(_field, region, lod: 0);
                _chunks.Add(sc.LoadTerrainChunk(chunk));
            }
```

with the streamer wiring (place it *after* the prop meshes are loaded, since the sink needs `_propMeshes`):

```csharp
        // Streamed, endless world: the streamer maintains a ring of chunks (+ their props) around the player,
        // building/unloading on the main thread within a per-frame budget. Walk any direction -> the world streams.
        _chunkSink = new Scene3DChunkSink(sc, _field, ScatterConfig.ForestRing(), _propMeshes,
            chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius);
        _streamer = new TerrainStreamer(StreamerConfig.Default, _chunkSink);
        _streamer.Update(_character.Position, 0f);   // prime the first ring before the first frame
```

Delete the now-unused `_placements` field and the `PropScatter.Generate(...)` clearing-ring line + its `Console.WriteLine` (props are now per-chunk via the sink). Keep loading `_propMeshes` from the manifest (the sink needs them).

In `OnUpdate`, after the character moves, drive the streamer:

```csharp
        _streamer.Update(_character.Position, dt);
```

In `OnDraw3D`, replace the chunk loop + `DrawProps` with the sink draw:

```csharp
    protected override void OnDraw3D(Scene3D scene)
    {
        // Streamed terrain + props (the sink draws every loaded chunk and its in-range props).
        _chunkSink.Draw(_character.Position);

        // Draw the capsule so its base sits on the ground (Position is the capsule centre).
        Vector3 p = _character.Position;
        scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
    }
```

Update the file header comment: change "nothing here is streamed (fixed chunk grid)" to note the streamer drives an endless world.

- [ ] **Step 2: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj`
Expected: Build succeeded, no unused-field warnings for `_chunks`/`_placements`/`GridRadius` (they were removed).

- [ ] **Step 3: Headless smoke run (KE_MAX_FRAMES)**

Run: `KE_MAX_FRAMES=8 dotnet run --project TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: exits 0, renders 8 frames without throwing. (This exercises the real GPU `Load`/`Draw` path the unit tests skip.)

> If the machine has no display/GPU for a headless run, skip this step and rely on the user's windowed validation at the end; note it in the commit message.

- [ ] **Step 4: Commit**

```bash
git add TerrainWalkSample/Program.cs
git commit -m "sample(terrain-walk): stream an endless world via TerrainStreamer instead of the fixed 7x7 grid"
```

---

### Task 6: Release — version bump + docs + pack

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>7.47.0</KhaozEngine5xVersion>` to `7.48.0`.

- [ ] **Step 2: `CHANGELOG.md` — newest-first detailed entry**

Add at the top (under the title), copy the exact heading style of the existing top entry:

```markdown
## 7.48.0

### Added
- **`KhaozEngine.Terrain.Render3D` client world streaming.** `TerrainStreamer` keeps a ring of terrain chunks
  (+ their deterministic props) loaded around the player so the overworld is effectively endless. `Update(playerPos, dt)`
  loads chunks inside `LoadRadius` (Euclidean chunk-distance), unloads past `UnloadRadius` (hysteresis band stops
  churn at boundaries), re-LODs loaded chunks whose `TerrainLod.PickLod` tier changed, and amortizes load/re-LOD to
  `MaxLoadsPerFrame` per update (main-thread, no hitch). New public API: `ChunkCoord`, `ChunkGrid` (coord<->world),
  `IChunkSink` (load/unload callback seam), `StreamerConfig` (+ `Default` = LoadRadius 4 / UnloadRadius 6 /
  MaxLoadsPerFrame 3 / 60 m chunks), `TerrainStreamer`, and `Scene3DChunkSink` (the production sink: builds the chunk
  mesh + scatters props on load, rebuilds on re-LOD, frees on unload, and draws the loaded ring + in-range props).
  The streamer's bookkeeping is GPU-free (headless-tested via a fake sink). `TerrainWalkSample` now streams an endless
  world instead of a fixed 7x7 grid. Server stays single-`World` (multi-cell sharding is sub-project 6b).
```

(Match the actual existing section format if it differs; mirror the latest entry's headings.)

- [ ] **Step 3: `CHANGENOTES.md` — one-line digest (newest first)**

Add at the top:

```markdown
- 7.48.0: Client world streaming. TerrainStreamer (in Terrain.Render3D) loads/unloads terrain chunks + props in a hysteresis ring around the player with distance-LOD re-meshing and an amortized per-frame build budget; TerrainWalkSample walks an endless world. Server stays single-World (sharding is 6b).
```

- [ ] **Step 4: Update the 3 guard declarations**

- `docs/CONSUMERS.md`: bump the "Engine current version" line to `7.48.0`.
- `docs/ROADMAP.md`: bump "Current released version" to `7.48.0` (and tick sub-project 6a under the overworld track if it lists them).
- `README.md`: bump the `<PackageReference ... Version="7.47.0" />` example to `7.48.0`.

Find the exact strings first:

```bash
grep -rn "7.47.0" docs/CONSUMERS.md docs/ROADMAP.md README.md
```

- [ ] **Step 5: `docs/USING-KHAOZENGINE.md` — streaming usage section**

Add a short section near the terrain usage. Content:

````markdown
### World streaming (endless terrain + props)

`TerrainStreamer` (in `KhaozEngine.Terrain.Render3D`) keeps a ring of chunks loaded around the player so the world
is effectively endless. Wire player position -> `Update` and draw the loaded set:

```csharp
var field = new TerrainField(TerrainPresets.Clearing());
var sink  = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), propMeshes,
                                 chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: 90f);
var streamer = new TerrainStreamer(StreamerConfig.Default, sink);

// each frame:
streamer.Update(playerPos, dt);   // loads/unloads/re-LODs within MaxLoadsPerFrame
// in your 3D draw pass:
sink.Draw(playerPos);             // draws every loaded chunk + its in-range props
```

`StreamerConfig.Default` is LoadRadius 4 / UnloadRadius 6 (hysteresis) / MaxLoadsPerFrame 3 / 60 m chunks. Terrain
and props are computed per-area from the seed, so streaming composes with the networked client unchanged (each client
streams locally; nothing about the world is replicated). For a custom sink (your own mesh/prop pipeline), implement
`IChunkSink` and pass it to `TerrainStreamer`.
````

- [ ] **Step 6: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (all three declarations == 7.48.0).

- [ ] **Step 7: Full doc sweep — grep the new type names**

```bash
grep -rn "TerrainStreamer\|Scene3DChunkSink\|IChunkSink\|StreamerConfig\|ChunkCoord\|ChunkGrid" --include=*.md .
```

Confirm `README.md` package-catalog description of `Terrain.Render3D` and `KhaozEngine/CLAUDE.md`'s Terrain.Render3D bullet mention streaming (add "+ `TerrainStreamer` client world streaming" to the `Terrain.Render3D` description in both if absent). No new package, so no catalog table row changes.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all green (existing + the new ChunkGrid/TerrainStreamer tests).

- [ ] **Step 9: Pack**

```bash
dotnet pack -c Release -o ./local-feed
```

Expected: all packable projects pack to `./local-feed` at `7.48.0`.

- [ ] **Step 10: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md docs/USING-KHAOZENGINE.md CLAUDE.md
git commit -m "release(7.48.0): client world streaming (TerrainStreamer + Scene3DChunkSink) in Terrain.Render3D"
```

---

### Task 7: Merge, tag, push, clean up

- [ ] **Step 1: Merge to main from the main checkout**

From the main repo root (not the worktree), merge `worktree-feature+world-streaming` into `main`, run the full test suite on the merged result.

- [ ] **Step 2: Repack from the main root** (the worktree's local-feed is deleted on removal)

```bash
dotnet pack -c Release -o ./local-feed
```

- [ ] **Step 3: Tag + push**

```bash
git tag v7.48.0
git push origin main
git push origin v7.48.0
```

- [ ] **Step 4: Clean up the worktree + merged branch** (local + remote if it was ever pushed).

- [ ] **Step 5: Windowed validation handoff** — give the user a single language-tagged ```bash block that builds+runs `TerrainWalkSample` from the worktree's absolute path (or the main checkout if already merged + removed).

## Self-Review

**Spec coverage:**
- `TerrainStreamer` + `StreamerConfig` + `IChunkSink` + Scene3D sink in `Terrain.Render3D` — Tasks 1-4. ✓
- Load ring / unload hysteresis / re-LOD on tier crossing / amortize — Task 3 (impl) + tests. ✓
- Default sink: `LoadTerrainChunk` + `PropScatter.Generate` over RectArea on Load, `UnloadMesh` on Unload, rebuild on ReLod — Task 4. ✓
- Integrate into `TerrainWalkSample`; composes with networked client (per-area determinism) — Task 5 + Global Constraints. ✓
- Testing list (loaded==ring, move loads/unloads, hysteresis no-churn, requested LOD==PickLod + ReLod on crossing, <=MaxLoadsPerFrame ops, prop parity) — Task 3 Steps 1-7 + Task 4 Step 1. ✓
- Release: minor bump, Directory.Build.props + CHANGELOG + CHANGENOTES + 3 guards + USING doc; pack; tag; push — Tasks 6-7. ✓
- Out-of-scope items (threaded build, sharding, WorldStore paging, impostors, prop-as-entity) — none built. ✓

**Placeholder scan:** no TBD/TODO; all code shown in full. ✓

**Type consistency:** `ChunkCoord(int X, int Z)`, `ChunkGrid.{CoordOf,CenterOf,RegionOf,AreaOf}`, `StreamerConfig(LoadRadius,UnloadRadius,MaxLoadsPerFrame,ChunkSize)`, `IChunkSink.{Load,ReLod,Unload}`, `TerrainStreamer.{Update,Loaded,LodOf}`, `Scene3DChunkSink.{Load,ReLod,Unload,Draw,ScatterFor,ChunkLoad}` — consistent across tasks. ✓
</content>
</invoke>
