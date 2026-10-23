using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Fact]
    public unsafe void Creator_is_bounded_at_the_pointer_before_decoding()
    {
        byte* bytes = stackalloc byte[4098];
        new Span<byte>(bytes, 4098).Fill((byte)'x'); bytes[4097] = 0;
        int decodes = 0;
        string Decode(ReadOnlySpan<byte> value) { decodes++; return Encoding.UTF8.GetString(value); }
        Assert.Throws<InvalidOperationException>(() => D3D11ReflectionDescription.ReadCreator((IntPtr)bytes, 4096, Decode));
        Assert.Equal(0, decodes);
        bytes[4096] = 0;
        Assert.Equal(4096, D3D11ReflectionDescription.ReadCreator((IntPtr)bytes, 4096, Decode).Length);
        Assert.Equal(1, decodes);
        bytes[0] = 0xff;
        Assert.Throws<DecoderFallbackException>(() => D3D11ReflectionDescription.ReadCreator((IntPtr)bytes, 4096, Decode));
        Assert.Equal(1, decodes);
        Assert.Equal(IntPtr.Size == 8 ? 160 : 152, Marshal.SizeOf<D3D11ReflectionDescription>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<D3D11ReflectionDescription>("Creator").ToInt32());
    }

    [Fact]
    public unsafe void GetDesc_uses_the_native_pointer_slot_and_uint_layout_without_managed_marshaling()
    {
        IntPtr* table = stackalloc IntPtr[4];
        table[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, D3D11ReflectionDescription*, int>)&FillDescription;
        IntPtr* self = stackalloc IntPtr[1]; self[0] = (IntPtr)table;
        var description = D3D11ReflectionDescription.Read((IntPtr)self);
        Assert.Equal(uint.MaxValue, description.Flags);
        Assert.Equal(uint.MaxValue, description.TempRegisterCount);
        Assert.Equal(42u, description.TextureStoreInstructions);
        Assert.Equal(IntPtr.Zero, description.Creator);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static unsafe int FillDescription(IntPtr self, D3D11ReflectionDescription* description)
    {
        *description = new() { Flags = uint.MaxValue, TempRegisterCount = uint.MaxValue, TextureStoreInstructions = 42 };
        return 0;
    }

    [Fact]
    public void Construction_getter_is_precomputed_and_survives_disposal()
    {
        var reflection = new ReflectionApi();
        var evidence = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0x8000,
            new(true, D3D11RecordMode.Deferred, false, false), reflection);
        using var shader = new D3D11ComputeShader(new DeviceLiveness(), new NativeHandle(), new byte[] { 1, 2, 3 }, 8, 1, 1, evidence);
        using var pipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(shader));
        var witness = ((IGpuPipelineConstructionDiagnostics)pipeline).ConstructionIdentity;
        Assert.Same(witness, pipeline.ReadCompilerReport().Identity);
        Assert.Same(witness, pipeline.ConstructionIdentity);
        pipeline.Dispose(); shader.Dispose();
        Assert.Same(witness, pipeline.ConstructionIdentity);
        Assert.Equal(1, reflection.Reads);
        Assert.Equal(GpuDiagnosticStatus.Invalid, pipeline.ReadCompilerReport().Status);
        var smaller = D3D11PipelineDiagnostics.Request with { MaximumTotalStatistics = 1 };
        var refused = pipeline.ReadCompilerReport(smaller, _ => { }, default);
        Assert.False(refused.Complete);
        Assert.Equal(smaller, refused.Request);
    }

    [Fact]
    public void Observation_error_keeps_statistics_actual_cause_and_known_counts()
    {
        var error = new System.IO.IOException("compiler observation failed");
        var native = new ObservationApi(error);
        var stage = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0,
            new(true, D3D11RecordMode.Deferred, false, false), native);
        var owner = new D3D11PipelineDiagnostics([stage], new('a', 64), new('b', 64));
        var result = owner.Read(false, D3D11PipelineDiagnostics.Request, _ => { }, default);
        Assert.False(result.Complete);
        Assert.True(result.Counts.CountsComplete);
        Assert.Equal(1ul, result.Counts.TotalStatistics);
        Assert.Equal(1, result.Counts.RetainedStatistics);
        Assert.Equal(0ul, result.Counts.OmittedStatistics);
        Assert.Same(error, Assert.Single(result.Failures).Error);
        Assert.Equal((ulong)uint.MaxValue, Assert.Single(Assert.Single(result.Report.Executables).Statistics).Metric.Value!.UInt64);
    }

    sealed class ObservationApi(Exception error) : ID3D11ShaderReflectionApi
    {
        public D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)
            => new("Compiler description unavailable", [new("InstructionCount", "Intermediate instruction count", uint.MaxValue)])
            { Failures = [new("d3d11.compiler-observation", error)] };
    }

    [Fact]
    public void Real_reflection_projection_retains_all_thirty_uint_counts()
    {
        var native = new D3D11ReflectionDescription { TempRegisterCount = uint.MaxValue, TextureStoreInstructions = 42 };
        var statistics = D3D11ReflectionStatistics.Read(native);
        Assert.Equal(30, statistics.Length);
        Assert.Equal(uint.MaxValue, Assert.Single(statistics, s => s.Name == "TempRegisterCount").Value);
        Assert.Equal(42u, Assert.Single(statistics, s => s.Name == "cTextureStoreInstructions").Value);
    }
}
