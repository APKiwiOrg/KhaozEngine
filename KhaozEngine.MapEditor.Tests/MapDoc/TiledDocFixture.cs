using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Shared fixtures for the tiled-format tests: a synthetic world spread across several document
    /// tiles including negative ones, a scratch directory helper, and the comparison keys the determinism
    /// group asserts on.</summary>
    internal static class TiledDocFixture
    {
        /// <summary>A world spanning four 512 m document tiles: (0, 0), (-2, 0), (1, -2) from placements and
        /// spawns, plus (-1, 0) from a sculpt tile whose origin corner lands there. Negative coordinates are
        /// deliberate: they are what the invariant-culture rule exists for.</summary>
        internal static MapDocument SampleDoc()
        {
            var doc = new MapDocument
            {
                Id = "tiled-zone",
                DisplayName = "Tiled Zone",
                Bounds = new MapBounds { MinX = -2048f, MinZ = -2048f, MaxX = 2048f, MaxZ = 2048f },
                TileSize = MapDocumentFile.DefaultTileSize,
            };
            doc.Terrain.Seed = 4711;
            doc.Terrain.WaterLevel = -0.25f;
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 40f, CenterZ = -20f, Radius = 30f, Depth = 4f });
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees",
                Seed = 0x5AFE,
                CellSize = 6f,
                Rules = { new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 0.3f, Kinds = { new MapPropKind { Id = "pine_a", Weight = 1f } } } },
            });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 20f } });
            doc.Regions.Add(new MapRegion { Name = "camp", Shape = new RectShapeDoc { MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f }, Tags = { "safe" } });

            doc.Placements.Add(new MapPlacement { Id = "p-a", Kind = "rock", X = 10f, Z = 20f, Yaw = 0.5f });
            doc.Placements.Add(new MapPlacement { Id = "p-b", Kind = "rock", X = 300f, Z = 40f });
            doc.Placements.Add(new MapPlacement { Id = "p-c", Kind = "hut", X = -600f, Z = 20f, Y = 3f });
            doc.Placements.Add(new MapPlacement { Id = "p-d", Kind = "hut", X = 700f, Z = -900f });
            doc.Spawns.Add(new MapSpawn { Id = "s-a", ArchetypeId = "wolf", X = 15f, Z = 25f });
            doc.Spawns.Add(new MapSpawn { Id = "s-b", ArchetypeId = "boar", X = -590f, Z = 30f });
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "ps-a", X = 5f, Z = 5f, Yaw = 1.1f });

            var overrides = new MapTerrainOverrides(2f);   // 64 m sculpt span
            overrides.SetDelta(4, 6, 2.5f);                // sculpt tile (0, 0) -> document tile (0, 0)
            overrides.SetDelta(-40, 10, 1.25f);            // sculpt tile (-2, 0) -> document tile (-1, 0)
            doc.TerrainOverrides = overrides;
            return doc;
        }

        /// <summary>The same world with no sculpt block at all, for the null-normalization arm of the hash
        /// tests.</summary>
        internal static MapDocument SampleDocWithoutSculpt()
        {
            MapDocument doc = SampleDoc();
            doc.TerrainOverrides = null;
            return doc;
        }

        internal static string NewDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "ke-mapdoc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static void Delete(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Runs a body against a fresh scratch directory and cleans it up either way.</summary>
        internal static void InDirectory(Action<string> body)
        {
            string directory = NewDirectory();
            try { body(directory); }
            finally { Delete(directory); }
        }

        internal static IEnumerable<string> PlacementKeys(MapDocument doc) =>
            doc.Placements.Select(p => $"{p.Id}|{p.Kind}|{p.X}|{p.Z}|{p.Y}|{p.Yaw}|{p.Scale}").OrderBy(k => k, StringComparer.Ordinal);

        internal static IEnumerable<string> SpawnKeys(MapDocument doc) =>
            doc.Spawns.Select(s => $"{s.Id}|{s.ArchetypeId}|{s.X}|{s.Z}|{s.Enabled}").OrderBy(k => k, StringComparer.Ordinal);

        internal static IEnumerable<string> PlayerSpawnKeys(MapDocument doc) =>
            doc.PlayerSpawns.Select(s => $"{s.Id}|{s.X}|{s.Z}|{s.Yaw}|{s.Enabled}").OrderBy(k => k, StringComparer.Ordinal);

        internal static IEnumerable<string> SculptKeys(MapDocument doc) =>
            (doc.TerrainOverrides?.Tiles ?? (IReadOnlyList<MapSculptTile>)Array.Empty<MapSculptTile>())
                .Select(t => $"{t.TileX}|{t.TileZ}|{string.Join(',', t.Deltas)}")
                .OrderBy(k => k, StringComparer.Ordinal);

        /// <summary>Every tile file currently on disk, relative to the document directory.</summary>
        internal static IReadOnlyList<string> TileFiles(string directory)
        {
            string tiles = Path.Combine(directory, "tiles");
            if (!Directory.Exists(tiles)) return Array.Empty<string>();
            return Directory.EnumerateFiles(tiles, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(directory, f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>A write-only sink that counts bytes and keeps none, so a save can be measured without the
    /// disk or the memory a MemoryStream would hold.</summary>
    internal sealed class CountingStream : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    }
}
