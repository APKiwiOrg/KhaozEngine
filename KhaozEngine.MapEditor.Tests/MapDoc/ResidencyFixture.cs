using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Shared fixtures for the residency tests: a synthetic document occupying exactly the tiles a test
    /// names, focus helpers in world space, and the fake sinks and dispatcher the lifecycle assertions run
    /// through. Documents are built in memory through <see cref="MapDocumentSource.FromDocument"/>, so no test
    /// here touches the disk and <c>ReadTile</c> still throws for an unindexed tile, which is what the
    /// absent-tile assertion rides on.</summary>
    internal static class ResidencyFixture
    {
        internal const float Tile = 512f;

        /// <summary>A document whose occupied tile set is EXACTLY <paramref name="tiles"/>, one placement each,
        /// positioned well inside its tile so no boundary rule is in play.</summary>
        internal static MapDocument Doc(params (int X, int Z)[] tiles)
        {
            var doc = new MapDocument
            {
                Id = "residency-zone",
                DisplayName = "Residency Zone",
                Bounds = new MapBounds { MinX = -100_000f, MinZ = -100_000f, MaxX = 100_000f, MaxZ = 100_000f },
                TileSize = Tile,
            };
            foreach ((int x, int z) in tiles)
                doc.Placements.Add(new MapPlacement
                {
                    Id = Name(x, z),
                    // Kind, not just Id: MapRuntime maps a document placement to a PropPlacement whose Id is the
                    // KIND (the asset-manifest kit id), so a per-tile kind is what makes an engine placement
                    // traceable back to the tile it streamed in from.
                    Kind = Name(x, z),
                    X = x * Tile + 10f,
                    Z = z * Tile + 20f,
                    Y = 0f,
                });
            return doc;
        }

        internal static MapDocumentSource Source(params (int X, int Z)[] tiles) =>
            MapDocumentSource.FromDocument(Doc(tiles));

        internal static string Name(int x, int z) => $"p_{x}_{z}";

        /// <summary>Every tile in the inclusive Chebyshev square of the given radius around the origin
        /// tile.</summary>
        internal static (int X, int Z)[] Square(int radius, int centreX = 0, int centreZ = 0)
        {
            var tiles = new List<(int, int)>();
            for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
                tiles.Add((centreX + x, centreZ + z));
            return tiles.ToArray();
        }

        /// <summary>A world focus inside a tile. The fractions default to the tile centre. Pass 0.01f to sit hard
        /// against a corner, which is the position every coverage guarantee is stated for.</summary>
        internal static Vector3 At(int tileX, int tileZ, float fx = 0.5f, float fz = 0.5f) =>
            new((tileX + fx) * Tile, 0f, (tileZ + fz) * Tile);

        internal static MapTileCoord C(int x, int z) => new(x, z);
    }

    /// <summary>Records every residency notification, in order.</summary>
    internal sealed class RecordingTileSink : IMapTileSink
    {
        public readonly List<(MapTileCoord Coord, ChunkRing Ring)> Loaded = new();
        public readonly List<(MapTileCoord Coord, ChunkRing Ring)> RingChanged = new();
        public readonly List<MapTileCoord> Unloaded = new();

        public void TileLoaded(MapTileCoord coord, MapTileContent content, ChunkRing ring)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));
            Loaded.Add((coord, ring));
        }

        public void TileRingChanged(MapTileCoord coord, MapTileContent content, ChunkRing ring)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));
            RingChanged.Add((coord, ring));
        }

        public void TileUnloaded(MapTileCoord coord) => Unloaded.Add(coord);

        public void Reset()
        {
            Loaded.Clear();
            RingChanged.Clear();
            Unloaded.Clear();
        }

        public IEnumerable<MapTileCoord> LoadedCoords()
        {
            foreach ((MapTileCoord coord, ChunkRing _) in Loaded) yield return coord;
        }
    }

    /// <summary>A CONSUMER sink of the kind the physics seam exists for: it hangs a static body off every
    /// placement of a Gameplay tile and frees them when the tile leaves or drops to Decor. The engine never
    /// touches a body, which is the whole point, so this fake is what proves the seam is usable at all.</summary>
    internal sealed class ColliderConsumerSink : IMapTileSink
    {
        readonly Dictionary<MapTileCoord, List<string>> _bodies = new();
        public int AddCalls;
        public int RemoveCalls;

        public IReadOnlyDictionary<MapTileCoord, List<string>> Bodies => _bodies;
        public int BodyCount { get { int n = 0; foreach (List<string> b in _bodies.Values) n += b.Count; return n; } }
        public bool HasBodies(MapTileCoord coord) => _bodies.ContainsKey(coord);

        public void TileLoaded(MapTileCoord coord, MapTileContent content, ChunkRing ring)
        {
            if (ring == ChunkRing.Gameplay) Add(coord, content);
        }

        public void TileRingChanged(MapTileCoord coord, MapTileContent content, ChunkRing ring)
        {
            if (ring == ChunkRing.Gameplay) Add(coord, content);
            else Remove(coord);
        }

        public void TileUnloaded(MapTileCoord coord) => Remove(coord);

        void Add(MapTileCoord coord, MapTileContent content)
        {
            var bodies = new List<string>();
            foreach (MapPlacement p in content.Placements) bodies.Add(p.Id);
            _bodies[coord] = bodies;
            AddCalls++;
        }

        void Remove(MapTileCoord coord)
        {
            if (_bodies.Remove(coord)) RemoveCalls++;
        }
    }

    /// <summary>Queues tile reads instead of running them, so a test drives completion order by hand. Mirrors
    /// the streamer suite's manual dispatcher.</summary>
    internal sealed class ManualTileDispatcher : IChunkBuildDispatcher
    {
        readonly List<Action> _queued = new();
        public int PendingCount => _queued.Count;
        public int Scheduled { get; private set; }

        public void Schedule(Action build) { _queued.Add(build); Scheduled++; }

        public void RunAt(int index) { Action a = _queued[index]; _queued.RemoveAt(index); a(); }

        public void RunAll()
        {
            var copy = new List<Action>(_queued);
            _queued.Clear();
            foreach (Action a in copy) a();
        }

        /// <summary>Runs every queued body last-to-first, so a test can prove the APPLY order is nearest-first
        /// rather than completion order.</summary>
        public void RunReverse() { while (_queued.Count > 0) RunAt(_queued.Count - 1); }

        public void Drain() => RunAll();
    }
}
