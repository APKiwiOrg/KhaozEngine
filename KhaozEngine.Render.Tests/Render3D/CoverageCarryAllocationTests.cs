using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageCarryAllocationTests
{
    [Fact]
    public void Warm_carry_and_hole_allocate_zero_managed_bytes()
    {
        var rig = new CoverageCarryReferenceTests.Fixture();
        var scratch = new CoverageCarryScratch(rig.Topology, 2);
        var uniforms = new CoverageCarryUniforms(rig.Topology, 2);
        var carried = new ModelVertex[rig.Topology.VertexCount];
        var uncovered = new ModelVertex[rig.Topology.VertexCount];
        Matrix4x4[] inverseBind = [Matrix4x4.Identity, Matrix4x4.Identity];
        CoverageCommandPose[] commands = [rig.PoseCommand(Matrix4x4.Identity)];
        Assert.True(uniforms.TryPrepare(rig.Palette, inverseBind, Matrix4x4.Identity, commands, out _));
        Assert.True(CoverageCarryReference.TryCarry(rig.Topology, uniforms.ComposedPalette, rig.Table, 0,
            uniforms.Commands, scratch, carried, out _));
        Assert.True(CoverageCarryReference.TryCarry(rig.Topology, uniforms.ComposedPalette, rig.Table, -1,
            uniforms.Commands, scratch, uncovered, out _));

        long allocated;
        bool prepared, applied, hole;
        using (AllocationRegion.Start("warm scalar carry and hole"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            prepared = uniforms.TryPrepare(rig.Palette, inverseBind, Matrix4x4.Identity, commands, out _);
            applied = CoverageCarryReference.TryCarry(rig.Topology, uniforms.ComposedPalette, rig.Table, 1,
                uniforms.Commands, scratch, carried, out _);
            hole = CoverageCarryReference.TryCarry(rig.Topology, uniforms.ComposedPalette, rig.Table, -1,
                uniforms.Commands, scratch, uncovered, out _);
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Assert.Equal(0, allocated);
        Assert.True(prepared && applied && hole);
        Assert.NotEmpty(carried);
        Assert.NotEmpty(uncovered);
        Assert.Contains(carried, v => v.Position != Vector3.Zero);
        Assert.NotEqual(CoverageCarryReferenceTests.Bytes(uncovered), CoverageCarryReferenceTests.Bytes(carried));
        Assert.Equal(CoverageCarryReferenceTests.Bytes(scratch.Before.ToArray()), CoverageCarryReferenceTests.Bytes(uncovered));
    }
}
