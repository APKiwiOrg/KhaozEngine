using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal;

internal readonly record struct MetalComputeLimits(ulong Width, ulong MaximumThreads);

/// <summary>Cold source identity, advertised counter metadata and compute limits. No reflection or capture flags.</summary>
internal sealed class MetalPipelineDiagnostics
{
    readonly ImmutableArray<MetalShaderEvidence> _stages;
    internal GpuPipelineConstructionIdentity ConstructionIdentity { get; }
    readonly uint _x, _y, _z;
    readonly Func<MetalComputeLimits>? _readLimits;
    readonly Func<MetalCounterMetadata>? _readCounters;

    internal MetalPipelineDiagnostics(ImmutableArray<MetalShaderEvidence> stages, string pipeline, string layout,
        Func<MetalComputeLimits>? readLimits = null, Func<MetalCounterMetadata>? readCounters = null,
        uint x = 0, uint y = 0, uint z = 0)
    {
        _stages = stages;
        _x = x; _y = y; _z = z; _readLimits = readLimits; _readCounters = readCounters;
        var stageIdentities = stages.Select(s => new GpuPipelineStageIdentity(s.Stage.ToString().ToLowerInvariant(),
            s.EntryPoint, GpuDiagnosticIdentityEncoding.Known(s.SourceSha256, "glsl.utf8/1"),
            GpuDiagnosticIdentityEncoding.Text(s.Msl, "msl.utf8/1"), new(null, "metal.native-artifact/1",
                GpuDiagnosticStatus.Unsupported, "The source-compilation path retains no native Metal compiler artifact. MSL is source."))).ToImmutableArray();
        ConstructionIdentity = new(GpuDiagnosticIdentityEncoding.Known(stages.IsEmpty ? null : pipeline, "metal.pipeline/1"),
            GpuDiagnosticIdentityEncoding.Stages("source", stageIdentities), GpuDiagnosticIdentityEncoding.Stages("artifact", stageIdentities),
            GpuDiagnosticIdentityEncoding.Known(layout, "metal.layout/1"),
            stages.FirstOrDefault()?.Effective is { } effective ? GpuDiagnosticIdentityEncoding.Configuration(effective)
                : GpuDiagnosticIdentityEncoding.Text(string.Join(" | ", stages.Select(s => s.Configuration)), "metal.diagnostic-configuration/1"),
            GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.MetalNative,
                [new("frontend", SpirvFrontEndPin.Identity), new("msl.compile", MslCompilePin.Identity),
                    new("msl.cross", MslCrossCompilePin.Identity), new("toolchain", SpirvToolchainVersion.Identity)]),
            string.Join(" | ", stages.Select(s => s.Stage + ": " + s.Configuration)), x, y, z, stageIdentities);
    }

    internal GpuPipelineCompilerReport Read(bool invalid)
    {
        const string unavailable = "The source-compilation path retains no native Metal compiler artifact, physical registers or spills. "
            + "MSL is source. Counter availability and compute limits are not compiler statistics or measured occupancy.";
        if (invalid) return Report(GpuDiagnosticStatus.Invalid, "The pipeline or its shader/device was disposed or lost.", []);
        bool missingIdentity = ConstructionIdentity.PipelineSha256.Status == GpuDiagnosticStatus.Invalid
            || ConstructionIdentity.SourceSha256.Status == GpuDiagnosticStatus.Invalid
            || ConstructionIdentity.LayoutSha256.Status == GpuDiagnosticStatus.Invalid
            || ConstructionIdentity.Stages.Any(s => s.EmittedSourceSha256?.Status == GpuDiagnosticStatus.Invalid);
        GpuDiagnosticStatus status = missingIdentity ? GpuDiagnosticStatus.Invalid : GpuDiagnosticStatus.Unsupported;
        string reason = unavailable + (missingIdentity ? " Construction identity is missing or invalid." : "");
        var width = Missing("threads", "No compute pipeline limit was queried.");
        var maximum = width;
        if (_readLimits is not null)
        {
            try
            {
                var limits = _readLimits();
                if (limits.Width == 0 || limits.MaximumThreads == 0)
                    throw new InvalidOperationException("Metal returned a zero compute execution-width or threadgroup limit.");
                width = Limit(limits.Width, "MTLComputePipelineState.threadExecutionWidth");
                maximum = Limit(limits.MaximumThreads, "MTLComputePipelineState.maxTotalThreadsPerThreadgroup");
            }
            catch (Exception e)
            {
                var failure = e is NotSupportedException ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Invalid;
                width = maximum = Missing("threads", e.Message, failure);
                if (failure == GpuDiagnosticStatus.Invalid) status = failure;
                reason += " " + e.Message;
            }
        }
        var statistics = ImmutableArray.CreateBuilder<GpuPipelineStatistic>();
        if (_readCounters is not null)
        {
            MetalCounterMetadata counters;
            try { counters = _readCounters(); }
            catch (Exception e) { counters = new([], [], e is NotSupportedException ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Invalid, e.Message); }
            if (counters.Status == GpuDiagnosticStatus.Invalid) status = counters.Status;
            if (counters.Reason is not null) reason += " " + counters.Reason;
            if (counters.TimestampCapability is { } timing)
            {
                statistics.Add(new("timestamp-counters-usable", "Actual timing capability probe, not a sampled GPU duration", timing));
                if (timing.Status == GpuDiagnosticStatus.Invalid) status = timing.Status;
                if (timing.Reason is not null) reason += " " + timing.Reason;
            }
            foreach (var set in counters.Sets)
            {
                statistics.Add(Advertised(set.Name, "MTLCounterSet.name, advertised set only, no runtime sample"));
                foreach (var counter in set.Counters)
                    statistics.Add(Advertised(counter, "MTLCounter.name in set '" + set.Name + "', advertised only, no runtime sample"));
            }
            foreach (uint point in counters.SamplingPoints)
                statistics.Add(Advertised(point switch
                {
                    0 => "MTLCounterSamplingPointAtStageBoundary",
                    1 => "MTLCounterSamplingPointAtDrawBoundary",
                    2 => "MTLCounterSamplingPointAtDispatchBoundary",
                    3 => "MTLCounterSamplingPointAtTileDispatchBoundary",
                    4 => "MTLCounterSamplingPointAtBlitBoundary",
                    _ => throw new InvalidOperationException("Unknown Metal sampling point.")
                }, "MTLDevice.supportsCounterSampling, advertised boundary only, no runtime sample"));
        }
        if (_stages.IsEmpty) reason += " Shader source metadata was not retained.";
        var executables = _stages.Select((s, index) => new GpuPipelineExecutableReport("MSL source SHA256 " + (ConstructionIdentity.Stages[index].EmittedSourceSha256?.Sha256 ?? "unavailable"),
            s.Stage.ToString().ToLowerInvariant(), s.EntryPoint,
            s.Stage == MetalShaderStage.Compute ? _x : 0, s.Stage == MetalShaderStage.Compute ? _y : 0,
            s.Stage == MetalShaderStage.Compute ? _z : 0, statistics.ToImmutable(),
            Missing("registers", "The Metal source-compilation API supplies no physical register counts."),
            Missing("bytes", "The Metal source-compilation API supplies no physical spill bytes."),
            Missing("ratio", "Advertised counters and thread limits do not measure achieved runtime occupancy."),
            s.Stage == MetalShaderStage.Compute ? width : Missing("threads", "Execution width is a compute-pipeline limit."),
            s.Stage == MetalShaderStage.Compute ? maximum : Missing("threads", "Maximum threads per group is a compute-pipeline limit."))).ToImmutableArray();
        return Report(status, reason, executables);
    }

    GpuPipelineCompilerReport Report(GpuDiagnosticStatus status, string reason, ImmutableArray<GpuPipelineExecutableReport> executables)
        => new(ConstructionIdentity, status, reason, executables);

    static GpuPipelineStatistic Advertised(string name, string source) => new(name, source,
        new(new(GpuDiagnosticValueType.Boolean, Boolean: true), "boolean", source,
            GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.HardwareLimit));
    static GpuDiagnosticMetric Limit(ulong value, string source) => new(new(GpuDiagnosticValueType.UInt64, UInt64: value),
        "threads", source, GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.HardwareLimit);
    static GpuDiagnosticMetric Missing(string unit, string reason, GpuDiagnosticStatus status = GpuDiagnosticStatus.Unsupported)
        => new(null, unit, "Metal pipeline diagnostics", status, reason, GpuDiagnosticProvenance.Unknown);
}
