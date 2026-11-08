using System;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed class CoverageCarryGpuFixture : IDisposable
{
    internal readonly CoverageBindingFixture Host = new();
    readonly HostCoverageBinding binding;
    internal readonly CoverageCarryTestDevice Device;
    internal readonly GpuRetireQueue Retired;
    internal readonly CoverageCarryBatch Batch;
    internal SkinnedHostCoverage Actor { get; }
    internal CoverageCarryGpuFixture(bool compute = true, bool completion = true, bool pipeline = true)
    {
        binding = Host.Binding();
        Assert.True(binding.TrySetEquipment([Host.Equipment()], out _));
        Host.Scene.Begin();
        Assert.True(Host.Prepare(binding, out var frame, out _));
        Actor = frame.Preparation!.Owner;
        Device = new(compute, completion) { RefusePipeline = !pipeline };
        Retired = GpuRetireQueue.CreateFrameCounted(Device, 3);
        Batch = new(Device, Retired);
        var t = Actor.Topology;
        Batch.Reserve(2, t.SourceVertexCount * 2, t.VertexCount * 2, t.TriangleCount * 2);
    }
    internal bool Stage(int slot, out CoverageFailure failure) => Batch.TryStage(Actor, Actor.Request.EntryIndex,
        slot, Actor.Request.Palette, Actor.Request.Model, Actor.Uniforms.Commands, out failure);
    internal int Stage()
    {
        Assert.True(Batch.TryAcquireOutput(Actor, out int slot, out var failure), failure.ToString());
        Assert.True(Stage(slot, out failure), failure.ToString());
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); Batch.Record(commands); commands.End();
        return slot;
    }
    public void Dispose() { Batch.Dispose(); Retired.Dispose(); Device.Dispose(); binding.Dispose(); Host.Dispose(); }
}
