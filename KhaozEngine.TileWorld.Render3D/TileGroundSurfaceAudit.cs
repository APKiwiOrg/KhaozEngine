using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.TileWorld;

/// <summary>One emitted tile/block, including all feather children and empty topology decisions.
/// Offsets address the completed mesh's original index buffer, never a reconstructed triangulation.</summary>
internal readonly record struct TileGroundSurfaceRange(TileGroundSurfaceExtent Tiles, int FirstIndex, int IndexCount,
    int FirstHeight, int HeightCount, int TopologyMargin, bool HeightCoordinatesRepresentable)
{
    // This rectangle is also the compact association into the shared coordinate-indexed topology grid.
    internal TileGroundAuditExtent TopologyExtent => Tiles.Expand(TopologyMargin);
    internal bool IsRepresentable => HeightCoordinatesRepresentable && TopologyExtent.IsAddressable;
}

/// <summary>A strict read at the shared geometry-height read point, including negative availability queries.</summary>
internal readonly struct TileGroundHeightWitness
{
    readonly RegionCoord[] _inspected;
    readonly byte[] _availability;
    internal int X { get; }
    internal int Z { get; }
    internal int RequestedPlane { get; }
    internal bool Available { get; }
    internal TileCornerHeightRead Value { get; }
    internal RegionCoord MissingRegion { get; }
    internal short SourceHeightCm { get; }
    internal short GeometryHeightCm { get; }
    internal ReadOnlySpan<RegionCoord> InspectedRegions => _inspected;
    internal long ArrayBytes => checked(9L * _inspected.Length);

    internal TileGroundHeightWitness(TileWorldDocument document, int x, int z, int plane, short geometryHeightCm)
    {
        X = x;
        Z = z;
        RequestedPlane = plane;
        GeometryHeightCm = geometryHeightCm;
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];
        Available = document.TryReadCornerHeight(x, z, plane, inspected, out int count,
            out TileCornerHeightRead value, out RegionCoord missing);
        Value = value;
        MissingRegion = missing;
        SourceHeightCm = Available ? document.CornerHeightCm(x, z, value.SourcePlane) : (short)0;
        _inspected = inspected[..count].ToArray();
        _availability = new byte[count];
        for (int i = 0; i < count; i++) _availability[i] = Availability(document, inspected[i]);
    }

    internal bool IsCurrent(TileWorldDocument document)
    {
        // Strict absence never certifies the fallback as optical bed, but its rendered value still
        // participates in mesh admission, including edge fan samples whose fallback is not another vertex.
        if (GeometryHeightCm != document.CornerHeightCm(X, Z, RequestedPlane)) return false;
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];
        bool available = document.TryReadCornerHeight(X, Z, RequestedPlane, inspected, out int count,
            out TileCornerHeightRead value, out RegionCoord missing);
        if (available != Available || value != Value || missing != MissingRegion ||
            !inspected[..count].SequenceEqual(_inspected)) return false;
        for (int i = 0; i < count; i++)
            if (Availability(document, inspected[i]) != _availability[i]) return false;
        return !available || SourceHeightCm == document.CornerHeightCm(X, Z, value.SourcePlane);
    }

    internal static byte Availability(TileWorldDocument document, RegionCoord region) =>
        document.GetRegion(region) is not null ? (byte)2 : document.UnloadedRegionHashes.ContainsKey(region) ? (byte)1 : (byte)0;
}

/// <summary>Source validity for exact emitted ranges. Admission may select only witnesses supporting its domain.
/// Value validation complements the publication owner's epochs, which detect edit/revert and unload/reload ABA.</summary>
internal sealed class TileGroundSurfaceAudit
{
    readonly TileGroundSurfaceRange[] _ranges;
    readonly TileGroundHeightWitness[] _heights;
    readonly int[] _rangeHeights;
    readonly Topology[] _topology;
    readonly int _topologyX, _topologyZ, _topologyWidth;
    readonly float _tileSize, _planeHeight, _featherWidth;
    readonly int _planeCount, _plane;

