using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfVulkanDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changing_counts_recheck_remaining_total_without_resetting_attempts(bool alwaysIncomplete)
    {
        var native = new ChangingApi(alwaysIncomplete);
        var materializer = new CountingMaterializer();
        var owner = new VulkanPipelineDiagnostics(true, true, true, native, "off", materializer: materializer);
        var result = owner.Read(73, owner.CaptureFlags, Identity, new(64, 256, 2, 256, 16384, 4), _ => { }, default);
        Assert.False(result.Complete);
        Assert.Equal(2, result.Counts.RetainedExecutables);
        Assert.Equal(1, result.Counts.RetainedStatistics);
        Assert.Single(result.Report.Executables[0].Statistics);
        Assert.Empty(result.Report.Executables[1].Statistics);
        Assert.Equal(alwaysIncomplete ? 4 : 1, native.SecondFills);
        Assert.Equal(alwaysIncomplete ? 4 : 2, native.SecondCounts);
        Assert.Equal(alwaysIncomplete ? 5 : 2, materializer.StatisticAllocations);
        if (alwaysIncomplete)
        {
            Assert.False(result.Counts.CountsComplete);
            Assert.Null(result.Counts.TotalStatistics);
            Assert.Null(result.Counts.OmittedStatistics);
        }
        if (!alwaysIncomplete)
        {
            Assert.Equal(1u, result.Counts.RefusedExecutableIndex);
            Assert.Equal(2u, result.Counts.RefusedCount);
            Assert.Equal(3ul, result.Counts.TotalStatistics);
            Assert.Equal(2ul, result.Counts.OmittedStatistics);
        }
    }

    [Theory]
    [InlineData(256, 256)]
    [InlineData(13, 12)]
    public void Native_text_refuses_before_managed_decoder(int length, int admitted)
    {
        var native = new TextApi(length);
        var materializer = new CountingMaterializer();
        var owner = new VulkanPipelineDiagnostics(true, true, true, native, "off", materializer: materializer);
        var result = owner.Read(73, owner.CaptureFlags, Identity, new(64, 256, 1, admitted, 16384, 4), _ => { }, default);
        Assert.False(result.Complete);
        Assert.Equal(0, materializer.Decodes);
        Assert.Equal(0, native.StatisticCalls);
    }

    [Fact]
    public void Invalid_request_flags_and_cancellation_refuse_without_native_output()
    {
        var native = new BoundedApi(1, 1);
        var materializer = new CountingMaterializer();
        var owner = new VulkanPipelineDiagnostics(true, true, true, native, "off", materializer: materializer);
        var request = new GpuPipelineCompilerReadRequest(64, 256, 1, 256, 16384, 4);
        foreach (var invalid in new[] { request with { MaximumExecutables = 65 }, request with { MaximumEnumerationAttempts = 5 },
            request with { MaximumTotalStatistics = 16385 }, request with { MaximumStatisticsPerExecutable = 257 } })
            Assert.False(owner.Read(73, owner.CaptureFlags, Identity, invalid, _ => { }, default).Complete);
        var flags = Identity with { CompilerFlags = new('x', 16385) };
        Assert.False(owner.Read(73, owner.CaptureFlags, flags, request, _ => { }, default).Complete);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = owner.Read(73, owner.CaptureFlags, Identity, request, _ => { }, cancellation.Token);
        Assert.IsType<OperationCanceledException>(Assert.Single(cancelled.Failures).Error);
        Assert.Equal(0, materializer.PropertyAllocations);
        Assert.Equal(0, materializer.Decodes);
    }

    sealed class ChangingApi(bool alwaysIncomplete) : IVulkanPipelineExecutableApi
    {
        readonly BoundedApi _inner = new(2, 1);
        internal int SecondFills, SecondCounts;
        public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
            => _inner.Properties(pipeline, ref count, values);
        public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        {
            if (executable == 0) return _inner.Statistics(pipeline, executable, ref count, values);
            if (values.IsEmpty) { count = !alwaysIncomplete && ++SecondCounts > 1 ? 2u : 1u; if (alwaysIncomplete) SecondCounts++; return Result.Success; }
            SecondFills++; return Result.Incomplete;
        }
    }

    sealed class TextApi(int length) : IVulkanPipelineExecutableApi
    {
        internal int StatisticCalls;
        public unsafe Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        {
            count = 1;
            if (!values.IsEmpty)
            {
                values[0] = new() { SType = StructureType.PipelineExecutablePropertiesKhr, Stages = ShaderStageFlags.ComputeBit };
                for (int i = 0; i < length; i++) values[0].Name[i] = (byte)'x';
            }
            return Result.Success;
        }
        public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        { StatisticCalls++; count = 0; return Result.Success; }
    }
}
