using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Render3D;

public static class MapCaptureFocus
{
    public static MapFramePoint Resolve(MapSnapshotRequest request, MapScopedScene scene)
    {
        if (request.Focus is { } explicitFocus) return explicitFocus;
        if (request.Route == MapCaptureRoute.View) return request.Framing.Eye;
        var b = request.Scope.DrawBounds; double x = b.MinX / 2 + b.MaxX / 2, z = b.MinZ / 2 + b.MaxZ / 2;
        var heights = new List<(string Owner, double Y)>();
        var surfaces = scene.Surfaces;
        foreach (var surface in surfaces.Refs.Where(s => s.Role == MapSurfaceRole.SupportFloor))
            foreach (var patch in surfaces.Patches.Values.Where(p => p.Key.SurfaceId == surface.Id))
            {
                var compiled = MapEffectiveGeometry.TransformPatch(scene.SurfaceView, MapSurfaceCompiler.Compile(surface, patch));
                foreach (var face in compiled.Faces)
                {
                    var t = compiled.ExactTriangle(face);
                    double ax = t.A.X.ToDouble(), az = t.A.Z.ToDouble(), bx = t.B.X.ToDouble(), bz = t.B.Z.ToDouble(), cx = t.C.X.ToDouble(), cz = t.C.Z.ToDouble();
                    double den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                    if (Math.Abs(den) < 1e-15) continue;
                    double u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / den;
                    double v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / den;
                    if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) continue;
                    double y = u * t.A.Y.ToDouble() + v * t.B.Y.ToDouble() + (1 - u - v) * t.C.Y.ToDouble();
                    if (y >= b.MinY && y <= b.MaxY && !heights.Any(h => h.Owner == surface.Id && Math.Abs(h.Y - y) < 1e-7)) heights.Add((surface.Id, y));
                }
            }
        if (heights.Count != 1) throw new ArgumentException("FocusUnavailable");
        var f = request.Scope.Surfaces.Frame;
        return new(f, new((float)(x - f.X * 128d), (float)(heights[0].Y + 2), (float)(z - f.Z * 128d)));
    }
}
