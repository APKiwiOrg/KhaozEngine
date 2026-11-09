using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A prefab or instance transform for a water domain. A local XZ point (x, z) maps to
/// <c>Vector3.Transform(new Vector3(x, 0, z) * Scale, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw)) + Position</c>
/// in float. <see cref="Scale"/> is positive.</summary>
public readonly record struct MapWaterPlacement(Vector3 Position, float Yaw, float Scale)
{
    public float SurfaceY(float local) => Kernel.ReleasedWaterHeight(local);
    internal MapResourceTransform Kernel => new(new(Position.X, Position.Y, Position.Z, Yaw, Scale));
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
    /// <summary>The most pair checks that simplicity, overlap and triangulation may spend in one validation call,
    /// one compile or one overlap test.</summary>
    internal const long MaxPairChecks = 16_777_216;

    readonly MapWideXz[][] _rings;
    readonly string[] _names;
    readonly MapWaterPolygons.Bounds[] _bounds;
    readonly MapWaterPolygons.Bounds _extent;
    readonly IReadOnlyList<IReadOnlyList<MapWideXz>>?[] _convexParts;
    readonly int _rectCount;

    internal MapWaterDomain(MapWideXz[][] rings, string[] names, int rectCount = 0)
    {
        _rings = rings;
        _names = names;
        _rectCount = rectCount;
        _bounds = rings.Select(MapWaterPolygons.Bounds.Of).ToArray();
        _extent = _bounds.Skip(1).Aggregate(_bounds[0], (all, b) => all.Include(b.Min).Include(b.Max));
        _convexParts = new IReadOnlyList<IReadOnlyList<MapWideXz>>?[rings.Length];
        Pieces = rings.Select(r => new MapWaterPiece(r.Select(Narrow).ToArray())).ToArray();
        Min = Narrow(_extent.Min);
        Max = Narrow(_extent.Max);
    }

    public IReadOnlyList<MapWaterPiece> Pieces { get; }
    /// <summary>The exact bounds of one piece, which also bound every one of its convex parts.</summary>
    internal MapWaterPolygons.Bounds PieceBounds(int piece) => _bounds[piece];
    internal bool PieceIsConvex(int piece) => MapWaterPolygons.IsConvex(_rings[piece]);
    public MapExactXz Min { get; }
    public MapExactXz Max { get; }

    /// <summary>Compiles rects then rings, in stored order, into pieces. Throws <see cref="MapDocumentException"/>
    /// for an invalid domain, with the first finding's reason and the ordered Findings snapshot.
    /// GeometryNotRepresentable names a corner that cannot convert exactly, while WorkCapacityExceeded
    /// names a domain count or pair-check budget.</summary>
    public static MapWaterDomain Compile(MapWaterDomainDoc doc, MapWaterPlacement? placement = null) =>
        Compile(doc, placement, MaxPieces, MaxPieceVertices);

    // Injects small count caps into the same compilation path without changing geometry admission.
    internal static MapWaterDomain CompileWithCountLimitsForTests(MapWaterDomainDoc doc, int maxPieces, int maxPieceVertices) =>
        Compile(doc, null, maxPieces, maxPieceVertices);

    static MapWaterDomain Compile(MapWaterDomainDoc doc, MapWaterPlacement? placement, int maxPieces, int maxPieceVertices)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        var domain = TryCompileEffective(doc, placement?.Kernel, errors, new MapWaterWorkBudget(), out var cause,
            releasedPlacement: placement is not null, findings: findings, maxPieces: maxPieces, maxPieceVertices: maxPieceVertices);
        if (domain is not null) return domain;
        string message = "water domain: " + errors[0] + ".";
        if (cause is { Reason: null }) throw cause.WithSourceName(null, message);
        throw MapDocumentRefusalFactory.FromFinding(findings[0] with { InvalidPrefixSuppressed = true },
            message, inner: cause, findings: findings);
    }

    /// <summary>Compiles the domain or records why it cannot, without a label. Counts are checked before any
    /// geometry work, and the simplicity checks charge <paramref name="budget"/>.</summary>
    internal static MapWaterDomain? TryCompile(MapWaterDomainDoc doc, MapWaterPlacement? placement, List<string> errors,
        MapWaterWorkBudget budget, List<MapDocumentFinding>? findings = null) =>
        TryCompile(doc, placement, errors, budget, findings, out _);

    internal static MapWaterDomain? TryCompile(MapWaterDomainDoc doc, MapWaterPlacement? placement, List<string> errors,
        MapWaterWorkBudget budget, List<MapDocumentFinding>? findings, out MapDocumentException? firstCause) =>
        TryCompileEffective(doc, placement?.Kernel, errors, budget, out firstCause, releasedPlacement: placement is not null, findings: findings);

    internal static MapWaterDomain? TryCompileEffective(MapWaterDomainDoc doc, MapResourceTransform? transform, List<string> errors,
        MapWaterWorkBudget budget, bool releasedPlacement = false, List<MapDocumentFinding>? findings = null) =>
        TryCompileEffective(doc, transform, errors, budget, out _, releasedPlacement, findings);

    internal static MapWaterDomain? TryCompileEffective(MapWaterDomainDoc doc, MapResourceTransform? transform, List<string> errors,
        MapWaterWorkBudget budget, out MapDocumentException? firstCause, bool releasedPlacement = false, List<MapDocumentFinding>? findings = null,
        int maxPieces = MaxPieces, int maxPieceVertices = MaxPieceVertices)
    {
        firstCause = null;
        if (doc.Rects is null || doc.Rings is null || doc.Frame is null)
        {
            Add(new(MapDocumentRefusal.SchemaValueRequired, "domain is required")
            { FieldPath = doc.Rects is null ? nameof(doc.Rects) : doc.Rings is null ? nameof(doc.Rings) : nameof(doc.Frame) });
            return null;
        }
        if (!WithinLimits(doc, errors, findings, maxPieces, maxPieceVertices)) return null;
        if (doc.Rects.Count + doc.Rings.Count == 0)
        {
            Add(new(MapDocumentRefusal.WaterDomainEmpty, "domain is empty") { FieldPath = "Domain", ActualCount = 0 });
            return null;
        }
        if (transform is { Transform: var p } && (!double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            !double.IsFinite(p.Z) || !double.IsFinite(p.YawRadians) || !double.IsFinite(p.Scale) || !(p.Scale > 0d)))
        {
            string field = !double.IsFinite(p.X) ? nameof(p.X) : !double.IsFinite(p.Y) ? nameof(p.Y)
                : !double.IsFinite(p.Z) ? nameof(p.Z) : !double.IsFinite(p.YawRadians) ? nameof(p.YawRadians) : nameof(p.Scale);
            double value = field == nameof(p.X) ? p.X : field == nameof(p.Y) ? p.Y : field == nameof(p.Z) ? p.Z
                : field == nameof(p.YawRadians) ? p.YawRadians : p.Scale;
            Add(new(double.IsFinite(value) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                "placement must be finite with a positive scale")
            { FieldPath = "Placement." + field, ActualNumber = value, MinimumNumber = double.IsFinite(value) ? 0 : null });
            return null;
        }
        int before = errors.Count;
        var rings = new List<MapWideXz[]>(doc.Rects.Count + doc.Rings.Count);
        var names = new List<string>(rings.Capacity);
        string? currentField = null;
        int? currentIndex = null;
        try
        {
            for (int i = 0; i < doc.Rects.Count; i++)
            {
                currentField = $"Rects[{i}]";
                currentIndex = i;
                MapWaterRect rect = doc.Rects[i];
                if (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ)
                {
                    Add(new(MapDocumentRefusal.BoundsInvalid, $"rect {i} is empty")
                    { FieldPath = currentField, SubjectKind = "rect", Index = i });
                    continue;
                }
                MapExactXz a = doc.Frame.WorldXz(MapLatticeAddress.Corner(rect.MinX, rect.MinZ));
                MapExactXz b = doc.Frame.WorldXz(MapLatticeAddress.Corner(rect.MaxX, rect.MaxZ));
                // Ordering by world coordinates lets a negative-Z frame own its world minimum.
                MapExactValue minX = Least(a.X, b.X), maxX = Most(a.X, b.X), minZ = Least(a.Z, b.Z), maxZ = Most(a.Z, b.Z);
                MapExactXz[] corners = { new(minX, minZ), new(maxX, minZ), new(maxX, maxZ), new(minX, maxZ) };
                AddRing(rings, names, $"rect {i}", corners, rect: true, i, transform, errors, budget, releasedPlacement, findings);
            }
            for (int i = 0; i < doc.Rings.Count; i++)
            {
                currentField = $"Rings[{i}]";
                currentIndex = i;
                IReadOnlyList<MapLatticeAddress>? vertices = doc.Rings[i]?.Vertices;
                int? vertexCount = vertices?.Count;
                if (vertices is null || vertexCount < 3)
                {
                    Add(new(vertices is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ValueOutsideRange,
                        $"ring {i} needs at least 3 vertices")
                    {
                        FieldPath = doc.Rings[i] is null ? currentField : currentField + ".Vertices",
                        Index = i,
                        ActualCount = vertexCount,
                        MinimumInteger = vertices is null ? null : 3
                    });
                    continue;
                }
                AddRing(rings, names, $"ring {i}", vertices.Select(doc.Frame.WorldXz).ToArray(), rect: false, i, transform,
                    errors, budget, releasedPlacement, findings);
            }
        }
        catch (MapExactOverflowException)
        {
            Add(new(MapDocumentRefusal.GeometryNotRepresentable, "a domain corner is not representable")
            { FieldPath = currentField, Index = currentIndex, Operation = "water domain" });
        }
        catch (MapDocumentException e)
        {
            if (errors.Count == before) firstCause = e;
            errors.Add(e.Message);
            if (MapDocumentFinding.FromException(e) is { } finding) findings?.Add(finding);
        }
        return errors.Count == before ? new MapWaterDomain(rings.ToArray(), names.ToArray(), doc.Rects.Count) : null;

        void Add(MapDocumentFinding finding) => AddFinding(errors, findings, finding);
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

    /// <summary>True when piece <paramref name="piece"/> holds the point under the OA7 half-open rule.</summary>
    public bool PieceContains(int piece, MapExactXz point)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(piece);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(piece, _rings.Length);
        MapWideXz p = MapWideXz.From(point);
        return _bounds[piece].Contains(p) && MapWaterPolygons.RingContains(_rings[piece], p);
    }

    /// <summary>True when the domains share positive area. Shared edges and vertices alone are not overlap. Throws
    /// <see cref="MapDocumentException"/> when the checks pass <see cref="MaxPairChecks"/>.</summary>
    public static bool OverlapsPositively(MapWaterDomain a, MapWaterDomain b) => OverlapsPositively(a, b, new MapWaterWorkBudget());

    /// <summary><see cref="OverlapsPositively(MapWaterDomain, MapWaterDomain)"/> charging <paramref name="budget"/>.</summary>
    internal static bool OverlapsPositively(MapWaterDomain a, MapWaterDomain b, MapWaterWorkBudget budget)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!a._extent.OverlapsOpen(b._extent)) return false;
        var pieces = new List<(MapWaterDomain Domain, int Piece)>(a._rings.Length + b._rings.Length);
        for (int i = 0; i < a._rings.Length; i++) pieces.Add((a, i));
        for (int i = 0; i < b._rings.Length; i++) pieces.Add((b, i));
        int split = a._rings.Length;
        return OverlappingPairs(pieces, (x, y) => x < split != y < split, budget).Any();
    }

    /// <summary>The source of a piece, such as "rect 2" or "ring 0", for error messages.</summary>
    internal string PieceName(int piece) => _names[piece];

    /// <summary>Every pair of pieces, across the given domain pieces, that shares positive area and passes
    /// <paramref name="candidate"/>. Candidate pairs come from <see cref="MapWaterBoundsSweep"/>, so only overlapping
    /// bounds reach the filter and only filtered pairs reach the exact test. All work charges
    /// <paramref name="budget"/>, which throws <see cref="MapDocumentException"/> once passed.</summary>
    internal static IEnumerable<(int A, int B)> OverlappingPairs(IReadOnlyList<(MapWaterDomain Domain, int Piece)> pieces,
        Func<int, int, bool> candidate, MapWaterWorkBudget budget)
    {
        MapWaterPolygons.Bounds[] bounds = pieces.Select(p => p.Domain._bounds[p.Piece]).ToArray();
        foreach ((int a, int b) in MapWaterBoundsSweep.Pairs(bounds, touching: false, budget))
            if (candidate(a, b) && MapWaterPolygons.IntersectPositively(pieces[a].Domain.ConvexParts(pieces[a].Piece, budget),
                pieces[b].Domain.ConvexParts(pieces[b].Piece, budget), budget))
                yield return (a, b);
    }

    /// <summary>Pairs of this domain's own pieces that share positive area.</summary>
    internal IEnumerable<(int A, int B)> OverlappingPieces(MapWaterWorkBudget budget) =>
        OverlappingPairs(Enumerable.Range(0, _rings.Length).Select(i => (this, i)).ToArray(), static (_, _) => true, budget);

    /// <summary>The piece as convex counter-clockwise parts: itself when convex, else its triangles, computed once.
    /// Throws <see cref="MapDocumentException"/> with WaterDomainTriangulationFailed and the stored
    /// piece Index, SubjectKind and FieldPath when it cannot be triangulated.</summary>
    internal IReadOnlyList<IReadOnlyList<MapWideXz>> ConvexParts(int piece, MapWaterWorkBudget budget)
    {
        if (_convexParts[piece] is { } cached) return cached;
        IReadOnlyList<IReadOnlyList<MapWideXz>> parts = PieceIsConvex(piece)
            ? new IReadOnlyList<MapWideXz>[] { _rings[piece] }
            : RequireTriangulation(MapWaterPolygons.Triangulate(_rings[piece], budget), piece);
        return _convexParts[piece] = parts;
    }

    IReadOnlyList<IReadOnlyList<MapWideXz>> RequireTriangulation(IReadOnlyList<IReadOnlyList<MapWideXz>>? parts, int piece) =>
        parts ?? throw new MapDocumentException(MapDocumentRefusal.WaterDomainTriangulationFailed, $"{_names[piece]} cannot be triangulated")
        {
            FieldPath = piece < _rectCount ? $"Rects[{piece}]" : $"Rings[{piece - _rectCount}]",
            SubjectKind = piece < _rectCount ? "rect" : "ring",
            Index = piece < _rectCount ? piece : piece - _rectCount
        };

    // Injects the failed triangulator result without mutating a compiled domain or weakening ring admission.
    internal void RejectTriangulationForTests(int piece) => _ = RequireTriangulation(null, piece);

    /// <summary>Refuses a domain over <see cref="MaxPieces"/> pieces or <see cref="MaxPieceVertices"/> vertices.</summary>
    internal static bool WithinLimits(MapWaterDomainDoc doc, List<string> errors, List<MapDocumentFinding>? findings = null,
        int maxPieces = MaxPieces, int maxPieceVertices = MaxPieceVertices)
    {
        long pieces = (long)doc.Rects.Count + doc.Rings.Count;
        if (pieces > maxPieces)
        {
            AddFinding(errors, findings, new(MapDocumentRefusal.WorkCapacityExceeded, $"domain exceeds {maxPieces} pieces")
            { Budget = "WaterDomainPieces", ActualCount = pieces, Limit = maxPieces, FieldPath = "Domain" });
            return false;
        }
        long vertices = 4L * doc.Rects.Count;
        foreach (MapWaterRing ring in doc.Rings) vertices += ring?.Vertices?.Count ?? 0;
        if (vertices > maxPieceVertices)
        {
            AddFinding(errors, findings, new(MapDocumentRefusal.WorkCapacityExceeded, $"domain exceeds {maxPieceVertices} vertices")
            { Budget = "WaterDomainVertices", ActualCount = vertices, Limit = maxPieceVertices, FieldPath = "Domain" });
            return false;
        }
        return true;
    }

    /// <summary>Places, checks and normalizes one piece. An unplaced rect has four distinct ordered corners, so it
    /// is simple once it has area and skips the simplicity sweep.</summary>
    static void AddRing(List<MapWideXz[]> rings, List<string> names, string name, MapExactXz[] corners, bool rect,
        int index, MapResourceTransform? transform, List<string> errors, MapWaterWorkBudget budget, bool releasedPlacement,
        List<MapDocumentFinding>? findings)
    {
        string path = rect ? $"Rects[{index}]" : $"Rings[{index}]";
        if (transform is not null)
        {
            for (int i = 0; i < corners.Length; i++)
            {
                string unrepresentable = $"{name} corner {i} is not representable under its placement";
                try { corners[i] = releasedPlacement ? transform.ReleasedWaterXz(corners[i]) : transform.Xz(corners[i]); }
                catch (Exception error) when (error is MapExactOverflowException or MapDocumentException or OverflowException)
                {
                    if (error is MapDocumentException { Reason: not null } known)
                        throw known.WithSourceName(null, unrepresentable);
                    throw new MapDocumentException(MapDocumentRefusal.GeometryNotRepresentable, unrepresentable, error)
                    { Operation = "water placement", FieldPath = path, SubjectKind = rect ? "rect" : "ring", Index = index, OtherIndex = i };
                }
            }
        }
        MapWideXz[] ring = corners.Select(MapWideXz.From).ToArray();
        if (MapWaterPolygons.IsCollinear(ring))
        {
            AddFinding(errors, findings, new(MapDocumentRefusal.WaterDomainZeroArea, $"{name} has zero area")
            { FieldPath = path, SubjectKind = rect ? "rect" : "ring", Index = index });
            return;
        }
        if (!(rect && transform is null) && !MapWaterPolygons.IsSimple(ring, budget))
        {
            AddFinding(errors, findings, new(MapDocumentRefusal.WaterDomainSelfIntersects, $"{name} self-intersects")
            { FieldPath = path, SubjectKind = rect ? "rect" : "ring", Index = index });
            return;
        }
        if (MapWaterPolygons.SignedArea(ring).Sign < 0) Array.Reverse(ring);
        rings.Add(ring);
        names.Add(name);
    }

    static void AddFinding(List<string> errors, List<MapDocumentFinding>? findings, MapDocumentFinding finding)
    {
        errors.Add(finding.Message);
        findings?.Add(finding);
    }

    // Every stored vertex came directly from a MapExactXz. Bounds select those same values without arithmetic.
    static MapExactXz Narrow(MapWideXz point) =>
        point.TryNarrow(out MapExactXz exact) ? exact : throw new InvalidOperationException("water domain vertex is not representable");

    static MapExactValue Least(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    static MapExactValue Most(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
}
