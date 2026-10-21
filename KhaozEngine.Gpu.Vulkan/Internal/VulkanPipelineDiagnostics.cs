using System;
using System.Collections.Immutable;
using System.Text;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal interface IVulkanPipelineExecutableApi
{
    Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values);
    Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values);
}

internal interface IVulkanPipelineDiagnosticsSource
{
    VulkanPipelineDiagnostics? Diagnostics { get; }
}

/// <summary>Cold, bounded collection. No vendor name is guessed to mean registers, spilling or occupancy.</summary>
internal sealed class VulkanPipelineDiagnostics
{
    internal static VulkanPipelineDiagnostics Production { get; } = new(false, false, false, null, "validation=unknown,statistics=off");
    const int MaximumExecutables = 64;
    const int MaximumStatistics = 256;
    const int MaximumAttempts = 4;
    readonly bool _requested, _extension, _feature;
    IVulkanPipelineExecutableApi? _api;
    readonly string _configuration;
    readonly string? _failure;

    internal VulkanPipelineDiagnostics(bool requested, bool extensionSupported, bool featureEnabled,
        IVulkanPipelineExecutableApi? api, string configuration, string? failure = null)
    {
        _requested = requested;
        _extension = extensionSupported;
        _feature = featureEnabled;
        _api = api;
        _configuration = configuration;
        _failure = failure;
    }

    internal PipelineCreateFlags CaptureFlags => _requested && _extension && _feature
        ? PipelineCreateFlags.CreateCaptureStatisticsBitKhr : 0;

    internal void Bind(IVulkanPipelineExecutableApi api) => _api = api;

    internal unsafe GpuPipelineCompilerReport Read(ulong pipeline, PipelineCreateFlags flags, VulkanPipelineIdentity identity)
    {
        string configuration = VulkanPipelineIdentity.HashText(
            $"VK_KHR_pipeline_executable_properties,requested={_requested},extension={_extension},"
            + $"pipelineExecutableInfo={_feature},flags={(uint)flags},{_configuration},{identity.CompilerFlags}");
        GpuPipelineCompilerReport Report(GpuDiagnosticStatus status, string? reason,
            ImmutableArray<GpuPipelineExecutableReport> executables = default)
            => new(identity.PipelineSha256, identity.SourceSha256, identity.ShaderArtifactSha256,
                identity.LayoutSha256, configuration, identity.CompilerFlags, status, reason,
                executables.IsDefault ? [] : executables);

        if (pipeline == 0) return Report(GpuDiagnosticStatus.Invalid, "The pipeline was disposed or has no native handle.");
        if (_failure is not null) return Report(GpuDiagnosticStatus.Invalid, _failure);
        if (!_requested) return Report(GpuDiagnosticStatus.Unsupported, "Pipeline diagnostics were not requested on this device.");
        if (!_extension) return Report(GpuDiagnosticStatus.Unsupported, "VK_KHR_pipeline_executable_properties is unavailable.");
        if (!_feature) return Report(GpuDiagnosticStatus.Unsupported, "pipelineExecutableInfo is unavailable or was not enabled.");
        if ((flags & PipelineCreateFlags.CreateCaptureStatisticsBitKhr) == 0)
            return Report(GpuDiagnosticStatus.Unsupported, "The pipeline lacks VK_PIPELINE_CREATE_CAPTURE_STATISTICS_BIT_KHR.");
        IVulkanPipelineExecutableApi? api = _api;
        if (api is null) return Report(GpuDiagnosticStatus.Invalid, "Pipeline executable entry points are unavailable.");

        try
        {
            PipelineExecutablePropertiesKHR[] properties = Enumerate<PipelineExecutablePropertiesKHR>(
                (ref uint count, Span<PipelineExecutablePropertiesKHR> values) => api.Properties(pipeline, ref count, values),
                () => new() { SType = StructureType.PipelineExecutablePropertiesKhr },
                MaximumExecutables, "vkGetPipelineExecutablePropertiesKHR");
            var executables = ImmutableArray.CreateBuilder<GpuPipelineExecutableReport>(properties.Length);
            int statisticCount = 0;
            bool invalid = false;
            for (uint index = 0; index < properties.Length; index++)
            {
                uint executableIndex = index;
                PipelineExecutableStatisticKHR[] native = Enumerate<PipelineExecutableStatisticKHR>(
                    (ref uint count, Span<PipelineExecutableStatisticKHR> values) => api.Statistics(pipeline, executableIndex, ref count, values),
                    () => new() { SType = StructureType.PipelineExecutableStatisticKhr },
                    MaximumStatistics, "vkGetPipelineExecutableStatisticsKHR");
                var statistics = ImmutableArray.CreateBuilder<GpuPipelineStatistic>(native.Length);
                foreach (PipelineExecutableStatisticKHR statistic in native)
                {
                    PipelineExecutableStatisticKHR copy = statistic;
                    GpuDiagnosticMetric metric = Metric(copy);
                    invalid |= metric.Status == GpuDiagnosticStatus.Invalid;
                    statistics.Add(new(Text(copy.Name), Text(copy.Description), metric));
                }
                statisticCount += native.Length;
                PipelineExecutablePropertiesKHR property = properties[index];
                bool compute = (property.Stages & ShaderStageFlags.ComputeBit) != 0;
                executables.Add(new(Text(property.Name), property.Stages.ToString(), "main",
                    compute ? identity.LocalSizeX : 0, compute ? identity.LocalSizeY : 0, compute ? identity.LocalSizeZ : 0,
                    statistics.MoveToImmutable(), Missing("registers", "No documented physical-register mapping is supplied."),
                    Missing("bytes", "No documented spill-byte mapping is supplied."),
                    Missing("ratio", "Compiler statistics do not measure achieved runtime occupancy."),
                    Missing("threads", "Executable subgroup size is not an execution-width limit."),
                    Missing("threads", "Executable statistics do not supply a threadgroup-size limit.")));
            }
            return Report(invalid ? GpuDiagnosticStatus.Invalid : statisticCount == 0
                    ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
                invalid ? "A vendor statistic has an unsupported format or non-finite value."
                    : statisticCount == 0 ? "The driver returned no executable statistics." : null,
                executables.MoveToImmutable());
        }
        catch (InvalidOperationException error)
        {
            return Report(GpuDiagnosticStatus.Invalid, error.Message);
        }
    }

