using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Navigation tile fixtures: a resolver-1 strip three 64 m tiles long and a stacked shaft across a tile
/// seam.</summary>
internal static partial class NativeWorldFixtures
{
    /// <summary>The vertical link of <see cref="BuildStackedCaveAcrossTiles"/>.</summary>
    internal const string ShaftLinkRecordId = "shaft-link";

    /// <summary>The flat analytic terrain height of the legacy strip.</summary>
    const float LegacyStripGroundY = 0f;

    /// <summary>The top of the seam deck in the legacy strip, the highest surface the strip has.</summary>
    const float LegacyStripDeckTopY = 1f;

    /// <summary>The seam capture's probe window: it starts 9 m above the deck top and reaches 20 m down, so it spans
    /// y -10 to 10. That covers the analytic ground at 0, the crate top at 0.2 and the deck top at 1, with more than a
    /// capsule's height of headroom above each. It is taken from the strip's known heights, never from the world's
    /// bounds, since a resolver-1 world's bounds exclude its analytic terrain.</summary>
    const float SeamProbeTopY = LegacyStripDeckTopY + 9f, SeamProbeRange = 20f;

    /// <summary>The seam capture's cell edge. 64 m and every seam margin the tests use are whole multiples of it, so
    /// both neighbours' columns share centres.</summary>
    const float SeamCellSize = 0.5f;

