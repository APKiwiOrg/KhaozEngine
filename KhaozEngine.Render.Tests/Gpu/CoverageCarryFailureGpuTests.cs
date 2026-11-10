using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryFailureGpuTests
{
    [GpuFact]
    public void Transport_direction_failure_preserves_covered_positions() => Run(transport: true);

    [GpuFact]
    public void Late_gather_direction_failure_remains_local() => Run(transport: false);

    [GpuFact]
    public void Position_failure_still_publishes_the_complete_uncovered_actor() => Run(transport: false, positionFailure: true);

    static void Run(bool transport, bool positionFailure = false)
    {
        var fixture = new CoverageCarryFailureFixture();
        if (transport) fixture.CancellingTransport(execute: false);
        else fixture.FailingGather(fail: !positionFailure);
        if (positionFailure) fixture.Work[CoverageCarryFailureFixture.After + 18] = CoverageCarryFailureFixture.Poison;
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        var factory = device.Factory;
        if (device.Backend == GpuBackendKind.VulkanNative && !device.Capabilities.SupportsShaderFloatControls)
        {
            Assert.Equal(GpuShaderPolicyRefusal.FloatControlsUnavailable,
                Assert.Throws<GpuShaderPolicyUnsupportedException>(() => factory.CreateComputeShaderFromSpirv(
                    CoverageCarryShaders.Kernels[1].Source, CoverageCarryShaders.Options)).Reason);
            return;
        }
        uint[][] words = [fixture.Static, fixture.Frames, fixture.Work, fixture.Errors, fixture.Current, fixture.Table];
        var buffers = new IGpuBuffer[words.Length];
        var resources = new List<IDisposable>();
        try
        {
            for (int i = 0; i < words.Length; i++)
            {
                buffers[i] = factory.CreateBuffer(new((uint)words[i].Length * 4,
                    i is 0 or 1 or 5 ? GpuBufferUsage.StructuredBufferReadOnly : GpuBufferUsage.StructuredBufferReadWrite, 4));
                resources.Add(buffers[i]); device.UpdateBuffer(buffers[i], 0, words[i]);
            }
            using var commands = factory.CreateCommandList();
            commands.Begin();
            int[] stages = transport ? [1, 3, 4] : [3, 4];
            foreach (int stage in stages)
            {
                var kernel = CoverageCarryShaders.Kernels[stage];
                var shader = factory.CreateComputeShaderFromSpirv(kernel.Source, CoverageCarryShaders.Options);
                resources.Add(shader);
                var layout = factory.CreateResourceLayout(CoverageCarryLayout.Resources(stage)); resources.Add(layout);
                IGpuBindableResource[] common = [buffers[0], buffers[1], buffers[2], buffers[3]];
                IGpuBindableResource[] bound = stage == 1 ? [.. common, buffers[5]]
                    : stage == 4 ? [.. common, buffers[4]] : common;
                var set = factory.CreateResourceSet(new(layout, bound)); resources.Add(set);
                var pipeline = factory.CreateComputePipeline(new(shader, layout)); resources.Add(pipeline);
                commands.SetComputePipeline(pipeline); commands.SetComputeResourceSet(0, set); commands.Dispatch(1, 1, 1);
            }
            commands.End(); device.Submit(commands); device.WaitForIdle();
            uint[] actual = GpuReadback.ReadBuffer<uint>(device, buffers[4], 32);
            GpuReadback.ReadBuffer<uint>(device, buffers[3], 8).CopyTo(fixture.Errors, 0);
            // The dispatches wrote carry storage on the device, and the position assertions compare against it.
            GpuReadback.ReadBuffer<uint>(device, buffers[2], fixture.Work.Length).CopyTo(fixture.Work, 0);
            actual.CopyTo(fixture.Current, 0);
            if (positionFailure) fixture.AssertUncovered(); else fixture.AssertCoveredDirections();
        }
        finally { for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose(); }
    }
}
