using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

internal sealed class ExactPanelFixture
{
    readonly CoverageFixture fixture;
    internal CoverageFixture Data => fixture;
    internal ExactPanelFixture(int level = 1, bool seams = true)
    {
        fixture = CoverageFixture.CurvedPanel(level: level, seams: seams);
        Topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var solid = new CoverageSourceTopology("synthetic-support", CoverageFixture.Hash, fixture.RigidPositions,
            Enumerable.Range(0, fixture.RigidPositions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)),
            Enumerable.Range(0, fixture.RigidIndices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0,
                (int)fixture.RigidIndices[t * 3], (int)fixture.RigidIndices[t * 3 + 1], (int)fixture.RigidIndices[t * 3 + 2])));
        var support = CoverageSupportSurface.Compile(solid, [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Command = new(23, 17, Topology, 0, support, Matrix4x4.Identity, Matrix4x4.Identity, -2, 2, .4f, .02f, .8f);
        Matrix4x4[] binds = [Matrix4x4.Identity, Matrix4x4.Identity];
        Constructor = new(Topology, binds);
        // Construction must own the inverse binds, independent of later source edits.
        Array.Fill(binds, Matrix4x4.CreateTranslation(100, 100, 100));
    }
    internal CoverageRefinement Topology { get; }
    internal CoverageCommandPose Command { get; }
    internal CoverageExactConstructor Constructor { get; }
    internal Matrix4x4[] Palette { get; } = CoverageFixture.Pose;
    internal ModelVertex[] Before()
    {
        var result = new ModelVertex[Topology.VertexCount];
        // Certification compares against the retained binary32 skinning and refinement graph.
        // The fixture's ordinary SkinningMath pose uses a different arithmetic graph.
        Assert.True(Constructor.TryConstruct(Palette, Matrix4x4.Identity, [], result,
            new float[Topology.PositionGroupCount], new int[Topology.PositionGroupCount], out var failure), failure.ToString());
        return result;
    }
    internal ModelVertex[] Construct()
    {
        var output = new ModelVertex[Topology.VertexCount];
        Assert.True(Constructor.TryConstruct(Palette, Matrix4x4.Identity, [Command], output,
            new float[Topology.PositionGroupCount], new int[Topology.PositionGroupCount], out var failure), failure.ToString());
        return output;
    }
    internal void Context(CoverageFailure failure)
    {
        Assert.Equal(Topology.HostIdentity, failure.HostIdentity);
        Assert.Equal(17ul, failure.EquipmentInstanceId);
        Assert.Equal(23u, failure.CommandId);
    }
    internal CoverageReferenceScratch PointwiseScratch()
    {
        var scratch = new CoverageReferenceScratch(Topology, Palette.Length);
        Before().CopyTo(scratch.Before, 0);
        Assert.True(CoverageReferenceFrames.TryPrepare(Topology, Matrix4x4.Identity, [Command], scratch, out var failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryConformPrepared(Topology, [Command], scratch, out failure), failure.ToString());
        return scratch;
    }

    internal void AssertCertified(ModelVertex[] final)
    {
        Assert.True(CoverageCertification.ValidateGeometry(Topology, Before(), final, Matrix4x4.Identity,
            [Command], new CoverageScratch(Topology), out var failure), failure.ToString());
    }

}
