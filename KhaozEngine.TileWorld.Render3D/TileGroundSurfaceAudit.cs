using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.TileWorld;

/// <summary>One emitted tile/block, including all feather children and empty topology decisions.
/// Offsets address the completed mesh's original index buffer, never a reconstructed triangulation.</summary>
internal readonly record struct TileGroundSurfaceRange(TileRect Tiles, int FirstIndex, int IndexCount,
    int FirstHeight, int HeightCount, int FirstTopology, int TopologyCount);

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
        TileGroundSurfaceRange[] ranges, TileGroundHeightWitness[] heights, int[] rangeHeights, Topology[] topology)
    {
        _ranges = ranges;
        _heights = heights;
        _rangeHeights = rangeHeights;
        _topology = topology;
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

    internal bool IsRangeAvailable(int range, out RegionCoord missingRegion)
    {
        foreach (int index in HeightIndices(range))
            if (!_heights[index].Available)
            {
                missingRegion = _heights[index].MissingRegion;
                return false;
            }
        missingRegion = default;
        return true;
    }

    internal bool IsRangeCurrent(int range, TileWorldDocument document, TileWorldCatalogs catalogs,
        TileGroundMesherOptions options)
    {
        if (_tileSize != document.TileSize || _planeHeight != document.PlaneHeight ||
            _planeCount != document.PlaneCount || _featherWidth != options.OverlayFeatherWidthMetres) return false;
        foreach (int index in HeightIndices(range))
            if (!_heights[index].IsCurrent(document)) return false;
        TileGroundSurfaceRange r = _ranges[range];
        foreach (Topology input in _topology.AsSpan(r.FirstTopology, r.TopologyCount))
            if (input != Topology.Read(document, catalogs, input.X, input.Z, _plane)) return false;
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
        readonly List<Topology> _topologies = new();
        TileRect _tiles;
        int _firstIndex, _margin;

        internal Builder(TileWorldDocument document, TileWorldCatalogs catalogs, int plane, TileGroundMesherOptions options)
        {
            Document = document;
            _catalogs = catalogs;
            Plane = plane;
            Options = options;
        }

        internal void Begin(TileRect tiles, int firstIndex, int margin)
        {
            _tiles = tiles;
            _firstIndex = firstIndex;
            _margin = margin;
            _activeHeights.Clear();
        }

        internal void ReadHeight(int x, int z, short geometryHeightCm)
        {
            if (!_heightLookup.TryGetValue((x, z), out int index))
            {
                index = Heights.Count;
                Heights.Add(new TileGroundHeightWitness(Document, x, z, Plane, geometryHeightCm));
                _heightLookup.Add((x, z), index);
            }
            _activeHeights.Add(index);
        }

        internal void End(int endIndex)
        {
            int firstHeight = RangeHeights.Count, firstTopology = _topologies.Count;
            RangeHeights.AddRange(_activeHeights);
            TileRect inputs = _tiles.Expand(_margin);
            for (int z = inputs.Z; z < inputs.Z1; z++)
                for (int x = inputs.X; x < inputs.X1; x++)
                    _topologies.Add(Topology.Read(Document, _catalogs, x, z, Plane));
            Ranges.Add(new TileGroundSurfaceRange(_tiles, _firstIndex, endIndex - _firstIndex,
                firstHeight, RangeHeights.Count - firstHeight, firstTopology, _topologies.Count - firstTopology));
        }

        internal TileGroundSurfaceAudit Complete() => new(Document, Plane, Options, Ranges.ToArray(),
            Heights.ToArray(), RangeHeights.ToArray(), _topologies.ToArray());
    }
}
