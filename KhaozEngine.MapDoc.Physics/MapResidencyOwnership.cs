using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Where one static lives on the aligned grids. <see cref="Membership"/> is every storage tile its world
/// bounds reach, and <see cref="NavTiles"/> and <see cref="Cells"/> the navigation tiles and server cells those
/// storage tiles fall in. Every list is in (Z, X) order without repeats.
/// <para><see cref="StorageOwner"/> is the storage tile the static is keyed by, following how it is stored. A placement
/// is keyed by the tile of its origin, <see cref="MapTileGrid.CoordOf"/> on the authored X and Z floats, exactly as
/// <see cref="MapSpatialIndex"/> buckets it, so this index agrees with <see cref="MapTileResidency"/> and tiled
/// windows. A terrain chunk is stored in surface pages by slot block rather than in document tiles, so it is keyed by
/// the tile holding its bounds' minimum corner.</para></summary>
public sealed record MapResidencyEntry(string OwnerId, MapTileCoord StorageOwner, IReadOnlyList<MapTileCoord> Membership,
    IReadOnlyList<MapNavTileCoord> NavTiles, IReadOnlyList<MapServerCellCoord> Cells, MapBox3 WorldBounds);

/// <summary>What one edit invalidates: owners in ordinal order, and storage and navigation tiles in (Z, X) order
/// without repeats.</summary>
public sealed record MapAffectedSet(IReadOnlyList<string> Owners, IReadOnlyList<MapTileCoord> StorageTiles,
    IReadOnlyList<MapNavTileCoord> NavTiles);

/// <summary>Deterministic ownership of a built world's statics and water bodies on aligned storage, navigation and
/// server cell grids, and the owners and tiles an edit invalidates.</summary>
public static class MapResidencyOwnership
{
    /// <summary>The most storage tiles one list here holds, 2^20: a static's
    /// <see cref="MapResidencyEntry.Membership"/>, and each extent <see cref="Affected(MapBuiltWorld, MapWorldGrids, MapNativeEditEffects)"/> lists, whether an edit box,
    /// a listed patch chunk or, for an unbounded edit, the whole world. At 64 m tiles that is, for example, a square
    /// about 65 km on a side.</summary>
    public const int MaxStorageTiles = 1 << 20;

    const MapNativeInvalidation Spatial = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav |
        MapNativeInvalidation.Residency | MapNativeInvalidation.Medium;

