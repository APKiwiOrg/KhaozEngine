using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Coincident shared boundaries whose drawn depths differ by binary32 interpolation are certified with
/// the measured discontinuity in the reconstruction bound. Positive-area disagreement and gaps still refuse.</summary>
public sealed class WaterOpticsTriangleDiscontinuityTests
{
    const float Deep = 0.7f;

    // Two tiles per row in local metres. The south row is subdivided into sixteenths with the feather mesher's
    // binary32 rule, a*wa + b*wb + c*wc, and the uncut north row keeps its long edges on z = 1. Depth is 0.7 m at
    // the shared corner (1, 1) and zero elsewhere, the acceptance fixture's -0.70 m deep corner on the exact datum.
    static List<WaterOpticsTriangle> FeatherJunction()
    {
        var triangles = new List<WaterOpticsTriangle>();
        for (int tile = 0; tile < 2; tile++)
        {
            Vector3 sw = Corner(tile, 0), se = Corner(tile + 1, 0), ne = Corner(tile + 1, 1), nw = Corner(tile, 1);
            Subdivide(triangles, sw, se, ne);
            Subdivide(triangles, sw, ne, nw);
            Vector3 north = Corner(tile, 2), northEast = Corner(tile + 1, 2);
            triangles.Add(new(nw, ne, northEast));
            triangles.Add(new(nw, northEast, north));
        }
        return triangles;
    }

    static Vector3 Corner(int x, int z) => new(x, x == 1 && z == 1 ? Deep : 0f, z);

    static void Subdivide(List<WaterOpticsTriangle> into, Vector3 a, Vector3 b, Vector3 c)
    {
        const int divisions = 16;
        var vertices = new Vector3[(divisions + 1) * (divisions + 1)];
        for (int y = 0; y <= divisions; y++)
            for (int x = 0; x <= divisions - y; x++)
            {
                float wb = (float)x / divisions, wc = (float)y / divisions, wa = 1 - wb - wc;
                vertices[y * (divisions + 1) + x] = a * wa + b * wb + c * wc;
            }
        for (int y = 0; y < divisions; y++)
            for (int x = 0; x < divisions - y; x++)
            {
                int i = y * (divisions + 1) + x, next = i + divisions + 1;
                into.Add(new(vertices[i], vertices[i + 1], vertices[next]));
                if (x + y < divisions - 1) into.Add(new(vertices[i + 1], vertices[next + 1], vertices[next]));
            }
    }

    static WaterOpticsTriangleField Build(IEnumerable<WaterOpticsTriangle> triangles) =>
        new(new Vector2(2f), 32, 32, [.. triangles], [new(Vector2.Zero, new Vector2(2f))]);

    // Binary64 depth of every source triangle whose closed projection contains (x, z). Fixture coordinates are
    // small dyadic values, so these cross products are exact and only the interpolation rounds.
    static List<double> Drawn(IEnumerable<WaterOpticsTriangle> triangles, double x, double z)
    {
        var depths = new List<double>();
        foreach (WaterOpticsTriangle t in triangles)
        {
            double d = ((double)t.B.X - t.A.X) * ((double)t.C.Z - t.A.Z) - ((double)t.B.Z - t.A.Z) * ((double)t.C.X - t.A.X);
            double l1 = ((x - t.A.X) * ((double)t.C.Z - t.A.Z) - (z - t.A.Z) * ((double)t.C.X - t.A.X)) / d;
            double l2 = (((double)t.B.X - t.A.X) * (z - t.A.Z) - ((double)t.B.Z - t.A.Z) * (x - t.A.X)) / d;
            double l0 = 1 - l1 - l2;
            if (l0 >= 0 && l1 >= 0 && l2 >= 0) depths.Add(l0 * t.A.Y + l1 * t.B.Y + l2 * t.C.Y);
        }
        return depths;
    }

    [Fact]
    public void FixtureReproducesTheOffLineFeatherVertex()
    {
        // The 6E diagnosis: x = 11.1875 on the shared edge drew -0.56874996 against the line's -0.56874999031.
        List<WaterOpticsTriangle> triangles = FeatherJunction();
        Assert.Contains(triangles, t => t.A == new Vector3(1.1875f, 0.56874996f, 1f)
            || t.B == new Vector3(1.1875f, 0.56874996f, 1f) || t.C == new Vector3(1.1875f, 0.56874996f, 1f));
        // The vertex is the correctly rounded product, so it lies off the exact long edge between its corners.
        Assert.True(Math.Abs(0.56874996f - 0.8125 * Deep) > 2.6e-8);
    }

