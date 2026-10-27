using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Compiles room polygons with the shared R4 world-edge ownership and R5 point-freeze policy.</summary>
public static class MapPrismGeometry
{
    public static MapWaterDomain Compile(MapScopedSurfaces view, MapPrismDomain domain) => Compile(view, domain, pointBudget: true);
    internal static MapWaterDomain Prepare(MapScopedSurfaces view, MapPrismDomain domain) => Compile(view, domain, pointBudget: false);
    static MapWaterDomain Compile(MapScopedSurfaces view, MapPrismDomain domain, bool pointBudget)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(domain);
        RequirePayload(domain);
        long cost = pointBudget ? Cost(domain) : 0;
        if (pointBudget && DemandDiagnostics.Check(domain, "MapPrismGeometry.MaxInspectedFaces.1", "MaxInspectedFaces", 0, cost, view.Scope.Limits.MaxInspectedFaces, cost > view.Scope.Limits.MaxInspectedFaces, CapacityDemandLifetime.Cumulative)) throw new MapDocumentException("capacity exceeded: prism vertices");
        var local = MapWaterDomain.Compile(domain.Footprint);
        var kernel = view.EffectiveContext?.Transform(MapPrefabChildKind.Record, domain.Id);
        if (kernel is null) return local;
        // Frozen transformed coordinates are dyadics. Encode them as integer addresses in a quantum frame,
        // then let the existing R4 compiler validate simplicity and normalize WORLD winding.
        var frame = new MapLatticeFrame(new(1, (int)MapResourceTransform.Quantum), new(1, 1), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var rings = local.Pieces.Select(piece => new MapWaterRing(piece.Ring.Select(p =>
        {
            var world = kernel.Xz(p);
            return MapLatticeAddress.Corner(Quantum(world.X), Quantum(world.Z));
        }).ToArray())).ToArray();
        return MapWaterDomain.Compile(new(frame, [], rings));
    }

    public static MapBox3 Bounds(MapScopedSurfaces view, MapPrismDomain domain)
    {
        var polygon = Compile(view, domain);
        var (lower, upper) = Heights(view, domain);
        return new(polygon.Min.X.ToDouble(), lower.ToDouble(), polygon.Min.Z.ToDouble(),
            polygon.Max.X.ToDouble(), upper.ToDouble(), polygon.Max.Z.ToDouble());
    }

    internal static (MapExactValue Lower, MapExactValue Upper) Heights(MapScopedSurfaces view, MapPrismDomain domain)
    {
        RequirePayload(domain);
        var kernel = view.EffectiveContext?.Transform(MapPrefabChildKind.Record, domain.Id);
        var lo = MapExactValue.FromSingle(domain.LowerY); var hi = MapExactValue.FromSingle(domain.UpperY);
        lo = kernel?.Height(lo) ?? lo; hi = kernel?.Height(hi) ?? hi;
        if (lo.CompareTo(hi) >= 0) throw new MapDocumentException("invalid prism separation");
        return (lo, hi);
    }

    internal static long Cost(MapPrismDomain domain) => checked(4L * domain.Footprint.Rects.Count + domain.Footprint.Rings.Sum(r => (long)r.Vertices.Count));

    internal static void RequirePayload(MapPrismDomain domain)
    {
        if (domain.PayloadVersion != 1 || !float.IsFinite(domain.LowerY) || !float.IsFinite(domain.UpperY) ||
            domain.LowerY >= domain.UpperY) throw new MapDocumentException("invalid prism payload or separation");
    }
    static long Quantum(MapExactValue value) => checked(value.Numerator * (MapResourceTransform.Quantum / value.Denominator));
}
