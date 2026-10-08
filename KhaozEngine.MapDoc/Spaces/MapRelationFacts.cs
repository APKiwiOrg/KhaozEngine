using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

public enum MapRelationStatus { Resolved, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }
public enum MapGeometricRelation { SameSpace, ConnectedThroughPortals, ConnectedThroughVerticalLink, NotConnectedWithinBound, Undetermined }
public enum MapPortalKind : byte { WallPortal, HorizontalOpening }
public enum MapPortalStateSource : byte { AuthoredOpen }
public enum MapPhysicalCertainty : byte { NotEvaluated, Clear, Blocked, Unknown }

public sealed record MapApertureColumn(MapLatticeVertex Vertex, MapExactPoint Bottom, MapExactPoint? Top);
public sealed record MapApertureGeometry(MapPortalKind Kind, IReadOnlyList<MapApertureColumn> Columns,
    IReadOnlyList<MapExactTriangle> PlaneTriangles, IReadOnlyList<MapRecordRef> Provenance);

/// <summary>Vertical extrema and minimum column height. Width is wall interval length or opening XZ bounds' larger span.</summary>
public sealed record MapApertureSummary(float BottomY, float? TopY, float? MinClearHeight, float Width);
public sealed record MapPortalFact(MapRecordRef Record, MapPortalKind Kind, MapRecordRef FromSpace,
    MapRecordRef ToSpace, MapRecordRef? ViaLink, MapApertureGeometry Geometry, MapApertureSummary Summary,
    MapPortalStateSource State);
public sealed record MapRelationQuery(int MaxPortalHops = 8, bool IncludeVerticalLinks = true);

/// <summary>Geometric facts from one acquisition. Physical tests and consumer policy remain unevaluated.</summary>
public sealed class MapRelationResult
{
    public MapRelationStatus Status { get; }
    public MapFramePoint A { get; }
    public MapFramePoint B { get; }
    public MapRelationQuery Query { get; }
    public MapMembershipResult MembershipA { get; }
    public MapMembershipResult MembershipB { get; }
    public MapGeometricRelation Relation { get; }
    public IReadOnlyList<MapPortalFact> Path { get; }
    public MapPhysicalCertainty Occlusion => MapPhysicalCertainty.NotEvaluated;
    public MapPhysicalCertainty Clearance => MapPhysicalCertainty.NotEvaluated;
    public string? Detail { get; }
    public MapReadWitness Witness { get; }

    internal MapRelationResult(MapRelationStatus status, MapFramePoint a, MapFramePoint b, MapRelationQuery query,
        MapMembershipResult membershipA, MapMembershipResult membershipB, MapGeometricRelation relation,
        IEnumerable<MapPortalFact> path, string? detail, MapReadWitness witness)
    {
        Status = status;
        A = a;
        B = b;
        Query = query;
        MembershipA = membershipA;
        MembershipB = membershipB;
        Relation = relation;
        Path = Array.AsReadOnly(path.ToArray());
        Detail = detail;
        Witness = witness;
    }
}
