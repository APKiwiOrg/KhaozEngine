# Client world streaming design (`TerrainStreamer`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO overworld render-scale track, sub-project 6a (walk an endless world)

## Context

The overworld track has shipped terrain, the walkable slice, prop scatter, and the networked
overworld (`7.43.0`–`7.47.0`): two clients walk the same *fixed-grid* forested world over the
network. The world is bounded — the client builds a fixed chunk grid around the origin.

"World streaming + sharding" is really two separable pieces; per the brainstorm it is **split**, and
this spec is the first half:

- **6a — Client world streaming (this spec)**: load/unload terrain chunks + props in a ring around
  the player so the world is effectively endless. Server stays single-`World` (with the AoI it
  already has). The visible "walk forever" win; bounded, render-side.
- **6b — Multi-cell server sharding (next spec)**: wire `WorldServer` onto the built `ShardHost`
  (per-cell sims, cross-cell ghosting, exactly-once handoff, client cell transitions). The scale
  step.

Prior specs: terrain / walkable-slice / prop-scatter / networked-overworld (all 2026-06-27).
Program reference repo: `https://github.com/levy-street/world-of-claudecraft` (its `render/terrain.ts`
chunks terrain with bounding volumes + distance LOD + skirts; a player-centred pool streams foliage).

### What already exists (this is assembly, not new rendering tech)

- `Scene3D.LoadTerrainChunk` (→ `MeshHandle`) + `Scene3D.UnloadMesh` — build and free chunk meshes.
- `TerrainLod.PickLod(distance)` and the chunk builder's LOD tiers + skirts (shipped with terrain).
- `TerrainField.SampleHeight` and `PropScatter.Generate(field, config, area)` are **per-area
  deterministic** (coordinate-hash) — terrain and props for any region are computed from the seed,
  no server fetch, identical regardless of load order.
- The server multi-cell stack (`ShardHost`, `CellSim`, `Ghost`, `Migrating`) exists but is **6b's**
  concern; this slice leaves `WorldServer` single-`World`.

### Locked decisions (from brainstorming)

1. **Split; client streaming first.** Server stays single-`World` here.
2. `TerrainStreamer` lives in `KhaozEngine.Terrain.Render3D` (reusable; the sample drives it).
3. Ring **load/unload with hysteresis**, **distance LOD** (rebuild on tier crossing), and
   **amortized main-thread loading** (a per-frame build budget) are all in — they are what makes a
   big world real rather than a toy.
4. **Threaded/background chunk build is deferred** (main-thread amortized now; `IJobScheduler` build
   is a later enhancement).
5. The streamer is built **GPU-free testable** via injected load/unload callbacks (an `IChunkSink`),
   so its bookkeeping is headless-tested; the real mesh build remains the already-tested chunk
   builder.

## Component: `TerrainStreamer` — `KhaozEngine.Terrain.Render3D`

Keeps the world loaded in a ring around the player.

```csharp
public interface IChunkSink
{
    object Load(ChunkCoord coord, int lod);   // build mesh + scatter props; returns an opaque handle
    void   ReLod(ChunkCoord coord, object handle, int lod);
    void   Unload(ChunkCoord coord, object handle);
}

public sealed class TerrainStreamer
{
    public TerrainStreamer(TerrainField field, ScatterConfig scatter, StreamerConfig config, IChunkSink sink);
    public void Update(Vector3 playerPos, float dt);  // maintains the loaded set
    public IReadOnlyCollection<ChunkCoord> Loaded { get; }
}

public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame /* , LOD tier distances */);
```

`Update`:
1. **Load ring** — chunks within `LoadRadius` (in chunks) of the player's chunk that aren't loaded →
   enqueue.
2. **Unload** — loaded chunks beyond `UnloadRadius` (> `LoadRadius`, hysteresis) → `sink.Unload`.
3. **Re-LOD** — for loaded chunks whose `PickLod(distance)` changed tier → enqueue a `sink.ReLod`.
4. **Amortize** — process at most `MaxLoadsPerFrame` load/re-LOD ops from the queue per `Update`.

