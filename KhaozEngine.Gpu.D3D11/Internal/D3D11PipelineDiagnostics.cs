using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Immutable cold evidence. Reads never reflect, create queries or alter a pipeline.</summary>
internal sealed class D3D11PipelineDiagnostics
{
    internal static readonly GpuPipelineCompilerReadRequest Request = new(2, 30, 60, 4096, 16384, 4);
    readonly GpuPipelineCompilerReport _report;
    readonly ImmutableArray<GpuDiagnosticFailure> _failures;
    readonly bool _reflectionRequested, _countsKnown;
    readonly int _nativeExecutables;
    internal GpuPipelineConstructionIdentity ConstructionIdentity => _report.Identity;

    internal D3D11PipelineDiagnostics(D3D11ShaderEvidence?[] stages, string pipeline, string layout,
        uint x = 0, uint y = 0, uint z = 0)
    {
        var known = stages.OfType<D3D11ShaderEvidence>().ToArray();
        _nativeExecutables = stages.Length;
        _countsKnown = known.Length == stages.Length && known.All(s => s.CountsComplete);
        _reflectionRequested = known.Length == stages.Length && known.All(s => s.ReflectionRequested);
        var status = known.Length != stages.Length || known.Any(s => s.Status == GpuDiagnosticStatus.Unsupported)
            ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available;
        if (known.Any(s => s.Status == GpuDiagnosticStatus.Invalid)) status = GpuDiagnosticStatus.Invalid;
        string? reason = status == GpuDiagnosticStatus.Available ? null : string.Join(" | ", stages
            .Select((s, i) => s is null ? $"Stage {i}: shader compiler metadata was not retained."
                : s.Status != GpuDiagnosticStatus.Available ? $"{s.Stage}: {s.Reason}" : null).OfType<string>());
        var executables = known.Select(s => new GpuPipelineExecutableReport("DXBC SHA256 " + s.Artifact,
            s.Stage, "main", s.Stage == "compute" ? x : 0, s.Stage == "compute" ? y : 0, s.Stage == "compute" ? z : 0,
            s.Statistics, Missing("registers", "DXBC temporary slots are not physical registers."),
            Missing("bytes", "D3D11 shader reflection does not report physical spills."),
            Missing("ratio", "Compiler reflection does not measure achieved runtime occupancy."),
            Missing("threads", "D3D11 shader reflection supplies no hardware execution-width limit."),
            Missing("threads", "D3D11 shader reflection supplies no device threadgroup-size limit."))).ToImmutableArray();
        var stageIdentities = known.Select(s => s.StageIdentity).ToImmutableArray();
        _failures = [.. known.SelectMany(s => s.Failures)];
        int flagBytes = known.Length == 0 ? 0 : checked((known.Length - 1) * 3);
        foreach (var stage in known)
            flagBytes = checked(flagBytes + GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(stage.Stage) + 2
                + GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(stage.Flags));
        if (flagBytes > 16384)
            throw new GpuDiagnosticFailureException(_failures.Add(new("d3d11.pipeline-evidence",
                new InvalidOperationException("Combined CompilerFlags exceeds 16384 UTF-8 bytes before retention."))));
        string flags = string.Join(" | ", known.Select(s => s.Stage + ": " + s.Flags));
        bool allStages = known.Length == stages.Length;
        GpuDiagnosticDigest MissingStage(string encoding) => new(null, encoding, GpuDiagnosticStatus.Invalid, "A shader stage identity was not retained.");
        var codeGeneration = !allStages ? MissingStage("khaozengine.pipeline-code-generation/1") : known.Length == 1 ? known[0].CodeGeneration : GpuDiagnosticIdentityEncoding.CodeGeneration(
            GpuBackendKind.Direct3D11Native, known.SelectMany((s, i) => s.CodeGenerationOptions.Select(o =>
                new GpuDiagnosticOption("stage." + i + "." + o.Key, o.Value))).ToImmutableArray());
        var identity = new GpuPipelineConstructionIdentity(
            GpuDiagnosticIdentityEncoding.Known(known.Length == stages.Length ? pipeline : null, "d3d11.pipeline/1"),
            allStages ? GpuDiagnosticIdentityEncoding.Stages("source", stageIdentities) : MissingStage("khaozengine.pipeline-stage-set/1"),
            allStages ? GpuDiagnosticIdentityEncoding.Stages("artifact", stageIdentities) : MissingStage("khaozengine.pipeline-stage-set/1"),
            GpuDiagnosticIdentityEncoding.Known(layout, "d3d11.layout/1"),
            known.FirstOrDefault()?.Effective is { } effective ? GpuDiagnosticIdentityEncoding.Configuration(effective)
                : GpuDiagnosticIdentityEncoding.Text(string.Join(" | ", known.Select(s => s.Configuration)), "d3d11.diagnostic-configuration/1"),
            codeGeneration, flags, x, y, z, stageIdentities);
        _report = new(identity, status, reason, executables);
    }

