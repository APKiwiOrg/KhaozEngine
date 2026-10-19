using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Invented surface fixtures for the legacy-tile compatibility sampler. <see cref="FlagsAndCuts"/> rebuilds, as a
/// native document, the invented relief, raster and per-case features that the frozen released sampler cases in
/// Render.Tests/Terrain use, so their pinned bits are the expected legacy behaviour. Nothing here reads a committed file.</summary>
internal static partial class FoliageFixtures
{
    internal const string GroundId = "ground";
    internal const ushort Grass = 1, Dirt = 2, Pond = 4, Road = 6;

    static readonly MapOverlayCut[] Cuts =
        [MapOverlayCut.Full, MapOverlayCut.DiagonalHalf, MapOverlayCut.CornerQuarter, MapOverlayCut.CornerThreeQuarter];

    /// <summary>The cut variant names, four cuts by four rotations, in the released case order.</summary>
    internal static IEnumerable<string> CutVariants() =>
        Cuts.SelectMany(cut => Enumerable.Range(0, 4).Select(rotation => CutName(cut, rotation)));

    internal static string CutName(MapOverlayCut cut, int rotation) => "cut-" + cut switch
    {
        MapOverlayCut.DiagonalHalf => "diagonalhalf",
        MapOverlayCut.CornerQuarter => "cornerquarter",
        MapOverlayCut.CornerThreeQuarter => "cornerthreequarter",
        _ => "full",
    } + "-r" + rotation;

