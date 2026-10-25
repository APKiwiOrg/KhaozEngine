using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterOpticsTriangleFieldTests
{
    [Fact]
    public void ImpossibleAtlasIsRefusedBeforeCopyingOrScanningSourceVertices()
    {
        // Even the header, one bin and 12 scalars per triangle exceed 1024 squared.
        // A NaN first vertex distinguishes the preflight from the later input scan.
        var triangles = new WaterOpticsTriangle[87_380];
        triangles[0] = new(new(float.NaN, 0, 0), default, default);
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, triangles, [Rectangle(0, 0, 1, 1)]));
        Assert.Equal(WaterOpticsSurfaceFailure.CapacityExceeded, error.Reason);
    }

    [Fact]
    public void CallerMutationCannotChangePackedGeometryOrCertificates()
    {
        WaterOpticsTriangle[] triangles = Square(2, -0.25f);
        WaterOpticsRectangle[] certificates = [Rectangle(0, 0, 2, 2)];
        var field = new WaterOpticsTriangleField(new(2), 1, 1, triangles, certificates);
        Array.Fill(triangles, default);
        certificates[0] = default;

        Assert.Equal(-0.25f, field.SampleDepth(.3f, .7f));
        Assert.Equal(Rectangle(0, 0, 2, 2), field.CertifiedRectangles[0]);
        Assert.Equal(2, field.TriangleCount);
        Assert.Equal(5, field.EdgeLineCount);
        Assert.Equal(2, field.CandidateReferenceCount);
        Assert.Equal(2, field.MaximumBinOccupancy);
        // U = 16 + 2 + 2 + 24 + 20 = 64. The physical atlas has two rows.
        Assert.Equal(64, field.TextureWidth);
        Assert.Equal(2, field.TextureHeight);
        Assert.Equal(512L, field.TextureBytes);
        Assert.Equal(528L, field.RetainedBytes);
        Assert.Equal(0, field.ReconstructionErrorBoundMetres);
        ReadOnlySpan<float> atlas = field.AtlasScalars;
        Assert.Equal(128, atlas.Length);
        Assert.Equal(new float[] { 1, 1, 1, 18, 20, 44, 2, 5, 64 }, atlas[..9].ToArray());
        Assert.Equal(new float[] { 18, 2, 0, 1 }, atlas.Slice(16, 4).ToArray());
        foreach (float scalar in atlas[64..]) Assert.Equal(0, scalar);
        for (int i = 9; i < 16; i++) Assert.Equal(0, atlas[i]);
    }

    [Theory]
    [InlineData(2f, 2.013671875f)]
    [InlineData(30f, 30.013671875f)]
    [InlineData(100f, 100.013671875f)]
    public void SignedAffinePlanesRetainSmallChangesAtRepresentativeDepths(float depth, float expected)
    {
        WaterOpticsTriangle[] triangles =
        [
            new(new(0, depth, 0), new(2, depth + .0078125f, 0), new(2, depth + .0234375f, 2)),
            new(new(0, depth, 0), new(2, depth + .0234375f, 2), new(0, depth + .015625f, 2))
        ];
        var field = new WaterOpticsTriangleField(new(2), 3, 5, triangles, [Rectangle(0, 0, 2, 2)]);
        Assert.InRange(Math.Abs(field.SampleDepth(.25f, .75f) - expected), 0, .00001f);
        Assert.InRange(field.ReconstructionErrorBoundMetres, float.Epsilon, .0001f);
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(30f)]
    [InlineData(100f)]
    public void OrdinaryExtentsAndFinalBinsRemainAvailable(float size)
    {
        var field = new WaterOpticsTriangleField(new(size), 1024, 1, Square(size, 30), [Rectangle(0, 0, size, size)]);
        Assert.True(field.TrySampleDepth(1, 1, out float corner));
        Assert.Equal(30, corner);
        Assert.Equal(30, field.SampleDepth(float.BitDecrement(1), 1));
        Assert.Equal(1024, field.TextureWidth);
        Assert.Equal(5, field.TextureHeight);
        Assert.Equal(4L * field.TextureWidth * field.TextureHeight, field.TextureBytes);
        Assert.Equal(20496, field.RetainedBytes);
    }

    [Fact]
    public void ActualHighDiagonalIsPiecewisePlanarInsteadOfBilinear()
    {
        WaterOpticsTriangle[] triangles =
        [new(new(0, -.02f, 0), new(1, .98f, 0), new(1, -.02f, 1)),
         new(new(0, -.02f, 0), new(1, -.02f, 1), new(0, .98f, 1))];
        var field = new WaterOpticsTriangleField(Vector2.One, 2, 2, triangles, [Rectangle(0, 0, 1, 1)]);
        Assert.Equal(-.02f, field.SampleDepth(.5f, .5f));
        Assert.InRange(field.SampleDepth(.75f, .25f), .4799999f, .4800001f);
        Assert.Equal(.98f, field.SampleDepth(1, 0));
    }

    [Fact]
    public void OutsideCertificateReportsUnavailable()
    {
        var field = new WaterOpticsTriangleField(Vector2.One, 1, 1, Square(1, 0), [Rectangle(.2f, .3f, .4f, .8f)]);
        Assert.False(field.TrySampleDepth(.5f, .5f, out _));
        Assert.Throws<ArgumentException>(() => field.SampleDepth(.5f, .5f));
        Assert.True(field.TrySampleDepth(.2f, .3f, out float signedDepth));
        Assert.Equal(0, signedDepth);
    }

    [Fact]
    public void NarrowHoleInsideCertificateIsRefused()
    {
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, RingWithNarrowHole(), [Rectangle(0, 0, 1, 1)]));
        Assert.Equal(WaterOpticsSurfaceFailure.IncompleteCoverage, error.Reason);
    }

    [Fact]
    public void OverlapCannotCancelAHole()
    {
        var triangles = new List<WaterOpticsTriangle>(RingWithNarrowHole());
        AddRectangle(triangles, 0, 0, .25f, .25f, 0);
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, triangles.ToArray(), [Rectangle(0, 0, 1, 1)]));
        Assert.Equal(WaterOpticsSurfaceFailure.IncompleteCoverage, error.Reason);
    }

    [Fact]
    public void HoleOutsideEveryCertificateIsAllowed()
    {
        var field = new WaterOpticsTriangleField(Vector2.One, 2, 3, RingWithNarrowHole(), [Rectangle(.5f, .5f, 1, 1)]);
        Assert.True(field.TrySampleDepth(.75f, .75f, out _));
        Assert.False(field.TrySampleDepth(.2f, .5f, out _));
    }

    [Fact]
    public void DistinctOverlappingBedsAreRefused()
    {
        WaterOpticsTriangle[] triangles = [.. Square(1, 2), .. Square(1, 30)];
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, triangles, [Rectangle(0, 0, 1, 1)]));
        Assert.Equal(WaterOpticsSurfaceFailure.AmbiguousSurface, error.Reason);
    }

    [Fact]
    public void DiscontinuousSharedBoundaryIsRefused()
    {
        WaterOpticsTriangle[] triangles =
        [new(new(0, 0, 0), new(1, 0, 0), new(1, 0, 1)),
         new(new(0, 1, 0), new(1, 1, 1), new(0, 1, 1))];
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, triangles, [Rectangle(0, 0, 1, 1)]));
        Assert.Equal(WaterOpticsSurfaceFailure.AmbiguousSurface, error.Reason);
    }

    [Fact]
    public void ThirtyThreeCandidatesRefuseWithoutDroppingTriangles()
    {
        var triangles = new WaterOpticsTriangle[33];
        Array.Fill(triangles, new(new(0, 2, 0), new(1, 2, 0), new(0, 2, 1)));
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1, 1, triangles, [Rectangle(0, 0, .25f, .25f)]));
        Assert.Equal(WaterOpticsSurfaceFailure.CapacityExceeded, error.Reason);
    }

    [Fact]
    public void ThirtyTwoCandidatesAreSupportedAndRemainInSourceOrder()
    {
        var triangles = new WaterOpticsTriangle[32];
        Array.Fill(triangles, new(new(0, -2, 0), new(1, -2, 0), new(0, -2, 1)));
        var field = new WaterOpticsTriangleField(Vector2.One, 1, 1, triangles, [Rectangle(0, 0, .25f, .25f)]);
        Assert.Equal(32, field.MaximumBinOccupancy);
        Assert.Equal(-2, field.SampleDepth(.1f, .1f));
        for (int i = 0; i < 32; i++) Assert.Equal(i, field.AtlasScalars[18 + i]);
    }

    [Fact]
    public void AtlasStorageOverflowIsATypedRefusal()
    {
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            Vector2.One, 1024, 1024, Square(1, 2), [Rectangle(0, 0, 1, 1)]));
        Assert.Equal(WaterOpticsSurfaceFailure.CapacityExceeded, error.Reason);
    }

    [Fact]
    public void BinsUseClosedTriangleIntersectionInsteadOfBoundingBoxes()
    {
        var field = new WaterOpticsTriangleField(Vector2.One, 4, 4,
            [new(new(0, 2, 0), new(1, 2, 0), new(0, 2, 1))], [Rectangle(0, 0, .25f, .25f)]);
        Assert.Equal(0, field.AtlasScalars[16 + 2 * 15 + 1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedVerticesAndLongShortTJunctionsHaveNoCracks(bool reverse)
    {
        var triangles = new List<WaterOpticsTriangle>();
        AddAffineRectangle(triangles, 0, 0, 1, 2);
        AddAffineRectangle(triangles, 1, 0, 2, 1);
        AddAffineRectangle(triangles, 1, 1, 2, 2);
        if (reverse) triangles.Reverse();
        var field = new WaterOpticsTriangleField(new(2), 3, 3, triangles.ToArray(), [Rectangle(0, 0, 2, 2)]);
        foreach (float u in new[] { 0, float.BitDecrement(.5f), .5f, float.BitIncrement(.5f), 1 })
            foreach (float v in new[] { 0, float.BitIncrement(0), float.BitDecrement(.5f), .5f, float.BitIncrement(.5f), 1 })
            {
                Assert.True(field.TrySampleDepth(u, v, out float value));
                // Independent affine oracle is q = x + 2z - 1.
                Assert.InRange(Math.Abs(value - ((double)u * 2 + v * 4 - 1)), 0, .00001);
            }
        int edgeBase = (int)field.AtlasScalars[5];
        int verticalSharedLines = 0;
        for (int i = 0; i < field.EdgeLineCount; i++)
        {
            ReadOnlySpan<float> line = field.AtlasScalars.Slice(edgeBase + 4 * i, 4);
            if (line[0] == 1 && line[2] == 1)
            {
                verticalSharedLines++;
                Assert.Equal(new float[] { 1, 0, 1, 2 }, line.ToArray());
            }
        }
        Assert.Equal(1, verticalSharedLines);
    }

    [Theory]
    [InlineData(false, .25f, .25f)]
    [InlineData(true, .25f, .25f)]
    [InlineData(false, .3f, .4f)]
    [InlineData(true, .3f, .4f)]
    public void ThreeWayVertexUsesRealTrianglePlanesInEitherOrder(bool reverse, float centerX, float centerZ)
    {
        var center = new Vector3(centerX, 3, centerZ);
        WaterOpticsTriangle[] triangles =
        [new(new(0, 0, 0), new(1, 2, 0), center),
         new(new(1, 2, 0), new(0, 1, 1), center),
         new(new(0, 1, 1), new(0, 0, 0), center)];
        if (reverse)
        {
            Array.Reverse(triangles);
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = new(triangles[i].C, triangles[i].B, triangles[i].A);
        }
        var field = new WaterOpticsTriangleField(Vector2.One, 7, 5, triangles, [Rectangle(0, 0, .5f, .5f)]);
        foreach (float u in new[] { float.BitDecrement(centerX), centerX, float.BitIncrement(centerX) })
            foreach (float v in new[] { float.BitDecrement(centerZ), centerZ, float.BitIncrement(centerZ) })
            {
                Assert.True(field.TrySampleDepth(u, v, out float depth));
                Assert.InRange(Math.Abs(depth - RayOracle(triangles, u, v)), 0, .00001);
            }
    }

    [Fact]
    public void UncertifiableConditioningIsRefusedInsteadOfMovingVertices()
    {
        WaterOpticsTriangle[] triangles =
        [new(new(1, 0, 0), new(float.BitIncrement(1), 100, 0), new(1, 0, 1)),
         new(new(float.BitIncrement(1), 100, 0), new(float.BitIncrement(1), 100, 1), new(1, 0, 1))];
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
            new(2), 1, 1, triangles, [Rectangle(1, 0, float.BitIncrement(1), .25f)]));
        Assert.Equal(WaterOpticsSurfaceFailure.UnrepresentableSurface, error.Reason);
    }

    [Theory]
    [InlineData(float.NaN, 1)]
    [InlineData(float.PositiveInfinity, 1)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    public void InvalidSizeIsAnOrdinaryArgumentError(float x, float z) => Assert.ThrowsAny<ArgumentException>(
        () => new WaterOpticsTriangleField(new(x, z), 1, 1, Square(1, 2), [Rectangle(0, 0, 1, 1)]));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1025, 1)]
    [InlineData(1, int.MaxValue)]
    public void InvalidBinAxesAreOrdinaryArgumentErrors(int columns, int rows) => Assert.ThrowsAny<ArgumentException>(
        () => new WaterOpticsTriangleField(Vector2.One, columns, rows, Square(1, 2), [Rectangle(0, 0, 1, 1)]));

    [Fact]
    public void EmptyOrMalformedGeometryAndCertificatesAreOrdinaryArgumentErrors()
    {
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1, null, [Rectangle(0, 0, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1, Square(1, 0), null));
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1, [default], [Rectangle(0, 0, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1,
            [new(new(0, 0, 0), new(1, 1, 1), new(.5f, 2, .5f))], [Rectangle(0, 0, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1,
            [new(new(-1, 0, 0), new(1, 0, 0), new(0, 0, 1))], [Rectangle(0, 0, .25f, .25f)]));
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1, Square(1, 0), [default]));
        Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1, Square(1, 0), [Rectangle(0, 0, 2, 1)]));
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1,
                [new(new(0, invalid, 0), new(1, 0, 0), new(0, 0, 1))], [Rectangle(0, 0, .25f, .25f)]));
            Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1,
                [new(new(invalid, 0, 0), new(1, 0, 0), new(0, 0, 1))], [Rectangle(0, 0, .25f, .25f)]));
            Assert.Throws<ArgumentException>(() => new WaterOpticsTriangleField(Vector2.One, 1, 1,
                Square(1, 0), [Rectangle(0, 0, invalid, 1)]));
        }
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    [InlineData(0, float.NegativeInfinity)]
    [InlineData(-.01f, .5f)]
    [InlineData(.5f, 1.01f)]
    public void InvalidQueriesThrowInsteadOfReportingMissing(float u, float v)
    {
        var field = new WaterOpticsTriangleField(Vector2.One, 1, 1, Square(1, 2), [Rectangle(0, 0, 1, 1)]);
        Assert.ThrowsAny<ArgumentException>(() => field.SampleDepth(u, v));
        Assert.ThrowsAny<ArgumentException>(() => field.TrySampleDepth(u, v, out _));
    }

    private static WaterOpticsRectangle Rectangle(float x0, float z0, float x1, float z1) => new(new(x0, z0), new(x1, z1));

    private static WaterOpticsTriangle[] Square(float size, float depth) =>
        [new(new(0, depth, 0), new(size, depth, 0), new(size, depth, size)),
         new(new(0, depth, 0), new(size, depth, size), new(0, depth, size))];

    private static void AddRectangle(List<WaterOpticsTriangle> target, float x0, float z0, float x1, float z1, float depth)
    {
        target.Add(new(new(x0, depth, z0), new(x1, depth, z0), new(x1, depth, z1)));
        target.Add(new(new(x0, depth, z0), new(x1, depth, z1), new(x0, depth, z1)));
    }

    private static WaterOpticsTriangle[] RingWithNarrowHole()
    {
        var triangles = new List<WaterOpticsTriangle>();
        float hi = float.BitIncrement(.2f);
        AddRectangle(triangles, 0, 0, .2f, 1, 0);
        AddRectangle(triangles, hi, 0, 1, 1, 0);
        AddRectangle(triangles, .2f, 0, hi, .3f, 0);
        AddRectangle(triangles, .2f, .7f, hi, 1, 0);
        return triangles.ToArray();
    }

    private static void AddAffineRectangle(List<WaterOpticsTriangle> target, float x0, float z0, float x1, float z1)
    {
        target.Add(new(new(x0, x0 + 2 * z0 - 1, z0), new(x1, x1 + 2 * z0 - 1, z0), new(x1, x1 + 2 * z1 - 1, z1)));
        target.Add(new(new(x0, x0 + 2 * z0 - 1, z0), new(x1, x1 + 2 * z1 - 1, z1), new(x0, x0 + 2 * z1 - 1, z1)));
    }

    // Independent double-precision vertical ray intersection. No atlas or production geometry helpers.
    private static double RayOracle(WaterOpticsTriangle[] triangles, double x, double z)
    {
        foreach (WaterOpticsTriangle triangle in triangles)
        {
            Vector3 a = triangle.A, b = triangle.B, c = triangle.C;
            double nx = ((double)b.Y - a.Y) * (c.Z - a.Z) - ((double)b.Z - a.Z) * (c.Y - a.Y);
            double ny = ((double)b.Z - a.Z) * (c.X - a.X) - ((double)b.X - a.X) * (c.Z - a.Z);
            double nz = ((double)b.X - a.X) * (c.Y - a.Y) - ((double)b.Y - a.Y) * (c.X - a.X);
            double ab = ((double)b.X - a.X) * (z - a.Z) - ((double)b.Z - a.Z) * (x - a.X);
            double bc = ((double)c.X - b.X) * (z - b.Z) - ((double)c.Z - b.Z) * (x - b.X);
            double ca = ((double)a.X - c.X) * (z - c.Z) - ((double)a.Z - c.Z) * (x - c.X);
            if ((ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0))
                return a.Y - (nx * (x - a.X) + nz * (z - a.Z)) / ny;
        }
        throw new InvalidOperationException("The independent ray missed the fixture.");
    }
}
