using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

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

/// <summary>Deterministic ownership of a built world's statics on aligned storage, navigation and server cell grids,
/// and the owners and tiles an edit invalidates.</summary>
public static class MapResidencyOwnership
{
    const MapNativeInvalidation Spatial =
        MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;

    /// <summary>One entry per static of <paramref name="world"/>, in static order. A static belongs to every storage
    /// tile its bounds reach, minimum inclusive and maximum exclusive on each axis. An axis with no extent belongs to
    /// the tile holding its minimum. Throws <see cref="MapDocumentException"/> when the grids do not align with the
    /// world.</summary>
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

    /// <summary>The owners with at least one storage tile inside <paramref name="window"/>, in ordinal order.</summary>
    public static IReadOnlyList<string> InWindow(IReadOnlyList<MapResidencyEntry> entries, MapTileRect window)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(e => e.Membership.Any(window.Contains)).Select(e => e.OwnerId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The owners and tiles <paramref name="effects"/> invalidates, empty unless it invalidates physics,
    /// navigation or residency. <paramref name="world"/> is the world built after the edit, so owners are that world's
    /// statics, while tiles from both the old and the new edit bounds cover any geometry the edit removed.
    /// <para>Owners are the statics whose bounds meet the old or new edit bounds, plus the chunks of every listed patch:
    /// its own surface chunks and the chunks of every wall strip the world records in it. Storage tiles are those the
    /// old and new edit bounds reach, under the membership rule of <see cref="Build"/>, plus the membership of those
    /// patch chunks. An owner listed only because its bounds meet an edit box does not widen the tiles: a terrain edit
    /// does not reseat placements, and a terrain edit reports whole-patch extents, so the changed geometry already lies
    /// inside the edit bounds.</para>
    /// <para>An edit flagged <see cref="MapNativeInvalidation.Unbounded"/> changed geometry its bounds do not limit, so
    /// every static is an owner and every storage tile the world's bounds reach is listed, together with the tiles of
    /// any edit bounds and listed patches.</para>
    /// <para>Throws <see cref="MapDocumentException"/> when the grids do not align with the world or an edit box has a
    /// coordinate beyond <see cref="MapWorldBuilder.MaxCoordinateMetres"/>.</para></summary>
    public static MapAffectedSet Affected(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(effects);
        grids.Validate(world);
        if ((effects.Invalidates & Spatial) == 0)
            return new(Array.Empty<string>(), Array.Empty<MapTileCoord>(), Array.Empty<MapNavTileCoord>());

        MapBox3[] edited = new[] { effects.OldBounds, effects.NewBounds }.Where(b => b.HasValue).Select(b => b!.Value).ToArray();
        foreach (MapBox3 box in edited) RequireWithinWorld(box);
        string[] prefixes = PatchChunkPrefixes(world, effects.Patches);
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        var tiles = new SortedSet<MapTileCoord>(TileOrder);
        bool unbounded = (effects.Invalidates & MapNativeInvalidation.Unbounded) != 0;
        foreach (MapBox3 box in edited) tiles.UnionWith(Enumerate(TilesOf(box, grids.StorageTileSize)));
        if (unbounded) tiles.UnionWith(Enumerate(TilesOf(world.Bounds, grids.StorageTileSize)));
        foreach (MapStaticDescriptor descriptor in world.Statics)
        {
            bool patchChunk = descriptor.Kind == MapStaticKind.TerrainChunk &&
                prefixes.Any(p => descriptor.OwnerId.StartsWith(p, StringComparison.Ordinal));
            if (patchChunk) tiles.UnionWith(Enumerate(TilesOf(descriptor.Bounds, grids.StorageTileSize)));
            if (unbounded || patchChunk || edited.Any(box => Meets(box, descriptor.Bounds))) owners.Add(descriptor.OwnerId);
        }
        MapNavTileCoord[] nav = tiles.Select(grids.AlignedNavTileOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray();
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
                prefixes.Add(strip.Id + "/");
        }
        return prefixes.ToArray();
    }

    static readonly Comparer<MapTileCoord> TileOrder =
        Comparer<MapTileCoord>.Create((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));

    static void RequireWithinWorld(MapBox3 box)
    {
        const double limit = MapWorldBuilder.MaxCoordinateMetres;
        foreach (double value in new[] { box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ })
            if (!(Math.Abs(value) <= limit))
                throw new MapDocumentException(FormattableString.Invariant(
                    $"Edit bounds ({box.MinX}, {box.MinY}, {box.MinZ}) to ({box.MaxX}, {box.MaxY}, {box.MaxZ}) reach beyond 1,000,000 m from the world origin on an axis."));
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
