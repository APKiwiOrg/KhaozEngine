using System;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Canonical membership coverage for a complete placement cell and its acquired policy dependencies.</summary>
internal static class MapRoofPolicyAcquisition
{
    internal static MapSurfaceScope Scope(MapBox3 bounds, MapQueryLimits limits)
    {
        var frame = WorldFrame.Nearest((float)(bounds.MinX / 2 + bounds.MaxX / 2), (float)(bounds.MinZ / 2 + bounds.MaxZ / 2));
        double x = (long)frame.X * (double)WorldFrame.Grid, z = (long)frame.Z * (double)WorldFrame.Grid;
        return new(frame, new(Lower(bounds.MinX - x), Lower(bounds.MinZ - z)),
            new(Upper(bounds.MaxX - x), Upper(bounds.MaxZ - z)), null, null, Enum.GetValues<MapSurfaceRole>(), null, limits);
    }

    internal static MapScopedScene Complete(MapScopedScene cell)
    {
        var bounds = cell.Scope.DrawBounds;
        // Required geometry comes from the neutral dependency closure, including linked rooms, groups,
        // parent support and complete acquired patches. A window's physical coverage is never inherited.
        foreach (var key in cell.Witness.Required)
            if (key.Kind is "Patch" or "Record" or "Surface" && cell.Source.Data.MetadataByKey.TryGetValue(key, out var metadata))
            {
                var b = metadata.Bounds;
                bounds = new(Math.Min(bounds.MinX, b.MinX), 0, Math.Min(bounds.MinZ, b.MinZ),
                    Math.Max(bounds.MaxX, b.MaxX), 0, Math.Max(bounds.MaxZ, b.MaxZ));
            }
        var current = cell;
        while (true)
        {
            var required = bounds;
            var view = current.SurfaceView;
            for (int i = 0; i < view.Witness.Records.Count; i++)
            {
                var reference = view.Witness.Records[i];
                if (!view.TryRecord(reference, out var record, out _, borrow: true))
                    throw new MapSceneSourceException(MapSceneStatus.Incomplete, "Canonical roof policy omitted an admitted record.");
                if (record is not MapPrismDomain prism) continue;
                var domain = PrismBounds(view, prism);
                required = new(Math.Min(required.MinX, domain.MinX), 0, Math.Min(required.MinZ, domain.MinZ),
                    Math.Max(required.MaxX, domain.MaxX), 0, Math.Max(required.MaxZ, domain.MaxZ));
            }
            // A newly acquired anchor can introduce another room or ancestor domain. Close over admitted
            // geometry, never over the incidental bounds of newly discovered storage patches.
            if (required == bounds && !ReferenceEquals(current, cell)) return current;
            bounds = required;
            using var diagnostics = MapSceneAdmission.Current?.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.Policy, MapAcquisitionCaller.PolicyReacquisition);
            MapSceneAdmission.Current?.AcquisitionDiagnostics?.Scene(MapAcquisitionCaller.PolicyReacquisition);
            var result = current.AcquireDependencies(current.Scope with { Surfaces = Scope(bounds, current.Scope.Surfaces.Limits) });
            var replacement = result.Scene ?? throw new MapSceneSourceException(result.Status,
                "Canonical roof policy dependencies refused. " + string.Join("\n", result.Findings), result.Capacity);
            // Admission includes the old and new snapshots until the replacement succeeds.
            current.PreparationOwnership?.Dispose();
            current = replacement;
        }
    }
    static MapBox3 PrismBounds(MapScopedSurfaces view, MapPrismDomain prism)
    {
        var transform = view.EffectiveContext?.Transform(MapPrefabChildKind.Record, prism.Id);
        var domain = prism.Footprint;
        double minX = double.PositiveInfinity, minZ = double.PositiveInfinity, maxX = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (int i = 0; i < domain.Rects.Count; i++)
        {
            var r = domain.Rects[i];
            Add(MapLatticeAddress.Corner(r.MinX, r.MinZ)); Add(MapLatticeAddress.Corner(r.MaxX, r.MinZ));
            Add(MapLatticeAddress.Corner(r.MinX, r.MaxZ)); Add(MapLatticeAddress.Corner(r.MaxX, r.MaxZ));
        }
        for (int ring = 0; ring < domain.Rings.Count; ring++)
            for (int v = 0; v < domain.Rings[ring].Vertices.Count; v++) Add(domain.Rings[ring].Vertices[v]);
        if (!double.IsFinite(minX) || !double.IsFinite(minZ) || !double.IsFinite(maxX) || !double.IsFinite(maxZ))
            throw new MapSceneSourceException(MapSceneStatus.Invalid, "Canonical roof policy has an empty room domain.");
        return new(minX, 0, minZ, maxX, 0, maxZ);

        void Add(MapLatticeAddress address)
        {
            var point = domain.Frame.WorldXz(address);
            if (transform is not null) point = transform.Xz(point);
            double x = point.X.ToDouble(), z = point.Z.ToDouble();
            minX = Math.Min(minX, x); minZ = Math.Min(minZ, z); maxX = Math.Max(maxX, x); maxZ = Math.Max(maxZ, z);
        }
    }
    static float Lower(double value) { float f = (float)value; return f > value ? MathF.BitDecrement(f) : f; }
    static float Upper(double value) { float f = (float)value; return f < value ? MathF.BitIncrement(f) : f; }
}
