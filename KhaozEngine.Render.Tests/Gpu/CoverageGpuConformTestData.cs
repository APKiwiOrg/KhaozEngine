using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuConformTestData
{
    internal static CoverageReferenceScratch Prepare(CoverageReferenceCase c, bool conform = false)
    {
        var s = new CoverageReferenceScratch(c.Topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, s, out var failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryInterpolate(c.Topology, c.Posed, s, out failure), failure.ToString());
        if (conform) Assert.True(CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, s, out failure), failure.ToString());
        return s;
    }

    internal static CoverageGpuTestSession Session(IGpuDevice device, CoverageReferenceCase[] cases, CoverageReferenceScratch[] scratch) =>
        new(device, cases.Select(c => c.Topology).ToArray(), cases.Select(_ => new[] { Matrix4x4.Identity, Matrix4x4.Identity }).ToArray(),
            cases.Select(c => c.Commands).ToArray(), scratch);

    internal static void Execute(IGpuDevice device, CoverageGpuPipeline pipeline, CoverageGpuTestSession session,
        CoverageGpuPhase last = CoverageGpuPhase.StoreTransition)
    {
        using var commands = device.Factory.CreateCommandList();
        commands.Begin();
        pipeline.RecordPhases(commands, session.Batch, CoverageGpuPhase.ValidateSource, last);
        session.RecordReadback(commands);
        commands.End(); device.Submit(commands); device.WaitForIdle(); session.CompleteReadback();
    }

    internal static void Log(GpuDeviceContext gpu, CoverageGpuPipeline pipeline, ITestOutputHelper output)
    {
        output.WriteLine($"Native backend {gpu.Backend}, adapter {gpu.GpuDevice.Capabilities.DeviceName}, path {pipeline.FilterPath}");
        foreach (var (phase, p) in pipeline.Pipelines)
            if (OperatingSystem.IsMacOS() && p is MetalComputePipeline metal)
            {
                output.WriteLine($"{phase}: maxTotalThreadsPerThreadgroup={metal.State.MaxTotalThreadsPerThreadgroup}, threadExecutionWidth={metal.State.ThreadExecutionWidth}");
                Assert.True(metal.State.MaxTotalThreadsPerThreadgroup >= 64);
            }
    }

    internal static uint[] Words(float[] values) => values.Select(BitConverter.SingleToUInt32Bits).ToArray();
    internal static uint[] Words(Vector3[] values) => Words(values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray());
    internal static uint[] Words(ModelVertex[] values) => Words(values.SelectMany(v => CoverageReferenceBits.Fields(v)).ToArray());

    internal static void Failure(CoverageFailure expected, CoverageGpuTestSession session, int actor)
    {
        uint[] result = session.ReadResult(actor), header = session.ReadHeader(actor);
        Assert.Equal((uint)expected.Code, result[8]);
        Assert.Equal((uint)expected.EquipmentInstanceId, result[9]);
        Assert.Equal((uint)(expected.EquipmentInstanceId >> 32), result[10]);
        Assert.Equal(expected.CommandId, result[11]);
        Assert.Equal((uint)expected.Count, result[12]);
        Assert.Equal(0u, result[13]);
        Assert.Equal((uint)expected.Code, header[26]);
        Assert.Equal((uint)expected.Count, header[28]);
        uint refused = expected.Code == CoverageFailureCode.None ? 0u : 1u;
        Assert.Equal(refused, result[14]);
        Assert.Equal(refused, header[30]);
        if (refused == 0u) Assert.Equal(uint.MaxValue, header[27]);
    }
}
