using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Storage;

internal static class ScopeFixtures
{
    internal static readonly MapSurfaceRole[] AllRoles =
        { MapSurfaceRole.SupportFloor, MapSurfaceRole.Ceiling, MapSurfaceRole.PaintOverride };

    internal static MapSurfaceScope Around(WorldFrame frame, float x, float z, float half = 2f, MapQueryLimits? limits = null) =>
        new(frame, new Vector2(x - half, z - half), new Vector2(x + half, z + half), null, null,
            AllRoles, null, limits ?? new MapQueryLimits());

    internal static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope) =>
        MapScopedSurfaces.Acquire(source, scope, Array.Empty<string>());

    internal static MapSurfaceScope Whole(MapDocument doc)
    {
        MapExactXz[] corners = doc.Surfaces.Patches.Values.SelectMany(p =>
        {
            MapLatticeFrame frame = doc.Surfaces.Refs.Single(s => s.Id == p.Key.SurfaceId).Frame;
            return new[] { frame.WorldXz(p.CornerAddress(0, 0)), frame.WorldXz(p.CornerAddress(p.Width, p.Depth)) };
        }).ToArray();
        return new(WorldFrame.Origin,
            corners.Length == 0 ? Vector2.Zero : new Vector2((float)corners.Min(c => c.X.Floor()), (float)corners.Min(c => c.Z.Floor())),
            corners.Length == 0 ? Vector2.Zero : new Vector2((float)corners.Max(c => c.X.Ceiling()), (float)corners.Max(c => c.Z.Ceiling())),
            null, null, AllRoles, null, new MapQueryLimits());
    }
}
