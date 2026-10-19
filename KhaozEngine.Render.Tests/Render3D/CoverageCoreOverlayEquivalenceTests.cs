using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Render3D.CoverageFaceOverlayTests;

namespace KhaozEngine.Tests.Render3D;

// Core face correction over the hoisted host area, cached support areas and deduplicated overlay, checked face by face
// against the frozen pair loop on the faces the real evaluator reaches.
[Collection("AllocSensitive")]
public sealed class CoverageCoreOverlayEquivalenceTests
{
    [Fact]
    public void Cached_support_areas_equal_the_recomputed_inward_edge()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        foreach (var support in new[] { CoverageCommands.Plate(fixture), Framed(fixture), Grid(), Grid(float.Epsilon) })
        {
            Assert.Equal(support.InwardSourceIndices.Length, support.InwardTriangles.Count);
            for (int index = 0; index < support.InwardTriangles.Count; index++)
            {
                CoverageSourceTriangle s = support.InwardTriangles[index];
                Assert.Equal(support.Source.Triangles[support.InwardSourceIndices[index]], s);
                var expected = CoverageGeometry.Edge(support.ProjectedPositions[s.A], support.ProjectedPositions[s.B], support.ProjectedPositions[s.C]);
                var cached = support.InwardArea(index);
                Assert.Equal(expected.Sign, cached.Sign);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Estimate), BitConverter.DoubleToInt64Bits(cached.Estimate));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Bounds.Min), BitConverter.DoubleToInt64Bits(cached.Bounds.Min));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Bounds.Max), BitConverter.DoubleToInt64Bits(cached.Bounds.Max));
            }
        }
    }

    [Fact]
    public void Support_maximum_hoists_the_host_area_and_keeps_a_partial_refusal()
    {
        var grid = Grid();
        Assert.Equal(64, grid.InwardTriangles.Count);
        CoverageAffinePoint[] host = [P(-.9, -.9, .3), P(.4, -.9, .1), P(-.9, Math.ScaleB(1, -830), .2)];
        var expected = CoverageFaceOverlayReference.TrySupportMaximum(host, grid);
        Assert.True(expected.Succeeded);
        Assert.True(double.IsFinite(expected.Maximum));
        Assert.Equal(56 * 64, expected.EdgeEvaluations);
        AssertSupport(host, grid, expected, 1 + 18 * 64);

        // The first inward triangle on the float.Epsilon column underflows a mixed product with the tiny host Y after
        // earlier overlapping triangles produced a finite maximum. Every earlier pair still runs, in support order.
        var column = Grid(float.Epsilon);
        expected = CoverageFaceOverlayReference.TrySupportMaximum(host, column);
        Assert.False(expected.Succeeded);
        Assert.True(expected.FailedIndex > 0, expected.ToString());
        Assert.True(double.IsFinite(expected.Maximum), expected.ToString());
        int partial = expected.EdgeEvaluations - 56 * expected.FailedIndex - 2;
        Assert.InRange(partial, 1, 18);
        AssertSupport(host, column, expected, 1 + 18 * expected.FailedIndex + partial);
    }

    // Pyramid and 64 triangle supports, welded material seam groups, two commands, admitted affine frames and a
    // recession refusal raised after correction. Every reached core face matches the frozen pair loop, and the witness does not perturb the result.
    [Theory]
    [InlineData("pyramid", false, false)]
    [InlineData("pyramid", true, false)]
    [InlineData("framed", true, false)]
    [InlineData("framed", true, true)]
    [InlineData("grid", true, false)]
    [InlineData("grid", true, true)]
    [InlineData("two-commands", true, false)]
    [InlineData("recession", true, false)]
    public void Reference_core_faces_preserve_affine_and_correction_only_budget_cases(string scene, bool seams, bool placed)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: seams, level: 3, secondRegion: scene == "two-commands");
        if (scene == "framed")
            fixture = fixture with
            {
                Commands = [fixture.Commands[0] with { CoverageLocal = Matrix4x4.CreateRotationZ(.11f) * Matrix4x4.CreateTranslation(.02f, -.01f, .003f) }],
            };
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = scene switch { "framed" => Framed(fixture), "grid" => Grid(), _ => CoverageCommands.Plate(fixture) };
        var actor = placed ? Matrix4x4.CreateRotationY(.23f) * Matrix4x4.CreateRotationZ(.31f) * Matrix4x4.CreateTranslation(2, -1, .5f)
            : Matrix4x4.Identity;
        var commands = CoverageCommands.Poses(fixture, topology, support, actor);
        if (scene == "recession")
        {
            // Between the pointwise and corrected displacement, as in the existing budget refusal.
            var before = CoverageCommands.Surface(topology, fixture.Posed);
            var pointwise = CoverageCoreCorrectionTests.Pointwise(topology, before, commands);
            var full = CoverageCoreCorrectionTests.Evaluate(fixture, topology, commands);
            float pointBudget = Enumerable.Range(0, before.Length).Max(i => Vector3.Distance(before[i].Position, pointwise[i].Position));
            float fullBudget = Enumerable.Range(0, before.Length).Max(i => Vector3.Distance(before[i].Position, full[i].Position));
            commands = [commands[0] with { MaxWorldRecession = (pointBudget + fullBudget) * .5f, TransitionWeight = 0 }];
        }

        var scratch = new CoverageScratch(topology);
        var final = new ModelVertex[topology.VertexCount];
        bool result = CoverageGeometryTestRun.TryEvaluate(topology, fixture.Posed, actor, commands, scratch, final,
            out var certificate, out var failure);
        Assert.Equal(scene != "recession", result);
        Assert.Contains(CoverageGeometryTestRun.Reference(scratch).FaceCorrections, extra => extra > 0);
        if (scene == "recession")
        {
            Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
            Assert.Equal(default, certificate);
        }
        else
        {
            var before = CoverageCommands.Surface(topology, fixture.Posed);
            Assert.True(CoverageCertification.ValidateGeometry(topology, before, final, actor, commands, scratch, out failure), failure.ToString());
        }
    }

    static void AssertSupport(CoverageAffinePoint[] host, CoverageSupportSurface support,
        CoverageFaceOverlayReference.SupportResult expected, int edges)
    {
        var sheet = new CoverageAffinePoint[3];
        int executed = 0;
        bool succeeded = CoverageFaceOverlay.TrySupportMaximum(host, support, sheet, ref executed, out double maximum);
        Assert.Equal(expected.Succeeded, succeeded);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Maximum), BitConverter.DoubleToInt64Bits(maximum));
        Assert.True(edges == executed, $"{executed} Edge evaluations, expected {edges} ({expected.EdgeEvaluations} before)");
    }

    static CoverageSupportSurface Framed(CoverageFixture fixture) => CoverageSupportSurface.Compile(
        CoverageCommands.Solid("framed-plate", fixture.RigidPositions, fixture.RigidIndices), [new(0, 0, 0, 4)],
        Matrix4x4.CreateScale(.83f, 1.17f, .91f) * Matrix4x4.CreateRotationZ(.19f) * Matrix4x4.CreateTranslation(.03f, -.02f, .01f));

    static CoverageSupportSurface Grid(float zeroColumn = 0)
    {
        var (positions, indices) = GridShell(8, 4, zeroColumn);
        return CoverageSupportSurface.Compile(CoverageCommands.Solid("synthetic-grid", positions, indices), [new(0, 0, 0, 64)], Matrix4x4.Identity);
    }

    /// <summary>A closed thick shell whose inward sheet is a columns by rows grid over [-1,1] squared, dipping toward the
    /// centre like the pyramid plate, with a flat back. Support triangles come first, row by row from (-1,-1). The x = 0
    /// column may be replaced by another value.</summary>
    internal static (Vector3[] Positions, uint[] Indices) GridShell(int columns, int rows, float zeroColumn = 0)
    {
        int width = columns + 1, count = width * (rows + 1);
        var positions = new Vector3[count * 2];
        for (int j = 0; j <= rows; j++)
            for (int i = 0; i <= columns; i++)
            {
                float x = -1 + 2f * i / columns, y = -1 + 2f * j / rows;
                if (x == 0) x = zeroColumn;
                positions[j * width + i] = new(x, y, .1f - .08f * (1 - Math.Abs(x)) * (1 - Math.Abs(y)));
                positions[count + j * width + i] = new(x, y, .2f);
            }
        var indices = new List<uint>();
        void Add(int a, int b, int c) => indices.AddRange([(uint)a, (uint)b, (uint)c]);
        int F(int i, int j) => j * width + i;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++) { Add(F(i, j), F(i + 1, j), F(i + 1, j + 1)); Add(F(i, j), F(i + 1, j + 1), F(i, j + 1)); }
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
            {
                Add(count + F(i, j), count + F(i + 1, j + 1), count + F(i + 1, j));
                Add(count + F(i, j), count + F(i, j + 1), count + F(i + 1, j + 1));
            }
        var rim = new List<int>();
        for (int i = 0; i < columns; i++) rim.Add(F(i, 0));
        for (int j = 0; j < rows; j++) rim.Add(F(columns, j));
        for (int i = columns; i > 0; i--) rim.Add(F(i, rows));
        for (int j = rows; j > 0; j--) rim.Add(F(0, j));
        for (int k = 0; k < rim.Count; k++)
        {
            int a = rim[k], b = rim[(k + 1) % rim.Count];
            Add(b, a, a + count); Add(b, a + count, b + count);
        }
        return (positions, indices.ToArray());
    }
}
