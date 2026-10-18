using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Seeds only the viewport state consumed by picking, with real streamer residency. BuildCore cannot
/// run without a GPU because it uploads the terrain material and installs a Scene3DChunkSink. Keep the field
/// setup here rather than add a production build API that bypasses those invariants.</summary>
sealed class HeadlessPickViewport : IDisposable
{
    readonly ViewportWorld _viewport;
    readonly TerrainStreamer _streamer;

    public HeadlessPickViewport(MapEditorScene scene)
    {
        _viewport = (ViewportWorld)Field<MapEditorScene>("_viewport").GetValue(scene)!;
        _streamer = new TerrainStreamer(new StreamerConfig(LoadRadius: 0, UnloadRadius: 1,
            MaxLoadsPerFrame: 1, ChunkSize: 60f, Async: false), new NullSink());
        _streamer.PrimeAround(Vector3.Zero);
        Set("_doc", scene.Document.Doc);
        Set("_field", scene.Controller.Field);
        Set("_streamer", _streamer);
        Set("_lastDrawFocus", Vector3.Zero);
        Set("_built", true);
    }

    public void Dispose()
    {
        Set("_built", false);
        Set("_streamer", null);
        Set("_lastDrawFocus", null);
        Set("_doc", null);
        Set("_field", null);
        _streamer.Dispose();
    }

    void Set(string name, object? value) => Field<ViewportWorld>(name).SetValue(_viewport, value);

    static FieldInfo Field<T>(string name) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing headless pick fixture field {typeof(T).Name}.{name}.");

    sealed class NullSink : IChunkSink
    {
        public object Load(ChunkCoord coord, int lod, ChunkRing ring) => coord;
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) { }
        public void Unload(ChunkCoord coord, object handle) { }
    }
}
