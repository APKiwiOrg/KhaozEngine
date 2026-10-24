using System;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;
using static KhaozEngine.Gpu.Vulkan.Internal.VulkanPipelineDiagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>One request-frozen native enumeration, retaining prior rows and the current valid prefix on refusal.</summary>
internal sealed class VulkanCompilerRead(IVulkanPipelineExecutableApi? api, IVulkanCompilerMaterializer materializer,
    ulong pipeline, GpuPipelineConstructionIdentity identity, GpuPipelineCompilerReadRequest request,
    Action<string> checkOperation, CancellationToken cancellation)
{
    readonly ImmutableArray<GpuPipelineExecutableReport>.Builder _rows = ImmutableArray.CreateBuilder<GpuPipelineExecutableReport>();
    readonly ImmutableArray<GpuDiagnosticFailure>.Builder _failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
    uint? _executableCount, _refusedIndex, _refusedCount, _currentIndex;
    ulong? _totalStatistics;
    ulong _knownStatistics;
    int _retainedStatistics;
    bool _enumerationRequired = true, _countsComplete, _executableTotalKnown;
    string? _refusal;

    internal unsafe GpuPipelineCompilerReadResult Run(string? invalid, string? absence)
    {
        try
        {
            ValidateRequest();
            Check("read-start");
            if (GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(identity.CompilerFlags) > request.MaximumCompilerFlagsUtf8Bytes)
                Refuse("CompilerFlags exceeds the admitted UTF-8 limit.");
            foreach (var digest in new[] { identity.PipelineSha256, identity.SourceSha256, identity.ShaderArtifactSha256,
                identity.LayoutSha256, identity.DiagnosticConfigurationSha256, identity.CodeGenerationSha256 })
                if (digest.Status != GpuDiagnosticStatus.Available || !GpuDiagnosticIdentityEncoding.ValidHash(digest.Sha256))
                    Refuse("Construction correspondence identity is missing or invalid.");
            if (invalid is not null) Refuse(invalid);
            if (absence is not null)
            {
                _enumerationRequired = false; _countsComplete = true;
                return Result(GpuDiagnosticStatus.Unsupported, absence, complete: true);
            }
            var properties = Enumerate<PipelineExecutablePropertiesKHR>(
                (ref uint count, Span<PipelineExecutablePropertiesKHR> values) => api!.Properties(pipeline, ref count, values),
                materializer.Properties, () => new() { SType = StructureType.PipelineExecutablePropertiesKhr },
                count => { if (count > request.MaximumExecutables) Refuse("Executable count exceeds the admitted limit.", null, count); },
                "vkGetPipelineExecutablePropertiesKHR", properties: true);
            _executableCount = checked((uint)properties.Length);
            if (properties.Length == 0) { _totalStatistics = 0; _countsComplete = true; }
            for (uint index = 0; index < properties.Length; index++)
            {
                _currentIndex = index;
                var property = properties[index];
                string name = Text(property.Name, "Executable name");
                var statistics = ImmutableArray.CreateBuilder<GpuPipelineStatistic>();
                bool compute = (property.Stages & ShaderStageFlags.ComputeBit) != 0;
                var row = new GpuPipelineExecutableReport(name, property.Stages.ToString(), "main",
                    compute ? identity.LocalSizeX : 0, compute ? identity.LocalSizeY : 0, compute ? identity.LocalSizeZ : 0, [],
                    Missing("registers", "No documented physical-register mapping is supplied."),
                    Missing("bytes", "No documented spill-byte mapping is supplied."),
                    Missing("ratio", "Compiler statistics do not measure achieved runtime occupancy."),
                    Missing("threads", "Executable subgroup size is not an execution-width limit."),
                    Missing("threads", "Executable statistics do not supply a threadgroup-size limit."));
                try
                {
                    uint current = index;
                    var native = Enumerate<PipelineExecutableStatisticKHR>(
                        (ref uint count, Span<PipelineExecutableStatisticKHR> values) => api!.Statistics(pipeline, current, ref count, values),
                        materializer.Statistics, () => new() { SType = StructureType.PipelineExecutableStatisticKhr },
                        count =>
                        {
                            if (count > request.MaximumStatisticsPerExecutable
                                || (ulong)_retainedStatistics + count > (ulong)request.MaximumTotalStatistics)
                                Refuse("Statistic count exceeds the admitted per-executable or cumulative limit.", current, count);
                        }, "vkGetPipelineExecutableStatisticsKHR", properties: false);
                    _knownStatistics = checked(_knownStatistics + (uint)native.Length);
                    if (index + 1 == _executableCount) _totalStatistics = _knownStatistics;
                    foreach (var nativeStatistic in native)
                    {
                        Check("statistic-conversion-before");
                        var copy = nativeStatistic;
                        string statisticName = Text(copy.Name, "Statistic name");
                        string description = Text(copy.Description, "Statistic description");
                        var metric = Metric(copy);
                        statistics.Add(new(statisticName, description, metric)); _retainedStatistics++;
                        Check("statistic-conversion-after");
                        if (metric.Status == GpuDiagnosticStatus.Invalid) Refuse("A vendor statistic has an unsupported format or non-finite value.", index);
                    }
                }
                finally { _rows.Add(row with { Statistics = statistics.ToImmutable() }); }
            }
            return Result(_retainedStatistics == 0 ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
                _retainedStatistics == 0 ? "The driver returned no executable statistics." : null, complete: true);
        }
        catch (Exception error)
        {
            _refusedIndex ??= _currentIndex;
            if (error is GpuDiagnosticFailureException transport) _failures.AddRange(transport.Failures);
            else _failures.Add(new("vulkan.compiler-read", error));
            _refusal ??= error is OperationCanceledException ? "Compiler read was cancelled."
                : "Compiler read failed. Actual exception retained in Failures.";
            return Result(GpuDiagnosticStatus.Invalid, _refusal, complete: false);
        }
    }

    void ValidateRequest()
    {
        if (request.MaximumExecutables is < 1 or > 64 || request.MaximumStatisticsPerExecutable is < 0 or > 256
            || request.MaximumTotalStatistics < 0 || request.MaximumTotalStatistics > Math.Min(16384,
                checked(request.MaximumExecutables * request.MaximumStatisticsPerExecutable))
            || request.MaximumTextUtf8Bytes is < 1 or > 256 || request.MaximumCompilerFlagsUtf8Bytes is < 1 or > 16384
            || request.MaximumEnumerationAttempts != 4)
            Refuse("Invalid Vulkan compiler-read request.");
    }

    delegate Silk.NET.Vulkan.Result Query<T>(ref uint count, Span<T> values);
    T[] Enumerate<T>(Query<T> query, Func<int, T[]> allocate, Func<T> initialize, Action<uint> admit, string operation, bool properties)
    {
        for (int attempt = 0; attempt < request.MaximumEnumerationAttempts; attempt++)
        {
            uint count = 0;
            Check(operation + "/count-before");
            UnknownCounts(properties);
            var counted = query(ref count, Span<T>.Empty);
            if (counted is Silk.NET.Vulkan.Result.Success or Silk.NET.Vulkan.Result.Incomplete)
                ObserveCount(count, counted == Silk.NET.Vulkan.Result.Success, properties);
            Check(operation + "/count-after"); Require(counted, operation); admit(count);
            if (count == 0)
            {
                if (counted == Silk.NET.Vulkan.Result.Success) return [];
                continue;
            }
            uint requested = count;
            var storage = allocate(checked((int)requested));
            if (storage.Length != requested) Refuse("Native output storage does not match its admitted capacity.");
            for (int i = 0; i < storage.Length; i++) storage[i] = initialize();
            Check(operation + "/fill-before");
            UnknownCounts(properties);
            var filled = query(ref count, storage);
            if (filled is Silk.NET.Vulkan.Result.Success or Silk.NET.Vulkan.Result.Incomplete)
                ObserveCount(count, filled == Silk.NET.Vulkan.Result.Success && count <= requested, properties);
            Check(operation + "/fill-after"); Require(filled, operation);
            if (count > requested) Refuse(operation + " returned a count above its supplied output capacity.", _refusedIndex, count);
            admit(count);
            if (filled == Silk.NET.Vulkan.Result.Incomplete) continue;
            if (count == requested) return storage;
            return storage.AsSpan(0, checked((int)count)).ToArray();
        }
        Refuse(operation + " remained VK_INCOMPLETE after 4 attempts.");
        return [];
    }

    void UnknownCounts(bool properties)
    {
        _totalStatistics = null; _countsComplete = false;
        if (properties) _executableTotalKnown = false;
    }

    void ObserveCount(uint count, bool complete, bool properties)
    {
        if (properties)
        {
            _executableCount = count; _executableTotalKnown = complete;
            if (complete && count == 0) { _totalStatistics = 0; _countsComplete = true; }
        }
        else if (complete && _currentIndex is { } index && _executableCount is { } total && index + 1 == total)
        {
            _totalStatistics = checked(_knownStatistics + count); _countsComplete = true;
        }
    }

    void Require(Silk.NET.Vulkan.Result result, string operation)
    {
        if (result != Silk.NET.Vulkan.Result.Success && result != Silk.NET.Vulkan.Result.Incomplete)
            Refuse($"{operation} failed with {result} ({(int)result}).");
    }

    unsafe string Text(byte* pointer, string field)
    {
        Check("text-conversion-before");
        var bytes = new ReadOnlySpan<byte>(pointer, 256);
        int length = bytes.IndexOf((byte)0);
        if (length < 0 || length > request.MaximumTextUtf8Bytes)
            Refuse(field + " is unterminated or exceeds the admitted UTF-8 limit.");
        var value = bytes[..length];
        _ = GpuDiagnosticIdentityEncoding.Utf8.GetCharCount(value);
        if (length == 0) Refuse(field + " is empty.");
        string result = materializer.Text(value);
        Check("text-conversion-after");
        return result;
    }

    void Check(string operation) { cancellation.ThrowIfCancellationRequested(); checkOperation(operation); cancellation.ThrowIfCancellationRequested(); }
    void Refuse(string reason, uint? index = null, uint? count = null)
    {
        _refusal = reason; _refusedIndex = index ?? _currentIndex; _refusedCount = count;
        throw new InvalidOperationException(reason);
    }

    GpuPipelineCompilerReadResult Result(GpuDiagnosticStatus status, string? reason, bool complete)
        => new(request, new(identity, status, reason, _rows.ToImmutable()), complete,
            new(_enumerationRequired, _executableCount, _totalStatistics, _rows.Count, _retainedStatistics,
                _enumerationRequired && _executableTotalKnown && _executableCount is { } n ? n - (uint)_rows.Count : null,
                _enumerationRequired && _totalStatistics is { } t ? t - (ulong)_retainedStatistics : null,
                _countsComplete, _refusedIndex, _refusedCount), _refusal, _failures.ToImmutable());

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
            value is null ? "Invalid vendor statistic format or value." : null, GpuDiagnosticProvenance.VendorCompiler);
    }
}
