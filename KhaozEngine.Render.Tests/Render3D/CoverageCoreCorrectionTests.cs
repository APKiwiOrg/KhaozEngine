using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageCoreCorrectionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Convex_sheet_levels_2_to_4_clear(int level)
    {
        var fixture = CoverageFixture.CurvedPanel(level: level);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        Assert.True(CoverageCommands.Witness(fixture, topology, before, before).BreakthroughArea > 0);
        var pointwise = Pointwise(topology, before, commands);
        Assert.True(CoverageCommands.Witness(fixture, topology, before, pointwise).ClearanceViolations > 0);
        Assert.False(CoverageCertification.ValidateGeometry(topology, before, pointwise, Matrix4x4.Identity, commands,
            new CoverageScratch(topology), out var negative));
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, negative.Code);
        var final = Evaluate(fixture, topology, commands);
        var witness = CoverageCommands.Witness(fixture, topology, before, final);
        Assert.True(witness.CoreMovedArea > 0);
        Assert.True(witness.CoreRecession > 0);
        Assert.True(witness.MinimumCoreClearance >= fixture.Commands[0].Clearance);
        Assert.Equal(0, witness.ClearanceViolations);
        Assert.Equal(0, witness.BreakthroughArea);
        Assert.Equal(0, witness.EquipmentIntersections);
        Assert.Equal(0, witness.NonadjacentIntersections);
        Assert.Equal(0, witness.Folds);
        Assert.Equal(0, witness.UnmatchedInteriorEdges);
        Assert.Equal(0, witness.WeldedPositionChanges);
        Assert.Equal(0, witness.OpenBoundaryEdges);
        Assert.Equal(0, witness.ProtectedPositionChanges);
        for (int i = 0; i < final.Length; i++)
            if (topology.Vertices[i].Strength == 0) Assert.Equal(before[i], final[i]);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.05f)]
    public void Zero_deficit_is_byte_exact(float concavity)
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        var shell = CoverageCommands.Gabled(.3f, concavity, .6f, 1.2f, 1.2f, outward: false);
        fixture = fixture with { RigidPositions = shell.Positions, RigidIndices = shell.Indices, SupportIndices = shell.Indices[..12] };
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var pointwise = ReferencePointwise(topology, fixture.Posed, commands);
        var scratch = new CoverageScratch(topology);
        var final = Evaluate(fixture, topology, commands, scratch);
        Assert.All(CoverageGeometryTestRun.Reference(scratch).FaceCorrections, extra => Assert.Equal(0, extra));
        Assert.Equal(pointwise.Select(v => v.Position), final.Select(v => v.Position));
        for (int i = 0; i < final.Length; i++)
            if (before[i].Position == final[i].Position) Assert.Equal(before[i], final[i]);
        var lowered = fixture.Posed.Select(v => { v.Position.Z -= .5f; return v; }).ToArray();
        var clear = fixture with { Posed = lowered };
        Assert.Equal(CoverageCommands.Surface(topology, lowered), Evaluate(clear, topology, commands));
    }

    [Fact]
    public void Incident_core_deficit_moves_a_pointwise_idle_vertex()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var pointwise = ReferencePointwise(topology, fixture.Posed, commands);
        var final = Evaluate(fixture, topology, commands);
        Assert.Contains(Enumerable.Range(0, final.Length), i => topology.IsCoreVertex(i)
            && pointwise[i].Position == before[i].Position && final[i].Position.Z < before[i].Position.Z);
        for (int i = 0; i < final.Length; i++)
            if (!topology.IsCoreVertex(i)) Assert.Equal(pointwise[i].Position, final[i].Position);
    }

    [Fact]
    public void Shared_group_correction_is_applied_once()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var final = Evaluate(fixture, topology, commands);
        var plain = CoverageFixture.CurvedPanel(seams: false, level: 3);
        var plainTopology = CoverageRefinement.Build(plain.Source, plain.Map);
        var plainFinal = Evaluate(plain, plainTopology,
            CoverageCommands.Poses(plain, plainTopology, CoverageCommands.Plate(plain), Matrix4x4.Identity));
        var plainBefore = CoverageCommands.Surface(plainTopology, plain.Posed);
        int movedAliases = 0;
        foreach (var group in Enumerable.Range(0, final.Length).GroupBy(i => topology.Vertices[i].PositionGroup))
        {
            int first = group.First();
            foreach (int i in group) Assert.Equal(final[first].Position, final[i].Position);
            int match = Array.FindIndex(plainBefore, v => v.Position == before[first].Position);
            Assert.True(match >= 0);
            Assert.Equal(plainFinal[match].Position, final[first].Position);
            if (group.Count() > 1 && final[first].Position != before[first].Position) movedAliases++;
        }
        Assert.True(movedAliases > 0);
    }

    [Fact]
    public void Correction_respects_budget_caps_and_transition()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var full = Evaluate(fixture, topology, commands);
        var half = Evaluate(fixture, topology, [commands[0] with { TransitionWeight = .5f }]);
        Assert.Equal(before, Evaluate(fixture, topology, [commands[0] with { TransitionWeight = 0 }]));
        for (int i = 0; i < full.Length; i++)
            Assert.True(Vector3.Distance(half[i].Position, (before[i].Position + full[i].Position) * .5f) < 1e-7f);
        var pointwise = Pointwise(topology, before, commands);
        float pointBudget = Enumerable.Range(0, before.Length).Max(i => Vector3.Distance(before[i].Position, pointwise[i].Position));
        float fullBudget = Enumerable.Range(0, before.Length).Max(i => Vector3.Distance(before[i].Position, full[i].Position));
        Assert.True(fullBudget > pointBudget);
        AssertRefusal([commands[0] with { MaxWorldRecession = (pointBudget + fullBudget) * .5f, TransitionWeight = 0 }], CoverageFailureCode.RecessionExceeded);
        AssertRefusal([commands[0] with { MaxZ = .3f }], CoverageFailureCode.CapContact);
        var collapsed = CoverageFixture.Pose.Select(m => Matrix4x4.Lerp(m * Matrix4x4.CreateRotationZ(MathF.PI / 2),
            m * Matrix4x4.CreateRotationZ(-MathF.PI / 2), .5f)).ToArray();
        var invalid = fixture.Source.SkinVertices.ToArray().Select(v => SkinningMath.SkinVertex(v, collapsed)).ToArray();
        var output = new ModelVertex[topology.VertexCount];
        Assert.False(CoverageGeometryTestRun.TryEvaluate(topology, invalid, Matrix4x4.Identity,
            [commands[0] with { TransitionWeight = .5f }], new CoverageScratch(topology), output, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);

        void AssertRefusal(CoverageCommandPose[] submitted, CoverageFailureCode expected)
        {
            var sentinel = new ModelVertex(new(7, 8, 9), Vector3.UnitY, Vector4.One);
            var result = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
            Assert.False(CoverageGeometryTestRun.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, submitted,
                new CoverageScratch(topology), result, out var certificate, out var failure));
            Assert.Equal(expected, failure.Code);
            Assert.Equal(default, certificate);
            Assert.Equal(1u, failure.CommandId);
            Assert.All(result, v => Assert.Equal(sentinel, v));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_sheet_frame_discrepancy_is_bounded_during_correction(bool placed)
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var local = Matrix4x4.CreateScale(.83f, 1.17f, .91f) * Matrix4x4.CreateRotationZ(.19f)
            * Matrix4x4.CreateTranslation(.03f, -.02f, .01f);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("framed-plate", fixture.RigidPositions, fixture.RigidIndices),
            [new(0, 0, 0, 4)], local);
        Assert.NotEqual(Matrix4x4.Identity, support.SourceToCoverage * local);
        var equipmentOffset = Matrix4x4.CreateRotationZ(.11f) * Matrix4x4.CreateTranslation(.02f, -.01f, .003f);
        fixture = fixture with { Commands = [fixture.Commands[0] with { CoverageLocal = equipmentOffset }] };
        var actor = placed ? Matrix4x4.CreateRotationY(.23f) * Matrix4x4.CreateRotationZ(.31f)
            * Matrix4x4.CreateTranslation(2, -1, .5f) : Matrix4x4.Identity;
        var commands = CoverageCommands.Poses(fixture, topology, support, actor);
        Assert.NotEqual(actor, commands[0].EquipmentWorld);
        Assert.NotEqual(commands[0].EquipmentWorld, commands[0].CoverageWorld);
        var scratch = new CoverageScratch(topology);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageGeometryTestRun.TryEvaluate(topology, fixture.Posed, actor, commands, scratch, final,
            out var failure), failure.ToString());
        Assert.Contains(CoverageGeometryTestRun.Reference(scratch).FaceCorrections, extra => extra > 0);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var world = CoverageCommands.World(fixture, actor);
        var witness = CoverageCommands.Witness(world, topology, CoverageCommands.Place(before, actor), CoverageCommands.Place(final, actor));
        Assert.True(witness.CoreMovedArea > 0);
        Assert.Equal(0, witness.ClearanceViolations);
        Assert.Equal(0, witness.EquipmentIntersections);
        Assert.True(CoverageCertification.ValidateGeometry(topology, before, final, actor, commands,
            scratch, out failure), failure.ToString());
    }

    internal static ModelVertex[] Evaluate(CoverageFixture fixture, CoverageRefinement topology, CoverageCommandPose[] commands,
        CoverageScratch? scratch = null)
    {
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageGeometryTestRun.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands,
            scratch ?? new CoverageScratch(topology), final, out var certificate, out var failure), failure.ToString());
        Assert.Equal(commands.All(c => c.TransitionWeight == 1), certificate.FullCoreClearance);
        return final;
    }

    static ModelVertex[] ReferencePointwise(CoverageRefinement topology, ModelVertex[] posed, CoverageCommandPose[] commands)
    {
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, out var failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out failure), failure.ToString());
        return scratch.After.ToArray();
    }

    // Independently construct the previous pointwise request. No face correction or production constructor is used.
    internal static ModelVertex[] Pointwise(CoverageRefinement topology, ModelVertex[] before, CoverageCommandPose[] commands)
    {
        var scratch = new CoverageScratch(topology);
        Assert.True(CoverageFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch,
            CoverageCertification.Magnitude(before, default), out var failure), failure.ToString());
        var result = before.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            var record = topology.Vertices[i];
            if (record.RegionIndex < 0 || record.Strength == 0) continue;
            int slot = scratch.RegionCommands[record.RegionIndex];
            if (slot < 0) continue;
            var frame = scratch.Frames[slot];
            var command = commands[frame.Command];
            var q = frame.HostToCoverage.Apply(before[i].Position);
            if (!command.Support.TryGetHeightBounds(CoverageAffinePoint.FromDouble(q.X, q.Y), out var height)) continue;
            double request = record.Strength * Math.Max(0, q.Z - (height.Min - frame.ClearanceCoverage - frame.Slack));
            result[i].Position = new((float)(before[i].Position.X - request * frame.Recess.X),
                (float)(before[i].Position.Y - request * frame.Recess.Y), (float)(before[i].Position.Z - request * frame.Recess.Z));
        }
        return result;
    }
}