    [Fact]
    public void FeatherTJunctionIsCertifiedWithItsMeasuredDiscontinuity()
    {
        List<WaterOpticsTriangle> triangles = FeatherJunction();
        WaterOpticsTriangleField field = Build(triangles);

        // The largest drawn gap along z = 1, measured independently in binary64 at each subdivision vertex.
        double gap = 0;
        for (int k = 0; k <= 32; k++)
        {
            List<double> drawn = Drawn(triangles, k / 16.0, 1);
            foreach (double depth in drawn) gap = Math.Max(gap, Math.Abs(depth - drawn[0]));
        }
        Assert.InRange(gap, 2.6e-8, 2.62e-8);
        Assert.True(field.ReconstructionErrorBoundMetres >= gap, $"Bound {field.ReconstructionErrorBoundMetres:R} below gap {gap:R}.");
        Assert.True(field.ReconstructionErrorBoundMetres < 0.0001f, $"Bound {field.ReconstructionErrorBoundMetres:R}.");

        // On, just south of and just north of the shared edge, at vertices and between them.
        int compared = 0;
        foreach (double z in new[] { 1.0, 1 - 1.0 / 128, 1 + 1.0 / 128 })
            for (int k = 0; k <= 64; k++)
            {
                float x = k / 32f;
                float sampled = field.SampleDepth(x / 2f, (float)z / 2f);
                foreach (double drawn in Drawn(triangles, x, z))
                {
                    Assert.True(Math.Abs(sampled - drawn) <= field.ReconstructionErrorBoundMetres,
                        $"({x}, {z}) sampled {sampled:R} drawn {drawn:R} bound {field.ReconstructionErrorBoundMetres:R}");
                    compared++;
                }
            }
        Assert.True(compared > 3 * 65, $"Only {compared} drawn comparisons.");
    }

    [Fact]
    public void TinyPositiveAreaDisagreementStillRefuses()
    {
        // Two whole beds a hundred times closer than the budget. Area overlap is two surfaces, never a seam.
        WaterOpticsTriangle[] triangles =
        [new(new(0, 0.5f, 0), new(1, 0.5f, 0), new(1, 0.5f, 1)), new(new(0, 0.5f, 0), new(1, 0.5f, 1), new(0, 0.5f, 1)),
         new(new(0, 0.5000001f, 0), new(1, 0.5000001f, 0), new(1, 0.5000001f, 1)),
         new(new(0, 0.5000001f, 0), new(1, 0.5000001f, 1), new(0, 0.5000001f, 1))];
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, triangles, [new(Vector2.Zero, Vector2.One)]));
        Assert.Equal(WaterOpticsSurfaceFailure.AmbiguousSurface, error.Reason);
    }

    [Fact]
    public void FeatherJunctionWithAMissingTriangleRefusesAsAGap()
    {
        List<WaterOpticsTriangle> triangles = FeatherJunction();
        triangles.RemoveAt(triangles.FindIndex(t => t.A == new Vector3(1.1875f, 0.56874996f, 1f)
            || t.B == new Vector3(1.1875f, 0.56874996f, 1f) || t.C == new Vector3(1.1875f, 0.56874996f, 1f)));
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => Build(triangles));
        Assert.Equal(WaterOpticsSurfaceFailure.IncompleteCoverage, error.Reason);
    }

    [Fact]
    public void DiscontinuityBeyondTheBudgetRefusesAsAmbiguous()
    {
        // The same T junction with its long edge lifted by a millimetre. Seams are charged, not forgiven.
        List<WaterOpticsTriangle> triangles = FeatherJunction();
        for (int i = triangles.Count - 4; i < triangles.Count; i++)
        {
            WaterOpticsTriangle t = triangles[i];
            triangles[i] = new(t.A + new Vector3(0, 0.001f, 0), t.B + new Vector3(0, 0.001f, 0), t.C + new Vector3(0, 0.001f, 0));
        }
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => Build(triangles));
        Assert.Equal(WaterOpticsSurfaceFailure.AmbiguousSurface, error.Reason);
    }
}
