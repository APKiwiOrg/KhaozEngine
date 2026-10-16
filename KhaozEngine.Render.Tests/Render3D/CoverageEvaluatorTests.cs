using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageEvaluatorTests
{
    [Fact]
    public void Actual_sheet_clears_the_core_and_preserves_the_host()
    {
        // Level 1 keeps the bulge apex as the only receding core point under the pyramid sheet.
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = CoverageCommands.Plate(fixture);
        Assert.True(support.InwardOfClosedSolid);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var baseline = CoverageCommands.Witness(fixture, topology, before, before);
        Assert.True(baseline.BreakthroughArea > 0);

        var final = new ModelVertex[topology.VertexCount];
        bool accepted = CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, new CoverageScratch(topology),
            final, out var certificate, out var failure);
        var result = CoverageCommands.Witness(fixture, topology, before, final);
        float requiredClearance = fixture.Commands[0].Clearance;
        double certifiedErrorBound = certificate.WorldErrorBound;
        Assert.True(accepted, failure.ToString());
        Assert.True(result.CoreMovedArea > 0);
        Assert.Equal(0, result.ProtectedPositionChanges);
        Assert.Equal(0, result.OpenBoundaryEdges);
        Assert.True(result.MinimumCoreClearance >= requiredClearance - certifiedErrorBound);
        Assert.Equal(0, result.BreakthroughArea);
        Assert.Equal(0, result.ClearanceViolations); Assert.Equal(0, result.EquipmentIntersections);
        Assert.Equal(0, result.NonadjacentIntersections); Assert.Equal(0, result.Folds);
        Assert.Equal(0, result.UnmatchedInteriorEdges); Assert.Equal(0, result.WeldedPositionChanges);
        Assert.True(result.CoreRecession > 0);
        Assert.True(certificate.FullCoreClearance);
        Assert.True(certificate.MinimumCoreClearance >= requiredClearance);
        Assert.True(certifiedErrorBound > 0 && certifiedErrorBound <= requiredClearance * .25);
        Assert.True(certificate.DisplacedVertices > 0);
        for (int i = 0; i < final.Length; i++)
        {
            // Zero-work points, including every protected point, are exact copies.
            if (final[i].Position == before[i].Position) Assert.Equal(before[i], final[i]);
            else
            {
                Assert.True(topology.Vertices[i].Strength > 0);
                Assert.True(final[i].Position.Z < before[i].Position.Z);
                Assert.True(Math.Abs(final[i].Position.X - before[i].Position.X) < 1e-6f && Math.Abs(final[i].Position.Y - before[i].Position.Y) < 1e-6f);
            }
        }
        Assert.True(CoverageCertification.Validate(topology, before, final, Matrix4x4.Identity, commands, new CoverageScratch(topology), out var check), check.ToString());

        // Removal-only control: hiding the covered core opens a visible gap under the floating rim.
        var removal = Enumerable.Range(0, topology.TriangleCount).Where(t => topology.Triangles[t].Role != CoverageTriangleRole.Core || topology.Triangles[t].RegionIndex < 0)
            .SelectMany(t => new[] { topology.Indices[t * 3], topology.Indices[t * 3 + 1], topology.Indices[t * 3 + 2] }).ToArray();
        Assert.True(CoverageCommands.Witness(fixture, topology, before, before, removal).OpenBoundaryEdges > 0);

        // Global minimum-plane control: the lowest sheet point minus clearance recedes more than the actual triangles need.
        float plane = fixture.SupportIndices.Min(i => fixture.RigidPositions[i].Z) - requiredClearance;
        var planar = before.ToArray();
        for (int i = 0; i < planar.Length; i++)
            if (topology.Vertices[i].Strength > 0 && planar[i].Position.Z > plane) planar[i].Position.Z = plane;
        Assert.True(CoverageCommands.Witness(fixture, topology, before, planar).CoreRecession > result.CoreRecession);
        Assert.Contains(Enumerable.Range(0, final.Length), i => final[i].Position != before[i].Position && final[i].Position.Z > plane);
    }

    [Fact]
    public void Closed_host_front_conforms_with_its_protected_rear_exact()
    {
        var shell = CoverageCommands.Gabled(.42f, .05f, .7f, 1.1f, 1.5f, outward: false);
        var fixture = CoverageFixture.ClosedEllipsoid() with { RigidPositions = shell.Positions, RigidIndices = shell.Indices, SupportIndices = shell.Indices[..12] };
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = CoverageCommands.Plate(fixture);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        Assert.True(CoverageCommands.Witness(fixture, topology, before, before).BreakthroughArea > 0);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, new CoverageScratch(topology), final,
            out var certificate, out var failure), failure.ToString());
        var result = CoverageCommands.Witness(fixture, topology, before, final);
        Assert.True(certificate.FullCoreClearance); Assert.True(result.CoreMovedArea > 0);
        Assert.Equal(0, result.BreakthroughArea); Assert.Equal(0, result.ClearanceViolations);
        Assert.Equal(0, result.EquipmentIntersections); Assert.Equal(0, result.NonadjacentIntersections);
        Assert.Equal(0, result.Folds); Assert.Equal(0, result.UnmatchedInteriorEdges); Assert.Equal(0, result.OpenBoundaryEdges);
        Assert.Equal(0, result.WeldedPositionChanges); Assert.Equal(0, result.ProtectedPositionChanges);
        Assert.True(result.MinimumCoreClearance >= fixture.Commands[0].Clearance - certificate.WorldErrorBound);
        int rear = 0;
        for (int i = 0; i < final.Length; i++)
            if (before[i].Position.Z <= 0) { Assert.Equal(before[i], final[i]); rear++; }
        Assert.True(rear > final.Length / 3);
    }

    [Fact]
    public void Positive_scale_and_shear_match_direct_world_geometry()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = CoverageCommands.Plate(fixture);
        var actor = new Matrix4x4(1.3f, 0, 0, 0, .25f, .8f, 0, 0, 0, .1f, 1.1f, 0, 2, -1, .5f, 1);
        var commands = CoverageCommands.Poses(fixture, topology, support, actor);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, actor, commands, new CoverageScratch(topology), final,
            out var certificate, out var failure), failure.ToString());
        var world = CoverageCommands.World(fixture, actor);
        var direct = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, world.Posed, Matrix4x4.Identity, commands, new CoverageScratch(topology), direct,
            out _, out failure), failure.ToString());
        var placed = CoverageCommands.Place(final, actor);
        for (int i = 0; i < placed.Length; i++) Assert.True(Vector3.Distance(placed[i].Position, direct[i].Position) < 2e-5f);
        var before = CoverageCommands.Place(CoverageCommands.Surface(topology, fixture.Posed), actor);
        var result = CoverageCommands.Witness(world, topology, before, placed);
        Assert.True(result.CoreMovedArea > 0); Assert.Equal(0, result.ClearanceViolations);
        Assert.Equal(0, result.EquipmentIntersections); Assert.Equal(0, result.Folds); Assert.Equal(0, result.ProtectedPositionChanges);
        Assert.True(result.MinimumCoreClearance >= fixture.Commands[0].Clearance - certificate.WorldErrorBound);
    }

    [Fact]
    public void Disjoint_regions_and_actors_are_independent_of_submission_order()
    {
        // The second patch poses lower than the first, so its own plate sits lower to give both regions real work.
        var fixture = CoverageFixture.CurvedPanel(level: 1, secondRegion: true);
        fixture = fixture with { Commands = [fixture.Commands[0], fixture.Commands[1] with { CoverageLocal = Matrix4x4.CreateTranslation(3.8f, 0, -.2f) }] };
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = CoverageCommands.Plate(fixture);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        var scratch = new CoverageScratch(topology);
        var forward = new ModelVertex[topology.VertexCount]; var reverse = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, scratch, forward, out var failure), failure.ToString());
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands.Reverse().ToArray(), scratch, reverse, out failure), failure.ToString());
        Assert.Equal(forward, reverse);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        foreach (int region in new[] { 0, 1 })
            Assert.Contains(Enumerable.Range(0, before.Length), i => topology.Vertices[i].RegionIndex == region && forward[i].Position != before[i].Position);
        var result = CoverageCommands.Witness(fixture, topology, before, forward);
        Assert.Equal(0, result.ClearanceViolations); Assert.Equal(0, result.EquipmentIntersections); Assert.Equal(0, result.ProtectedPositionChanges);

        // A second actor shares the compiled topology and support but owns its scratch and pose.
        var other = fixture.SecondActor();
        var otherFinal = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, other.Posed, Matrix4x4.Identity, CoverageCommands.Poses(other, topology, support, Matrix4x4.Identity),
            new CoverageScratch(topology), otherFinal, out failure), failure.ToString());
        var again = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, scratch, again, out failure));
        Assert.Equal(forward, again);
        for (int i = 0; i < forward.Length; i++)
            Assert.True(Vector3.Distance(forward[i].Position + new Vector3(3, 0, 0), otherFinal[i].Position) < 1e-5f);
    }

    [Fact]
    public void Transition_below_full_weight_never_claims_full_core_clearance()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = CoverageCommands.Plate(fixture);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var full = new ModelVertex[topology.VertexCount]; var half = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity,
            CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity), new CoverageScratch(topology), full, out var complete, out var failure));
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity,
            CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity, .5f), new CoverageScratch(topology), half, out var partial, out failure), failure.ToString());
        Assert.True(complete.FullCoreClearance);
        Assert.False(partial.FullCoreClearance); Assert.True(double.IsNaN(partial.MinimumCoreClearance));
        Assert.True(partial.DisplacedVertices > 0);
        for (int i = 0; i < half.Length; i++)
        {
            float expected = before[i].Position.Z + (full[i].Position.Z - before[i].Position.Z) * .5f;
            Assert.True(Math.Abs(half[i].Position.Z - expected) < 1e-5f);
        }
        var result = CoverageCommands.Witness(fixture, topology, before, half);
        Assert.Equal(0, result.Folds); Assert.Equal(0, result.NonadjacentIntersections); Assert.Equal(0, result.ProtectedPositionChanges);
        Assert.Equal(0, result.UnmatchedInteriorEdges);
    }

    [Fact]
    public void Warm_evaluation_and_validation_allocate_nothing()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var scratch = new CoverageScratch(topology);
        var final = new ModelVertex[topology.VertexCount];
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, scratch, final, out var failure), failure.ToString());
        Assert.True(CoverageCertification.Validate(topology, before, final, Matrix4x4.Identity, commands, scratch, out failure), failure.ToString());
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool evaluated = CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, scratch, final, out _);
        bool validated = CoverageCertification.Validate(topology, before, final, Matrix4x4.Identity, commands, scratch, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(evaluated && validated);
        Assert.Equal(0, allocated);
    }
}

