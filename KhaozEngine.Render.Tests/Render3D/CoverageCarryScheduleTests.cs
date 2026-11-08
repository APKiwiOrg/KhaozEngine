using System;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Attachments.Render3D;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryScheduleTests
{
    [Fact]
    public void Carry_schedule_publishes_one_complete_surface()
    {
        using var f = new CoverageCarryGpuFixture();
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int slot, out _));
        Assert.True(f.Stage(slot, out _));
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); f.Batch.Record(commands); commands.End();
        var surface = f.Batch.Surface(f.Actor, slot, -1);
        Assert.Equal(4, commands.Dispatches.Count);
        var bindings = commands.Dispatches.Select(d => d.Set.Description.Resources).ToArray();
        Assert.Same(bindings[0][2], bindings[1][2]);
        Assert.Same(bindings[1][2], bindings[2][2]);
        Assert.Same(bindings[2][2], bindings[3][2]);
        Assert.Same(surface.Current, bindings[3][4]);
        Assert.Same(surface.Current, surface.Previous);
        Assert.NotSame(bindings[1][4], surface.Current);
        Assert.NotSame(bindings[1][4], bindings[0][2]);
        Assert.Equal(f.Actor.Topology.VertexCount, surface.VertexCount);
        Assert.Equal(f.Actor.Topology.Indices.Length, surface.IndexCount);
        int[] extents = [f.Actor.Topology.SourceVertexCount, f.Actor.Topology.VertexCount,
            f.Actor.Topology.TriangleCount, f.Actor.Topology.VertexCount];
        Assert.Equal(extents.Select(n => (uint)((n + 63) / 64)), commands.Groups.Select(g => g.X));
        Assert.All(commands.Groups, g => { Assert.Equal(1u, g.Y); Assert.Equal(1u, g.Z); });
        Assert.Equal(-1, f.Batch.CommittedSlot(f.Actor));
        Assert.DoesNotContain(commands.Uploads, upload => ReferenceEquals(upload.Buffer, surface.Previous));
    }

    [Fact]
    public void Two_actors_share_one_immutable_table_and_keep_independent_rounded_extents()
    {
        using var f = new CoverageCarryGpuFixture();
        using var binding = new HostCoverageBinding(f.Host.Adapter, f.Host.Skeleton, f.Host.Hosts, MotionKey.From(902), f.Host.Table);
        Assert.True(binding.TrySetEquipment([f.Host.Equipment(18)], out _));
        Assert.True(f.Host.Prepare(binding, out var frame, out _));
        var other = frame.Preparation!.Owner;
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int a, out _));
        Assert.True(f.Stage(a, out _));
        Assert.True(f.Batch.TryAcquireOutput(other, out int b, out _));
        Assert.True(f.Batch.TryStage(other, other.Request.EntryIndex, b, other.Request.Palette,
            other.Request.Model, other.Uniforms.Commands, out _));
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); f.Batch.Record(commands); commands.End();
        Assert.Equal(4, commands.Dispatches.Count);
        var first = f.Batch.Surface(f.Actor, a, -1);
        var second = f.Batch.Surface(other, b, -1);
        Assert.Same(first.Current, second.Current);
        Assert.True((ulong)first.CurrentBase + (ulong)first.VertexCount * 16 <= second.CurrentBase);
        Assert.Same(first.Indices, second.Indices);
        int source = f.Actor.Topology.SourceVertexCount;
        Assert.NotEqual(0, source % 64);
        Assert.Equal((uint)((source + 63) / 64 * 2), commands.Groups[0].X);
        int vertices = f.Actor.Topology.VertexCount, triangles = f.Actor.Topology.TriangleCount;
        Assert.Equal((uint)((vertices + 63) / 64 * 2), commands.Groups[1].X);
        Assert.Equal((uint)((triangles + 63) / 64 * 2), commands.Groups[2].X);
        var scalar = (KhaozEngine.Gpu.IGpuBuffer)commands.Dispatches[1].Set.Description.Resources[4];
        uint[] expected = new uint[f.Host.Table.PositionGroupCount * (f.Host.Table.EntryCount + 1)];
        for (int group = 0; group < f.Host.Table.PositionGroupCount; group++)
            expected[group] = unchecked((uint)f.Host.Table.Representatives[group]);
        for (int entry = 0; entry < f.Host.Table.EntryCount; entry++)
            for (int group = 0; group < f.Host.Table.PositionGroupCount; group++)
                expected[(entry + 1) * f.Host.Table.PositionGroupCount + group] = BitConverter.SingleToUInt32Bits(f.Host.Table.Entry(entry)[group]);
        Assert.Equal(expected, f.Device.Memory[scalar]);
        Assert.NotSame(scalar, first.Current);
        Assert.NotSame(scalar, first.Previous);
        Assert.NotSame(scalar, commands.Dispatches[0].Set.Description.Resources[2]);
        Assert.Equal(-1, f.Batch.CommittedSlot(other));
    }

    [Fact]
    public void Warm_recording_keeps_table_words_immutable_and_uploads_only_uniforms_and_resets()
    {
        using var f = new CoverageCarryGpuFixture();
        f.Stage(); f.Batch.AbandonRecording();
        var before = f.Device.Memory.ToDictionary(p => p.Key, p => p.Value.ToArray());
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int slot, out _));
        Assert.True(f.Stage(slot, out _));
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); f.Batch.Record(commands); commands.End();
        var table = commands.Dispatches[1].Set.Description.Resources[4];
        Assert.Equal(before[(KhaozEngine.Gpu.IGpuBuffer)table], f.Device.Memory[(KhaozEngine.Gpu.IGpuBuffer)table]);
        Assert.Equal(2, commands.Uploads.Count);
        Assert.DoesNotContain(commands.Uploads, u => ReferenceEquals(u.Buffer, table));
        Assert.Equal(new uint[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0 }, commands.Uploads[1].Words);
    }
}
