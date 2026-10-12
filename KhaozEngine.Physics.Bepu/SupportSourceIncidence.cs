using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Same-query direct source incidence. Every raw vertex link remains present.
/// The offered bitmap carries only overlapping point witnesses, never transitive connectivity.</summary>
internal sealed class SupportSourceIncidence
{
    readonly record struct VertexKey(int Map, int X, int Y, int Z);
    readonly record struct Link(int Polygon, int Vertex, int Next);
    readonly Dictionary<SupportPolygonSourceMap, int> _maps = new(SupportSourceMapComparer.Instance);
    readonly Dictionary<VertexKey, int> _heads = [];
    readonly List<Link> _links = [];
    readonly SupportPolygon?[] _polygons = new SupportPolygon?[SupportFootprintResult.MaximumElements];
    readonly SupportMeetingDomain?[] _domains = new SupportMeetingDomain?[SupportFootprintResult.MaximumElements];
    readonly bool[] _prepared = new bool[SupportFootprintResult.MaximumElements];
    readonly ulong[] _offered = new ulong[1024];
    int _count, _stride;

    internal void Build(ReadOnlySpan<SupportPolygon?> polygons)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportIncidenceSetup);
        if (polygons.Length > _polygons.Length) throw new ArgumentOutOfRangeException(nameof(polygons));
        _count = polygons.Length;
        _stride = SupportFootprintResult.JoinWordsFor(_count);
        Array.Clear(_polygons);
        polygons.CopyTo(_polygons);
        Array.Clear(_prepared);
        _offered.AsSpan(0, _count * _stride).Clear();
        _maps.Clear();
        _heads.Clear();
        _links.Clear();
        for (int i = 0; i < _count; i++)
        {
            if (_polygons[i] is not { HasSource: true } polygon) continue;
            if (!_maps.TryGetValue(polygon.SourceMap, out int map))
            {
                map = _maps.Count;
                _maps.Add(polygon.SourceMap, map);
            }
            for (int vertex = 0; vertex < polygon.SourceVertices.Length; vertex++)
            {
                var source = polygon.SourceVertices[vertex];
                var key = new VertexKey(map, BitConverter.SingleToInt32Bits(source.X),
                    BitConverter.SingleToInt32Bits(source.Y), BitConverter.SingleToInt32Bits(source.Z));
                int head = _heads.TryGetValue(key, out int prior) ? prior : -1;
                for (int link = head; link >= 0; link = _links[link].Next)
                {
                    Link other = _links[link];
                    if (other.Polygon == i || !Overlap(polygon.Vertices[vertex],
                        _polygons[other.Polygon]!.Vertices[other.Vertex])) continue;
                    _offered[i * _stride + other.Polygon / 64] |= 1UL << (other.Polygon % 64);
                    _offered[other.Polygon * _stride + i / 64] |= 1UL << (i % 64);
                }
                _heads[key] = _links.Count;
                _links.Add(new(i, vertex, head));
            }
        }
    }

    internal bool Offers(int first, int second) => (uint)first < (uint)_count && (uint)second < (uint)_count &&
        (_offered[first * _stride + second / 64] & (1UL << (second % 64))) != 0;

    internal bool ProvesMeeting(int first, int second)
    {
        if (!Offers(first, second)) return false;
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportIncidenceMeeting);
        SupportPolygon a = _polygons[first]!, b = _polygons[second]!;
        if (SupportJoinIncidence.SamePlane(a, b)) return true;
        if (!Prepare(first) || !Prepare(second)) return false;
        return SupportMeetingDomain.Proves(a, b, _domains[first]!, _domains[second]!);
    }

    bool Prepare(int index)
    {
        SupportMeetingDomain domain = _domains[index] ??= new();
        if (!_prepared[index])
        {
            domain.Prepare(_polygons[index]!);
            _prepared[index] = true;
        }
        return domain.Valid;
    }

    // Closed overlap includes zero in each difference enclosure. The edge-start vertex
    // stratum consequently has an attained candidate whose distance lower bound is zero.
    static bool Overlap(FeaturePoint a, FeaturePoint b) =>
        Overlap(a.X.Bounds, b.X.Bounds) && Overlap(a.Y.Bounds, b.Y.Bounds) && Overlap(a.Z.Bounds, b.Z.Bounds);
    static bool Overlap(GeometryInterval a, GeometryInterval b) => a.IsResolved && b.IsResolved &&
        a.Lower <= b.Upper && b.Lower <= a.Upper;
}
