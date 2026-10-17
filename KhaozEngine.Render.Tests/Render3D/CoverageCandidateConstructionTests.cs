using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The scene's candidate-only construction shares the geometry evaluator's construction, normals and atomic
/// copy and certifies nothing. Once the full late validation accepts its bytes, they are exactly what the public
/// evaluator already accepted, so moving the one certification late changes no covered result.</summary>
public sealed class CoverageCandidateConstructionTests
{
    static CoverageRenderStamp Stamp => new(1, 2, 3, MotionKey.From(4), 5);

    [Theory]
    [InlineData(2, true, false, false, 1f)]
    [InlineData(3, true, false, false, 1f)]
    [InlineData(4, false, false, false, 1f)]
    [InlineData(2, false, true, false, 1f)]
    [InlineData(2, false, true, true, 1f)]
    [InlineData(2, true, false, false, .5f)]
    [InlineData(2, true, false, false, 0f)]
    public void Late_acceptance_of_the_candidate_implies_the_former_early_acceptance(int level, bool seams, bool second,
        bool reversed, float transition)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: seams, level: level, secondRegion: second);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity, transition);
        if (reversed) Array.Reverse(commands);
        var scratch = new CoverageScratch(topology);
        var work = new List<CoverageCertificationWork>();
        scratch.CertificationWitnessForTests = (kind, _) => work.Add(kind);
        var candidate = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryConstructCandidate(topology, fixture.Posed, Matrix4x4.Identity, commands, scratch, candidate,
            out var failure), failure.ToString());
        Assert.Empty(work);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        // A zero transition is exact no-work data.
        if (transition == 0) Assert.Equal(Bytes(before), Bytes(candidate));
        var context = Context(topology, candidate, commands, fixture.RigidPositions);
        Assert.True(CoverageCertification.Validate(topology, before, candidate, Matrix4x4.Identity, commands, context,
            new CoverageScratch(topology), out var certificate, out failure), failure.ToString());

        // Late acceptance implies the former early acceptance, with the same bytes and certificate bits.
        var early = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, fixture.Posed, Matrix4x4.Identity, commands,
            new CoverageScratch(topology), early, out failure), failure.ToString());
        Assert.Equal(Bytes(early), Bytes(candidate));
        var evaluated = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, context,
            new CoverageScratch(topology), evaluated, out var expected, out failure), failure.ToString());
        Assert.Equal(Bytes(early), Bytes(evaluated));
        CoverageRenderSeparationTests.Same(expected, certificate);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Every_posed_source_vertex_is_copied_so_the_late_magnitude_dominates(int level, bool seams)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: seams, level: level);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount];
        Assert.True(topology.TryInterpolate(fixture.Posed, before, out var failure), failure.ToString());
        for (int s = 0; s < topology.SourceVertexCount; s++)
            Assert.Contains(Enumerable.Range(0, topology.VertexCount), i => topology.Vertices[i].SourceVertex == s
                && Bytes(before.AsSpan(i, 1)).SequenceEqual(Bytes(fixture.Posed.AsSpan(s, 1))));
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var candidate = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryConstructCandidate(topology, fixture.Posed, Matrix4x4.Identity, commands,
            new CoverageScratch(topology), candidate, out failure), failure.ToString());
        double staged = CoverageCertification.Magnitude(fixture.Posed, default), late = CoverageCertification.Magnitude(before, candidate);
        Assert.True(late >= staged);
        var early = new CoverageScratch(topology);
        var resolved = new CoverageScratch(topology);
        Assert.True(CoverageFrames.TryPrepare(topology, Matrix4x4.Identity, commands, early, staged, out failure), failure.ToString());
        Assert.True(CoverageFrames.TryPrepare(topology, Matrix4x4.Identity, commands, resolved, late, out failure), failure.ToString());
        for (int slot = 0; slot < commands.Length; slot++)
            Assert.True(resolved.Frames[slot].ConstructionErrorBound >= early.Frames[slot].ConstructionErrorBound);
    }

    [Fact]
    public void A_larger_magnitude_only_raises_the_bound_and_never_turns_refusal_into_acceptance()
    {
        // Each command's patch lies Near from the other command's box, the only place Certify reads the bound.
        var scene = CoverageRenderSeparationTests.Gap(CoverageRenderSeparationTests.Near);
        double[] magnitudes = [1, 64, 512, 2048, 4096];
        var bounds = new double[magnitudes.Length, 2];
        var accepted = new bool[magnitudes.Length];
        for (int m = 0; m < magnitudes.Length; m++)
        {
            var scratch = new CoverageScratch(scene.Topology);
            Assert.True(CoverageFrames.TryPrepare(scene.Topology, Matrix4x4.Identity, scene.Commands, scratch, magnitudes[m], out var failure),
                failure.ToString());
            for (int slot = 0; slot < 2; slot++) bounds[m, slot] = scratch.Frames[slot].ConstructionErrorBound;
            accepted[m] = CoverageCertification.Certify(scene.Topology, scene.Posed, scene.Posed, scene.Commands, scratch, out _, out failure);
            if (!accepted[m]) Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
        }
        for (int m = 1; m < magnitudes.Length; m++)
        {
            for (int slot = 0; slot < 2; slot++) Assert.True(bounds[m, slot] >= bounds[m - 1, slot]);
            Assert.True(!accepted[m] || accepted[m - 1]);
        }
        Assert.True(accepted[0]);
        Assert.False(accepted[^1]);
    }

    [Theory]
    [InlineData("unknown-region", false)]
    [InlineData("stale-identity", false)]
    [InlineData("stale-frame", false)]
    [InlineData("overlap", false)]
    [InlineData("capacity", false)]
    [InlineData("escaped-core", false)]
    [InlineData("cap-contact", false)]
    [InlineData("budget", false)]
    [InlineData("vertical", true)]
    [InlineData("support-gap", false)]
    [InlineData("inside-solid", true)]
    [InlineData("rear-host", true)]
    [InlineData("mirror", false)]
    [InlineData("singular", false)]
    [InlineData("projective", false)]
    [InlineData("nonfinite", false)]
    [InlineData("unabsorbed-error", false)]
    public void Only_certification_refusals_move_from_staging_to_late_validation(string kind, bool deferred)
    {
        var (topology, posed, actor, commands, scratch) = CoverageCertificationTests.Scenario(kind);
        var sentinel = new ModelVertex(new(7, 7, 7), Vector3.UnitY, Vector4.One);
        var early = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryEvaluateGeometry(topology, posed, actor, commands, scratch, early, out var expected));
        var candidate = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        bool staged = CoverageEvaluator.TryConstructCandidate(topology, posed, actor, commands, scratch, candidate, out var failure);
        Assert.Equal(deferred, staged);
        if (!deferred)
        {
            Assert.Equal(expected, failure);
            Assert.All(candidate, v => Assert.Equal(sentinel, v));
            return;
        }
        Assert.True(failure.IsSuccess);
        Assert.All(candidate, v => Assert.True(CoverageRefinement.Finite(v.Position)));
        Assert.False(CoverageCertification.ValidateGeometry(topology, CoverageCommands.Surface(topology, posed), candidate, actor, commands,
            new CoverageScratch(topology), out var late));
        Assert.Equal((expected.Code, expected.EquipmentInstanceId, expected.CommandId), (late.Code, late.EquipmentInstanceId, late.CommandId));
    }

    [Fact]
    public void Normal_refusal_stays_at_staging_ahead_of_the_former_face_refusal()
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false, challengingCollar: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var posed = fixture.Posed.ToArray();
        // Two neighbouring receding collar vertices coincide, so the displaced collar faces along their shared edge are
        // degenerate. Core correction reads only core faces, so construction still succeeds.
        posed[2 * 7 + 1].Position = posed[1 * 7 + 1].Position;
        var sentinel = new ModelVertex(new(7, 7, 7), Vector3.UnitY, Vector4.One);
        var early = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryEvaluateGeometry(topology, posed, Matrix4x4.Identity, commands, new CoverageScratch(topology),
            early, out var former));
        Assert.Equal((CoverageFailureCode.FoldedPatch, 1u), (former.Code, former.CommandId));
        var candidate = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryConstructCandidate(topology, posed, Matrix4x4.Identity, commands, new CoverageScratch(topology),
            candidate, out var failure));
        // The normal update reports its own refusal, with no command, before any certification runs.
        Assert.Equal(new CoverageFailure(CoverageFailureCode.FoldedPatch, topology.HostIdentity, 0, 0, 0, 0), failure);
        Assert.All(candidate, v => Assert.Equal(sentinel, v));
    }

    static CoverageRenderContext Context(CoverageRefinement topology, ModelVertex[] final, CoverageCommandPose[] commands, Vector3[] plate)
    {
        var passes = new List<CoverageRenderPass>
        {
            new(0, 0, Matrix4x4.Identity, Matrix4x4.Identity, final.Select(v => v.Position).Aggregate(Vector3.Min),
                final.Select(v => v.Position).Aggregate(Vector3.Max), 1e-4),
        };
        foreach (var command in commands)
            passes.Add(new(command.Id, command.EquipmentInstanceId, command.EquipmentWorld, Matrix4x4.Identity,
                plate.Aggregate(Vector3.Min), plate.Aggregate(Vector3.Max), 0));
        return new(topology, Stamp, Vector3.Zero, passes.ToArray());
    }

    static byte[] Bytes(ReadOnlySpan<ModelVertex> vertices) => MemoryMarshal.AsBytes(vertices).ToArray();
}
