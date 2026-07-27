using System.Runtime.CompilerServices;
using KhaozEngine.Terrain;

// The streamer core moved to KhaozEngine.Terrain so a headless server can reference it without dragging in
// Render3D/Physics (docs/design/TILED-MAPDOC-AND-RESIDENCY-DESIGN-2026-07-27.md section 9). Every moved type
// keeps its namespace and source compatibility is total, but an assembly compiled against the old
// KhaozEngine.Terrain.Render3D would resolve these types here and fail with a TypeLoadException against the
// new package set without these forwarders. They make the move binary-compatible too, so it ships as a minor.
[assembly: TypeForwardedTo(typeof(ChunkCoord))]
[assembly: TypeForwardedTo(typeof(ChunkGrid))]
[assembly: TypeForwardedTo(typeof(ChunkRing))]
[assembly: TypeForwardedTo(typeof(ChunkBuild<>))]
[assembly: TypeForwardedTo(typeof(ChunkBuildException))]
[assembly: TypeForwardedTo(typeof(ChunkBuildScheduler<>))]
[assembly: TypeForwardedTo(typeof(IChunkBuildDispatcher))]
[assembly: TypeForwardedTo(typeof(TaskChunkBuildDispatcher))]
[assembly: TypeForwardedTo(typeof(IChunkSink))]
[assembly: TypeForwardedTo(typeof(IAsyncChunkSink))]
[assembly: TypeForwardedTo(typeof(StreamerConfig))]
[assembly: TypeForwardedTo(typeof(TerrainStreamer))]
[assembly: TypeForwardedTo(typeof(TerrainLod))]
[assembly: TypeForwardedTo(typeof(TerrainLodTier))]
[assembly: TypeForwardedTo(typeof(TerrainLodConfig))]
[assembly: TypeForwardedTo(typeof(TerrainChunkRegion))]
