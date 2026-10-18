using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using P = KhaozEngine.Render3D.CoverageReferencePrecision;
using M = KhaozEngine.Render3D.CoverageReferenceMath;
using ReferenceSupport = KhaozEngine.Render3D.CoverageReferenceSupport;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferencePrimitiveTests
{
    [Fact]
    public void Scalar_float_query_order_and_gather_tables_are_pinned()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        var skin = fixture.Source.SkinVertices.ToArray();
        for (int v = 0; v < skin.Length; v++) skin[v].Normal = Vector3.UnitZ;
        var source = new CoverageSourceTopology("reference-smooth-seam", CoverageFixture.Hash, fixture.Source.Positions,
            fixture.Source.Vertices, fixture.Source.Triangles, skin);
        var topology = CoverageRefinement.Build(source, fixture.Map);
        var data = new CoverageReferenceTopology(topology);
        Assert.Same(topology, data.Topology);
        Assert.Equal(topology.PositionGroupCount, data.Recipes.Length);
        Assert.Equal(12, data.Recipes.First(r => r.WeightA != 12).Denominator);
        Assert.Equal(0, data.CoreFaceOffsets[0]);
        Assert.Equal(data.CoreFaces.Length, data.CoreFaceOffsets[^1]);
        Assert.Equal(data.VertexCorners.Length, data.VertexFaceOffsets[^1]);
        Assert.Equal(data.ShadingCorners.Length, data.ShadingCornerOffsets[^1]);
        Assert.True(EachRowIsStrictlySortedAndDuplicateFree(data.CoreFaceOffsets, data.CoreFaces));
        Assert.True(EachRowIsStrictlySortedAndDuplicateFree(data.VertexFaceOffsets, data.VertexCorners));
        Assert.True(EachRowIsStrictlySortedAndDuplicateFree(data.ShadingCornerOffsets, data.ShadingCorners));
        int[] indices = topology.Indices.ToArray().Select(i => (int)i).ToArray();
        for (int g = 0; g < topology.PositionGroupCount; g++)
        {
            Assert.Equal(Enumerable.Range(0, topology.VertexCount).First(v => topology.Vertices[v].PositionGroup == g), data.GroupRepresentatives[g]);
            Assert.Equal(topology.GetPositionRecipe(g), data.Recipes[g]);
            var recipe = data.Recipes[g];
            Assert.Equal(12, recipe.WeightA + recipe.WeightB + recipe.WeightC);
            int[] expected = Enumerable.Range(0, topology.TriangleCount).Where(t => topology.Triangles[t].Role == CoverageTriangleRole.Core
                && topology.Triangles[t].RegionIndex >= 0 && indices.Skip(t * 3).Take(3).Any(v => topology.Vertices[v].PositionGroup == g)).ToArray();
            Assert.Equal(expected, data.CoreFaces[data.CoreFaceOffsets[g]..data.CoreFaceOffsets[g + 1]]);
        }
        for (int v = 0; v < topology.VertexCount; v++)
            Assert.Equal(Enumerable.Range(0, indices.Length).Where(c => indices[c] == v), data.VertexCorners[data.VertexFaceOffsets[v]..data.VertexFaceOffsets[v + 1]]);
        for (int g = 0; g < topology.ShadingGroupCount; g++)
            Assert.Equal(Enumerable.Range(0, indices.Length).Where(c => topology.Vertices[indices[c]].ShadingGroup == g),
                data.ShadingCorners[data.ShadingCornerOffsets[g]..data.ShadingCornerOffsets[g + 1]]);
        Assert.Contains(Enumerable.Range(0, topology.ShadingGroupCount), g =>
            data.ShadingCorners[data.ShadingCornerOffsets[g]..data.ShadingCornerOffsets[g + 1]].Select(c => indices[c]).Distinct().Count() > 1);
        // Cancellation distinguishes the written left-associated graph from a reassociated dot.
        Assert.Equal(1f, M.Dot3(new(MathF.ScaleB(1, 25), -MathF.ScaleB(1, 25), 1), Vector3.One));
        float step = MathF.ScaleB(1, -23);
        Assert.Equal(0f, M.Dot3(new(1 + step, -1, 0), new(1 - step, 1, 0)));
        Assert.Equal(Vector3.Zero, M.Cross(new(0, 1 + step, 1), new(0, 1, 1 - step)));
        Assert.Equal(11f, M.Dot2(new(1, 2), new(3, 4)));
        Assert.Equal(new Vector3(-3, 6, -3), M.Cross(new(1, 2, 3), new(4, 5, 6)));
        var matrix = new Matrix4x4(1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 1);
        Assert.Equal(new Vector3(30, 36, 42), M.Linear(new(1, 2, 3), matrix));
        Assert.Equal(new Vector3(40, 47, 54), M.Point(new(1, 2, 3), matrix));
        var cancellationMatrix = new Matrix4x4(1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1);
        Vector3 cancellation = new(MathF.ScaleB(1, 25), -MathF.ScaleB(1, 25), 1);
        Assert.Equal(Vector3.One, M.Linear(cancellation, cancellationMatrix));
        Assert.Equal(new Vector3(2), M.Point(cancellation, cancellationMatrix));
        var scratch = new CoverageReferenceScratch(topology, 2);
        Assert.Equal(topology.VertexCount, scratch.Before.Length);
        Assert.Equal(topology.VertexCount, scratch.After.Length);
        Assert.Equal(topology.VertexCount, scratch.Published.Length);
        Assert.Equal(topology.TriangleCount, scratch.FaceCorrections.Length);
        Assert.Equal(topology.TriangleCount, scratch.FaceGuards.Length);
        Assert.Equal(2, scratch.Frames.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageReferenceScratch(topology, 5));
    }

    [Fact]
    public void Coordinate_only_roundoff_does_not_bound_skinny_triangle_height()
    {
        Vector3 a = Vector3.Zero, b = new(1, 1, 0), c = new(1, 1 + MathF.ScaleB(1, -23), 1);
        Assert.Equal(0f, M.Height(a, b, c, new(.5f, .5f)));
        Assert.Equal(.5f, M.Height(a, b, c, new(.5f, .5f + MathF.ScaleB(1, -24))));
        Assert.Equal(MathF.ScaleB(1, -23), CoverageReferencePredicates.Edge(a, b, c).Value);
        // This is the height expression graph on a query interval covering both adjacent points.
        I x = F.Point(.5f), y = new(.5, .5 + Math.ScaleB(1, -24));
        I Edge(Vector3 from, Vector3 to, I px, I py) => F.Subtract(
            F.Multiply(F.Subtract(F.Point(to.X), F.Point(from.X)), F.Subtract(py, F.Point(from.Y))),
            F.Multiply(F.Subtract(F.Point(to.Y), F.Point(from.Y)), F.Subtract(px, F.Point(from.X))));
        I area = Edge(a, b, F.Point(c.X), F.Point(c.Y));
        I u = Edge(b, c, x, y), v = Edge(c, a, x, y), w = Edge(a, b, x, y);
        I height = F.Divide(F.Add(F.Add(F.Multiply(u, F.Point(a.Z)), F.Multiply(v, F.Point(b.Z))), F.Multiply(w, F.Point(c.Z))), area);
        Assert.True(height.Contains(0));
        Assert.True(height.Contains(.5));
        Assert.True(height.Max - height.Min >= .5);
    }

    [Fact]
    public void Interval_operations_pin_ulps_domains_and_input_flushing()
    {
        Assert.Equal((1, 4, 16, 1), (P.BasicUlps, P.DivideUlps, P.SqrtUlps, P.StoreUlps));
        Assert.Equal(P.MinNormal32, Math.ScaleB(1, -126));
        Assert.Equal(P.MinSubnormal32, Math.ScaleB(1, -149));
        Assert.Equal(P.MaxDivisor32, Math.ScaleB(1, 126));
        Assert.Equal(.25 * (double).02f, P.Budget(.02f));
        I stored = F.Store32(F.Point(1));
        Assert.True(stored.Min < 1 && stored.Max > 1);
        float n = MathF.ScaleB(1, -140), large = MathF.ScaleB(1, 100);
        I product = F.Multiply(F.Point(n), F.Point(large));
        Assert.True(product.Contains(0));
        Assert.True(product.Contains((double)n * large));
        Assert.True(F.Hull(F.Point(n), F.Point(n)).Contains(0));
        Assert.True(F.Negate(F.Point(n)).Contains(0));
        Assert.True(F.Abs(F.Point(-n)).Contains(0));
        Assert.Equal(CoverageIntervalTruth.Either, F.CompareLess(F.Point(n), F.Point(n * .5f)));
        Assert.False(F.Divide(F.Point(1), new(-1, 1)).IsFinite);
        Assert.False(F.Divide(F.Point(1), F.Point(MathF.ScaleB(1, -127))).IsFinite);
        Assert.False(F.Divide(F.Point(1), F.Point(MathF.ScaleB(1, 127))).IsFinite);
        Assert.False(F.Sqrt(new(-1, 1)).IsFinite);
        Assert.False(F.Add(F.Point(float.MaxValue), F.Point(float.MaxValue)).IsFinite);
        Assert.False(F.Multiply(F.Point(float.MaxValue), F.Point(2)).IsFinite);
        foreach (float divisor in new[] { MathF.ScaleB(1, -126), MathF.ScaleB(1, 126) })
        {
            I quotient = F.Divide(F.Point(divisor), F.Point(divisor));
            Assert.True(quotient.IsFinite);
            Assert.True(quotient.Contains(1));
        }
        Assert.True(F.Add(F.Point(1), F.Point(-1)).Contains(0));
        Assert.True(F.Multiply(new(-1, 1), F.Point(2)).IsFinite);
        Assert.True(F.Subtract(F.Point(-0f), F.Point(0f)).Contains(0));
        I cross = F.Add(F.Point(MathF.BitDecrement(2)), F.Point(MathF.ScaleB(1, -24)));
        Assert.True(cross.Contains((double)MathF.BitDecrement(2) + Math.ScaleB(1, -24)));
        Assert.Equal(CoverageIntervalTruth.True, F.CompareLess(new(1, 2), new(3, 4)));
        Assert.Equal(CoverageIntervalTruth.False, F.CompareLess(new(3, 4), new(1, 2)));
        Assert.Equal(CoverageIntervalTruth.Either, F.CompareLess(new(1, 3), new(2, 4)));
        Assert.Equal(new I(1, 3), F.Hull(new(1, 2), new(2, 3)));
        Assert.Equal(new I(1, 2), F.Min(new(1, 3), new(2, 2)));
        Assert.Equal(new I(2, 3), F.Max(new(1, 3), new(2, 2)));
        Assert.Equal(new I(0, 1), F.Clamp(new(-1, 2), 0, 1));
        Assert.False(F.Store32(F.Unbounded).IsFinite);
    }

    [Theory]
    [InlineData(.75f, 1.25f)]
    [InlineData(-3f, 2f)]
    [InlineData(1.9999999f, 1.0000001f)]
    public void Arithmetic_endpoints_bracket_independent_binary64_results(float a, float b)
    {
        I[] actual = [F.Add(F.Point(a), F.Point(b)), F.Subtract(F.Point(a), F.Point(b)),
            F.Multiply(F.Point(a), F.Point(b)), F.Divide(F.Point(a), F.Point(b))];
        double[] ideal = [(double)a + b, (double)a - b, (double)a * b, (double)a / b];
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(actual[i].IsFinite);
            Assert.True(actual[i].Min < ideal[i] && actual[i].Max > ideal[i]);
        }
    }

    [Fact]
    public void Square_root_enclosure_at_zero_and_subnormals_is_finite()
    {
        foreach (I input in new[] { new I(0, 0), new I(0, Math.ScaleB(1, -140)), new I(0, 1) })
        {
            I actual = F.Sqrt(input);
            Assert.True(actual.IsFinite);
            Assert.True(actual.Min >= 0);
            Assert.True(actual.Contains(0));
            Assert.True(actual.Contains(Math.Sqrt(input.Max)));
        }
        foreach (I input in new[] { new I(P.MinNormal32, 1), new I(1, float.MaxValue) })
        {
            I actual = F.Sqrt(input);
            Assert.True(actual.IsFinite);
            Assert.True(actual.Min >= 0);
            Assert.True(actual.Contains(Math.Sqrt(input.Min)));
            Assert.True(actual.Contains(Math.Sqrt(input.Max)));
        }
    }

    [Theory]
    [InlineData(-4f)]
    [InlineData(.5f)]
    [InlineData(1.9999999f)]
    [InlineData(2f)]
    public void Division_contains_independent_reciprocal_multiply_expansion(float divisor)
    {
        double reciprocal = 1 / (double)divisor;
        double ulp = Math.ScaleB(1, Math.ILogB(Math.Abs(reciprocal)) - 23);
        double error = Math.BitIncrement(ulp + P.MinNormal32);
        double low = Math.BitDecrement(reciprocal - error), high = Math.BitIncrement(reciprocal + error);
        double productLow = Math.BitDecrement(3 * low), productHigh = Math.BitIncrement(3 * high);
        double productUlp = Math.ScaleB(1, Math.ILogB(Math.Max(Math.Abs(productLow), Math.Abs(productHigh))) - 23);
        double productError = Math.BitIncrement(productUlp + P.MinNormal32);
        I result = F.Divide(F.Point(3), F.Point(divisor));
        Assert.True(result.IsFinite);
        Assert.True(result.Min <= Math.BitDecrement(productLow - productError));
        Assert.True(result.Max >= Math.BitIncrement(productHigh + productError));
        I inverse = F.Reciprocal1(F.Point(divisor));
        Assert.True(inverse.Min <= low && inverse.Max >= high);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3.9999998f)]
    [InlineData(4f)]
    [InlineData(1.17549435e-38f)]
    public void Square_root_contains_independent_symmetric_inherited_expansion(float x)
    {
        // Independent endpoint evaluation of two ULP inverse sqrt followed by strict division.
        double root = Math.Sqrt(x), inverse = 1 / root;
        double inverseUlp = Math.ScaleB(1, Math.Max(-149, Math.ILogB(inverse) - 23));
        double e = Math.BitIncrement(2 * inverseUlp + Math.ScaleB(1, -126));
        double lo = Math.BitDecrement(inverse - e), hi = Math.BitIncrement(inverse + e);
        double lower = Math.BitDecrement(1 / hi), upper = Math.BitIncrement(1 / lo);
        double divisionError = Math.BitIncrement(4 * Math.ScaleB(1, Math.Max(-149, Math.ILogB(upper) - 23)) + Math.ScaleB(1, -126));
        double radius = Math.BitIncrement(Math.Max(root - Math.BitDecrement(lower - divisionError), Math.BitIncrement(upper + divisionError) - root));
        I result = F.Sqrt(F.Point(x));
        Assert.True(result.IsFinite);
        Assert.True(result.Min <= root - radius && result.Max >= root + radius);
    }

    [Fact]
    public void Radius_composes_host_and_equipment_error_outward()
    {
        var center = new CoverageVertexEnclosure(F.Point(0), F.Point(0), F.Point(0));
        var device = new CoverageVertexEnclosure(new(-3, 3), new(-4, 4), F.Point(0));
        Assert.True(device.Contains(new(3, -4, 0)));
        Assert.False(device.Contains(new(4, 0, 0)));
        double radius = P.Radius(device, center);
        Assert.True(radius > 5 && radius < Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(5)))));
        Assert.Equal(0, P.ComposeTolerance(0, 0));
        Assert.True(P.ComposeTolerance(.003, .002) > .005);
        Assert.True(double.IsPositiveInfinity(P.ComposeTolerance(double.PositiveInfinity, 0)));
        Assert.True(double.IsPositiveInfinity(P.Radius(new(F.Unbounded, F.Point(0), F.Point(0)), center)));
    }

    [Fact]
    public void Shared_edge_and_boundary_ties_use_source_order()
    {
        var support = Square();
        var data = support.ReferenceData;
        Assert.Same(data, support.ReferenceData);
        Assert.Equal(support.InwardSourceIndices.ToArray(), data.SourceIndices);
        Assert.Equal(support.BoundarySourceVertices.ToArray(), data.BoundarySourceVertices);
        foreach (Vector2 p in new[] { new Vector2(0, 0), new Vector2(1, 1), new Vector2(-1, -1), new Vector2(1, -1) })
        {
            Assert.True(data.TryHeight(p, out float height, out int sourceTriangle));
            Assert.Equal(p.X, height);
            Assert.Equal(0, sourceTriangle);
        }
        // Edge zero is the bottom rim. The square centre ties all four edges, so bottom wins.
        Assert.Equal(0, data.BoundarySourceVertices[0]);
        Assert.True(data.TryBoundary(Vector2.Zero, out float boundaryHeight, out float distance));
        Assert.Equal(0f, boundaryHeight);
        Assert.Equal(1f, distance);
        Assert.True(data.TryBoundary(new(2, 0), out _, out distance));
        Assert.Equal(1f, distance);
        float collarWidth = 1;
        float spatial = Math.Clamp(1 - distance / collarWidth, 0, 1);
        Assert.Equal(0f, spatial * spatial * (3 - 2 * spatial));
        Assert.False(data.TryHeight(new(1.0001f, 0), out _));
        Assert.False(data.TryHeight(new(float.NaN, 0), out _));
        Assert.False(data.TryBoundary(new(float.MaxValue, float.MaxValue), out _, out _));
        // The outer maxima are cell 15, and every cell retains original source order.
        Assert.Equal(257, data.CellOffsets.Length);
        Assert.True(EachRowIsStrictlySortedAndDuplicateFree(data.CellOffsets, data.CellTriangles));
        Assert.Contains(0, data.CellTriangles[data.CellOffsets[255]..data.CellOffsets[256]]);
        for (int y = 0; y <= 16; y++)
            for (int x = 0; x <= 16; x++)
            {
                Vector2 p = new(-1 + x / 8f, -1 + y / 8f);
                Assert.True(data.TryHeight(p, out float h));
                Assert.Equal(p.X, h);
            }
    }

    [Fact]
    public void Grid_queries_match_a_source_ordered_scalar_scan_with_inclusive_bounds()
    {
        ReferenceSupport[] supports = [GridSupport([-2, -1, 0, 1, 2]),
            GridSupport([-1, -MathF.ScaleB(1, -25), 1]), RoundedEdgeSupport(2), RoundedEdgeSupport(3)];
        var random = new Random(1602);
        foreach (var support in supports)
        {
            Assert.True(EachRowIsStrictlySortedAndDuplicateFree(support.CellOffsets, support.CellTriangles));
            for (int y = 0; y <= 32; y++)
                for (int x = 0; x <= 32; x++)
                    Check(new(support.GridMin.X + x / 32f * (support.GridMax.X - support.GridMin.X),
                        support.GridMin.Y + y / 32f * (support.GridMax.Y - support.GridMin.Y)));
            for (int i = 0; i < 512; i++)
                Check(new(support.GridMin.X + (float)(random.NextDouble() * 1.5 - .25) * (support.GridMax.X - support.GridMin.X),
                    support.GridMin.Y + (float)(random.NextDouble() * 1.5 - .25) * (support.GridMax.Y - support.GridMin.Y)));
            foreach (var triangle in support.Triangles)
            {
                Vector3 a = support.Positions[triangle.A], b = support.Positions[triangle.B], c = support.Positions[triangle.C];
                foreach (float x in BoundsNeighbours(a.X, b.X, c.X))
                    foreach (float y in BoundsNeighbours(a.Y, b.Y, c.Y)) Check(new(x, y));
            }
            Check(new(1, -MathF.ScaleB(1, -24) / (1 - MathF.ScaleB(1, -24))));
            Check(new(float.NaN, 0));
            Check(new(0, float.PositiveInfinity));
            Assert.True(support.CellTriangles.Length < support.Triangles.Length * 256);
            void Check(Vector2 point)
            {
                bool expected = ScanBounds(support, point, out float expectedHeight, out int expectedSource);
                Assert.Equal(expected, support.TryHeight(point, out float actualHeight, out int actualSource));
                Assert.Equal(expectedSource, actualSource);
                Assert.Equal(BitConverter.SingleToInt32Bits(expectedHeight), BitConverter.SingleToInt32Bits(actualHeight));
            }
        }
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(3f)]
    public void Rounded_edge_acceptance_outside_triangle_bounds_takes_the_miss_path(float right)
    {
        var support = RoundedEdgeSupport(right);
        var triangle = support.Triangles[0];
        Vector3 a = support.Positions[triangle.A], b = support.Positions[triangle.B], c = support.Positions[triangle.C];
        Vector2 point = new(1, -MathF.ScaleB(1, -24) / (1 - MathF.ScaleB(1, -24)));
        Vector3 p = new(point, 0);
        float RoundedEdge(Vector3 x, Vector3 y, Vector3 z) =>
            ((y.X - x.X) * (z.Y - x.Y)) - ((y.Y - x.Y) * (z.X - x.X));
        Assert.True(RoundedEdge(a, b, c) > 0);
        Assert.Equal(0f, RoundedEdge(b, c, p));
        Assert.True(RoundedEdge(c, a, p) > 0);
        Assert.Equal(0f, RoundedEdge(a, b, p));
        Assert.True(point.X > b.X);
        Assert.False(support.Support.TryGetHeight(point, out _));
        Assert.False(support.TryHeight(point, out float height, out int source));
        Assert.Equal(0f, height);
        Assert.Equal(-1, source);
    }

    [Fact]
    public void Triangle_bounds_corners_and_inside_float_neighbours_find_the_binned_candidate()
    {
        var rounded = GridSupport([-1, -MathF.ScaleB(1, -25), 1]);
        // Subtraction rounds this interior corner onto cell (8,8). Binary64 addressing gives (7,7).
        Assert.Contains(0, rounded.CellTriangles[rounded.CellOffsets[136]..rounded.CellOffsets[137]]);
        foreach (var support in new[] { rounded, GridSupport([-2, -1, 0, 1, 2]), RoundedEdgeSupport(2), RoundedEdgeSupport(3) })
            for (int t = 0; t < support.Triangles.Length; t++)
            {
                var triangle = support.Triangles[t];
                Vector3 a = support.Positions[triangle.A], b = support.Positions[triangle.B], c = support.Positions[triangle.C];
                foreach (float x in BoundsNeighbours(a.X, b.X, c.X))
                    foreach (float y in BoundsNeighbours(a.Y, b.Y, c.Y))
                    {
                        if (OutsideBounds(x, a.X, b.X, c.X) || OutsideBounds(y, a.Y, b.Y, c.Y)) continue;
                        int cell = Cell(y, support.GridMin.Y, support.GridMax.Y) * 16 + Cell(x, support.GridMin.X, support.GridMax.X);
                        Assert.Contains(t, support.CellTriangles[support.CellOffsets[cell]..support.CellOffsets[cell + 1]]);
                    }
            }
        static int Cell(float value, float min, float max) => Math.Clamp((int)(((value - min) / (max - min)) * 16), 0, 15);
    }

    [Fact]
    public void Reference_frames_do_not_call_exact_uncertainty_admission()
    {
        var (topology, _, model, commands, exact) = CoverageCertificationTests.Scenario("unabsorbed-error");
        Assert.False(CoverageFrames.TryPrepare(topology, model, commands, exact, 1, out _));
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out var failure), failure.ToString());
        Assert.True(scratch.Frames[0].CorrectionRate > 0);
    }

    [Fact]
    public void Nonfinite_float_slope_denominator_refuses_the_reference_frame()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var solid = CoverageCommands.Gabled(0, 0, 1, 1e20f, 1e20f, outward: false);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-overflow-slope", solid.Positions, solid.Indices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Assert.True(support.InwardOfClosedSolid);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        Assert.False(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, new(topology), out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
    }

    [Theory]
    [InlineData("unknown-region")]
    [InlineData("stale-identity")]
    [InlineData("stale-frame")]
    [InlineData("overlap")]
    [InlineData("mirror")]
    [InlineData("singular")]
    [InlineData("projective")]
    [InlineData("nonfinite")]
    [InlineData("support-gap")]
    [InlineData("capacity")]
    public void Reference_and_exact_frames_share_admission_refusals(string scenario)
    {
        var (topology, _, model, commands, exact) = CoverageCertificationTests.Scenario(scenario);
        Assert.False(CoverageFrames.TryPrepare(topology, model, commands, exact, 1, out var expected));
        Assert.False(CoverageReferenceFrames.TryPrepare(topology, model, commands, new(topology, exact.CommandCapacity), out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Reference_frames_reserve_clearance_and_retain_input_indices_in_id_order()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = Square();
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity).Reverse().ToArray();
        var scratch = new CoverageReferenceScratch(topology);
        // Square is open, so use the closed fixture support for admission and numerical frame assertions.
        var closed = CoverageCommands.Plate(fixture);
        for (int i = 0; i < commands.Length; i++) commands[i] = commands[i] with { Support = closed };
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, out var failure), failure.ToString());
        Assert.Equal(1, scratch.Frames[0].Command);
        Assert.Equal(0, scratch.Frames[1].Command);
        Assert.Equal(new[] { 0, 1 }, scratch.RegionCommands);
        Assert.Equal(2, scratch.ActiveCommands);
        Assert.Equal(Vector3.UnitZ, scratch.Frames[0].Recess);
        Assert.Equal(1f, scratch.Frames[0].ZScale);
        Assert.Equal(.02f, scratch.Frames[0].CapMargin);
        float reserved = .02f + .02f * .25f;
        float expected = reserved * MathF.Sqrt(1 + .08f * .08f);
        Assert.Equal(expected, scratch.Frames[0].ClearanceCoverage);
        Assert.Equal(expected, scratch.Frames[0].ActualClearanceCoverage);
        Assert.Equal(1f, scratch.Frames[0].CorrectionRate);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, [], scratch, out _));
        Assert.Equal(0, scratch.ActiveCommands);
        Assert.All(scratch.RegionCommands, r => Assert.Equal(-1, r));
    }

    [Fact]
    public void Reference_frames_compose_in_binary64_before_one_coefficient_rounding()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-projected-plate", fixture.RigidPositions, fixture.RigidIndices),
            [new(0, 0, 0, fixture.SupportIndices.Length / 3)], Matrix4x4.CreateScale(3));
        var equipment = new Matrix4x4(2, 1, 0, 0, 0, 4, 0, 0, 0, 0, 8, 0, 1, 2, 3, 1);
        var command = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity)[0] with
        {
            EquipmentWorld = equipment,
            CoverageWorld = support.CoverageLocal * equipment,
        };
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.CreateScale(5), [command], scratch, out var failure), failure.ToString());
        var frame = scratch.Frames[0];
        // inverse(C).M11 is exactly 1/6, but the actual sheet carries the uploaded float 1/3.
        Assert.Equal((float)(5d / 6), frame.HostToCoverage.M11);
        Assert.Equal((float)(2.5d * (double)(1f / 3)), frame.HostToActualSheet.M11);
        Assert.NotEqual(frame.HostToCoverage.M11, frame.HostToActualSheet.M11);
        Assert.Equal((float)(-5d / 24), frame.HostToCoverage.M12);
        Assert.Equal((float)(-1d / 6), frame.HostToCoverage.M41);
        Assert.Equal(new Vector3(0, 0, 4.8f), frame.Recess);
        Assert.Equal(24f, frame.ZScale);
        Assert.Equal(.02f * (1f / 24), frame.CapMargin);
        Assert.Equal(Matrix4x4.CreateScale(5), frame.ActorLinear);
    }

    static CoverageSupportSurface Square() => CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-square",
        [new(-1, -1, -1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, -1)], [0, 1, 2, 0, 2, 3]), [new(0, 0, 0, 2)], Matrix4x4.Identity);

    static ReferenceSupport GridSupport(float[] coordinates)
    {
        int width = coordinates.Length;
        Vector3[] positions = Enumerable.Range(0, width * width).Select(i =>
            new Vector3(coordinates[i % width], coordinates[i / width], coordinates[i % width])).ToArray();
        uint[] indices = Enumerable.Range(0, (width - 1) * (width - 1)).SelectMany(i =>
        {
            uint a = (uint)(i / (width - 1) * width + i % (width - 1)), step = (uint)width;
            return new[] { a, a + 1, a + step + 1, a, a + step + 1, a + step };
        }).ToArray();
        return CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-grid", positions, indices),
            [new(0, 0, 0, indices.Length / 3)], Matrix4x4.Identity).ReferenceData;
    }

    static ReferenceSupport RoundedEdgeSupport(float right) => CoverageSupportSurface.Compile(
        CoverageCommands.Solid("reference-rounded-edge",
            [new(0, 1 - MathF.ScaleB(1, -13), 0), new(1 - MathF.ScaleB(1, -24), 0, 0),
                new(0, 1, 0), new(right, -MathF.ScaleB(1, -15), 0)], [0, 1, 2, 1, 3, 2]),
        [new(0, 0, 0, 2)], Matrix4x4.Identity).ReferenceData;

    static float[] BoundsNeighbours(float a, float b, float c)
    {
        float min = MathF.Min(a, MathF.Min(b, c)), max = MathF.Max(a, MathF.Max(b, c));
        return [MathF.BitDecrement(min), min, MathF.BitIncrement(min), MathF.BitDecrement(max), max, MathF.BitIncrement(max)];
    }

    static bool OutsideBounds(float p, float a, float b, float c) => p < a && p < b && p < c || p > a && p > b && p > c;

    static bool ScanBounds(ReferenceSupport support, Vector2 point, out float height, out int source)
    {
        height = 0;
        source = -1;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        for (int i = 0; i < support.Triangles.Length; i++)
        {
            var triangle = support.Triangles[i];
            Vector3 a = support.Positions[triangle.A], b = support.Positions[triangle.B], c = support.Positions[triangle.C];
            if (OutsideBounds(point.X, a.X, b.X, c.X) || OutsideBounds(point.Y, a.Y, b.Y, c.Y)) continue;
            Vector3 p = new(point, 0);
            CoverageAffinePoint Exact(Vector3 v) => CoverageAffinePoint.FromDouble(v.X, v.Y, v.Z);
            var area = CoverageGeometry.Edge(Exact(a), Exact(b), Exact(c));
            var u = CoverageGeometry.Edge(Exact(b), Exact(c), Exact(p));
            var v = CoverageGeometry.Edge(Exact(c), Exact(a), Exact(p));
            var w = CoverageGeometry.Edge(Exact(a), Exact(b), Exact(p));
            if (!area.IsFinite || area.Sign == 0 || !u.IsFinite || !v.IsFinite || !w.IsFinite) return false;
            if (u.Sign * area.Sign < 0 || v.Sign * area.Sign < 0 || w.Sign * area.Sign < 0) continue;
            height = M.Height(a, b, c, point);
            if (!float.IsFinite(height)) return false;
            source = support.SourceIndices[i];
            return true;
        }
        return false;
    }

    static bool EachRowIsStrictlySortedAndDuplicateFree(int[] offsets, int[] values)
    {
        for (int row = 0; row + 1 < offsets.Length; row++)
            for (int i = offsets[row] + 1; i < offsets[row + 1]; i++)
                if (values[i - 1] >= values[i]) return false;
        return true;
    }
}
