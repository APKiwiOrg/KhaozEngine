using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A prefab or instance transform for a water domain. A local XZ point (x, z) maps to
/// <c>Vector3.Transform(new Vector3(x, 0, z) * Scale, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw)) + Position</c>
/// in float. <see cref="Scale"/> is positive.</summary>
public readonly record struct MapWaterPlacement(Vector3 Position, float Yaw, float Scale)
{
    public float SurfaceY(float local) => Position.Y + Scale * local;

    internal Vector3 Transform(Quaternion yaw, float x, float z) => Vector3.Transform(new Vector3(x, 0, z) * Scale, yaw) + Position;
}

/// <summary>One simple domain piece, counter-clockwise in world XZ.</summary>
public sealed record MapWaterPiece(IReadOnlyList<MapExactXz> Ring);

/// <summary>A compiled water domain: the union of its pieces in world XZ under the OA7 half-open rule. Every
/// predicate is exact.</summary>
public sealed class MapWaterDomain
{
    /// <summary>The most pieces one domain may hold.</summary>
    internal const int MaxPieces = 65_536;
    /// <summary>The most ring vertices one domain may hold, counting four per rect.</summary>
    internal const int MaxPieceVertices = 262_144;

    readonly MapWideXz[][] _rings;
    readonly MapWaterPolygons.Bounds[] _bounds;
    readonly MapWaterPolygons.Bounds _extent;
    readonly IReadOnlyList<IReadOnlyList<MapWideXz>>?[] _convexParts;

    MapWaterDomain(MapWideXz[][] rings)
    {
        _rings = rings;
        _bounds = rings.Select(MapWaterPolygons.Bounds.Of).ToArray();
        _extent = _bounds.Skip(1).Aggregate(_bounds[0], (all, b) => all.Include(b.Min).Include(b.Max));
        _convexParts = new IReadOnlyList<IReadOnlyList<MapWideXz>>?[rings.Length];
        Pieces = rings.Select(r => new MapWaterPiece(r.Select(Narrow).ToArray())).ToArray();
        Min = Narrow(_extent.Min);
        Max = Narrow(_extent.Max);
    }

    public IReadOnlyList<MapWaterPiece> Pieces { get; }
    public MapExactXz Min { get; }
    public MapExactXz Max { get; }

    /// <summary>Compiles rects then rings, in stored order, into pieces. Throws <see cref="MapDocumentException"/>
    /// for an invalid domain, including "not representable" when a placed corner cannot convert exactly.</summary>
    public static MapWaterDomain Compile(MapWaterDomainDoc doc, MapWaterPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var errors = new List<string>();
        return TryCompile(doc, placement, errors) ?? throw new MapDocumentException("water domain: " + errors[0] + ".");
    }

