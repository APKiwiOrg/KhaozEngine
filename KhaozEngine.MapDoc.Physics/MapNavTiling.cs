using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>How navigation tiles are captured. <see cref="SeamMarginMetres"/> grows each tile's capture past its edges
/// so neighbouring captures overlap at every seam. <see cref="ProfileIdentity"/> names the navigation profile and
/// <see cref="ControllerIdentity"/> the movement controller a capture is proved with. All three enter every capture
/// identity.</summary>
public sealed record MapNavTileOptions(float SeamMarginMetres, string ProfileIdentity, string ControllerIdentity);

/// <summary>One navigation tile of a built world. <see cref="Bounds"/> is the whole navigation tile on X and Z and the
/// world's bounds on Y. <see cref="CaptureBounds"/> is <see cref="Bounds"/> grown by the seam margin on X and Z. Both are
/// residency extents, not probe windows: a resolver-1 world's bounds exclude its analytic terrain height, so a capture
/// picks its own vertical window. <see cref="GeometryDigest"/> names the geometry a capture over
/// <see cref="CaptureBounds"/> can see, and <see cref="CaptureIdentity"/> names that capture.</summary>
public sealed record MapNavTile(MapNavTileCoord Coord, MapBox3 Bounds, MapBox3 CaptureBounds, string GeometryDigest,
    string CaptureIdentity);

/// <summary>The shared edge of two neighbouring navigation tiles, <see cref="A"/> before <see cref="B"/> in (Z, X)
/// order. <see cref="Digest"/> covers both tiles' geometry digests, the edge and the seam margin.</summary>
public sealed record MapNavSeam(MapNavTileCoord A, MapNavTileCoord B, string Digest);

/// <summary>One R2 portal or vertical link record whose aperture geometry reaches two navigation tiles,
/// <see cref="From"/> before <see cref="To"/> in (Z, X) order. <see cref="Digest"/> covers the record id, the semantic
/// digests of the record and of every aperture record it names, and both tiles.</summary>
public sealed record MapNavLink(string RecordId, MapNavTileCoord From, MapNavTileCoord To, string Digest);

/// <summary>Deterministic navigation tiles over a built world, with their capture identities, seams, cross-tile links
/// and the tiles an edit invalidates, so a consumer rebakes only those tiles. Navigation tiles are columns in X and Z.
/// Vertical layers on native worlds wait for #438 phase 5.</summary>
public static class MapNavTiling
{
    /// <summary>The most navigation tiles <see cref="Partition"/> lists, 2^20. At 64 m tiles that is, for example, a
    /// square world about 65 km on a side.</summary>
    public const int MaxPartitionTiles = 1 << 20;

    static readonly Comparer<MapNavTileCoord> TileOrder =
        Comparer<MapNavTileCoord>.Create((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));

