using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
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
        Assert.Equal(1f, M.Dot3(new CoverageReferenceArithmetic(), new(MathF.ScaleB(1, 25), -MathF.ScaleB(1, 25), 1), Vector3.One));
        float step = MathF.ScaleB(1, -23);
        Assert.Equal(0f, M.Dot3(new CoverageReferenceArithmetic(), new(1 + step, -1, 0), new(1 - step, 1, 0)));
        Assert.Equal(Vector3.Zero, M.Cross(new CoverageReferenceArithmetic(), new(0, 1 + step, 1), new(0, 1, 1 - step)));
        Assert.Equal(11f, M.Dot2(new CoverageReferenceArithmetic(), new(1, 2), new(3, 4)));
        Assert.Equal(new Vector3(-3, 6, -3), M.Cross(new CoverageReferenceArithmetic(), new(1, 2, 3), new(4, 5, 6)));
        var matrix = new Matrix4x4(1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 1);
        Assert.Equal(new Vector3(30, 36, 42), M.Linear(new CoverageReferenceArithmetic(), new(1, 2, 3), matrix));
        Assert.Equal(new Vector3(40, 47, 54), M.Point(new CoverageReferenceArithmetic(), new(1, 2, 3), matrix));
        var cancellationMatrix = new Matrix4x4(1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1);
        Vector3 cancellation = new(MathF.ScaleB(1, 25), -MathF.ScaleB(1, 25), 1);
        Assert.Equal(Vector3.One, M.Linear(new CoverageReferenceArithmetic(), cancellation, cancellationMatrix));
        Assert.Equal(new Vector3(2), M.Point(new CoverageReferenceArithmetic(), cancellation, cancellationMatrix));
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
    public void Skinny_triangle_heights_differ_by_half_between_adjacent_queries()
    {
        Vector3 a = Vector3.Zero, b = new(1, 1, 0), c = new(1, 1 + MathF.ScaleB(1, -23), 1);
        Assert.Equal(0f, M.Height(new CoverageReferenceArithmetic(), a, b, c, new(.5f, .5f)));
        Assert.Equal(.5f, M.Height(new CoverageReferenceArithmetic(), a, b, c, new(.5f, .5f + MathF.ScaleB(1, -24))));
        Assert.Equal(MathF.ScaleB(1, -23), CoverageReferencePredicates.Edge(a, b, c).Value);
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
            Assert.True(data.TryHeight(new CoverageReferenceArithmetic(), p, out float height, out int sourceTriangle));
            Assert.Equal(p.X, height);
            Assert.Equal(0, sourceTriangle);
        }
        // Edge zero is the bottom rim. The square centre ties all four edges, so bottom wins.
        Assert.Equal(0, data.BoundarySourceVertices[0]);
        Assert.True(data.TryBoundary(new CoverageReferenceArithmetic(), Vector2.Zero, out float boundaryHeight, out float distance));
        Assert.Equal(0f, boundaryHeight);
        Assert.Equal(1f, distance);
        Assert.True(data.TryBoundary(new CoverageReferenceArithmetic(), new(2, 0), out _, out distance));
        Assert.Equal(1f, distance);
        float collarWidth = 1;
        float spatial = Math.Clamp(1 - distance / collarWidth, 0, 1);
        Assert.Equal(0f, spatial * spatial * (3 - 2 * spatial));
        Assert.False(data.TryHeight(new CoverageReferenceArithmetic(), new(1.0001f, 0), out _));
        Assert.False(data.TryHeight(new CoverageReferenceArithmetic(), new(float.NaN, 0), out _));
        Assert.False(data.TryBoundary(new CoverageReferenceArithmetic(), new(float.MaxValue, float.MaxValue), out _, out _));
        // The outer maxima are cell 15, and every cell retains original source order.
        Assert.Equal(257, data.CellOffsets.Length);
        Assert.True(EachRowIsStrictlySortedAndDuplicateFree(data.CellOffsets, data.CellTriangles));
        Assert.Contains(0, data.CellTriangles[data.CellOffsets[255]..data.CellOffsets[256]]);
        for (int y = 0; y <= 16; y++)
            for (int x = 0; x <= 16; x++)
            {
                Vector2 p = new(-1 + x / 8f, -1 + y / 8f);
                Assert.True(data.TryHeight(new CoverageReferenceArithmetic(), p, out float h));
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
                Assert.Equal(expected, support.TryHeight(new CoverageReferenceArithmetic(), point, out float actualHeight, out int actualSource));
                Assert.Equal(expectedSource, actualSource);
                Assert.Equal(BitConverter.SingleToInt32Bits(expectedHeight), BitConverter.SingleToInt32Bits(actualHeight));
            }
        }
    }

    [Fact]
    public void Tiny_grid_query_coordinates_are_flushed_before_source_order_selection()
    {
        var support = GridSupport([0f, 1f]);
        Vector2 point = new(-float.Epsilon, 0);
        Assert.False(support.Support.TryGetHeight(point, out _));
        var f = new CoverageReferenceArithmetic();
        Assert.True(support.TryHeight(f, point, out float height, out int source));
        Assert.Equal(0, source);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(height));
        Assert.True(f.EventCount > 0);
        Assert.Equal(CoverageExactnessOperation.FiniteTest, f.FirstSite.Operation);
        Assert.Equal(0x80000001u, f.FirstSite.ValueBits);
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
        Assert.False(support.TryHeight(new CoverageReferenceArithmetic(), point, out float height, out int source));
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
        point = new(FlushInput(point.X), FlushInput(point.Y));
        height = 0;
        source = -1;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        for (int i = 0; i < support.Triangles.Length; i++)
        {
            var triangle = support.Triangles[i];
            Vector3 a = Input(support.Positions[triangle.A]), b = Input(support.Positions[triangle.B]), c = Input(support.Positions[triangle.C]);
            if (OutsideBounds(point.X, a.X, b.X, c.X) || OutsideBounds(point.Y, a.Y, b.Y, c.Y)) continue;
            Vector3 p = new(point, 0);
            CoverageAffinePoint Exact(Vector3 v) => CoverageAffinePoint.FromDouble(v.X, v.Y, v.Z);
            var area = CoverageGeometry.Edge(Exact(a), Exact(b), Exact(c));
            var u = CoverageGeometry.Edge(Exact(b), Exact(c), Exact(p));
            var v = CoverageGeometry.Edge(Exact(c), Exact(a), Exact(p));
            var w = CoverageGeometry.Edge(Exact(a), Exact(b), Exact(p));
            if (!area.IsFinite || area.Sign == 0 || !u.IsFinite || !v.IsFinite || !w.IsFinite) return false;
            if (u.Sign * area.Sign < 0 || v.Sign * area.Sign < 0 || w.Sign * area.Sign < 0) continue;
            height = M.Height(new CoverageReferenceArithmetic(), a, b, c, point);
            if (!float.IsFinite(height)) return false;
            source = support.SourceIndices[i];
            return true;
        }
        return false;
    }

    // Independent B38 upload rule for the brute-force candidate oracle.
    static float FlushInput(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        return (bits & 0x7fffffffu) <= 0x00800000u ? BitConverter.UInt32BitsToSingle(bits & 0x80000000u) : value;
    }

    static Vector3 Input(Vector3 value) => new(FlushInput(value.X), FlushInput(value.Y), FlushInput(value.Z));

    static bool EachRowIsStrictlySortedAndDuplicateFree(int[] offsets, int[] values)
    {
        for (int row = 0; row + 1 < offsets.Length; row++)
            for (int i = offsets[row] + 1; i < offsets[row + 1]; i++)
                if (values[i - 1] >= values[i]) return false;
        return true;
    }
}
