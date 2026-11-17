using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>Exact construction rig over <see cref="CoverageChildCoreFixture"/>: the flat panel, its small rigid plate as
/// the support and one command, with an identity pose so the posed source equals the rest source.</summary>
internal sealed class ExactChildCoreRig
{
    static readonly Matrix4x4[] Palette = [Matrix4x4.Identity, Matrix4x4.Identity];

    internal ExactChildCoreRig(HostCoverageMap map)
    {
        Topology = CoverageRefinement.Build(CoverageChildCoreFixture.Source(), map);
        var positions = CoverageChildCoreFixture.PlatePositions;
        var indices = CoverageChildCoreFixture.PlateIndices;
        var solid = new CoverageSourceTopology("synthetic-support", CoverageFixture.Hash, positions,
            Enumerable.Range(0, positions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)),
            Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0,
                (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])));
        var support = CoverageSupportSurface.Compile(solid, [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Command = new(23, 17, Topology, 0, support, Matrix4x4.Identity, Matrix4x4.Identity, -2, 2, .3f, .02f, .8f);
        Constructor = new(Topology, [Matrix4x4.Identity, Matrix4x4.Identity]);
    }

    internal CoverageRefinement Topology { get; }
    internal CoverageCommandPose Command { get; }
    internal CoverageExactConstructor Constructor { get; }

    internal bool TryConstruct(out ModelVertex[] output, out CoverageFailure failure)
    {
        output = new ModelVertex[Topology.VertexCount];
        return Constructor.TryConstruct(Palette, Matrix4x4.Identity, [Command], output,
            new float[Topology.PositionGroupCount], new int[Topology.PositionGroupCount], out failure);
    }

    /// <summary>The retained binary32 skinning with no command, which certification compares against.</summary>
    internal ModelVertex[] Before()
    {
        var result = new ModelVertex[Topology.VertexCount];
        Assert.True(Constructor.TryConstruct(Palette, Matrix4x4.Identity, [], result,
            new float[Topology.PositionGroupCount], new int[Topology.PositionGroupCount], out var failure), failure.ToString());
        return result;
    }

    internal bool Certify(ModelVertex[] final, out CoverageFailure failure) =>
        CoverageCertification.ValidateGeometry(Topology, Before(), final, Matrix4x4.Identity, [Command],
            new CoverageScratch(Topology), out failure);
}
