using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Immutable cold evidence. A read never calls reflection, creates a query or changes a pipeline.</summary>
internal sealed class D3D11PipelineDiagnostics
{
    readonly GpuPipelineCompilerReport _report;

    internal D3D11PipelineDiagnostics(D3D11ShaderEvidence?[] stages, string pipeline, string layout,
        uint x = 0, uint y = 0, uint z = 0)
    {
        var known = stages.OfType<D3D11ShaderEvidence>().ToArray();
        var status = known.Length != stages.Length || known.Any(s => s.Status == GpuDiagnosticStatus.Unsupported)
            ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available;
        if (known.Any(s => s.Status == GpuDiagnosticStatus.Invalid)) status = GpuDiagnosticStatus.Invalid;
        string? reason = status == GpuDiagnosticStatus.Available ? null : string.Join(" | ", stages
            .Select((s, i) => s is null ? $"Stage {i}: shader compiler metadata was not retained."
                : s.Status != GpuDiagnosticStatus.Available ? $"{s.Stage}: {s.Reason}" : null)
            .OfType<string>());
        var executables = known.Select(s => new GpuPipelineExecutableReport("DXBC SHA256 " + s.Artifact,
            s.Stage, "main", s.Stage == "compute" ? x : 0, s.Stage == "compute" ? y : 0, s.Stage == "compute" ? z : 0,
            s.Statistics, Missing("registers", "DXBC temporary slots are not physical registers."),
            Missing("bytes", "D3D11 shader reflection does not report physical spills."),
            Missing("ratio", "Compiler reflection does not measure achieved runtime occupancy."),
            Missing("threads", "D3D11 shader reflection supplies no hardware execution-width limit."),
            Missing("threads", "D3D11 shader reflection supplies no device threadgroup-size limit."))).ToImmutableArray();
        string Join(Func<D3D11ShaderEvidence, string> field) => known.Length != stages.Length ? "" : known.Length == 1 ? field(known[0])
            : D3D11PipelineIdentity.Digest(w => { w.Write(known.Length); foreach (var s in known) w.Write(field(s)); });
        _report = new(known.Length == stages.Length ? pipeline : "", Join(s => s.Source), Join(s => s.Artifact), layout,
            Join(s => D3D11ShaderEvidence.Hash(System.Text.Encoding.UTF8.GetBytes(s.Configuration))),
            string.Join(" | ", known.Select(s => s.Stage + ": " + s.Flags)), status, reason, executables);
    }

    internal GpuPipelineCompilerReport Read(bool invalid)
        => invalid ? _report with { Status = GpuDiagnosticStatus.Invalid, Reason = "The pipeline or its shader/device was disposed or lost.", Executables = [] }
            : _report;

    internal static GpuDiagnosticMetric Missing(string unit, string reason)
        => new(null, unit, "ID3D11ShaderReflection::GetDesc / D3D11_SHADER_DESC", GpuDiagnosticStatus.Unsupported,
            reason, GpuDiagnosticProvenance.Unknown);
}
