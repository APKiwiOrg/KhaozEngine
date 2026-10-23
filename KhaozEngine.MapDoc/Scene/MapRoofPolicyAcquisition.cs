using System;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
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
        var result = cell.AcquireDependencies(cell.Scope with { Surfaces = Scope(bounds, cell.Scope.Surfaces.Limits) });
        return result.Scene ?? throw new MapSceneSourceException(result.Status,
            "Canonical roof policy dependencies refused. " + string.Join("\n", result.Findings), result.Capacity);
    }
    static float Lower(double value) { float f = (float)value; return f > value ? MathF.BitDecrement(f) : f; }
    static float Upper(double value) { float f = (float)value; return f < value ? MathF.BitIncrement(f) : f; }
}
