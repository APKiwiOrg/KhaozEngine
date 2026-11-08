using System;
using System.Numerics;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Seeds only the viewport state consumed by picking, with real streamer residency. BuildCore cannot
/// run without a GPU because it uploads the terrain material and installs a Scene3DChunkSink. Keep the streamer
/// fixture unchanged while an internal scope binds and restores the picking state.</summary>
sealed class HeadlessPickViewport : IDisposable
{
    readonly IDisposable _binding;
    readonly TerrainStreamer _streamer;

    public HeadlessPickViewport(MapEditorScene scene)
    {
        _streamer = new TerrainStreamer(new StreamerConfig(LoadRadius: 0, UnloadRadius: 1,
            MaxLoadsPerFrame: 1, ChunkSize: 60f, Async: false), new NullSink());
        _streamer.PrimeAround(Vector3.Zero);
        _binding = scene.BindPickingViewportForTests(_streamer, Vector3.Zero);
    }

    public void Dispose()
    {
        _binding.Dispose();
        _streamer.Dispose();
    }

    sealed class NullSink : IChunkSink
    {
        public object Load(ChunkCoord coord, int lod, ChunkRing ring) => coord;
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) { }
        public void Unload(ChunkCoord coord, object handle) { }
    }
}