    /// <summary>The navigation tiles the world's bounds reach in (Z, X) order, minimum inclusive and maximum exclusive
    /// on X and Z, and an axis with no extent in the tile holding its minimum.
    /// <para><see cref="MapNavTile.GeometryDigest"/> covers, in ordinal owner order, the owner id and digest of every
    /// static whose residency bounds meet <see cref="MapNavTile.CaptureBounds"/> on X and Z, edges included. For a
    /// resolver-1 world it also covers <see cref="MapBuiltWorld.LegacyTerrainBlockDigest"/> and every sculpt tile whose
    /// footprint meets the capture bounds on X and Z. Every static lies within the world's vertical bounds, so Y never
    /// excludes one. <see cref="MapNavTile.CaptureIdentity"/> is the SHA-256 over the geometry digest, profile,
    /// controller, seam margin, tile coordinate and tile bounds.</para>
    /// <para>Throws <see cref="MapDocumentException"/> naming the grid alignment when the grids do not align with the
    /// world, including an origin so far from the world that a navigation tile or server cell index of its bounds
    /// leaves the int range, naming the tile range when a seam margin carries a capture outside the int range, naming
    /// the tile count when the world's bounds reach more than <see cref="MaxPartitionTiles"/> tiles or one static
    /// reaches more than <see cref="MapResidencyOwnership.MaxStorageTiles"/> storage tiles, and
    /// <see cref="ArgumentException"/> for invalid options.</para></summary>
    public static IReadOnlyList<MapNavTile> Partition(MapBuiltWorld world, MapWorldGrids grids, MapNavTileOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        ValidateOptions(options);
        // The tile count is refused before any static is placed on the grids.
        grids.Validate(world);
        (int minX, int maxX, int minZ, int maxZ) = PartitionRange(world, grids);
        long count = RequirePartitionCount(minX, maxX, minZ, maxZ);
        IReadOnlyList<MapResidencyEntry> entries = MapResidencyOwnership.Build(world, grids);
        var digests = new Dictionary<string, string>(world.Statics.Count, StringComparer.Ordinal);
        foreach (MapStaticDescriptor descriptor in world.Statics) digests.Add(descriptor.OwnerId, descriptor.Digest);
        var statics = new Dictionary<MapNavTileCoord, List<MapResidencyEntry>>();
        foreach (MapResidencyEntry entry in entries)
            foreach (MapNavTileCoord tile in CaptureTilesMeeting(entry.WorldBounds.MinX, entry.WorldBounds.MinZ,
                entry.WorldBounds.MaxX, entry.WorldBounds.MaxZ, grids, options.SeamMarginMetres))
                if (tile.X >= minX && tile.X <= maxX && tile.Z >= minZ && tile.Z <= maxZ)
                    Bucket(statics, tile).Add(entry);
        var sculpt = new Dictionary<MapNavTileCoord, List<MapLegacySculptTile>>();
        foreach (MapLegacySculptTile tile in world.LegacySculptTiles)
            foreach (MapNavTileCoord coord in CaptureTilesMeeting(tile.Footprint.MinX, tile.Footprint.MinZ,
                tile.Footprint.MaxX, tile.Footprint.MaxZ, grids, options.SeamMarginMetres))
                if (coord.X >= minX && coord.X <= maxX && coord.Z >= minZ && coord.Z <= maxZ)
                    Bucket(sculpt, coord).Add(tile);

        var tiles = new List<MapNavTile>((int)count);
        for (long z = minZ; z <= maxZ; z++)
            for (long x = minX; x <= maxX; x++)
            {
                var coord = new MapNavTileCoord((int)x, (int)z);
                MapBox3 bounds = TileBounds(coord, grids, world.Bounds.MinY, world.Bounds.MaxY);
                MapBox3 capture = Grow(bounds, options.SeamMarginMetres);
                MapResidencyEntry[] seen = statics.TryGetValue(coord, out List<MapResidencyEntry>? found)
                    ? found.OrderBy(e => e.OwnerId, StringComparer.Ordinal).ToArray() : Array.Empty<MapResidencyEntry>();
                MapLegacySculptTile[] touched = sculpt.TryGetValue(coord, out List<MapLegacySculptTile>? cells)
                    ? cells.ToArray() : Array.Empty<MapLegacySculptTile>();
                string geometry = GeometryDigest(world, seen, digests, touched);
                tiles.Add(new MapNavTile(coord, bounds, capture, geometry, CaptureIdentity(coord, bounds, geometry, options)));
            }
        return tiles.AsReadOnly();
    }