The default `IChunkSink` (in the sample, or a provided `Scene3DChunkSink`) calls `LoadTerrainChunk` +
`PropScatter.Generate` over the chunk's `RectArea` on `Load`, `UnloadMesh` on `Unload`, and rebuilds
on `ReLod`. Loaded chunks' meshes + in-range prop instances are drawn through `Scene3D` each frame.

## Sample

`TerrainWalkSample` replaces its fixed 7×7 grid with a `TerrainStreamer` driven by the player
position — walk any direction and the world streams forever. The networked client (`NetWorld`)
composes the same streamer (terrain/props are client-deterministic, so streaming is orthogonal to
replication).

## Data flow

```
player moves → TerrainStreamer.Update(pos, dt):
   load-ring enqueue · unload-far (hysteresis) · re-LOD tier crossings · amortize to MaxLoadsPerFrame
   → IChunkSink (LoadTerrainChunk + PropScatter) → Scene3D draws loaded chunks + in-range props
```

## Testing (headless, manager logic via a fake `IChunkSink` — no GPU)

- After `Update` at a position, `Loaded` == the expected ring for `LoadRadius`.
- Moving the player loads the newly-in-range chunks and unloads the newly-out-of-range ones.
- **Hysteresis**: oscillating the player across a chunk boundary does **not** churn load/unload.
- **LOD**: a chunk's requested LOD equals `PickLod(distance)`; a tier crossing produces a `ReLod`.
- **Amortization**: at most `MaxLoadsPerFrame` sink ops per `Update`; the backlog drains over frames.
- Prop placements requested for a loaded chunk match `PropScatter.Generate` for that chunk's area.

## Scope

### In scope

- `TerrainStreamer` + `StreamerConfig` + `IChunkSink` (+ a `Scene3D`-backed sink) in
  `KhaozEngine.Terrain.Render3D`: ring load/unload, hysteresis, distance LOD with re-LOD, amortized
  main-thread loading, per-chunk prop scatter.
- Integrate into `TerrainWalkSample` (and it composes with the networked client).
- Headless tests (manager logic via fake sink).
- Release: **minor** bump (additive API in an existing package — no new package). Update
  `Directory.Build.props`, `CHANGELOG.md` + `CHANGENOTES.md`, the 3 guard declarations,
  `docs/USING-KHAOZENGINE.md` (streaming usage section). End with the sample boot command.

### Out of scope (named so they are not forgotten)

- **Threaded/background chunk build** — main-thread amortized now; `IJobScheduler` build is a later
  enhancement.
- **Multi-cell server sharding** — sub-project **6b** (`WorldServer` onto `ShardHost`).
- **Server-side streaming / `WorldStore` paging** — server stays single-`World`.
- **Distant-chunk impostors** — distance LOD only.
- **Prop-as-entity** — props remain client-deterministic scatter.

## Engine-first placement

- `TerrainStreamer` (+ config + sink interface + Scene3D sink) → `KhaozEngine.Terrain.Render3D`
  (reusable). The sample only wires player position → `Update` and draws the loaded set.

## Open items to confirm during implementation

- `ChunkCoord` type — reuse/align with the terrain chunk grid (and, for 6b, with `Sharding`'s
  `CellCoord` ratio).
- Default `LoadRadius` / `UnloadRadius` / `MaxLoadsPerFrame` and the LOD tier distances (tune so a
  brisk walk never outruns the load budget and never hitches).
- Whether the `Scene3D` chunk sink ships in `Terrain.Render3D` or the sample (prefer the package, so
  every game gets it).
- Per-frame prop-instance draw radius vs the chunk load radius (props can cull tighter than terrain).

## The overworld program (for orientation)

1–4 ✅ (asset foundation / terrain / walkable / prop scatter). 5 ✅ networked overworld (`7.47.0`).
**6a World streaming — this spec.**
6b Multi-cell server sharding — `WorldServer` onto `ShardHost` (ghosting + handoff + client cell
transitions); builds on this streamer + the built `Sharding` stack.
7 Procedural dungeon generator — parallel track.
Later polish: PBR splat textures + water; glTF animation-clip playback → animated characters.