    /// <summary>One slot of grass on a legacy-tile surface over the invented noncoplanar relief
    /// ((x * 37 + z * 11) % 23 * 5 - 40) cm, its x = 64 and z = 64 corners repeating the last authored corner as the
    /// released edge extension reads them. "slope-2m" uses a native 2 m lattice. Cut variants overlay road on cell
    /// (40, 40). "void-underlay", "allowed-underlay", "water", "indoors", "nodraw", "blocked" and "nodraw-blocked" change
    /// cell (30, 30). "zero-raster" zeroes the four raster samples around tile point (30.5, 30.5).</summary>
    internal static FoliageFixture FlagsAndCuts(string variant = "control")
    {
        float size = variant == "slope-2m" ? 2f : 1f;
        MapLatticeFrame frame = size == 1f ? MapLatticeFrame.ImportedMetreCentimetre
            : new(new(2, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        var patch = new MapSurfacePatch
        {
            Key = new(GroundId, 0, 0),
            Width = 64,
            Depth = 64,
            Heights = new int[65 * 65],
            Cells = Enumerable.Repeat(new MapSurfaceCell(Grass, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), 64 * 64).ToArray(),
            Presence = Enumerable.Repeat(ulong.MaxValue, 64).ToArray(),
        };
        for (int z = 0; z <= 64; z++)
            for (int x = 0; x <= 64; x++) patch.Heights[z * 65 + x] = ReleasedHeight(Math.Min(x, 63), Math.Min(z, 63));
        void Change(int x, int z, Func<MapSurfaceCell, MapSurfaceCell> change) => patch.Cells[z * 64 + x] = change(patch.Cells[z * 64 + x]);
        ushort[] allowed = [];
        (float X, float Z)? zeroAt = null;
        switch (variant)
        {
            case "void-underlay": Change(30, 30, c => c with { Underlay = 0 }); break;
            case "allowed-underlay": Change(30, 30, c => c with { Underlay = Dirt }); allowed = [Grass]; break;
            case "water": Change(30, 30, c => c with { Underlay = Pond }); break;
            case "indoors": Change(30, 30, c => c with { Flags = MapCellFlags.Indoor }); break;
            case "nodraw": Change(30, 30, c => c with { Flags = MapCellFlags.NoDraw }); break;
            case "blocked": Change(30, 30, c => c with { Flags = MapCellFlags.Blocked }); break;
            case "nodraw-blocked": Change(30, 30, c => c with { Flags = MapCellFlags.NoDraw | MapCellFlags.Blocked }); break;
            case "zero-raster": zeroAt = (30.5f, 30.5f); break;
        }
        foreach (MapOverlayCut cut in Cuts)
            for (int rotation = 0; rotation < 4; rotation++)
                if (variant == CutName(cut, rotation))
                {
                    Change(40, 40, c => c with { Overlay = Road, Cut = cut, Rotation = (byte)rotation });
                    allowed = [Grass];
                }
        var set = new MapSurfaceSet();
        set.Refs.Add(new MapSurfaceRef(GroundId, frame, MapSurfaceRole.SupportFloor,
            size == 1f ? MapPresencePolicy.LegacyTileWorld : MapPresencePolicy.Native, null, null, ""));
        set.Patches.Add(patch.Key, patch);
        return Assemble(set, ReleasedLayer(size, allowed, zeroAt));
    }

    internal static int ReleasedHeight(int x, int z) => (x * 37 + z * 11) % 23 * 5 - 40;

    /// <summary>The released invented raster: 36 by 36 samples of twice the tile size from (-2, -66) tiles, bytes
    /// 96 + (x * 29 + z * 17) % 160, optionally zeroing the four samples around one tile point.</summary>
    internal static MapFoliageLayer ReleasedLayer(float tileSize, ushort[] allowed, (float X, float Z)? zeroAt = null)
    {
        const int size = 36;
        float originX = -2f * tileSize, originZ = -66f * tileSize, cell = 2f * tileSize;
        var bytes = new byte[size * size];
        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++) bytes[z * size + x] = (byte)(96 + (x * 29 + z * 17) % 160);
        if (zeroAt is { } tile)
        {
            int u = (int)MathF.Floor((tile.X * tileSize - originX) / cell);
            int v = (int)MathF.Floor((-tile.Z * tileSize - originZ) / cell);
            for (int z = v; z <= v + 1; z++)
                for (int x = u; x <= u + 1; x++) bytes[z * size + x] = 0;
        }
        return Layer(GroundId, Surface(GroundId), originX, originZ, cell, size, size, bytes, allowed);
    }

    /// <summary>A native metre and centimetre floor: slot 0 holds 64 by 64 cells at 100 cm except corner (64, 32) at
    /// 130 cm. Slot 1 is absent unless <paramref name="advertiseSlot1"/>, which adds it at 100 cm.</summary>
    internal static FoliageFixture SlopedBorder(bool advertiseSlot1 = false)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(new MapSurfaceRef(FloorId, MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
            MapPresencePolicy.Native, null, null, ""));
        foreach (long slot in advertiseSlot1 ? new long[] { 0, 1 } : [0])
        {
            var patch = new MapSurfacePatch
            {
                Key = new(FloorId, slot, 0),
                Width = 64,
                Depth = 64,
                Heights = Enumerable.Repeat(100, 65 * 65).ToArray(),
                Cells = Enumerable.Repeat(new MapSurfaceCell(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), 64 * 64).ToArray(),
                Presence = Enumerable.Repeat(ulong.MaxValue, 64).ToArray(),
            };
            if (slot == 0) patch.Heights[32 * 65 + 64] = 130;
            set.Patches.Add(patch.Key, patch);
        }
        var bytes = Enumerable.Repeat((byte)200, 36 * 36).ToArray();
        return Assemble(set, Layer(LayerId, Surface(FloorId), -2, -66, 2, 36, 36, bytes, []));
    }

    /// <summary>The sloped border with slot 1 advertised, for a store whose slot 1 payload then goes missing.</summary>
    internal static FoliageFixture UnavailableHalo() => SlopedBorder(advertiseSlot1: true);

