using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageSkinOverflowTests
{
    const float Limit = 1 << 20;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Finite_palettes_over_the_coordinate_budget_refuse_before_carry(int weightSum)
    {
        var (topology, uniforms, command) = Fixture(weightSum);
        float maximumScale = Limit / (4 * weightSum);
        Matrix4x4[] palette = [Matrix4x4.CreateScale(MathF.BitIncrement(maximumScale), 1, 1)];
        Assert.False(uniforms.TryPrepare(palette, [Matrix4x4.Identity], Matrix4x4.Identity, [command], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(topology.HostIdentity, failure.HostIdentity);
        Assert.True(uniforms.ComposedPalette.IsEmpty);
        Assert.True(uniforms.Commands.IsEmpty);
        palette[0].M11 = MathF.BitDecrement(maximumScale);
        Assert.True(uniforms.TryPrepare(palette, [Matrix4x4.Identity], Matrix4x4.Identity, [command], out failure));
        Assert.True(failure.IsSuccess);
    }

    [Fact]
    public void MaxValue_with_source_X_two_is_refused_at_CPU_admission()
    {
        var (topology, uniforms, command) = Fixture(1);
        Assert.Equal(2f, topology.Source.Positions.ToArray().Max(p => MathF.Abs(p.X)));
        var palette = Matrix4x4.Identity; palette.M11 = float.MaxValue;
        Assert.True(CoverageAffine.TryFrom(palette, out _));
        Assert.False(uniforms.TryPrepare([palette], [Matrix4x4.Identity], Matrix4x4.Identity, [command], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.True(uniforms.ComposedPalette.IsEmpty);
        Assert.True(uniforms.Commands.IsEmpty);
    }

    [Fact]
    public void Source_overflow_rejection_cannot_record_a_carry_dispatch()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var seed, out _));
        var owner = seed.Preparation!.Owner;
        f.Scene.Begin();
        f.Palette[1].M11 = float.MaxValue;
        Assert.True(CoverageAffine.TryFrom(CoverageReferenceSkinning.Compose(f.Palette[1], owner.InverseBind[1]), out _));
        bool accepted = f.Prepare(binding, out _, out var failure);
        using var device = new CoverageCarryTestDevice();
        using var retired = KhaozEngine.Gpu.GpuRetireQueue.CreateFrameCounted(device, 3);
        using var batch = new CoverageCarryBatch(device, retired);
        var t = owner.Topology;
        batch.Reserve(1, t.SourceVertexCount, t.VertexCount, t.TriangleCount);
        if (accepted)
        {
            Assert.True(batch.TryAcquireOutput(owner, out int slot, out _));
            Assert.True(batch.TryStage(owner, owner.Request.EntryIndex, slot, owner.Request.Palette,
                owner.Request.Model, owner.Uniforms.Commands, out _));
        }
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); batch.Record(commands); commands.End();
        Assert.False(accepted);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Empty(commands.Dispatches);
    }

    [Fact]
    public void Limit_palette_keeps_skin_and_generated_Before_finite_in_poisoned_storage()
    {
        var (_, uniforms, command) = Fixture(1);
        // Two units of source X, unit weight sum and the twofold rounding allowance give 4 * scale.
        const float scale = Limit / 4;
        Assert.True(uniforms.TryPrepare([Matrix4x4.CreateScale(scale, 1, 1)], [Matrix4x4.Identity],
            Matrix4x4.Identity, [command], out _));
        var fixture = new CoverageCarryFailureFixture();
        fixture.SkinAndCancellingTransport(scale);
        Assert.All(fixture.Work.AsSpan(CoverageCarryFailureFixture.Source, 48).ToArray(),
            word => Assert.True(float.IsFinite(BitConverter.UInt32BitsToSingle(word))));
        Assert.All(fixture.Work.AsSpan(CoverageCarryFailureFixture.Before, 32).ToArray(),
            word => Assert.True(float.IsFinite(BitConverter.UInt32BitsToSingle(word))));
        fixture.Publish();
        fixture.AssertCoveredDirections();
    }

    [Fact]
    public void Warm_overflow_preflight_does_not_allocate()
    {
        var (_, uniforms, command) = Fixture(1);
        Matrix4x4[] palette = [Matrix4x4.Identity], inverse = [Matrix4x4.Identity];
        CoverageCommandPose[] commands = [command];
        Assert.True(uniforms.TryPrepare(palette, inverse, Matrix4x4.Identity, commands, out _));
        bool accepted; long bytes;
        using (AllocationRegion.Start("warm skin overflow preflight"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            accepted = uniforms.TryPrepare(palette, inverse, Matrix4x4.Identity, commands, out _);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.True(accepted); Assert.Equal(0, bytes);
    }

    static (CoverageRefinement Topology, CoverageCarryUniforms Uniforms, CoverageCommandPose Command) Fixture(int weightSum)
    {
        var rig = new CoverageCarryReferenceTests.Fixture();
        var old = rig.Topology.Source;
        var skin = old.SkinVertices.ToArray();
        float maximum = skin.Max(v => MathF.Abs(v.Position.X));
        foreach (ref var vertex in skin.AsSpan())
        {
            vertex.Position.X *= 2 / maximum;
            vertex.BoneIndices = Vector4.Zero;
            vertex.BoneWeights = weightSum == 1 ? Vector4.UnitX : new Vector4(.5f);
        }
        var source = new CoverageSourceTopology("overflow-host", old.SourceSha256,
            skin.Select(v => v.Position).ToArray(), old.Vertices, old.Triangles, skin);
        var topology = CoverageRefinement.Build(source, rig.Topology.Map);
        var command = rig.PoseCommand(Matrix4x4.Identity) with { Topology = topology };
        return (topology, new(topology, 1), command);
    }
}