    /// <summary>One entry per static of <paramref name="world"/>, in static order. A static belongs to every storage
    /// tile its bounds reach, minimum inclusive and maximum exclusive on each axis. An axis with no extent belongs to
    /// the tile holding its minimum. Throws <see cref="MapDocumentException"/> when the grids do not align with the
    /// world, and naming the tile count, before listing them, when one static reaches more than
    /// <see cref="MaxStorageTiles"/> storage tiles.</summary>
    public static IReadOnlyList<MapResidencyEntry> Build(MapBuiltWorld world, MapWorldGrids grids)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        grids.Validate(world);
        var entries = new MapResidencyEntry[world.Statics.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            MapStaticDescriptor descriptor = world.Statics[i];
            MapTileRect tiles = TilesOf(descriptor.Bounds, grids.StorageTileSize);
            RequireStorageTiles(tiles, $"Static '{descriptor.OwnerId}'");
            MapNavTileCoord navMin = grids.AlignedNavTileOf(tiles.Min), navMax = grids.AlignedNavTileOf(tiles.Max);
            MapServerCellCoord cellMin = grids.AlignedServerCellOf(tiles.Min), cellMax = grids.AlignedServerCellOf(tiles.Max);
            // A placement static's position is its resolved transform position, whose X and Z are the authored floats.
            MapTileCoord owner = descriptor.Kind == MapStaticKind.Placement
                ? MapTileGrid.CoordOf(descriptor.Position.X, descriptor.Position.Z, grids.StorageTileSize)
                : tiles.Min;
            entries[i] = new(descriptor.OwnerId, owner, Enumerate(tiles),
                Rows(navMin.X, navMin.Z, navMax.X, navMax.Z, (x, z) => new MapNavTileCoord(x, z)),
                Rows(cellMin.X, cellMin.Z, cellMax.X, cellMax.Z, (x, z) => new MapServerCellCoord(x, z)),
                descriptor.Bounds);
        }
        return Array.AsReadOnly(entries);
    }

    /// <summary>One entry per compiled water body of <paramref name="world"/>, in body id order. A body's owner id is
    /// <c>water:{bodyId}</c>, and it belongs to every storage tile any of its region triangles' XZ bounds reaches, under the
    /// membership rule of <see cref="Build"/>, so an L-shaped body does not claim the tiles inside its corner. Its
    /// storage owner is the tile holding its bounds' minimum corner, and its navigation tiles and server cells are
    /// those its storage tiles fall in. A body with no region claims no tile and has no entry. Throws as
    /// <see cref="Build"/> does, naming the body when its tiles exceed <see cref="MaxStorageTiles"/>, and refuses a
    /// body that is not complete or has regions but no bounds.</summary>
    public static IReadOnlyList<MapResidencyEntry> BuildWater(MapBuiltWorld world, MapWorldGrids grids)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        grids.Validate(world);
        var entries = new List<MapResidencyEntry>(world.Water.Count);
        foreach (MapCompiledWaterBody body in world.Water)
        {
            MapWorldBuilder.RequireWaterBody(body);
            MapTileCoord[] membership = WaterTiles(body, grids);
            if (membership.Length == 0 || body.Bounds is not { } bounds) continue;
            entries.Add(new(WaterOwnerId(body.BodyId), TilesOf(bounds, grids.StorageTileSize).Min, membership,
                membership.Select(grids.AlignedNavTileOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray(),
                membership.Select(grids.AlignedServerCellOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray(),
                bounds));
        }
        return entries.AsReadOnly();
    }

    /// <summary>One entry per effective light and marker, sorted by typed owner ID. Light membership includes its
    /// scaled world-metre radius. Markers occupy their exact point. These entries never install statics.</summary>
    public static IReadOnlyList<MapResidencyEntry> BuildAnnotations(MapBuiltWorld world, MapWorldGrids grids)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        grids.Validate(world);
        var entries = new List<MapResidencyEntry>();
        foreach (var light in world.Lights) Add("light:" + light.Id, light.Position, MapEffectiveAnnotations.Bounds(light));
        foreach (var marker in world.Markers) Add("marker:" + marker.Id, marker.Position, MapEffectiveAnnotations.Bounds(marker));
        return entries.AsReadOnly();

        void Add(string id, MapExactPoint point, MapBox3 bounds)
        {
            var tiles = TilesOf(bounds, grids.StorageTileSize);
            RequireStorageTiles(tiles, $"Annotation '{id}'");
            var membership = Enumerate(tiles);
            entries.Add(new(id, TilesOf(new(point.X.ToDouble(), 0, point.Z.ToDouble(), point.X.ToDouble(), 0, point.Z.ToDouble()), grids.StorageTileSize).Min,
                membership, membership.Select(grids.AlignedNavTileOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray(),
                membership.Select(grids.AlignedServerCellOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray(), bounds));
        }
    }

    internal static string WaterOwnerId(string bodyId) => "water:" + bodyId;

    /// <summary>The storage tiles in (Z, X) order that any region triangle of <paramref name="body"/> reaches with its
    /// XZ bounds, under the membership rule of <see cref="Build"/>.</summary>
    internal static MapTileCoord[] WaterTiles(MapCompiledWaterBody body, MapWorldGrids grids)
    {
        string what = $"Water body '{body.BodyId}'";
        var tiles = new SortedSet<MapTileCoord>(TileOrder);
        foreach (var region in body.Regions)
            foreach (var bounds in region.WidePolygons.Count == 0 ? region.Triangles.Select(XzBounds) : region.WidePolygons.SelectMany(p =>
                Enumerable.Range(1, p.Count - 2).Select(i => MapWaterOccupiedPieces.Bounds(new[] { p[0], p[i], p[i + 1] }, default, default))))
            {
                MapTileRect rect = TilesOf(bounds, grids.StorageTileSize);
                RequireStorageTiles(rect, what);
                tiles.UnionWith(Enumerate(rect));
                if (tiles.Count > MaxStorageTiles)
                    throw new MapDocumentException(string.Format(CultureInfo.InvariantCulture,
                        "{0} reaches more than the {1:N0} storage tiles one list allows.", what, MaxStorageTiles));
            }
        return tiles.ToArray();
    }

    /// <summary>The XZ bounds of a region triangle, with no height.</summary>
    internal static MapBox3 XzBounds(MapExactTriangle triangle)
    {
        double[] xs = { triangle.A.X.ToDouble(), triangle.B.X.ToDouble(), triangle.C.X.ToDouble() };
        double[] zs = { triangle.A.Z.ToDouble(), triangle.B.Z.ToDouble(), triangle.C.Z.ToDouble() };
        return new MapBox3(xs.Min(), 0, zs.Min(), xs.Max(), 0, zs.Max());
    }

    /// <summary>The owners with at least one storage tile inside <paramref name="window"/>, in ordinal order.</summary>
    public static IReadOnlyList<string> InWindow(IReadOnlyList<MapResidencyEntry> entries, MapTileRect window)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(e => e.Membership.Any(window.Contains)).Select(e => e.OwnerId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The owners and tiles <paramref name="effects"/> invalidates, empty unless it invalidates physics,
    /// navigation, residency or water medium. <paramref name="world"/> is the world built after the edit, so owners are
    /// that world's statics, while tiles from both the old and the new edit bounds cover any geometry the edit removed.
    /// <para>When <see cref="MapNativeInvalidation.Medium"/> is flagged, every body named in
    /// <see cref="MapNativeEditEffects.WaterBodyIds"/> is a <c>water:{bodyId}</c> owner, and the storage tiles of
    /// each one <paramref name="world"/> holds, under the rule of <see cref="BuildWater"/>, join the tiles of the
    /// reported edit bounds.</para>
    /// <para>Edit extents use <see cref="MapNativeEditEffects.OldSpatialBounds"/> and <see cref="MapNativeEditEffects.NewSpatialBounds"/>,
    /// keeping tight reported interaction bounds separate from conservative installed geometry.</para>
    /// <para>Owners are the statics whose bounds meet the old or new edit bounds, plus the chunks of every listed patch:
    /// its own surface chunks and the chunks of every wall strip the world records in it. Storage tiles are those the
    /// old and new edit bounds reach, under the membership rule of <see cref="Build"/>, plus the membership of those
    /// patch chunks. An owner listed only because its bounds meet an edit box does not widen the tiles. A terrain edit
    /// reports whole-patch extents, so its terrain geometry lies inside the edit bounds, but a placement it reseats
    /// through surface support is not widened yet (KhaozEngine#1348).</para>
    /// <para><see cref="MapNativeInvalidation.Unbounded"/> is a full reset: discard all prior derived state for this
    /// world and rebuild from the post-edit world. Every static and water body is an owner, and every storage tile the
    /// post-edit world's bounds reach is listed, together with the tiles of any edit bounds, listed patches and water
    /// bodies. Pre-edit extents outside the post-edit world cannot be listed.</para>
    /// <para>Throws <see cref="MapDocumentException"/> when the grids do not align with the world or an edit box has a
    /// coordinate beyond <see cref="MapWorldBuilder.MaxCoordinateMetres"/>, and naming the tile count, before listing
    /// them, when one extent reaches more than <see cref="MaxStorageTiles"/> storage tiles.</para></summary>
    public static MapAffectedSet Affected(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(effects);
        grids.Validate(world);
        if ((effects.Invalidates & Spatial) == 0)
            return new(Array.Empty<string>(), Array.Empty<MapTileCoord>(), Array.Empty<MapNavTileCoord>());

        MapBox3[] edited = new[] { effects.OldSpatialBounds, effects.NewSpatialBounds }.Where(b => b.HasValue).Select(b => b!.Value).ToArray();
        foreach (MapBox3 box in edited) RequireWithinWorld(box);
        string[] prefixes = PatchChunkPrefixes(world, effects.Patches);
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        var tiles = new SortedSet<MapTileCoord>(TileOrder);
        bool unbounded = (effects.Invalidates & MapNativeInvalidation.Unbounded) != 0;
        foreach (MapBox3 box in edited) tiles.UnionWith(StorageTiles(box, grids, "An edit box"));
        if (unbounded) tiles.UnionWith(StorageTiles(world.Bounds, grids, "The world"));
        foreach (MapStaticDescriptor descriptor in world.Statics)
        {
            bool patchChunk = descriptor.Kind == MapStaticKind.TerrainChunk &&
                prefixes.Any(p => descriptor.OwnerId.StartsWith(p, StringComparison.Ordinal));
            if (patchChunk) tiles.UnionWith(StorageTiles(descriptor.Bounds, grids, $"Chunk '{descriptor.OwnerId}'"));
            if (unbounded || patchChunk || edited.Any(box => Meets(box, descriptor.Bounds))) owners.Add(descriptor.OwnerId);
        }
        foreach (var annotation in BuildAnnotations(world, grids))
            if (unbounded || edited.Any(box => Meets(box, annotation.WorldBounds)))
            {
                owners.Add(annotation.OwnerId);
                tiles.UnionWith(annotation.Membership);
            }
        bool medium = (effects.Invalidates & MapNativeInvalidation.Medium) != 0;
        var named = new HashSet<string>(medium ? effects.WaterBodyIds : Array.Empty<string>(), StringComparer.Ordinal);
        owners.UnionWith(named.Select(WaterOwnerId));
        foreach (MapCompiledWaterBody body in world.Water)
            if (unbounded || named.Contains(body.BodyId))
            {
                owners.Add(WaterOwnerId(body.BodyId));
                tiles.UnionWith(WaterTiles(body, grids));
            }
        MapNavTileCoord[] nav = tiles.Select(grids.AlignedNavTileOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray();
        return new(owners.ToArray(), tiles.ToArray(), nav);
    }

    /// <summary>Consequences across both snapshots, including removed owner IDs and their old membership. Unknown
    /// geometry keeps Unbounded's reset semantics. Changed support-bound placement extents belong in the effects
    /// emitted by preparation, rather than being inferred from an edited terrain patch.</summary>
    public static MapAffectedSet Affected(MapBuiltWorld before, MapBuiltWorld after, MapWorldGrids grids, MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(before);
        var old = Affected(before, grids, effects);
        var current = Affected(after, grids, effects);
        if ((effects.Invalidates & Spatial) == 0) return current;
        var owners = new SortedSet<string>(old.Owners.Concat(current.Owners), StringComparer.Ordinal);
        var tiles = new SortedSet<MapTileCoord>(old.StorageTiles.Concat(current.StorageTiles), TileOrder);
        var live = after.Statics.Select(s => s.OwnerId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in before.Statics.Where(s => !live.Contains(s.OwnerId)))
        {
            owners.Add(removed.OwnerId);
            tiles.UnionWith(StorageTiles(removed.Bounds, grids, $"Removed owner '{removed.OwnerId}'"));
        }
        var liveAnnotations = BuildAnnotations(after, grids).Select(a => a.OwnerId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in BuildAnnotations(before, grids).Where(a => !liveAnnotations.Contains(a.OwnerId)))
        {
            owners.Add(removed.OwnerId);
            tiles.UnionWith(removed.Membership);
        }
        var nav = tiles.Select(grids.AlignedNavTileOf).Distinct().OrderBy(t => t.Z).ThenBy(t => t.X).ToArray();
        return new(owners.ToArray(), tiles.ToArray(), nav);
    }

    /// <summary>The chunk id prefixes of every listed patch: its own surface chunks and the chunks of every wall strip
    /// the world records in it. Each prefix ends in its separator, so "wall-1/" never matches "wall-10/...".</summary>
    internal static string[] PatchChunkPrefixes(MapBuiltWorld world, IReadOnlyList<MapPatchKey> patches)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (MapPatchKey key in patches)
        {
            prefixes.Add(MapTerrainPhysics.PatchChunkPrefix(key));
            foreach (MapWallStrip strip in world.Surfaces.RecordsIn(key).OfType<MapWallStrip>())
                prefixes.Add(MapTerrainPhysics.StripChunkPrefix(strip.Id));
        }
        return prefixes.ToArray();
    }

    static readonly Comparer<MapTileCoord> TileOrder =
        Comparer<MapTileCoord>.Create((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));

    /// <summary>Refuses an edit box with a coordinate beyond <see cref="MapWorldBuilder.MaxCoordinateMetres"/>.</summary>
    internal static void RequireWithinWorld(MapBox3 box)
    {
        const double limit = MapWorldBuilder.MaxCoordinateMetres;
        foreach (double value in new[] { box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ })
            if (!(Math.Abs(value) <= limit))
                throw new MapDocumentException(FormattableString.Invariant(
                    $"Edit bounds ({box.MinX}, {box.MinY}, {box.MinZ}) to ({box.MaxX}, {box.MaxY}, {box.MaxZ}) reach beyond {limit:N0} m from the world origin on an axis."));
    }

    // The storage tiles box reaches, after refusing more than MaxStorageTiles of them.
    static IReadOnlyList<MapTileCoord> StorageTiles(MapBox3 box, MapWorldGrids grids, string what)
    {
        MapTileRect tiles = TilesOf(box, grids.StorageTileSize);
        RequireStorageTiles(tiles, what);
        return Enumerate(tiles);
    }

    static void RequireStorageTiles(MapTileRect tiles, string what)
    {
        long count = ((long)tiles.Max.X - tiles.Min.X + 1) * ((long)tiles.Max.Z - tiles.Min.Z + 1);
        if (count > MaxStorageTiles)
            throw new MapDocumentException(string.Format(CultureInfo.InvariantCulture,
                "{0} reaches {1:N0} storage tiles, more than the {2:N0} one list allows.", what, count, MaxStorageTiles));
    }

    /// <summary>The storage tiles a box reaches: minimum inclusive, maximum exclusive, and an axis with no extent in
    /// the tile holding its minimum. Spatial membership floors the exact double bounds, while a placement's storage key
    /// is <see cref="MapTileGrid.CoordOf"/> on its stored floats. Next to a seam the two can differ, and each is right
    /// for its purpose: membership finds the tiles the geometry reaches, the key finds the tile that stores it.</summary>
    static MapTileRect TilesOf(MapBox3 box, float tileSize)
    {
        (int minX, int maxX) = Axis(box.MinX, box.MaxX, tileSize);
        (int minZ, int maxZ) = Axis(box.MinZ, box.MaxZ, tileSize);
        return new(new(minX, minZ), new(maxX, maxZ));
    }

    static (int Min, int Max) Axis(double min, double max, double tileSize)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max < min)
            throw new ArgumentException(FormattableString.Invariant($"bounds [{min}, {max}] are not a finite range"));
        int first = checked((int)Math.Floor(min / tileSize));
        if (!(max > min)) return (first, first);
        return (first, Math.Max(first, checked((int)Math.Ceiling(max / tileSize) - 1)));
    }

    static bool Meets(MapBox3 a, MapBox3 b) =>
        a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY && a.MinZ <= b.MaxZ && b.MinZ <= a.MaxZ;

    static IReadOnlyList<MapTileCoord> Enumerate(MapTileRect rect) =>
        Rows(rect.Min.X, rect.Min.Z, rect.Max.X, rect.Max.Z, (x, z) => new MapTileCoord(x, z));

    // Navigation tiles and server cells are monotone in the storage tile, so a storage tile rectangle maps onto the
    // rectangle between the blocks of its corners. Z outer, X inner, so the list is in (Z, X) order.
    static IReadOnlyList<T> Rows<T>(int minX, int minZ, int maxX, int maxZ, Func<int, int, T> make)
    {
        var list = new List<T>(checked((maxX - minX + 1) * (maxZ - minZ + 1)));
        for (long z = minZ; z <= maxZ; z++)
            for (long x = minX; x <= maxX; x++)
                list.Add(make((int)x, (int)z));
        return list.AsReadOnly();
    }
}