    /// <summary>A context over <paramref name="fixture"/> whose surfaces come from its tiled store with the slot (1, 0) payload
    /// deleted, so that advertised slot is unavailable to every acquisition that reaches it.</summary>
    internal static MapFoliageGenerationContext UnavailableContext(FoliageFixture fixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "foliage-halo-" + Guid.NewGuid().ToString("N"));
        MapDocumentFile.SaveTiled(fixture.Document, root);
        SurfaceStorageFixtures.DeletePayload(root, new(FloorId, 1, 0));
        MapEffectiveResources resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets, new(), null, MapStoredSurfaceSource.Open(root));
        MapFoliageContextResult result = MapFoliageGenerationContext.CaptureEffective(fixture.Document, fixture.Assets, resources);
        return result.Context ?? throw new InvalidOperationException("unavailable-halo capture refused: " + result.Status);
    }

    /// <summary>The released eight-level stack, wrapped as a resolver-2 document, with a layer bound to
    /// <paramref name="support"/> over its two by two cells.</summary>
    internal static FoliageFixture Stacked(MapSupportBinding support)
    {
        MapSurfaceSet set = SupportFixtures.EightStacked().Surfaces;
        var bytes = Enumerable.Repeat((byte)200, 9).ToArray();
        return Assemble(set, Layer(LayerId, support, -1, -1, 2, 3, 3, bytes, []));
    }

    /// <summary>The released equal-height slab pair, wrapped as a resolver-2 document, with a layer bound to
    /// <paramref name="support"/> over its one cell.</summary>
    internal static FoliageFixture Slabs(MapSupportBinding support)
    {
        MapSurfaceSet set = SupportFixtures.EqualSlabs(secondIsPaint: false).Surfaces;
        return Assemble(set, Layer(LayerId, support, -1, -1, 2, 3, 3, Enumerable.Repeat((byte)200, 9).ToArray(), []));
    }

    /// <summary>A captured context, failing the test fixture when capture refuses.</summary>
    internal static MapFoliageGenerationContext Context(FoliageFixture fixture)
    {
        MapFoliageContextResult result = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets);
        return result.Context ?? throw new InvalidOperationException("foliage fixture capture refused: " + result.Status);
    }

    /// <summary>The invented layer shape shared by the surface fixtures: the basic seed, spacing, scales and models, the
    /// released material ids (2 and 6 ordinary soil, 1 ordinary moss, 4 water) and no clearance.</summary>
    internal static MapFoliageLayer Layer(string id, MapSupportBinding support, float originX, float originZ, float cell, int width,
        int depth, byte[] bytes, ushort[] allowed)
    {
        var chunks = new List<MapFoliageChunk>();
        for (int cz = 0; cz * MapFoliageChunk.Size < depth; cz++)
            for (int cx = 0; cx * MapFoliageChunk.Size < width; cx++)
            {
                var data = new byte[MapFoliageChunk.ByteLength];
                for (int z = 0; z < MapFoliageChunk.Size && cz * MapFoliageChunk.Size + z < depth; z++)
                    for (int x = 0; x < MapFoliageChunk.Size && cx * MapFoliageChunk.Size + x < width; x++)
                        data[z * MapFoliageChunk.Size + x] = bytes[(cz * MapFoliageChunk.Size + z) * width + cx * MapFoliageChunk.Size + x];
                chunks.Add(new MapFoliageChunk(new(cx, cz), data));
            }
        MapFoliageCompatibilityOrdinal[] ordinals = support.SurfaceId is { } surface ? [new(surface, 0)] : [];
        return new MapFoliageLayer(1, id, support, originX, originZ, cell, width, depth, MapRowDirection.PositiveZ, 17, 0.5f, 0.8f, 1.4f,
            -0.01f, [new("reed-a", 3), new("reed-b", 1)], allowed,
            [new(Grass, "moss-material", MapFoliageMaterialKind.Ordinary), new(Dirt, "soil-material", MapFoliageMaterialKind.Ordinary),
             new(Pond, "pond-material", MapFoliageMaterialKind.Water), new(Road, "soil-material", MapFoliageMaterialKind.Ordinary),
             new(10, "moss-material", MapFoliageMaterialKind.Ordinary)],
            new("legacy-tile", 1, 0), true, true, 0, 0, ordinals, [], [], [], chunks);
    }

    static FoliageFixture Assemble(MapSurfaceSet set, MapFoliageLayer layer)
    {
        var (roots, source) = AssetInputs();
        MapDocument document = MixedResolutionFixtures.Document(set);
        document.Id = "foliage-surface-fixture";
        document.NativeAssets = roots.ToList();
        document.Foliage = new MapFoliageSet(1, [layer]);
        return new(document, MapAssetClosure.Load(roots, source));
    }
}
