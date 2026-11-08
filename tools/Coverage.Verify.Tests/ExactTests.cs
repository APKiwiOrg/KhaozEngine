using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactTests
{
    [Fact]
    public void Commanded_triangle_crossing_refuses()
    {
        var rig = new ExactPanelFixture();
        var before = rig.Before();
        var beforeCallerBytes = Bytes(before);
        bool certified = CoverageCertification.ValidateGeometry(rig.Topology, before, before,
            Matrix4x4.Identity, [rig.Command], new(rig.Topology), out var failure);
        Assert.False(certified);
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
        rig.Context(failure);
        Assert.Equal(beforeCallerBytes, Bytes(before));
        var output = rig.Construct();
        Assert.True(CoverageCertification.ValidateGeometry(rig.Topology, before, output,
            Matrix4x4.Identity, [rig.Command], new(rig.Topology), out failure), failure.ToString());
    }

    [Fact]
    public void Protected_bytes_and_world_recession_budget_are_checked()
    {
        var rig = new ExactPanelFixture();
        var before = rig.Before();
        var output = rig.Construct();
        var scratch = new CoverageScratch(rig.Topology);
        Assert.True(CoverageCertification.ValidateGeometry(rig.Topology, before, output,
            Matrix4x4.Identity, [rig.Command], scratch, out _));
        var tampered = output.ToArray();
        int protectedVertex = Enumerable.Range(0, output.Length).First(v => rig.Topology.Vertices[v].Strength == 0);
        // +0 and -0 compare equal as floats, but protected records retain their exact authored bytes.
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(tampered[protectedVertex].Uv.X));
        tampered[protectedVertex].Uv.X = BitConverter.UInt32BitsToSingle(0x80000000u);
        var original = Bytes(tampered);
        Assert.False(CoverageCertification.ValidateGeometry(rig.Topology, before, tampered,
            Matrix4x4.Identity, [rig.Command], scratch, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        Assert.Equal(rig.Topology.HostIdentity, failure.HostIdentity);
        Assert.Equal(original, Bytes(tampered));
        float modelDistance = before.Zip(output, (b, a) => Vector3.Distance(b.Position, a.Position)).Max();
        Assert.True(modelDistance > 0);
        var scaledModel = Matrix4x4.CreateScale(2);
        var scaled = rig.Command with { EquipmentWorld = scaledModel, CoverageWorld = scaledModel };
        Assert.True(CoverageCertification.ValidateGeometry(rig.Topology, before, output,
            scaledModel, [scaled], scratch, out failure), failure.ToString());
        var tight = scaled with { MaxWorldRecession = modelDistance * 1.5f };
        Assert.False(CoverageCertification.ValidateGeometry(rig.Topology, before, output,
            scaledModel, [tight], scratch, out failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
        rig.Context(failure);
        float[] scalars = Enumerable.Repeat(7f, rig.Topology.PositionGroupCount).ToArray();
        int[] representatives = Enumerable.Repeat(77, scalars.Length).ToArray();
        var oldOutput = Bytes(output);
        Assert.False(rig.Constructor.TryConstruct(rig.Palette, scaledModel, [tight], output,
            scalars, representatives, out failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
        rig.Context(failure);
        Assert.Equal(oldOutput, Bytes(output));
        Assert.All(scalars, value => Assert.Equal(7f, value));
        Assert.All(representatives, value => Assert.Equal(77, value));
    }

    [Fact]
    public void Exact_construction_exports_independent_scalar_storage()
    {
        var rig = new ExactPanelFixture();
        float[] scalars = new float[rig.Topology.PositionGroupCount];
        int[] representatives = new int[scalars.Length];
        var output = new ModelVertex[rig.Topology.VertexCount];
        Assert.True(rig.Constructor.TryConstruct(rig.Palette, Matrix4x4.Identity,
            [rig.Command with { TransitionWeight = .5f }], output, scalars, representatives, out var failure), failure.ToString());
        Assert.Contains(scalars, value => value > 0);
        var saved = scalars.ToArray();
        var savedRepresentatives = representatives.ToArray();
        var before = rig.Before();
        int witness = Array.FindIndex(scalars, value => value > 0);
        int vertex = representatives[witness];
        Assert.True(vertex >= 0);
        Assert.Equal(before[vertex].Position.Z - scalars[witness] * .5f, output[vertex].Position.Z);
        Assert.True(rig.Constructor.TryConstruct(rig.Palette, Matrix4x4.Identity, [], output,
            new float[scalars.Length], new int[scalars.Length], out failure), failure.ToString());
        Assert.Equal(saved, scalars);
        Assert.Equal(savedRepresentatives, representatives);
        Assert.True(rig.Constructor.TryConstruct(rig.Palette, Matrix4x4.Identity, [rig.Command], output,
            new float[scalars.Length], new int[scalars.Length], out failure), failure.ToString());
        Assert.Equal(before[vertex].Position.Z - saved[witness], output[vertex].Position.Z);
        Assert.Equal(saved, scalars);
    }

    static byte[] Bytes(ModelVertex[] vertices) => MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();

}