    internal static GpuPipelineCompilerReport Disabled(bool disposed)
        => new("", "", "", "", "", "", disposed ? GpuDiagnosticStatus.Invalid : GpuDiagnosticStatus.Unsupported,
            disposed ? "The pipeline was disposed." : "Pipeline diagnostics were not requested on this device.", []);

    internal static GpuDiagnosticMetric Missing(string unit, string reason)
        => new(null, unit, "VK_KHR_pipeline_executable_properties", GpuDiagnosticStatus.Unsupported,
            reason, GpuDiagnosticProvenance.Unknown);

    static GpuDiagnosticMetric Metric(PipelineExecutableStatisticKHR statistic)
    {
        GpuDiagnosticValue? value = statistic.Format switch
        {
            PipelineExecutableStatisticFormatKHR.Bool32Khr => new(GpuDiagnosticValueType.Boolean, Boolean: (bool)statistic.Value.B32),
            PipelineExecutableStatisticFormatKHR.Int64Khr => new(GpuDiagnosticValueType.Int64, Int64: statistic.Value.I64),
            PipelineExecutableStatisticFormatKHR.Uint64Khr => new(GpuDiagnosticValueType.UInt64, UInt64: statistic.Value.U64),
            PipelineExecutableStatisticFormatKHR.Float64Khr when double.IsFinite(statistic.Value.F64)
                => new(GpuDiagnosticValueType.Float64, Float64: statistic.Value.F64),
            _ => null
        };
        return new(value, "vendor-defined", "vkGetPipelineExecutableStatisticsKHR", value is null
                ? GpuDiagnosticStatus.Invalid : GpuDiagnosticStatus.Available,
            value is null ? $"Invalid vendor statistic format or value ({statistic.Format})." : null,
            GpuDiagnosticProvenance.VendorCompiler);
    }

    delegate Result Query<T>(ref uint count, Span<T> values);

    static T[] Enumerate<T>(Query<T> query, Func<T> initialize, int capacity, string operation)
    {
        T[] storage = new T[capacity];
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            uint count = 0;
            Result counted = query(ref count, Span<T>.Empty);
            Require(counted, operation);
            if (count > capacity) throw new InvalidOperationException($"{operation} count {count} exceeds fixed capacity {capacity}.");
            if (count == 0)
            {
                if (counted == Result.Success) return [];
                continue;
            }
            uint requested = count;
            for (int i = 0; i < count; i++) storage[i] = initialize();
            Result filled = query(ref count, storage.AsSpan(0, (int)requested));
            Require(filled, operation);
            if (count > requested) throw new InvalidOperationException($"{operation} returned an out-of-range count {count}.");
            if (filled == Result.Incomplete) continue;
            return storage.AsSpan(0, (int)count).ToArray();
        }
        throw new InvalidOperationException($"{operation} remained VK_INCOMPLETE after {MaximumAttempts} attempts.");
    }

    static void Require(Result result, string operation)
    {
        if (result != Result.Success && result != Result.Incomplete)
            throw new InvalidOperationException($"{operation} failed with {result} ({(int)result}).");
    }

    static unsafe string Text(byte* value)
    {
        var bytes = new ReadOnlySpan<byte>(value, 256);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}

internal sealed record VulkanPipelineDiagnosticBinding(VulkanPipelineDiagnostics Diagnostics,
    VulkanPipelineIdentity Identity, PipelineCreateFlags Flags)
{
    internal GpuPipelineCompilerReport Read(ulong handle) => Diagnostics.Read(handle, Flags, Identity);
}