    readonly record struct Topology(int X, int Z, ushort Underlay, ushort Overlay, TileOverlayShape Shape,
        int Rotation, TileSettings Settings, GroundMaterialKind UnderlayKind, GroundMaterialKind OverlayKind,
        byte Availability)
    {
        internal static Topology Read(TileWorldDocument doc, TileWorldCatalogs catalogs, int x, int z, int plane)
        {
            ushort underlay = doc.GetUnderlay(x, z, plane), overlay = doc.GetOverlay(x, z, plane);
            return new Topology(x, z, underlay, overlay, doc.GetOverlayShape(x, z, plane),
                doc.GetOverlayRotation(x, z, plane),
                doc.GetSettings(x, z, plane) & (TileSettings.NoDraw | TileSettings.Bridge | TileSettings.FeatherOverlay),
                catalogs.Material(underlay)?.Kind ?? GroundMaterialKind.Ground,
                catalogs.Material(overlay)?.Kind ?? GroundMaterialKind.Ground,
                TileGroundHeightWitness.Availability(doc, RegionCoord.Of(x, z)));
        }
    }

    TileGroundSurfaceAudit(TileWorldDocument document, int plane, TileGroundMesherOptions options,
        TileGroundSurfaceRange[] ranges, TileGroundHeightWitness[] heights, int[] rangeHeights, Topology[] topology,
        int topologyX, int topologyZ, int topologyWidth)
    {
        _ranges = ranges;
        _heights = heights;
        _rangeHeights = rangeHeights;
        _topology = topology;
        _topologyX = topologyX;
        _topologyZ = topologyZ;
        _topologyWidth = topologyWidth;
        _tileSize = document.TileSize;
        _planeHeight = document.PlaneHeight;
        _planeCount = document.PlaneCount;
        _plane = plane;
        _featherWidth = options.OverlayFeatherWidthMetres;
        RetainedBytes = checked((long)_ranges.Length * Unsafe.SizeOf<TileGroundSurfaceRange>() +
            (long)_heights.Length * Unsafe.SizeOf<TileGroundHeightWitness>() + 4L * _rangeHeights.Length +
            (long)_topology.Length * Unsafe.SizeOf<Topology>());
        foreach (TileGroundHeightWitness height in _heights) RetainedBytes = checked(RetainedBytes + height.ArrayBytes);
    }

    internal ReadOnlySpan<TileGroundSurfaceRange> Ranges => _ranges;
    internal ReadOnlySpan<TileGroundHeightWitness> HeightReads => _heights;
    // Owned backing storage, including range associations and negative lookup arrays. No managed headers.
    internal long RetainedBytes { get; }
    internal ReadOnlySpan<int> HeightIndices(int range) =>
        _rangeHeights.AsSpan(_ranges[range].FirstHeight, _ranges[range].HeightCount);

    // Check this before interpreting missingRegion. An unaddressable footprint has no RegionCoord.
    internal bool IsRangeRepresentable(int range) => _ranges[range].IsRepresentable;

    internal bool IsRangeAvailable(int range, out RegionCoord missingRegion)
    {
        missingRegion = default;
        if (!IsRangeRepresentable(range)) return false;
        foreach (int index in HeightIndices(range))
            if (!_heights[index].Available)
            {
                missingRegion = _heights[index].MissingRegion;
                return false;
            }
        return true;
    }

    internal bool IsRangeCurrent(int range, TileWorldDocument document, TileWorldCatalogs catalogs,
        TileGroundMesherOptions options)
    {
        if (_tileSize != document.TileSize || _planeHeight != document.PlaneHeight ||
            _planeCount != document.PlaneCount || _featherWidth != options.OverlayFeatherWidthMetres) return false;
        foreach (int index in HeightIndices(range))
            if (!_heights[index].IsCurrent(document)) return false;
        // Currentness is separate from representability, so unsupported edge ranges do not abort ground
        // admission or refuse another range's valid interior optical domain.
        TileGroundAuditExtent inputs = _ranges[range].TopologyExtent.AddressablePart;
        for (long z = inputs.Z; z < inputs.Z1; z++)
            for (long x = inputs.X; x < inputs.X1; x++)
            {
                int at = checked((int)((z - _topologyZ) * _topologyWidth + x - _topologyX));
                if (_topology[at] != Topology.Read(document, catalogs, checked((int)x), checked((int)z), _plane))
                    return false;
            }
        return true;
    }

    internal bool IsCurrent(TileWorldDocument document, TileWorldCatalogs catalogs, TileGroundMesherOptions options)
    {
        for (int i = 0; i < _ranges.Length; i++)
            if (!IsRangeCurrent(i, document, catalogs, options)) return false;
        return true;
    }

