using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.CoverageClaimFixtures;

namespace KhaozEngine.Tests.Attachments;

public class BodyTriangleIndexTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.00000025f)]
    [InlineData(-0.000004f)]
    public void Indexed_nearest_hits_match_a_full_scan_at_ridges_edges_corners_and_interiors(float ridgeOffset)
    {
        var (meshes, body) = RidgeBody(ridgeOffset);
        var index = new BodyTriangleIndex(meshes, body);
        foreach (Vector3 vertex in QueryPoints())
        {
            var expected = Scan(meshes, vertex);
            BodyTriangleHit hit = index.Nearest(new ClaimPoint(vertex));
            Assert.Equal(expected.Triangle, hit.Triangle);
            Assert.Equal(expected.Triangle == 3 ? 1 : 0, hit.Region);
            Assert.Equal(expected.Height, hit.HeightMetres, 12);
            Assert.Equal(expected.W1, hit.W1, 12);
            Assert.Equal(expected.W2, hit.W2, 12);
        }
    }

    static IEnumerable<Vector3> QueryPoints()
    {
        yield return Vector3.Zero;
        yield return Vector3.UnitY;
        yield return new Vector3(0.125f, 0.5f, 0.0625f);
        yield return new Vector3(-0.25f, 0.25f, 0.0625f);
        yield return new Vector3(0.0625f, 1.125f, 0.0625f);
        yield return new Vector3(-0.75f, 0.75f, 0.125f);
        yield return new Vector3(0, 0.375f, -0.25f);
        var random = new Random(1901);
        for (int i = 0; i < 96; i++)
            yield return new Vector3(random.Next(-512, 513) / 32f, random.Next(-16, 49) / 32f, random.Next(-12, 13) / 32f);
    }

    static (int Triangle, double Height, double W1, double W2) Scan(CoverageClaimMesh[] meshes, Vector3 point)
    {
        // The fixture contains unit, axis-aligned right triangles. Project onto their 2D unit simplices,
        // independently of the production triangle and bounding-box calculations.
        var candidates = new (int Triangle, double Distance, double Height, double W1, double W2)[meshes.Length - 1];
        for (int i = 1; i < meshes.Length; i++)
        {
            Vector3 origin = meshes[i].Positions.Span[0];
            double x = (double)point.X - origin.X;
            double y = point.Y;
            double z = point.Z;
            bool side = i == 3;
            double u = side ? -z : y;
            double v = side ? y : -x;
            double height = side ? x : z;

            double w1 = Math.Clamp(u, 0, 1);
            double w2 = Math.Clamp(v, 0, 1);
            if (w1 + w2 > 1)
            {
                w1 = Math.Clamp((1 + u - v) / 2, 0, 1);
                w2 = 1 - w1;
            }

            double du = u - w1;
            double dv = v - w2;
            candidates[i - 1] = (i, Math.Sqrt(du * du + dv * dv + height * height), height, w1, w2);
        }

        double nearest = candidates.Min(candidate => candidate.Distance);
        var best = candidates.Where(candidate => candidate.Distance - nearest <= 0.000001)
            .OrderByDescending(candidate => candidate.Height).ThenBy(candidate => candidate.Triangle).First();
        return (best.Triangle, best.Height, best.W1, best.W2);
    }
}
