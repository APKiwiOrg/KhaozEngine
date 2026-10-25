using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfVulkanDiagnosticsTests
{
    [Theory]
    [InlineData(65, 1, 16384, 0, 0)]
    [InlineData(1, 257, 16384, 1, 0)]
    [InlineData(1, 2, 1, 1, 0)]
    public void Admitted_count_plus_one_refuses_before_native_fill_or_decode(int executables, int statistics,
        int total, int propertyAllocations, int statisticAllocations)
    {
        var api = new BoundedApi((uint)executables, (uint)statistics);
        var materializer = new CountingMaterializer();
        var owner = new VulkanPipelineDiagnostics(true, true, true, api, "off", materializer: materializer);
        var request = new GpuPipelineCompilerReadRequest(64, 256, total, 256, 16384, 4);
        var result = owner.Read(73, owner.CaptureFlags, Identity, request, _ => { }, default);
        Assert.False(result.Complete);
        Assert.Equal(GpuDiagnosticStatus.Invalid, result.Report.Status);
        Assert.Equal(propertyAllocations, materializer.PropertyAllocations);
        Assert.Equal(statisticAllocations, materializer.StatisticAllocations);
        Assert.Equal(0, api.StatisticFills);
        Assert.Equal((uint)(executables > 64 ? executables : statistics), result.Counts.RefusedCount);
        Assert.NotEmpty(result.Failures);
    }

    [Fact]
    public void Native_failure_keeps_original_reference_and_partial_prefix()
    {
        var error = new InvalidOperationException("sentinel native failure");
        var api = new BoundedApi(2, 1) { FailureAtSecond = error };
        var owner = new VulkanPipelineDiagnostics(true, true, true, api, "off");
        var result = owner.Read(73, owner.CaptureFlags, Identity, new(64, 256, 10, 256, 16384, 4), _ => { }, default);
        Assert.Same(error, Assert.Single(result.Failures).Error);
        Assert.Single(result.Report.Executables[0].Statistics);
        Assert.Null(result.Counts.TotalStatistics);
        Assert.Null(result.Counts.OmittedStatistics);
    }

    sealed class CountingMaterializer : IVulkanCompilerMaterializer
    {
        internal int PropertyAllocations, StatisticAllocations, Decodes;
        public PipelineExecutablePropertiesKHR[] Properties(int count) { PropertyAllocations++; return new PipelineExecutablePropertiesKHR[count]; }
        public PipelineExecutableStatisticKHR[] Statistics(int count) { StatisticAllocations++; return new PipelineExecutableStatisticKHR[count]; }
        public string Text(ReadOnlySpan<byte> bytes) { Decodes++; return System.Text.Encoding.UTF8.GetString(bytes); }
    }

    sealed class BoundedApi(uint executables, uint statistics) : IVulkanPipelineExecutableApi
    {
        internal int StatisticFills;
        internal Exception? FailureAtSecond;
        public unsafe Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        {
            count = executables;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = new() { SType = StructureType.PipelineExecutablePropertiesKhr, Stages = ShaderStageFlags.ComputeBit };
                values[i].Name[0] = (byte)'x';
            }
            return Result.Success;
        }
        public unsafe Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        {
            if (executable == 1 && FailureAtSecond is { } error) throw error;
            count = statistics;
            if (!values.IsEmpty) StatisticFills++;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = new() { SType = StructureType.PipelineExecutableStatisticKhr, Format = PipelineExecutableStatisticFormatKHR.Uint64Khr };
                values[i].Name[0] = (byte)'n'; values[i].Description[0] = (byte)'d';
                values[i].Value.U64 = ulong.MaxValue;
            }
            return Result.Success;
        }
    }
}