    /// <summary>Build-scoped recorder. Cache hits re-associate their original strict witness with the active
    /// emission range. Normals and colours are excluded because they cannot change bed positions.</summary>
    internal sealed class Builder
    {
        internal readonly TileWorldDocument Document;
        internal readonly TileGroundMesherOptions Options;
        internal readonly int Plane;
        readonly TileWorldCatalogs _catalogs;
        readonly Dictionary<(int X, int Z), int> _heightLookup = new();
        readonly HashSet<int> _activeHeights = new();
        internal readonly List<TileGroundSurfaceRange> Ranges = new();
        internal readonly List<TileGroundHeightWitness> Heights = new();
        internal readonly List<int> RangeHeights = new();
        // Private so the immutable witness's topology representation cannot escape.
        readonly Dictionary<(int X, int Z), Topology> _topologies = new();
        TileGroundSurfaceExtent _tiles;
        int _firstIndex, _margin;
        bool _heightCoordinatesRepresentable;
        internal int ReadCount { get; private set; }

        internal Builder(TileWorldDocument document, TileWorldCatalogs catalogs, int plane, TileGroundMesherOptions options)
        {
            Document = document;
            _catalogs = catalogs;
            Plane = plane;
            Options = options;
        }

        internal void Begin(TileRect tiles, int firstIndex, int margin)
        {
            _tiles = new TileGroundSurfaceExtent(tiles.X, tiles.Z, tiles.Width, tiles.Height);
            _firstIndex = firstIndex;
            _margin = margin;
            _activeHeights.Clear();
            _heightCoordinatesRepresentable = true;
        }

        internal void ReadHeight(int x, int z, short geometryHeightCm)
        {
            // Legacy mesh arithmetic can wrap a corner beyond the last signed tile. Preserve that mesh,
            // but never query the wrapped coordinate as if it were an actual optical dependency.
            if (x < _tiles.X || x > _tiles.X1 || z < _tiles.Z || z > _tiles.Z1)
            {
                _heightCoordinatesRepresentable = false;
                return;
            }
            if (!_heightLookup.TryGetValue((x, z), out int index))
            {
                ReadCount = checked(ReadCount + 1);
                index = Heights.Count;
                Heights.Add(new TileGroundHeightWitness(Document, x, z, Plane, geometryHeightCm));
                _heightLookup.Add((x, z), index);
            }
            _activeHeights.Add(index);
        }

        internal void End(int endIndex)
        {
            int firstHeight = RangeHeights.Count;
            RangeHeights.AddRange(_activeHeights);
            TileGroundAuditExtent inputs = _tiles.Expand(_margin).AddressablePart;
            for (long z = inputs.Z; z < inputs.Z1; z++)
                for (long x = inputs.X; x < inputs.X1; x++)
                {
                    var key = (checked((int)x), checked((int)z));
                    if (_topologies.ContainsKey(key)) continue;
                    ReadCount = checked(ReadCount + 1);
                    _topologies.Add(key, Topology.Read(Document, _catalogs, key.Item1, key.Item2, Plane));
                }
            Ranges.Add(new TileGroundSurfaceRange(_tiles, _firstIndex, endIndex - _firstIndex,
                firstHeight, RangeHeights.Count - firstHeight, _margin, _heightCoordinatesRepresentable));
        }

        internal TileGroundSurfaceAudit Complete()
        {
            // One immutable slot per coordinate, shared by every overlapping footprint. Any unused grid
            // slots are still charged in RetainedBytes. Only recorded range footprints ever read them.
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var key in _topologies.Keys)
            {
                minX = Math.Min(minX, key.X);
                minZ = Math.Min(minZ, key.Z);
                maxX = Math.Max(maxX, key.X);
                maxZ = Math.Max(maxZ, key.Z);
            }
            int width = _topologies.Count == 0 ? 0 : checked((int)((long)maxX - minX + 1));
            int height = _topologies.Count == 0 ? 0 : checked((int)((long)maxZ - minZ + 1));
            var topology = new Topology[checked(width * height)];
            foreach (var entry in _topologies)
                topology[checked((int)(((long)entry.Key.Z - minZ) * width + entry.Key.X - minX))] = entry.Value;
            return new(Document, Plane, Options, Ranges.ToArray(), Heights.ToArray(), RangeHeights.ToArray(),
                topology, minX, minZ, width);
        }
    }
}
