using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal sealed record SceneTerrainPaintTriangle(SceneSurfaceTriangle Triangle, MapTerrainResource Payload,
    ModelVertex A, ModelVertex B, ModelVertex C);

/// <summary>Source-selected legacy feather lattice samples certify the submitted interpolated paint field.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneTerrainPaint
{
    internal static bool Matches(SceneTerrainFace source, IReadOnlyList<SceneTerrainPaintTriangle> actual)
    {
        foreach (var point in Samples(source))
        {
            var covering = actual.Where(t => t.Triangle.Contains(point)).ToArray();
            if (covering.Length == 0 || covering.Any(t => !At(source, t, point))) return false;
        }
        return true;
    }

    static IEnumerable<SceneSurfacePoint> Samples(SceneTerrainFace source)
    {
        var t = source.Triangle;
        // These are the released TileOverlayFeatherMesh sample locations, derived from source faces and
        // mesher options. Scene inputs require one-metre tiles. Native triangle vertices select no samples.
        int divisions = 1;
        if (source.Boundary is not null)
        {
            float width = MathF.Min(new TileGroundMesherOptions().OverlayFeatherWidthMetres, .5f);
            while (divisions < 64 && divisions * width < 2) divisions *= 2;
        }
        for (int y = 0; y <= divisions; y++)
            for (int x = 0; x <= divisions - y; x++)
            {
                double b = (double)x / divisions, c = (double)y / divisions, a = 1 - b - c;
                yield return new(a * t.A.X + b * t.B.X + c * t.C.X, a * t.A.Y + b * t.B.Y + c * t.C.Y,
                    a * t.A.Z + b * t.B.Z + c * t.C.Z);
            }
        if (source.Boundary is null)
            yield return new((t.A.X + t.B.X + t.C.X) / 3, (t.A.Y + t.B.Y + t.C.Y) / 3, (t.A.Z + t.B.Z + t.C.Z) / 3);
    }

    static bool At(SceneTerrainFace source, SceneTerrainPaintTriangle actual, SceneSurfacePoint point)
    {
        var t = actual.Triangle;
        double area = SceneSurfaceTriangle.Cross(t.A, t.B, t.C);
        double b = SceneSurfaceTriangle.Cross(t.A, point, t.C) / area;
        double c = SceneSurfaceTriangle.Cross(t.A, t.B, point) / area, a = 1 - b - c;
        if (a < 0 || b < 0 || c < 0)
        {
            // Tolerant membership can admit points just outside. Keep interpolation inside the closed triangle.
            a = Math.Max(0, a); b = Math.Max(0, b); c = Math.Max(0, c);
            double total = a + b + c;
            a /= total; b /= total; c /= total;
        }
        float Mix(float av, float bv, float cv) => (float)(a * av + b * bv + c * cv);
        Vector4 Mix4(Vector4 av, Vector4 bv, Vector4 cv) => new(Mix(av.X, bv.X, cv.X), Mix(av.Y, bv.Y, cv.Y),
            Mix(av.Z, bv.Z, cv.Z), Mix(av.W, bv.W, cv.W));
        Vector2 uv = new(Mix(actual.A.Uv.X, actual.B.Uv.X, actual.C.Uv.X), Mix(actual.A.Uv.Y, actual.B.Uv.Y, actual.C.Uv.Y));
        return ShippedSceneTerrainCoverage.Paint(source, actual.Payload, point,
            Mix4(actual.A.Color, actual.B.Color, actual.C.Color), uv, Mix4(actual.A.Tangent, actual.B.Tangent, actual.C.Tangent));
    }
}