    static readonly MapLatticeFrame TwoMetreCells =
        new(new(2, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);

    /// <summary>A resolver-1 strip over x 0 to 192 and z 0 to 64, so it spans navigation tiles (0, 0) to (2, 0) of a
    /// 64 m grid at the origin, over flat analytic ground at y 0. The crate stands at (16, 0, 16) in tile (0, 0). The
    /// 18 by 5 m bridge deck lies across the x 64 seam, centred at (64, 1, 32) with its top at y 1, so both captures of
    /// that seam see one physical surface.</summary>
    internal static MapBuiltWorld BuildLegacyStrip() => BuildLegacyStripWith(16f, null);

    /// <summary><see cref="BuildLegacyStrip"/> with the crate moved to (63, 0, 16), still in tile (0, 0) but within
    /// the 2 m seam margin of x 64.</summary>
    internal static MapBuiltWorld BuildLegacyStripWithCrateAtSeam() => BuildLegacyStripWith(63f, null);

    /// <summary><see cref="BuildLegacyStrip"/> with one 0.25 m sculpt delta at 0.5 m cell (112, 40), at world (56, 20)
    /// in sculpt tile (3, 1). That tile's cells span x 48 to 64 and its footprint x 47.5 to 64, so it reaches the
    /// capture of navigation tile (1, 0), which starts at x 62.</summary>
    internal static MapBuiltWorld BuildLegacyStripWithSeamSculpt() => BuildLegacyStripWith(16f, (112, 40));

    /// <summary><see cref="BuildLegacyStrip"/> with the crate moved 8 m east to (24, 0, 16), still in tile (0, 0).</summary>
    internal static MapBuiltWorld BuildLegacyStripWithMovedCrate() => BuildLegacyStripWith(24f, null);

    /// <summary><see cref="BuildLegacyStrip"/> with one 0.25 m sculpt delta at 0.5 m cell (20, 20), at world (10, 10)
    /// in sculpt tile (0, 0), whose footprint spans x and z -0.5 to 16 inside navigation tile (0, 0).</summary>
    internal static MapBuiltWorld BuildLegacyStripWithSculptInTileZero() => BuildLegacyStripWith(16f, (20, 20));

    /// <summary>The crate's move from x 16 to x 24, bounded by a 2 m box around each collider, inside tile (0, 0) and
    /// more than the seam margin from its edges.</summary>
    internal static MapNativeEditEffects MovedCrateEffects() => new(
        new MapBox3(15, 0, 15, 17, 1, 17), new MapBox3(23, 0, 15, 25, 1, 17),
        Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapDigestChange>(),
        MapNativeInvalidation.Placements | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav);

    /// <summary>The sculpt of cell (20, 20), bounded by the cells its heights interpolate into, inside tile
    /// (0, 0).</summary>
    internal static MapNativeEditEffects SculptInTileZeroEffects() => new(
        new MapBox3(9.5, -1, 9.5, 11, 1, 11), new MapBox3(9.5, -1, 9.5, 11, 1, 11),
        Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapDigestChange>(),
        MapNativeInvalidation.Terrain | MapNativeInvalidation.Nav);

    /// <summary>The crate's move from x 16 to x 63, bounded by a box around each collider. The new box lies inside
    /// tile (0, 0) but within the seam margin of x 64, so only the margin widening reaches tile (1, 0).</summary>
    internal static MapNativeEditEffects CrateToSeamEffects() => new(
        new MapBox3(15, 0, 15, 17, 1, 17), new MapBox3(62.5, 0, 15, 63.5, 1, 17),
        Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapDigestChange>(),
        MapNativeInvalidation.Placements | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav);

    /// <summary>The removal of <see cref="BuildLegacyStripWithSeamSculpt"/>'s only sculpt tile, as undoing the stroke
    /// that created it reports it. Its brush bounds span x 55 to 57, more than the seam margin from tile (1, 0), and
    /// the world built after it has no sculpt left.</summary>
    internal static MapNativeEditEffects RemovedSeamSculptEffects() => new(
        new MapBox3(55, -1, 19.5, 57, 1, 21), new MapBox3(55, -1, 19.5, 57, 1, 21),
        Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapDigestChange>(),
        MapNativeInvalidation.Terrain | MapNativeInvalidation.Nav);

    /// <summary>A resolver-1 world over x 0 to 128 and z -64 to 0, so it spans navigation tiles (0, -1) and (1, -1) of
    /// a 64 m grid at the origin, over flat analytic ground at y 0. Its terrain block and its one sculpt tile are the
    /// ones <c>MapLegacyTerrainDigestTests</c> pins: seed 7, water level -0.5 and one meadow band at base height 1.5,
    /// and tile (1, -1) at the default 0.5 m cell with delta 1.5 at local cell (0, 0) and -0.25 at (1, 1). That tile's
    /// footprint spans x 15.5 to 32 and z -16.5 to 0, so it meets the capture of tile (0, -1) only. The crate stands at
    /// (16, 0, -48) in tile (0, -1).</summary>
    internal static MapBuiltWorld BuildLegacyPinnedTerrain()
    {
        NativeFixture f = Crate();
        MapDocument document = f.Document;
        document.ResolverIdentity = new(1, 1);
        document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        document.Surfaces = new MapSurfaceSet();
        document.Bounds = new() { MinX = 0, MinZ = -64, MaxX = 128, MaxZ = 0 };
        document.PlayableBounds = new() { MinX = 0, MinZ = -64, MaxX = 128, MaxZ = 0 };
        document.Placements[0].X = 16f;
        document.Placements[0].Z = -48f;
        document.Terrain.Seed = 7;
        document.Terrain.WaterLevel = -0.5f;
        document.Terrain.Biomes.Add(new MapBiomeBand { Biome = BiomeId.Meadow, BaseHeight = 1.5f });
        var tile = new MapSculptTile(1, -1);
        tile[0, 0] = 1.5f;
        tile[1, 1] = -0.25f;
        document.TerrainOverrides = new MapTerrainOverrides();
        document.TerrainOverrides.PutTile(tile);
        return MapWorldBuilder.Build(document, f.Assets,
            Options() with { Resolve = LegacyResolveOptions, LegacySupportHeight = (_, _) => 0f });
    }

    static MapBuiltWorld BuildLegacyStripWith(float crateX, (int X, int Z)? sculptCell)
    {
        NativeFixture f = Crate();
        MapDocument document = f.Document;
        document.ResolverIdentity = new(1, 1);
        document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        document.Surfaces = new MapSurfaceSet();
        document.Bounds = new() { MinX = 0, MinZ = 0, MaxX = 192, MaxZ = 64 };
        document.PlayableBounds = new() { MinX = 0, MinZ = 0, MaxX = 192, MaxZ = 64 };
        document.Placements[0].X = crateX;
        document.Placements[0].Z = 16f;
        MapPlacement deck = OnFloor("seam-deck", "bridge-deck", 64f, 32f);
        deck.Y = LegacyStripDeckTopY;
        document.Placements.Add(deck);
        if (sculptCell is { } cell)
        {
            document.TerrainOverrides = new MapTerrainOverrides(0.5f);
            document.TerrainOverrides.SetDelta(cell.X, cell.Z, 0.25f);
        }
        return MapWorldBuilder.Build(document, f.Assets,
            Options() with { Resolve = LegacyResolveOptions, LegacySupportHeight = (_, _) => LegacyStripGroundY });
    }

    /// <summary>Whether neighbouring tiles <paramref name="a"/> and <paramref name="b"/> of a resolver-1
    /// <paramref name="world"/> capture the same columns along their shared edge. Each tile is captured with
    /// <see cref="PhysicsNavBake.Capture"/> over its capture bounds through the registration's legacy move context and
    /// baked for <see cref="MoveTuning.Default"/>. A column takes part when its centre lies within the seam margin of the
    /// edge and its capsule footprint lies inside both captures, since a capture refuses a footprint it did not
    /// capture. For every such column the surface heights and node passability must be identical, and so must
    /// <see cref="GroundNavigation.AllowsSegment"/> between every surface pair of it and each taking-part neighbour.
    /// False also when no passable surface took part, so agreement is never vacuous.</summary>
    internal static bool SeamColumnsAgree(MapBuiltWorld world, MapNavTile a, MapNavTile b)
    {
        MoveTuning tuning = MoveTuning.Default;
        double margin = a.Bounds.MinX - a.CaptureBounds.MinX;
        bool acrossX = a.Coord.Z == b.Coord.Z;
        double edge = acrossX ? a.Bounds.MaxX : a.Bounds.MaxZ;
        double alongMin = acrossX ? a.Bounds.MinZ : a.Bounds.MinX, alongMax = acrossX ? a.Bounds.MaxZ : a.Bounds.MaxX;
        double radius = tuning.CapsuleRadius;

        using var physics = new BepuPhysicsWorld();
        using MapPhysicsRegistration registration = MapPhysicsRegistration.Register(world, physics);
        GroundMoveContext context = registration.CreateLegacyMoveContext();
        GroundNavigation first = SeamProfile(context, a, tuning), second = SeamProfile(context, b, tuning);

        var across = new List<double>();
        for (double c = edge - margin + SeamCellSize / 2; c < edge + margin; c += SeamCellSize)
            if (c - radius >= edge - margin && c + radius < edge + margin) across.Add(c);
        var along = new List<double>();
        for (double c = alongMin + SeamCellSize / 2; c < alongMax; c += SeamCellSize) along.Add(c);

        int passable = 0;
        for (int i = 0; i < across.Count; i++)
            for (int j = 0; j < along.Count; j++)
            {
                (float X, float Z) at = Column(across[i], along[j]);
                var mine = Surfaces(first, at.X, at.Z);
                if (!mine.SequenceEqual(Surfaces(second, at.X, at.Z))) return false;
                passable += mine.Count(s => s.Passable);
                foreach ((int di, int dj) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
                {
                    if (i + di >= across.Count || j + dj < 0 || j + dj >= along.Count) continue;
                    (float X, float Z) to = Column(across[i + di], along[j + dj]);
                    foreach (var source in mine)
                        foreach (var target in Surfaces(first, to.X, to.Z))
                        {
                            var p = new Vector3(at.X, source.Height, at.Z);
                            var q = new Vector3(to.X, target.Height, to.Z);
                            if (first.AllowsSegment(p, q) != second.AllowsSegment(p, q) ||
                                first.AllowsSegment(q, p) != second.AllowsSegment(q, p)) return false;
                        }
                }
            }
        return passable > 0;

        (float X, float Z) Column(double acrossValue, double alongValue) =>
            acrossX ? ((float)acrossValue, (float)alongValue) : ((float)alongValue, (float)acrossValue);
    }

    static GroundNavigation SeamProfile(GroundMoveContext context, MapNavTile tile, MoveTuning tuning)
    {
        MapBox3 c = tile.CaptureBounds;
        var options = new PhysicsNavBakeOptions((float)c.MinX, (float)c.MinZ, (float)c.MaxX, (float)c.MaxZ, SeamCellSize,
            SeamProbeTopY, SeamProbeRange, tuning.MaxSlopeRadians, 65_536, 65_536);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(context, options, _ => 1u);
        return bake.BuildProfile(tuning, default);
    }

    /// <summary>Every baked surface of the column at (<paramref name="x"/>, <paramref name="z"/>) across all layers, in
    /// ascending height.</summary>
    static (float Height, bool Passable)[] Surfaces(GroundNavigation navigation, float x, float z)
    {
        var found = new List<(float Height, bool Passable)>();
        foreach (NavGrid grid in navigation.Space.Layers)
        {
            (int cx, int cz) = grid.CellOf(x, z);
            if (grid.InBounds(cx, cz) && grid.SurfaceHeightAt(cx, cz) is float height)
                found.Add((height, grid.IsPassable(cx, cz, 0f)));
        }
        return found.OrderBy(s => s.Height).ThenBy(s => s.Passable).ToArray();
    }

    /// <summary>A stacked shaft at 2 m cells, so one patch slot spans 128 m and the cave can straddle the x 64 tile
    /// seam: a lower floor at y 0, a lower ceiling at y 3, an upper floor at y 3.5 and an upper ceiling at y 6.5 over x
    /// 56 to 68 and z 0 to 12. The lower ceiling is open over x 60 to 64 and the upper floor over x 64 to 68, both at z
    /// 4 to 8, and <see cref="ShaftLinkRecordId"/> joins the lower and upper rooms through both openings. The openings
    /// are offset: the lower one spans x 60 to 64 in navigation tile (0, 0) and the upper one x 64 to 68 in tile
    /// (1, 0) of a 64 m grid at the origin, so a link reaches both tiles only through both openings. The shaft has no
    /// walls, since only its apertures matter here.</summary>
    internal static MapBuiltWorld BuildStackedCaveAcrossTiles()
    {
        const int ox = 28, oz = 0;
        var set = new MapSurfaceSet();
        Func<int, int, bool> lowerSolid = (x, z) => !(x - ox is >= 2 and < 4 && z - oz is >= 2 and < 4);
        Func<int, int, bool> upperSolid = (x, z) => !(x - ox is >= 4 and < 6 && z - oz is >= 2 and < 4);
        MapSurfacePatch lowerFloor = AddPatch(set, "seam-cave-lower-floor", MapSurfaceRole.SupportFloor, TwoMetreCells,
            ox, oz, 6, 6, (_, _) => 0);
        MapSurfacePatch lowerCeiling = AddPatch(set, "seam-cave-lower-ceiling", MapSurfaceRole.Ceiling, TwoMetreCells,
            ox, oz, 6, 6, (_, _) => 300, lowerSolid);
        MapSurfacePatch upperFloor = AddPatch(set, "seam-cave-upper-floor", MapSurfaceRole.SupportFloor, TwoMetreCells,
            ox, oz, 6, 6, (_, _) => 350, upperSolid);
        AddPatch(set, "seam-cave-upper-ceiling", MapSurfaceRole.Ceiling, TwoMetreCells, ox, oz, 6, 6, (_, _) => 650);
        int SlotCell(int x, int z) => SlotOffset(oz + z) * MapPatchKey.SlotCells + SlotOffset(ox + x);
        int[] lower = { SlotCell(2, 2), SlotCell(3, 2), SlotCell(2, 3), SlotCell(3, 3) };
        int[] upper = { SlotCell(4, 2), SlotCell(5, 2), SlotCell(4, 3), SlotCell(5, 3) };

        lowerCeiling.Records.Add(new MapHorizontalOpening("seam-lower-opening", lowerCeiling.Key, lower));
        upperFloor.Records.Add(new MapHorizontalOpening("seam-upper-opening", upperFloor.Key, upper));
        MapRecordRef[] link = { new(ShaftLinkRecordId, lowerFloor.Key) };
        lowerFloor.Records.Add(CaveSpace("seam-lower-room", Array.Empty<MapBoundaryRef>(), link));
        upperFloor.Records.Add(CaveSpace("seam-upper-room", Array.Empty<MapBoundaryRef>(), link));
        lowerFloor.Records.Add(new MapVerticalLink(ShaftLinkRecordId, new("seam-upper-room", upperFloor.Key),
            new("seam-lower-room", lowerFloor.Key),
            new MapRecordRef[] { new("seam-lower-opening", lowerCeiling.Key), new("seam-upper-opening", upperFloor.Key) },
            Array.Empty<MapRecordRef>(), Array.Empty<MapRecordRef>()));
        return Build(Terrain(set, 64, 0));
    }

    /// <summary>Whether <paramref name="boxes"/> tile <paramref name="world"/>'s bounds exactly once: their union is the
    /// world's bounds widened outward to whole navigation tiles on X and Z with the world's own Y, and their X and Z
    /// areas add up to the union's, so no two overlap.</summary>
    internal static bool BoundsUnionEquals(IEnumerable<MapBox3> boxes, MapBox3 world, MapWorldGrids grids)
    {
        MapBox3[] all = boxes.ToArray();
        if (all.Length == 0) return false;
        double size = grids.NavTileSize;
        (double MinX, double MaxX) x = Snap(world.MinX, world.MaxX, grids.Origin.X);
        (double MinZ, double MaxZ) z = Snap(world.MinZ, world.MaxZ, grids.Origin.Y);
        var union = new MapBox3(all.Min(b => b.MinX), all.Min(b => b.MinY), all.Min(b => b.MinZ),
            all.Max(b => b.MaxX), all.Max(b => b.MaxY), all.Max(b => b.MaxZ));
        var expected = new MapBox3(x.MinX, world.MinY, z.MinZ, x.MaxX, world.MaxY, z.MaxZ);
        double area = all.Sum(b => (b.MaxX - b.MinX) * (b.MaxZ - b.MinZ));
        return union == expected && area == (union.MaxX - union.MinX) * (union.MaxZ - union.MinZ);

        (double, double) Snap(double min, double max, double origin)
        {
            double low = origin + Math.Floor((min - origin) / size) * size;
            double high = origin + Math.Ceiling((max - origin) / size) * size;
            return (low, high > low ? high : low + size);
        }
    }
}
