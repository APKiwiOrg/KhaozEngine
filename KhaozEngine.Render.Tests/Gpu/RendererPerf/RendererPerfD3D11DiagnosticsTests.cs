using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Fact]
    public void Reflection_temporaries_are_not_hardware_registers()
    {
        var api = new ReflectionApi();
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var evidence = D3D11ShaderEvidence.Capture("compute source", new byte[] { 1, 2, 3 }, "compute", 0x8000,
            options, api);
        using var shader = new D3D11ComputeShader(new DeviceLiveness(), new NativeHandle(), new byte[] { 1, 2, 3 },
            8, 4, 2, evidence);
        using var pipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(shader));
        var report = ((IGpuPipelineDiagnostics)pipeline).ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
        var executable = Assert.Single(report.Executables);
        var temporary = Assert.Single(executable.Statistics, x => x.Name == "TempRegisterCount");
        Assert.Equal(17ul, temporary.Metric.Value!.UInt64);
        Assert.Equal(GpuDiagnosticProvenance.IntermediateCode, temporary.Metric.Provenance);
        Assert.Equal("slots", temporary.Metric.Unit);
        Assert.Equal("D3D11_SHADER_DESC.TempRegisterCount (UINT)", temporary.Metric.Source);
        Assert.Equal(111ul, Assert.Single(executable.Statistics, x => x.Name == "InstructionCount").Metric.Value!.UInt64);
        Assert.Equal(3ul, Assert.Single(executable.Statistics, x => x.Name == "BoundResources").Metric.Value!.UInt64);
        Assert.Equal(8u, executable.LocalSizeX);
        Assert.Equal(4u, executable.LocalSizeY);
        Assert.Equal(2u, executable.LocalSizeZ);
        foreach (var field in new[] { executable.PhysicalRegisters, executable.SpillBytes, executable.AchievedOccupancy })
        {
            Assert.Equal(GpuDiagnosticStatus.Unsupported, field.Status);
            Assert.Null(field.Value);
            Assert.False(string.IsNullOrWhiteSpace(field.Reason));
        }
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", report.Identity.ShaderArtifactSha256.Sha256);
        Assert.Contains("0x00008000", report.Identity.CompilerFlags);
        Assert.Contains("record=Deferred", report.Identity.CompilerFlags);
        Assert.Contains("test compiler 1", report.Identity.CompilerFlags);
        Assert.Equal(1, api.Reads);
        Assert.Equal(report, ((IGpuPipelineDiagnostics)pipeline).ReadCompilerReport());
        Assert.Equal(1, api.Reads);
        pipeline.Dispose();
        Assert.Equal(GpuDiagnosticStatus.Invalid, ((IGpuPipelineDiagnostics)pipeline).ReadCompilerReport().Status);
    }

    [Fact]
    public void Unavailable_residency_is_not_zero()
    {
        var memory = new D3D11MemoryDiagnostics();
        using var payload = memory.TrackBuffer(new GpuBufferDescription(48, GpuBufferUsage.UniformBuffer), 3);
        var snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(48ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(768ul, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(48ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(1ul, snapshot.LiveResourceCount.Value!.UInt64);
        foreach (var field in new[] { snapshot.CommittedBytes, snapshot.ResidentBytes, snapshot.BudgetBytes })
        {
            Assert.Equal(GpuDiagnosticStatus.Unsupported, field.Status);
            Assert.Null(field.Value);
            Assert.False(string.IsNullOrWhiteSpace(field.Reason));
        }
        Assert.Null(snapshot.UnifiedMemory);
        payload.Dispose();
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(48ul, memory.ReadMemorySnapshot().PeakLogicalPayloadBytes.Value!.UInt64);
    }

    [Fact]
    public void Diagnostic_query_is_outside_timed_pipeline_configuration()
    {
        var queries = new QueryApi();
        var source = new D3D11InvocationDiagnostics(false, D3D11RecordMode.Deferred, queries, new object());
        bool executed = false;
        using var refused = source.CaptureInvocations(() => executed = true);
        Assert.True(refused.TryRead(out var missing));
        Assert.Equal(GpuDiagnosticStatus.Unsupported, missing!.Status);
        Assert.False(executed);
        Assert.Empty(queries.Events);
        Assert.False(new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false).Invocations);
    }

    internal sealed class ReflectionApi : ID3D11ShaderReflectionApi
    {
        internal int Reads;
        internal Exception? Failure;
        internal bool Empty;
        public D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, dxbc.ToArray());
            Assert.Equal(4096, maximumCreatorUtf8Bytes);
            Reads++;
            if (Failure is not null) throw Failure;
            return new("test compiler 1", Empty ? [] : [new("TempRegisterCount", "The number of temporary registers in the compiled shader.", 17),
                new("InstructionCount", "The number of intermediate-language instructions in the compiled shader.", 111),
                new("BoundResources", "The number of resource (textures and buffers) bound to a shader.", 3)]);
        }
    }

    internal sealed class NativeHandle : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
}