    /// <summary>Compiles the domain or records why it cannot, without a label. Counts are checked before any
    /// geometry work.</summary>
    internal static MapWaterDomain? TryCompile(MapWaterDomainDoc doc, MapWaterPlacement? placement, List<string> errors)
    {
        if (doc.Rects is null || doc.Rings is null || doc.Frame is null)
        {
            errors.Add("domain is required");
            return null;
        }
        if (!WithinLimits(doc, errors)) return null;
        if (doc.Rects.Count + doc.Rings.Count == 0)
        {
            errors.Add("domain is empty");
            return null;
        }
        if (placement is { } p && (!float.IsFinite(p.Position.X) || !float.IsFinite(p.Position.Y) ||
            !float.IsFinite(p.Position.Z) || !float.IsFinite(p.Yaw) || !float.IsFinite(p.Scale) || !(p.Scale > 0f)))
        {
            errors.Add("placement must be finite with a positive scale");
            return null;
        }
        int before = errors.Count;
        var rings = new List<MapWideXz[]>(doc.Rects.Count + doc.Rings.Count);
        try
        {
            Quaternion yaw = placement is { } q ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, q.Yaw) : Quaternion.Identity;
            for (int i = 0; i < doc.Rects.Count; i++)
            {
                MapWaterRect rect = doc.Rects[i];
                if (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ)
                {
                    errors.Add($"rect {i} is empty");
                    continue;
                }
                MapExactXz a = doc.Frame.WorldXz(MapLatticeAddress.Corner(rect.MinX, rect.MinZ));
                MapExactXz b = doc.Frame.WorldXz(MapLatticeAddress.Corner(rect.MaxX, rect.MaxZ));
                // Ordering by world coordinates lets a negative-Z frame own its world minimum.
                MapExactValue minX = Least(a.X, b.X), maxX = Most(a.X, b.X), minZ = Least(a.Z, b.Z), maxZ = Most(a.Z, b.Z);
                MapExactXz[] corners = { new(minX, minZ), new(maxX, minZ), new(maxX, maxZ), new(minX, maxZ) };
                AddRing(rings, $"rect {i}", corners, placement, yaw, errors);
            }
            for (int i = 0; i < doc.Rings.Count; i++)
            {
                IReadOnlyList<MapLatticeAddress>? vertices = doc.Rings[i]?.Vertices;
                if (vertices is null || vertices.Count < 3)
                {
                    errors.Add($"ring {i} needs at least 3 vertices");
                    continue;
                }
                AddRing(rings, $"ring {i}", vertices.Select(doc.Frame.WorldXz).ToArray(), placement, yaw, errors);
            }
        }
        catch (MapExactOverflowException)
        {
            errors.Add("a domain corner is not representable");
        }
        catch (MapDocumentException e)
        {
            errors.Add(e.Message);
        }
        return errors.Count == before ? new MapWaterDomain(rings.ToArray()) : null;
    }

    /// <summary>True when the domain holds the point under the OA7 half-open rule.</summary>
    public bool Contains(MapExactXz point)
    {
        MapWideXz p = MapWideXz.From(point);
        if (!_extent.Contains(p)) return false;
        for (int i = 0; i < _rings.Length; i++)
            if (_bounds[i].Contains(p) && MapWaterPolygons.RingContains(_rings[i], p)) return true;
        return false;
    }

    public bool PieceContains(int piece, MapExactXz point)
    {
        MapWideXz p = MapWideXz.From(point);
        return _bounds[piece].Contains(p) && MapWaterPolygons.RingContains(_rings[piece], p);
    }

    /// <summary>True when the domains share positive area. Shared edges and vertices alone are not overlap.</summary>
    public static bool OverlapsPositively(MapWaterDomain a, MapWaterDomain b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!a._extent.OverlapsOpen(b._extent)) return false;
        var owners = new List<(MapWaterDomain Domain, int Piece)>(a._rings.Length + b._rings.Length);
        for (int i = 0; i < a._rings.Length; i++) owners.Add((a, i));
        for (int i = 0; i < b._rings.Length; i++) owners.Add((b, i));
        return OverlappingPairs(owners).Any(pair => owners[pair.A].Domain != owners[pair.B].Domain);
    }

    /// <summary>Every pair of pieces, across the given domain pieces, that shares positive area. Candidate pairs
    /// come from a sorted bounds sweep, so only overlapping bounds reach the exact test.</summary>
    internal static IEnumerable<(int A, int B)> OverlappingPairs(IReadOnlyList<(MapWaterDomain Domain, int Piece)> pieces)
    {
        MapWaterPolygons.Bounds[] bounds = pieces.Select(p => p.Domain._bounds[p.Piece]).ToArray();
        foreach ((int a, int b) in MapWaterPolygons.BoundsSweep(bounds, touching: false))
            if (MapWaterPolygons.IntersectPositively(pieces[a].Domain.ConvexParts(pieces[a].Piece),
                pieces[b].Domain.ConvexParts(pieces[b].Piece)))
                yield return (a, b);
    }

    /// <summary>Pairs of this domain's own pieces that share positive area.</summary>
    internal IEnumerable<(int A, int B)> OverlappingPieces() =>
        OverlappingPairs(Enumerable.Range(0, _rings.Length).Select(i => (this, i)).ToArray());

    IReadOnlyList<IReadOnlyList<MapWideXz>> ConvexParts(int piece) => _convexParts[piece] ??=
        MapWaterPolygons.IsConvex(_rings[piece])
            ? new IReadOnlyList<MapWideXz>[] { _rings[piece] }
            : MapWaterPolygons.Triangulate(_rings[piece]);

    /// <summary>Refuses a domain over <see cref="MaxPieces"/> pieces or <see cref="MaxPieceVertices"/> vertices.</summary>
    internal static bool WithinLimits(MapWaterDomainDoc doc, List<string> errors)
    {
        long pieces = (long)doc.Rects.Count + doc.Rings.Count;
        if (pieces > MaxPieces)
        {
            errors.Add($"domain exceeds {MaxPieces} pieces");
            return false;
        }
        long vertices = 4L * doc.Rects.Count;
        foreach (MapWaterRing ring in doc.Rings) vertices += ring?.Vertices?.Count ?? 0;
        if (vertices > MaxPieceVertices)
        {
            errors.Add($"domain exceeds {MaxPieceVertices} vertices");
            return false;
        }
        return true;
    }

    static void AddRing(List<MapWideXz[]> rings, string name, MapExactXz[] corners, MapWaterPlacement? placement,
        Quaternion yaw, List<string> errors)
    {
        if (placement is { } p)
        {
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 world = p.Transform(yaw, corners[i].X.ToSingle(), corners[i].Z.ToSingle());
                try { corners[i] = new(MapExactValue.FromSingle(world.X), MapExactValue.FromSingle(world.Z)); }
                catch (MapExactOverflowException) { throw new MapDocumentException($"{name} corner {i} is not representable under its placement"); }
            }
        }
        MapWideXz[] ring = corners.Select(MapWideXz.From).ToArray();
        if (MapWaterPolygons.IsCollinear(ring))
        {
            errors.Add($"{name} has zero area");
            return;
        }
        if (!MapWaterPolygons.IsSimple(ring))
        {
            errors.Add($"{name} self-intersects");
            return;
        }
        if (MapWaterPolygons.SignedArea(ring).Sign < 0) Array.Reverse(ring);
        rings.Add(ring);
    }

    static MapExactXz Narrow(MapWideXz point) =>
        point.TryNarrow(out MapExactXz exact) ? exact : throw new MapDocumentException("water domain vertex is not representable");

    static MapExactValue Least(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    static MapExactValue Most(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
}
