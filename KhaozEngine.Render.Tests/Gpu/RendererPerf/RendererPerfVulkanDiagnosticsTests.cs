using System;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfVulkanDiagnosticsTests
{
    static readonly VulkanPipelineIdentity Identity = TestIdentity();
    static VulkanPipelineIdentity TestIdentity()
    {
        var shader = VulkanShaderIdentity.Compute("source", new byte[] { 1, 2, 3 }, "fixture");
        return new(VulkanPipelineIdentity.HashText("pipeline"), shader.Source, shader.Artifact,
            VulkanPipelineIdentity.HashText("layout"), shader.CompilerFlags, 8, 4, 2)
        { Stages = shader.Stages, CodeGeneration = shader.CodeGeneration };
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void Capture_requires_extension_feature_and_pipeline_flag(bool requested, bool extension, bool feature, bool flag)
    {
        var api = new ExecutableApi();
        var diagnostics = new VulkanPipelineDiagnostics(requested, extension, feature, api, "validation=off");
        PipelineCreateFlags flags = flag ? PipelineCreateFlags.CreateCaptureStatisticsBitKhr : 0;

        GpuPipelineCompilerReport report = diagnostics.Read(73, flags, Identity);

        Assert.Equal(requested && extension && feature, diagnostics.CaptureFlags != 0);
        if (requested && extension && feature && flag)
        {
            Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
            Assert.Single(report.Executables);
        }
        else
        {
            Assert.Equal(requested && extension && feature ? GpuDiagnosticStatus.Invalid : GpuDiagnosticStatus.Unsupported, report.Status);
            Assert.NotEmpty(report.Reason!);
            Assert.Equal(0, api.Calls);
        }
    }

    [Fact]
    public void Incomplete_enumeration_retries_within_fixed_capacity()
    {
        var api = new ExecutableApi { Grow = true };
        var diagnostics = new VulkanPipelineDiagnostics(true, true, true, api, "validation=off");

        GpuPipelineCompilerReport report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);

        Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
        Assert.Equal(2, report.Executables.Length);
        Assert.All(report.Executables, executable => Assert.Equal(2, executable.Statistics.Length));
        Assert.True(api.Calls <= 24);

        api = new ExecutableApi { AlwaysIncomplete = true };
        diagnostics = new VulkanPipelineDiagnostics(true, true, true, api, "validation=off");
        report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Status);
        Assert.Empty(report.Executables);
        Assert.True(api.Calls <= 16);

        api = new ExecutableApi { ExcessiveCount = true };
        diagnostics = new VulkanPipelineDiagnostics(true, true, true, api, "validation=off");
        report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Status);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public void Missing_register_spill_occupancy_stays_unavailable()
    {
        var api = new ExecutableApi();
        var diagnostics = new VulkanPipelineDiagnostics(true, true, true, api, "validation=off");
        GpuPipelineCompilerReport report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);

        GpuPipelineExecutableReport executable = Assert.Single(report.Executables);
        GpuPipelineStatistic statistic = Assert.Single(executable.Statistics);
        Assert.Equal("vendor cycles", statistic.Name);
        Assert.Equal("Raw vendor estimate", statistic.Description);
        Assert.Equal(ulong.MaxValue, statistic.Metric.Value!.UInt64);
        Assert.Equal(GpuDiagnosticValueType.UInt64, statistic.Metric.Value.Type);
        Assert.Equal("vendor-defined", statistic.Metric.Unit);
        Assert.Equal(GpuDiagnosticProvenance.VendorCompiler, statistic.Metric.Provenance);
        foreach (GpuDiagnosticMetric metric in new[] { executable.PhysicalRegisters, executable.SpillBytes,
                     executable.AchievedOccupancy, executable.ExecutionWidth, executable.MaximumThreadsPerGroup })
        {
            Assert.Equal(GpuDiagnosticStatus.Unsupported, metric.Status);
            Assert.Null(metric.Value);
            Assert.NotEmpty(metric.Reason!);
        }
        Assert.Equal(8u, executable.LocalSizeX);
        Assert.Equal("main", executable.EntryPoint);
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", report.Identity.ShaderArtifactSha256.Sha256);
    }

    [Fact]
    public void Suballocated_payload_does_not_double_count_heap()
    {
        var timeline = new VulkanTimeline(new FakeVulkanTimelineSemaphore());
        var retired = new VulkanRetireList();
        using var allocator = new VulkanMemoryAllocator(new FakeVulkanDeviceMemoryApi(),
            new VulkanMemoryFacts([new(0, 0, VulkanMemoryTrait.DeviceLocal)], 64, 4096),
            new VulkanTimelineRetirement(timeline, retired), chunkSize: 4096, dedicatedThreshold: 2048);
        var request = new VulkanMemoryRequest(256, 16, 1, VulkanMemoryUsage.DeviceLocal, VulkanMemoryTiling.Linear);
        VulkanMemoryAllocation first = allocator.Allocate(request, new VulkanMemoryPayload(BufferBytes: 100));
        VulkanMemoryAllocation second = allocator.Allocate(request, new VulkanMemoryPayload(TextureBytes: 120));
        GpuResourceMemorySnapshot snapshot = allocator.ReadMemorySnapshot();

        Assert.Equal(first.Memory, second.Memory);
        Assert.Equal(100ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(120ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(220ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(4096ul, snapshot.CommittedBytes.Value!.UInt64);
        Assert.Equal(2ul, snapshot.LiveResourceCount.Value!.UInt64);
        Assert.Equal(GpuDiagnosticProvenance.BackendAllocation, snapshot.CommittedBytes.Provenance);
        Assert.Null(snapshot.ResidentBytes.Value);
        Assert.Null(snapshot.BudgetBytes.Value);

        allocator.Free(first);
        allocator.Free(second);
        snapshot = allocator.ReadMemorySnapshot();
        Assert.Equal(0ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(0ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(220ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(4096ul, snapshot.CommittedBytes.Value!.UInt64);

        VulkanMemoryAllocation dedicated = allocator.Allocate(request with { Size = 2048 },
            new VulkanMemoryPayload(BufferBytes: 2000, StagingCapacityBytes: 2048));
        allocator.Free(dedicated);
        Assert.Equal(6144ul, allocator.ReadMemorySnapshot().CommittedBytes.Value!.UInt64);
        retired.Drain(timeline.CompletedValue);
        Assert.Equal(4096ul, allocator.ReadMemorySnapshot().CommittedBytes.Value!.UInt64);
        allocator.Dispose();
        Assert.Equal(0ul, allocator.ReadMemorySnapshot().CommittedBytes.Value!.UInt64);
    }

    [Theory]
    [InlineData(true, false, GpuDiagnosticStatus.Unsupported)]
    [InlineData(false, true, GpuDiagnosticStatus.Invalid)]
    public void Empty_or_failed_native_data_is_not_available(bool empty, bool fail, GpuDiagnosticStatus expected)
    {
        var diagnostics = new VulkanPipelineDiagnostics(true, true, true,
            new ExecutableApi { Empty = empty, Fail = fail }, "validation=off");
        GpuPipelineCompilerReport report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);
        Assert.Equal(expected, report.Status);
        Assert.NotEmpty(report.Reason!);
        if (fail) Assert.Contains("ErrorUnknown", report.Reason);
    }

    [Theory]
    [InlineData(PipelineExecutableStatisticFormatKHR.Bool32Khr)]
    [InlineData(PipelineExecutableStatisticFormatKHR.Int64Khr)]
    [InlineData(PipelineExecutableStatisticFormatKHR.Uint64Khr)]
    [InlineData(PipelineExecutableStatisticFormatKHR.Float64Khr)]
    public void Vendor_scalar_types_are_preserved(PipelineExecutableStatisticFormatKHR format)
    {
        var diagnostics = new VulkanPipelineDiagnostics(true, true, true, new ExecutableApi { Format = format }, "off");
        GpuPipelineCompilerReport report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);
        GpuDiagnosticValue value = Assert.Single(Assert.Single(report.Executables).Statistics).Metric.Value!;
        switch (format)
        {
            case PipelineExecutableStatisticFormatKHR.Bool32Khr: Assert.True(value.Boolean); break;
            case PipelineExecutableStatisticFormatKHR.Int64Khr: Assert.Equal(long.MinValue, value.Int64); break;
            case PipelineExecutableStatisticFormatKHR.Uint64Khr: Assert.Equal(ulong.MaxValue, value.UInt64); break;
            case PipelineExecutableStatisticFormatKHR.Float64Khr: Assert.Equal(0.125, value.Float64); break;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Statistic_failure_or_malformed_value_is_invalid(bool malformed)
    {
        var api = new ExecutableApi { FailStatistics = !malformed, NonFinite = malformed };
        var diagnostics = new VulkanPipelineDiagnostics(true, true, true, api, "off");
        GpuPipelineCompilerReport report = diagnostics.Read(73, diagnostics.CaptureFlags, Identity);
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Status);
        if (malformed) Assert.Null(Assert.Single(Assert.Single(report.Executables).Statistics).Metric.Value);
        else Assert.Contains("ErrorUnknown", report.Reason!);
    }

    sealed class ExecutableApi : IVulkanPipelineExecutableApi
    {
        internal int Calls;
        internal bool Grow, AlwaysIncomplete, ExcessiveCount, Empty, Fail, FailStatistics, NonFinite;
        internal PipelineExecutableStatisticFormatKHR Format = PipelineExecutableStatisticFormatKHR.Uint64Khr;
        internal ulong ExpectedPipeline = 73;
        int _propertyFills;
        readonly int[] _statisticFills = new int[2];

        public unsafe Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        {
            Assert.Equal(ExpectedPipeline, pipeline);
            Calls++;
            if (Fail) return Result.ErrorUnknown;
            if (values.IsEmpty)
            {
                count = ExcessiveCount ? uint.MaxValue : Grow && _propertyFills > 0 ? 2u : 1u;
                return Result.Success;
            }
            Assert.Equal(StructureType.PipelineExecutablePropertiesKhr, values[0].SType);
            if (AlwaysIncomplete || Grow && _propertyFills++ == 0) return Result.Incomplete;
            for (int i = 0; i < count; i++)
            {
                var property = new PipelineExecutablePropertiesKHR
                {
                    SType = StructureType.PipelineExecutablePropertiesKhr,
                    Stages = ShaderStageFlags.ComputeBit,
                    SubgroupSize = 32
                };
                Encoding.UTF8.GetBytes("kernel", new Span<byte>(property.Name, 256));
                Encoding.UTF8.GetBytes("Compiled compute executable", new Span<byte>(property.Description, 256));
                values[i] = property;
            }
            return Result.Success;
        }

        public unsafe Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        {
            Assert.Equal(ExpectedPipeline, pipeline);
            Calls++;
            if (FailStatistics) return Result.ErrorUnknown;
            if (values.IsEmpty)
            {
                count = Empty ? 0 : Grow && _statisticFills[executable] > 0 ? 2u : 1u;
                return Result.Success;
            }
            Assert.Equal(StructureType.PipelineExecutableStatisticKhr, values[0].SType);
            if (Grow && _statisticFills[executable]++ == 0) return Result.Incomplete;
            for (int i = 0; i < count; i++)
            {
                var statistic = new PipelineExecutableStatisticKHR
                {
                    SType = StructureType.PipelineExecutableStatisticKhr,
                    Format = NonFinite ? PipelineExecutableStatisticFormatKHR.Float64Khr : Format,
                    Value = new PipelineExecutableStatisticValueKHR { U64 = ulong.MaxValue }
                };
                if (Format == PipelineExecutableStatisticFormatKHR.Bool32Khr) statistic.Value.B32 = true;
                if (Format == PipelineExecutableStatisticFormatKHR.Int64Khr) statistic.Value.I64 = long.MinValue;
                if (Format == PipelineExecutableStatisticFormatKHR.Float64Khr) statistic.Value.F64 = 0.125;
                if (NonFinite) statistic.Value.F64 = double.NaN;
                Encoding.UTF8.GetBytes("vendor cycles", new Span<byte>(statistic.Name, 256));
                Encoding.UTF8.GetBytes("Raw vendor estimate", new Span<byte>(statistic.Description, 256));
                values[i] = statistic;
            }
            return Result.Success;
        }
    }
}