    internal GpuPipelineCompilerReport Read(bool invalid)
        => invalid ? _report with { Status = GpuDiagnosticStatus.Invalid, Reason = "The pipeline or its shader/device was disposed or lost.", Executables = [] }
            : _report;

    internal GpuPipelineCompilerReadResult Read(bool invalid, GpuPipelineCompilerReadRequest request,
        Action<string> checkOperation, CancellationToken cancellation)
    {
        var report = Read(invalid);
        var failures = _failures;
        string? refusal = null;
        try
        {
            if (request != Request) throw new InvalidOperationException("D3D11 requires the fixed two-by-30 compiler-read profile.");
            cancellation.ThrowIfCancellationRequested(); checkOperation("d3d11.compiler-read-before"); cancellation.ThrowIfCancellationRequested();
            if (invalid) throw new ObjectDisposedException("D3D11 pipeline", "The pipeline or its shader/device was disposed or lost.");
            if (!_reflectionRequested) throw new InvalidOperationException("D3D11 compiler reflection was not captured for every stage.");
            foreach (var digest in new[] { ConstructionIdentity.PipelineSha256, ConstructionIdentity.SourceSha256,
                ConstructionIdentity.ShaderArtifactSha256, ConstructionIdentity.LayoutSha256,
                ConstructionIdentity.DiagnosticConfigurationSha256, ConstructionIdentity.CodeGenerationSha256 })
                if (digest.Status != GpuDiagnosticStatus.Available || !GpuDiagnosticIdentityEncoding.ValidHash(digest.Sha256))
                    throw new InvalidOperationException("Construction correspondence identity is missing or invalid.");
            if (report.Status == GpuDiagnosticStatus.Invalid)
            {
                refusal = report.Reason;
                if (failures.IsEmpty) throw new InvalidOperationException("Invalid compiler evidence has no originating failure event.");
            }
            checkOperation("d3d11.compiler-read-after"); cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            if (error is GpuDiagnosticFailureException transport) failures = failures.AddRange(transport.Failures);
            else failures = failures.Add(new("d3d11.compiler-read", error));
            refusal ??= "D3D11 compiler read refused. Actual exception retained in Failures.";
        }
        bool complete = refusal is null && failures.IsEmpty;
        if (!complete) report = report with { Status = GpuDiagnosticStatus.Invalid, Reason = refusal ?? "Reflection failed. Actual exception retained in Failures." };
        int statistics = report.Executables.Sum(e => e.Statistics.Length);
        int knownStatistics = _report.Executables.Sum(e => e.Statistics.Length);
        bool countsKnown = _countsKnown;
        return new(request, report, complete, new(true, (uint)_nativeExecutables,
            countsKnown ? (ulong)knownStatistics : null, report.Executables.Length, statistics,
            (uint)(_nativeExecutables - report.Executables.Length), countsKnown ? (ulong)(knownStatistics - statistics) : null,
            countsKnown, null, null), refusal, failures);
    }

    internal static GpuDiagnosticMetric Missing(string unit, string reason)
        => new(null, unit, "ID3D11ShaderReflection::GetDesc / D3D11_SHADER_DESC", GpuDiagnosticStatus.Unsupported,
            reason, GpuDiagnosticProvenance.Unknown);
}
