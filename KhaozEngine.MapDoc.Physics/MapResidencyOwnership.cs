using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Where one static lives on the aligned grids. <see cref="Membership"/> is every storage tile its world
/// bounds reach, <see cref="StorageOwner"/> the one holding its minimum corner, and <see cref="NavTiles"/> and
/// <see cref="Cells"/> the navigation tiles and server cells those storage tiles fall in. Every list is in (Z, X)
/// order without repeats.</summary>
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
            MapNavTileCoord navMin = grids.NavTileOf(tiles.Min), navMax = grids.NavTileOf(tiles.Max);
            MapServerCellCoord cellMin = grids.ServerCellOf(tiles.Min), cellMax = grids.ServerCellOf(tiles.Max);
            entries[i] = new(descriptor.OwnerId, tiles.Min, Enumerate(tiles),
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

    /// <summary>The owners and tiles <paramref name="effects"/> invalidates in <paramref name="world"/>, empty unless
    /// it invalidates physics, navigation or residency. Owners are the statics whose bounds meet the old or new edit
    /// bounds, plus the chunks of every listed patch: its own surface chunks and the chunks of every wall strip the
    /// world records in it. Storage tiles are those the old and new edit bounds reach, under the membership rule of
    /// <see cref="Build"/>, plus the membership of those patch chunks. Throws <see cref="MapDocumentException"/> when the
    /// grids do not align with the world.</summary>
    public static MapAffectedSet Affected(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(effects);
        grids.Validate(world);
        if ((effects.Invalidates & Spatial) == 0)
            return new(Array.Empty<string>(), Array.Empty<MapTileCoord>(), Array.Empty<MapNavTileCoord>());

        MapBox3[] edited = new[] { effects.OldBounds, effects.NewBounds }.Where(b => b.HasValue).Select(b => b!.Value).ToArray();
        string[] prefixes = PatchChunkPrefixes(world, effects.Patches);
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        var tiles = new SortedSet<MapTileCoord>(TileOrder);
        foreach (MapBox3 box in edited) tiles.UnionWith(Enumerate(TilesOf(box, grids.StorageTileSize)));
        foreach (MapStaticDescriptor descriptor in world.Statics)
        {
            bool patchChunk = descriptor.Kind == MapStaticKind.TerrainChunk &&
                prefixes.Any(p => descriptor.OwnerId.StartsWith(p, StringComparison.Ordinal));
            if (patchChunk) tiles.UnionWith(Enumerate(TilesOf(descriptor.Bounds, grids.StorageTileSize)));
            if (patchChunk || edited.Any(box => Meets(box, descriptor.Bounds))) owners.Add(descriptor.OwnerId);
        }
        MapNavTileCoord[] nav = tiles.Select(grids.NavTileOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToArray();
        return new(owners.ToArray(), tiles.ToArray(), nav);
    }

    // Each prefix ends in its separator, so "wall-1/" never matches "wall-10/...".
    static string[] PatchChunkPrefixes(MapBuiltWorld world, IReadOnlyList<MapPatchKey> patches)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (MapPatchKey key in patches)
        {
            prefixes.Add(FormattableString.Invariant($"{key.SurfaceId}/{key.SlotX},{key.SlotZ}/"));
            foreach (MapWallStrip strip in world.Surfaces.RecordsIn(key).OfType<MapWallStrip>())
                prefixes.Add(strip.Id + "/");
        }
        return prefixes.ToArray();
    }

    static readonly Comparer<MapTileCoord> TileOrder =
        Comparer<MapTileCoord>.Create((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));

    /// <summary>The storage tiles a box reaches: minimum inclusive, maximum exclusive, and an axis with no extent in
    /// the tile holding its minimum.</summary>
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