// Public synthetic command inputs shared by the evaluator and certification tests.
internal static class CoverageCommands
{
    internal static CoverageSourceTopology Solid(string identity, Vector3[] positions, uint[] indices) => new(identity, CoverageFixture.Hash, positions,
        Enumerable.Range(0, positions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)),
        Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0, (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])));

    internal static CoverageSupportSurface Plate(CoverageFixture fixture) => CoverageSupportSurface.Compile(
        Solid("synthetic-plate", fixture.RigidPositions, fixture.RigidIndices), [new(0, 0, 0, fixture.SupportIndices.Length / 3)], Matrix4x4.Identity);

    // The fixture places equipment relative to the posed host, so its world draw follows the actor model.
    internal static CoverageCommandPose[] Poses(CoverageFixture fixture, CoverageRefinement topology, CoverageSupportSurface support,
        Matrix4x4 actor, float transition = 1) => fixture.Commands.Select(c =>
        {
            Matrix4x4 equipment = c.CoverageLocal * actor;
            int region = fixture.Map.Regions.Select((r, i) => (r.Key, i)).Single(p => p.Key == c.Region).i;
            return new CoverageCommandPose(c.Id, c.EquipmentInstanceId, topology, region, support, equipment, support.CoverageLocal * equipment,
                c.MinZ, c.MaxZ, c.CollarWidth, c.Clearance, c.MaxWorldRecession, transition);
        }).ToArray();

    internal static ModelVertex[] Surface(CoverageRefinement topology, ModelVertex[] posed)
    {
        var surface = new ModelVertex[topology.VertexCount];
        topology.Interpolate(posed, surface);
        return surface;
    }

    internal static CoverageGeometryOracle.Result Witness(CoverageFixture fixture, CoverageRefinement topology, ModelVertex[] before,
        ModelVertex[] after, uint[]? indices = null) => CoverageGeometryOracle.Validate(before, after, fixture,
        indices ?? topology.Indices.ToArray(), topology.Vertices.Select(v => v.Strength == 0).ToArray());

    internal static ModelVertex[] Place(ModelVertex[] vertices, Matrix4x4 matrix) =>
        vertices.Select(v => { v.Position = Vector3.Transform(v.Position, matrix); return v; }).ToArray();

    // The same scene expressed directly in world space, for the oracle and for direct-world comparison.
    internal static CoverageFixture World(CoverageFixture fixture, Matrix4x4 actor) => fixture with
    {
        Posed = Place(fixture.Posed, actor),
        Commands = fixture.Commands.Select(c => c with { CoverageLocal = c.CoverageLocal * actor }).ToArray(),
    };

    /// <summary>A closed thick shell whose inward sheet is a concave gable h = ridge - drop |x| / halfX over the full
    /// XY footprint, with a flat back. Support triangles come first. Either winding sign describes the same solid.</summary>
    internal static (Vector3[] Positions, uint[] Indices) Gabled(float ridge, float drop, float back, float halfX, float halfY, bool outward)
    {
        var positions = new Vector3[12];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 3; i++)
            {
                float x = (i - 1) * halfX, y = (j * 2 - 1) * halfY;
                positions[i + 3 * j] = new(x, y, ridge - drop * Math.Abs(i - 1));
                positions[6 + i + 3 * j] = new(x, y, back);
            }
        var indices = new System.Collections.Generic.List<uint>();
        void Add(int a, int b, int c)
        {
            if (outward) indices.AddRange(new[] { (uint)a, (uint)c, (uint)b });
            else indices.AddRange(new[] { (uint)a, (uint)b, (uint)c });
        }
        for (int i = 0; i < 2; i++) { Add(i, i + 1, i + 4); Add(i, i + 4, i + 3); }
        for (int i = 0; i < 2; i++) { Add(6 + i, 6 + i + 4, 6 + i + 1); Add(6 + i, 6 + i + 3, 6 + i + 4); }
        int[] rim = [0, 1, 2, 5, 4, 3];
        for (int k = 0; k < rim.Length; k++)
        {
            int a = rim[k], b = rim[(k + 1) % rim.Length];
            Add(b, a, a + 6); Add(b, a + 6, b + 6);
        }
        return (positions, indices.ToArray());
    }
}
