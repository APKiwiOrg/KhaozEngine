using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfVulkanDiagnosticsTests
{
    [Fact]
    public void Current_executable_keeps_valid_statistic_prefix_when_a_later_native_field_fails()
    {
        var owner = new VulkanPipelineDiagnostics(true, true, true, new MalformedApi(), "off");
        var result = owner.Read(73, owner.CaptureFlags, Identity, new(64, 256, 2, 256, 16384, 4), _ => { }, default);
        Assert.False(result.Complete);
        Assert.Equal(1, result.Counts.RetainedExecutables);
        Assert.Equal(1, result.Counts.RetainedStatistics);
        Assert.Equal(2ul, result.Counts.TotalStatistics);
        Assert.Equal(1ul, result.Counts.OmittedStatistics);
        Assert.Equal(0u, result.Counts.RefusedExecutableIndex);
        Assert.Equal(ulong.MaxValue, Assert.Single(Assert.Single(result.Report.Executables).Statistics).Metric.Value!.UInt64);
        Assert.IsType<System.Text.DecoderFallbackException>(Assert.Single(result.Failures).Error);
    }

    [Fact]
    public void Capability_absence_and_zero_statistics_have_distinct_complete_count_domains()
    {
        var native = new BoundedApi(1, 0);
        var owner = new VulkanPipelineDiagnostics(true, false, false, native, "off");
        var request = new GpuPipelineCompilerReadRequest(64, 256, 0, 256, 16384, 4);
        var absence = owner.Read(73, 0, Identity, request, _ => { }, default);
        Assert.True(absence.Complete);
        Assert.False(absence.Counts.EnumerationRequired);
        Assert.True(absence.Counts.CountsComplete);
        Assert.Null(absence.Counts.ExecutableCount);
        Assert.Null(absence.Counts.TotalStatistics);
        Assert.Null(absence.Counts.OmittedStatistics);
        var notRequested = new VulkanPipelineDiagnostics(false, true, true, native, "off")
            .Read(73, 0, Identity, request, _ => { }, default);
        Assert.False(notRequested.Complete);
        Assert.Equal(GpuDiagnosticStatus.Invalid, notRequested.Report.Status);
        Assert.True(notRequested.Counts.EnumerationRequired);
        owner = new(true, true, true, native, "off");
        var zero = owner.Read(73, owner.CaptureFlags, Identity, request, _ => { }, default);
        Assert.True(zero.Complete);
        Assert.True(zero.Counts.EnumerationRequired);
        Assert.Equal(1u, zero.Counts.ExecutableCount);
        Assert.Equal(0ul, zero.Counts.TotalStatistics);
        Assert.Equal(1, zero.Counts.RetainedExecutables);
    }

    [Fact]
    public void Fill_count_above_supplied_capacity_refuses_before_any_decode()
    {
        var materializer = new CountingMaterializer();
        var owner = new VulkanPipelineDiagnostics(true, true, true, new OversizedFillApi(), "off", materializer: materializer);
        var result = owner.Read(73, owner.CaptureFlags, Identity, new(64, 256, 2, 256, 16384, 4), _ => { }, default);
        Assert.False(result.Complete);
        Assert.Equal(2u, result.Counts.RefusedCount);
        Assert.Equal(0, materializer.Decodes);
        Assert.Equal(0, materializer.StatisticAllocations);
    }

    [Fact]
    public void Raw_flags_hard_bound_is_enforced_before_shader_identity_retention()
    {
        int prefix = KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(
            KhaozEngine.Gpu.Internal.SpirvFrontEnd.OptionsIdentity(""));
        var accepted = VulkanShaderIdentity.Compute("source", new byte[] { 1, 2, 3 }, new('x', 16384 - prefix));
        Assert.Equal(16384, KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(accepted.CompilerFlags));
        var refusal = Assert.Throws<GpuDiagnosticFailureException>(() =>
            VulkanShaderIdentity.Compute("source", new byte[] { 1, 2, 3 }, new('x', 16385 - prefix)));
        Assert.Contains("16384", Assert.Single(refusal.Failures).Error.Message);
    }

    sealed class MalformedApi : IVulkanPipelineExecutableApi
    {
        readonly BoundedApi _inner = new(1, 2);
        public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values) => _inner.Properties(pipeline, ref count, values);
        public unsafe Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        {
            var result = _inner.Statistics(pipeline, executable, ref count, values);
            if (!values.IsEmpty) values[1].Description[0] = 0xff;
            return result;
        }
    }
    sealed class OversizedFillApi : IVulkanPipelineExecutableApi
    {
        public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        { count = values.IsEmpty ? 1u : 2u; return Result.Success; }
        public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
            => throw new InvalidOperationException("Statistic read must not start.");
    }
}