    /// <summary>The shared edge of every pair of neighbouring tiles in <paramref name="tiles"/>, ordered by
    /// <see cref="MapNavSeam.A"/> and then <see cref="MapNavSeam.B"/> in (Z, X) order. Throws
    /// <see cref="ArgumentException"/> when two tiles share a coordinate, neighbours do not meet at one edge, or a tile's
    /// vertical bounds are not <paramref name="world"/>'s, which means it was partitioned from another world.</summary>
    public static IReadOnlyList<MapNavSeam> Seams(IReadOnlyList<MapNavTile> tiles, MapBuiltWorld world, MapNavTileOptions options)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(world);
        ValidateOptions(options);
        var byCoord = new Dictionary<MapNavTileCoord, MapNavTile>(tiles.Count);
        foreach (MapNavTile tile in tiles)
        {
            ArgumentNullException.ThrowIfNull(tile, nameof(tiles));
            if (tile.Bounds.MinY != world.Bounds.MinY || tile.Bounds.MaxY != world.Bounds.MaxY)
                throw new ArgumentException($"Tile {tile.Coord} was not partitioned from this world.", nameof(tiles));
            if (!byCoord.TryAdd(tile.Coord, tile))
                throw new ArgumentException($"Two tiles share the coordinate {tile.Coord}.", nameof(tiles));
        }
        var seams = new List<MapNavSeam>();
        foreach (MapNavTile a in byCoord.Values.OrderBy(t => t.Coord, TileOrder))
        {
            if (byCoord.TryGetValue(new MapNavTileCoord(a.Coord.X + 1, a.Coord.Z), out MapNavTile? east))
                seams.Add(Seam(a, east, edgeAlongX: false, options.SeamMarginMetres));
            if (byCoord.TryGetValue(new MapNavTileCoord(a.Coord.X, a.Coord.Z + 1), out MapNavTile? north))
                seams.Add(Seam(a, north, edgeAlongX: true, options.SeamMarginMetres));
        }
        return seams.OrderBy(s => s.A, TileOrder).ThenBy(s => s.B, TileOrder).ToArray();
    }

    /// <summary>The cross-tile links of every R2 cave portal and vertical link record in the world's surfaces, in ordinal
    /// record id order. A cave portal's aperture is its interval, a vertical link's the opening planes of its horizontal
    /// openings and the intervals of its portals. Each interval segment and opening triangle reaches the tiles its X
    /// and Z bounds reach under the rule of <see cref="Partition"/>. A record reaching two tiles yields one link. A
    /// record reaching more yields one link per pair of them, ordered by <see cref="MapNavLink.From"/> and then
    /// <see cref="MapNavLink.To"/>. A resolver-1 world has no surfaces and so no links.
    /// <para>Throws <see cref="MapDocumentException"/> naming the grid alignment when the grids do not align with the
    /// world, naming missing geometry when an aperture record, patch or surface is absent, and naming the tile range
    /// when an aperture reaches a tile index outside the int range.</para></summary>
    public static IReadOnlyList<MapNavLink> Links(MapBuiltWorld world, MapWorldGrids grids)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        grids.Validate(world);
        MapScopedSurfaces view = world.Surfaces;
        var links = new List<MapNavLink>();
        foreach (MapRecordRef reference in view.Witness.Records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            MapTopologyRecord record = Record<MapTopologyRecord>(view, reference);
            if (record is not (MapCavePortal or MapVerticalLink)) continue;
            var tiles = new SortedSet<MapNavTileCoord>(TileOrder);
            var apertures = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (record is MapCavePortal portal) AddPortal(view, portal, grids, tiles);
            else
            {
                var link = (MapVerticalLink)record;
                foreach (MapRecordRef opening in link.Openings)
                {
                    MapHorizontalOpening found = Record<MapHorizontalOpening>(view, opening);
                    AddOpening(view, found, grids, tiles);
                    apertures[found.Id] = MapSurfaceSemantics.RecordDigest(opening.Anchor, found);
                }
                foreach (MapRecordRef portalRef in link.Portals)
                {
                    MapCavePortal found = Record<MapCavePortal>(view, portalRef);
                    AddPortal(view, found, grids, tiles);
                    apertures[found.Id] = MapSurfaceSemantics.RecordDigest(portalRef.Anchor, found);
                }
            }
            if (tiles.Count < 2) continue;
            string semantic = MapSurfaceSemantics.RecordDigest(reference.Anchor, record);
            MapNavTileCoord[] reached = tiles.ToArray();
            for (int i = 0; i < reached.Length; i++)
                for (int j = i + 1; j < reached.Length; j++)
                    links.Add(new MapNavLink(record.Id, reached[i], reached[j],
                        LinkDigest(record.Id, semantic, apertures, reached[i], reached[j])));
        }
        return links.AsReadOnly();
    }

    /// <summary>The navigation tiles <paramref name="effects"/> invalidates in (Z, X) order, empty unless it invalidates
    /// navigation. <paramref name="world"/> is the world built after the edit, as for
    /// <see cref="MapResidencyOwnership.Affected"/>, whose navigation tiles this widens by the seam margin: every tile
    /// whose capture bounds meet, on X and Z, the old or new edit bounds or the bounds of a chunk of a listed patch. For a
    /// resolver-1 terrain edit it also widens each edit box by the footprint of every sculpt tile position the box meets,
    /// taken arithmetically from <see cref="TerrainSculpt.TileSize"/> and <see cref="MapBuiltWorld.LegacySculptCellSize"/>
    /// whether or not that tile exists, so a sculpt tile the edit added or removed lists every tile whose geometry
    /// digest it enters or leaves. The result is clipped to the tiles <see cref="Partition"/> lists for
    /// <paramref name="world"/>. An edit flagged <see cref="MapNativeInvalidation.Unbounded"/>, a full reset, lists every
    /// one of those tiles without first listing every storage tile.
    /// <para>A tile that leaves the partition, because the edit shrank the world's bounds, is never listed, since the
    /// result is clipped to the post-edit partition. A consumer that keeps derived state per tile diffs the old and new
    /// partitions to find removed tiles.</para>
    /// <para>Throws as <see cref="MapResidencyOwnership.Affected"/> does, including for an unbounded edit's boxes, as
    /// <see cref="Partition"/> does when an unbounded edit's world reaches more than <see cref="MaxPartitionTiles"/>
    /// tiles, and <see cref="ArgumentException"/> for invalid options.</para></summary>
    public static IReadOnlyList<MapNavTileCoord> AffectedTiles(MapBuiltWorld world, MapWorldGrids grids,
        MapNavTileOptions options, MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grids);
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(effects);
        const MapNativeInvalidation unbounded = MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded;
        if ((effects.Invalidates & unbounded) == unbounded)
        {
            // Every partition tile, without enumerating every storage tile through Affected first. The edit boxes are
            // still checked, as Affected checks them.
            grids.Validate(world);
            foreach (MapBox3? box in new[] { effects.OldBounds, effects.NewBounds })
                if (box is { } within) MapResidencyOwnership.RequireWithinWorld(within);
            (int minX, int maxX, int minZ, int maxZ) = PartitionRange(world, grids);
            var every = new MapNavTileCoord[RequirePartitionCount(minX, maxX, minZ, maxZ)];
            int index = 0;
            for (long z = minZ; z <= maxZ; z++)
                for (long x = minX; x <= maxX; x++)
                    every[index++] = new MapNavTileCoord((int)x, (int)z);
            return every;
        }
        MapAffectedSet affected = MapResidencyOwnership.Affected(world, grids, effects);
        if ((effects.Invalidates & MapNativeInvalidation.Nav) == 0) return Array.Empty<MapNavTileCoord>();

        MapBox3[] edited = new[] { effects.OldBounds, effects.NewBounds }.Where(b => b.HasValue).Select(b => b!.Value).ToArray();
        var widen = new List<(double MinX, double MinZ, double MaxX, double MaxZ)>();
        foreach (MapBox3 box in edited) widen.Add((box.MinX, box.MinZ, box.MaxX, box.MaxZ));
        string[] prefixes = MapResidencyOwnership.PatchChunkPrefixes(world, effects.Patches);
        foreach (MapStaticDescriptor descriptor in world.Statics)
            if (descriptor.Kind == MapStaticKind.TerrainChunk &&
                prefixes.Any(p => descriptor.OwnerId.StartsWith(p, StringComparison.Ordinal)))
                widen.Add((descriptor.Bounds.MinX, descriptor.Bounds.MinZ, descriptor.Bounds.MaxX, descriptor.Bounds.MaxZ));
        if (!world.IsNative && (effects.Invalidates & MapNativeInvalidation.Terrain) != 0)
            foreach (MapBox3 box in edited)
            {
                (double minX, double maxX) = SculptSpan(box.MinX, box.MaxX, world.LegacySculptCellSize);
                (double minZ, double maxZ) = SculptSpan(box.MinZ, box.MaxZ, world.LegacySculptCellSize);
                widen.Add((minX, minZ, maxX, maxZ));
            }

        var tiles = new SortedSet<MapNavTileCoord>(affected.NavTiles, TileOrder);
        foreach (var (minX, minZ, maxX, maxZ) in widen)
            tiles.UnionWith(CaptureTilesMeeting(minX, minZ, maxX, maxZ, grids, options.SeamMarginMetres));
        (int firstX, int lastX, int firstZ, int lastZ) = PartitionRange(world, grids);
        return tiles.Where(t => t.X >= firstX && t.X <= lastX && t.Z >= firstZ && t.Z <= lastZ).ToArray();
    }

    /// <summary>The number of tiles in a partition range, after refusing a range beyond
    /// <see cref="MaxPartitionTiles"/>.</summary>
    static long RequirePartitionCount(int minX, int maxX, int minZ, int maxZ)
    {
        long count = ((long)maxX - minX + 1) * ((long)maxZ - minZ + 1);
        if (count > MaxPartitionTiles)
            throw new MapDocumentException(string.Format(CultureInfo.InvariantCulture,
                "The world's bounds reach {0:N0} navigation tiles, more than the {1:N0} a partition allows.", count,
                MaxPartitionTiles));
        return count;
    }

    /// <summary>The tile range <see cref="Partition"/> lists for <paramref name="world"/>.</summary>
    static (int MinX, int MaxX, int MinZ, int MaxZ) PartitionRange(MapBuiltWorld world, MapWorldGrids grids)
    {
        (int minX, int maxX) = TileAxis(world.Bounds.MinX, world.Bounds.MaxX, grids.Origin.X, grids.NavTileSize);
        (int minZ, int maxZ) = TileAxis(world.Bounds.MinZ, world.Bounds.MaxZ, grids.Origin.Y, grids.NavTileSize);
        return (minX, maxX, minZ, maxZ);
    }

    /// <summary>The union along one axis of the footprints of every sculpt tile position whose footprint meets
    /// [<paramref name="min"/>, <paramref name="max"/>], edges included. Neighbouring footprints overlap by a cell, so
    /// the union is one span. The candidate range is one tile wider on each side than the arithmetic needs and is then
    /// narrowed against <see cref="MapWorldBuilder.SculptFootprintAxis"/>, so it matches the footprints the builder
    /// records exactly.</summary>
    static (double Min, double Max) SculptSpan(double min, double max, double cell)
    {
        double width = TerrainSculpt.TileSize * cell;
        long first = checked((long)Math.Floor(min / width) - 1), last = checked((long)Math.Floor(max / width) + 1);
        while (MapWorldBuilder.SculptFootprintAxis(first, cell).Max < min) first++;
        while (MapWorldBuilder.SculptFootprintAxis(last, cell).Min > max) last--;
        return (MapWorldBuilder.SculptFootprintAxis(first, cell).Min, MapWorldBuilder.SculptFootprintAxis(last, cell).Max);
    }

    static void ValidateOptions(MapNavTileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!float.IsFinite(options.SeamMarginMetres) || options.SeamMarginMetres < 0f)
            throw new ArgumentException("SeamMarginMetres must be finite and not negative.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ProfileIdentity))
            throw new ArgumentException("ProfileIdentity must name the navigation profile.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ControllerIdentity))
            throw new ArgumentException("ControllerIdentity must name the movement controller.", nameof(options));
    }

    static List<T> Bucket<T>(Dictionary<MapNavTileCoord, List<T>> buckets, MapNavTileCoord tile)
    {
        if (!buckets.TryGetValue(tile, out List<T>? list)) buckets.Add(tile, list = new List<T>());
        return list;
    }

    /// <summary>The tiles a range reaches: minimum inclusive, maximum exclusive, and an axis with no extent in the tile
    /// holding its minimum, counted from <paramref name="origin"/>. Throws <see cref="MapDocumentException"/> naming the
    /// tile range when an index falls outside the int range.</summary>
    static (int Min, int Max) TileAxis(double min, double max, double origin, double size)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max < min)
            throw new ArgumentException(FormattableString.Invariant($"bounds [{min}, {max}] are not a finite range"));
        double first = Math.Floor((min - origin) / size);
        double last = max > min ? Math.Max(first, Math.Ceiling((max - origin) / size) - 1) : first;
        if (first < int.MinValue || last > int.MaxValue)
            throw new MapDocumentException(FormattableString.Invariant(
                $"Bounds [{min}, {max}] reach navigation tiles {first:F0} to {last:F0} from origin {origin}, ") +
                FormattableString.Invariant($"outside the {int.MinValue:N0} to {int.MaxValue:N0} tile range."));
        return ((int)first, (int)last);
    }

    static MapBox3 TileBounds(MapNavTileCoord tile, MapWorldGrids grids, double minY, double maxY)
    {
        double size = grids.NavTileSize;
        double x = grids.Origin.X + tile.X * size, z = grids.Origin.Y + tile.Z * size;
        return new MapBox3(x, minY, z, x + size, maxY, z + size);
    }

    static MapBox3 Grow(MapBox3 box, double margin) =>
        new(box.MinX - margin, box.MinY, box.MinZ - margin, box.MaxX + margin, box.MaxY, box.MaxZ + margin);

    /// <summary>Every tile whose capture bounds meet the closed XZ rectangle, edges included. Candidates come from a
    /// range one tile wider on each side than the arithmetic needs, and each is kept only when
    /// <see cref="MeetsXz"/> holds against its capture bounds, so rounding never decides membership.</summary>
    static IEnumerable<MapNavTileCoord> CaptureTilesMeeting(double minX, double minZ, double maxX, double maxZ,
        MapWorldGrids grids, double margin)
    {
        double size = grids.NavTileSize;
        int x0 = TileIndex(Math.Floor((minX - margin - grids.Origin.X) / size) - 1);
        int x1 = TileIndex(Math.Floor((maxX + margin - grids.Origin.X) / size) + 1);
        int z0 = TileIndex(Math.Floor((minZ - margin - grids.Origin.Y) / size) - 1);
        int z1 = TileIndex(Math.Floor((maxZ + margin - grids.Origin.Y) / size) + 1);
        for (long z = z0; z <= z1; z++)
            for (long x = x0; x <= x1; x++)
            {
                var tile = new MapNavTileCoord((int)x, (int)z);
                MapBox3 capture = Grow(TileBounds(tile, grids, 0, 0), margin);
                if (MeetsXz(capture.MinX, capture.MinZ, capture.MaxX, capture.MaxZ, minX, minZ, maxX, maxZ)) yield return tile;
            }
    }

    // A candidate index. Validate keeps the world's own range, one tile wider, inside int, so only geometry or a seam
    // margin reaching far past the world refuses here.
    static int TileIndex(double index) => index is >= int.MinValue and <= int.MaxValue
        ? (int)index
        : throw new MapDocumentException(FormattableString.Invariant(
            $"Navigation tile {index:F0} lies outside the {int.MinValue:N0} to {int.MaxValue:N0} tile range."));

    static bool MeetsXz(double aMinX, double aMinZ, double aMaxX, double aMaxZ, double bMinX, double bMinZ, double bMaxX,
        double bMaxZ) => aMinX <= bMaxX && bMinX <= aMaxX && aMinZ <= bMaxZ && bMinZ <= aMaxZ;

    static string GeometryDigest(MapBuiltWorld world, IReadOnlyList<MapResidencyEntry> statics,
        Dictionary<string, string> digests, IReadOnlyList<MapLegacySculptTile> sculpt) => CanonicalHash(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", "kemap/nav-tile-geometry/1");
        w.WriteBoolean("native", world.IsNative);
        w.WriteStartArray("statics");
        foreach (MapResidencyEntry entry in statics)
        {
            w.WriteStartArray();
            w.WriteStringValue(entry.OwnerId);
            w.WriteStringValue(digests[entry.OwnerId]);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteString("legacyTerrainBlock", world.LegacyTerrainBlockDigest);
        w.WriteStartArray("sculpt");
        foreach (MapLegacySculptTile tile in sculpt)
        {
            w.WriteStartArray();
            w.WriteNumberValue(tile.TileX);
            w.WriteNumberValue(tile.TileZ);
            w.WriteStringValue(tile.Digest);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    static string CaptureIdentity(MapNavTileCoord coord, MapBox3 bounds, string geometry, MapNavTileOptions options) =>
        CanonicalHash(w =>
        {
            w.WriteStartObject();
            w.WriteString("domain", "kemap/nav-tile-capture/1");
            w.WriteString("geometryDigest", geometry);
            w.WriteString("profile", options.ProfileIdentity);
            w.WriteString("controller", options.ControllerIdentity);
            w.WriteNumber("seamMarginMetres", options.SeamMarginMetres);
            Tile(w, "tile", coord);
            w.WriteStartArray("bounds");
            w.WriteNumberValue(bounds.MinX);
            w.WriteNumberValue(bounds.MinZ);
            w.WriteNumberValue(bounds.MaxX);
            w.WriteNumberValue(bounds.MaxZ);
            w.WriteEndArray();
            w.WriteEndObject();
        });

    static MapNavSeam Seam(MapNavTile a, MapNavTile b, bool edgeAlongX, float margin)
    {
        // An edge along X joins a tile to its north neighbour at z = a.MaxZ. Otherwise the edge runs along Z and joins a
        // tile to its east neighbour at x = a.MaxX.
        double x0, z0, x1, z1;
        if (edgeAlongX)
        {
            if (a.Bounds.MaxZ != b.Bounds.MinZ) throw new ArgumentException($"Tiles {a.Coord} and {b.Coord} do not meet at one edge.");
            (x0, x1, z0, z1) = (Math.Max(a.Bounds.MinX, b.Bounds.MinX), Math.Min(a.Bounds.MaxX, b.Bounds.MaxX), a.Bounds.MaxZ, a.Bounds.MaxZ);
        }
        else
        {
            if (a.Bounds.MaxX != b.Bounds.MinX) throw new ArgumentException($"Tiles {a.Coord} and {b.Coord} do not meet at one edge.");
            (x0, x1, z0, z1) = (a.Bounds.MaxX, a.Bounds.MaxX, Math.Max(a.Bounds.MinZ, b.Bounds.MinZ), Math.Min(a.Bounds.MaxZ, b.Bounds.MaxZ));
        }
        if (x1 < x0 || z1 < z0) throw new ArgumentException($"Tiles {a.Coord} and {b.Coord} do not meet at one edge.");
        return new MapNavSeam(a.Coord, b.Coord, CanonicalHash(w =>
        {
            w.WriteStartObject();
            w.WriteString("domain", "kemap/nav-seam/1");
            Tile(w, "a", a.Coord);
            w.WriteString("aGeometry", a.GeometryDigest);
            Tile(w, "b", b.Coord);
            w.WriteString("bGeometry", b.GeometryDigest);
            w.WriteStartArray("edge");
            w.WriteNumberValue(x0);
            w.WriteNumberValue(z0);
            w.WriteNumberValue(x1);
            w.WriteNumberValue(z1);
            w.WriteEndArray();
            w.WriteNumber("seamMarginMetres", margin);
            w.WriteEndObject();
        }));
    }

    static string LinkDigest(string id, string semantic, SortedDictionary<string, string> apertures, MapNavTileCoord from,
        MapNavTileCoord to) => CanonicalHash(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", "kemap/nav-link/1");
        w.WriteString("recordId", id);
        w.WriteString("semantic", semantic);
        w.WriteStartArray("apertures");
        foreach (KeyValuePair<string, string> aperture in apertures)
        {
            w.WriteStartArray();
            w.WriteStringValue(aperture.Key);
            w.WriteStringValue(aperture.Value);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        Tile(w, "from", from);
        Tile(w, "to", to);
        w.WriteEndObject();
    });

    static void Tile(Utf8JsonWriter w, string name, MapNavTileCoord tile)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(tile.X);
        w.WriteNumberValue(tile.Z);
        w.WriteEndArray();
    }

    static void AddPortal(MapScopedSurfaces view, MapCavePortal portal, MapWorldGrids grids, SortedSet<MapNavTileCoord> tiles)
    {
        if (portal.Interval.Count < 2)
            throw new MapDocumentException($"invalid vertex sequence: portal '{portal.Id}'");
        MapExactXz[] points = portal.Interval.Select(v => Surface(view, v.SurfaceId).Frame.WorldXz(v.Address)).ToArray();
        for (int i = 1; i < points.Length; i++)
            AddReach(tiles, grids, points[i - 1].X.ToDouble(), points[i - 1].Z.ToDouble(), points[i].X.ToDouble(),
                points[i].Z.ToDouble());
    }

    static void AddOpening(MapScopedSurfaces view, MapHorizontalOpening opening, MapWorldGrids grids,
        SortedSet<MapNavTileCoord> tiles)
    {
        MapSurfacePatch patch = view.Patch(opening.Patch).Patch
            ?? throw new MapDocumentException($"missing geometry: opening '{opening.Id}' patch {opening.Patch}");
        MapOpeningPlane plane = MapOpeningBoundary.Compile(Surface(view, opening.Patch.SurfaceId), patch, opening);
        foreach (MapExactTriangle t in plane.ExactTriangles)
        {
            double[] xs = { t.A.X.ToDouble(), t.B.X.ToDouble(), t.C.X.ToDouble() };
            double[] zs = { t.A.Z.ToDouble(), t.B.Z.ToDouble(), t.C.Z.ToDouble() };
            AddReach(tiles, grids, xs.Min(), zs.Min(), xs.Max(), zs.Max());
        }
    }

    static void AddReach(SortedSet<MapNavTileCoord> tiles, MapWorldGrids grids, double ax, double az, double bx, double bz)
    {
        (int minX, int maxX) = TileAxis(Math.Min(ax, bx), Math.Max(ax, bx), grids.Origin.X, grids.NavTileSize);
        (int minZ, int maxZ) = TileAxis(Math.Min(az, bz), Math.Max(az, bz), grids.Origin.Y, grids.NavTileSize);
        for (long z = minZ; z <= maxZ; z++)
            for (long x = minX; x <= maxX; x++)
                tiles.Add(new MapNavTileCoord((int)x, (int)z));
    }

    static MapSurfaceRef Surface(MapScopedSurfaces view, string id) =>
        view.Surfaces.FirstOrDefault(s => s.Id == id) ?? throw new MapDocumentException($"missing geometry: surface '{id}'");

    static T Record<T>(MapScopedSurfaces view, MapRecordRef reference) where T : MapTopologyRecord
    {
        if (!view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus status) || record is not T typed)
            throw new MapDocumentException($"missing geometry: record '{reference.Id}' {reference.Anchor} ({status})");
        return typed;
    }

    // Compact canonical JSON, hashed as written.
    static string CanonicalHash(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            body(writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
