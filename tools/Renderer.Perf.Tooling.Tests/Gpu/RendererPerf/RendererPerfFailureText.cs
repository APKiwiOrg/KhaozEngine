using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfFailureText
{
    internal const string OmittedOriginal = "Original failure text omitted due to evidence capacity.";
    internal static RendererPerfReport Project(RendererPerfReport report, ImmutableArray<GpuDiagnosticFailure> failures,
        int availableUtf8Bytes)
    {
        var text = Project(failures, availableUtf8Bytes);
        return report with
        {
            Publication = report.Publication with { OriginalFailure = text.Original, Errors = text.Errors, FailureText = text.Evidence },
            Assessment = report.Assessment with { ValidForPromotion = report.Assessment.ValidForPromotion && failures.IsEmpty && text.Evidence.Complete }
        };
    }
    internal static RendererPerfUnitFailure Project(RendererPerfUnitFailure evidence, ImmutableArray<GpuDiagnosticFailure> failures,
        int availableUtf8Bytes)
    {
        var text = Project(failures, availableUtf8Bytes);
        return evidence with { OriginalFailure = text.Original ?? OmittedOriginal, Errors = text.Errors, FailureText = text.Evidence };
    }
    static (string? Original, ImmutableArray<string> Errors, RendererPerfFailureTextEvidence Evidence)
        Project(ImmutableArray<GpuDiagnosticFailure> failures, int available)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(available);
        // Exact JSON string costs, array punctuation and null. The fixed omission token belongs to the header reserve.
        long required = failures.IsEmpty ? 6 : 2;
        foreach (var failure in failures) required = checked(required + Cost(failure.Error.Message));
        if (failures.Length > 2) required = checked(required + failures.Length - 2);
        int omitted = 0;
        string? original = failures.IsEmpty ? null : failures[0].Error.Message;
        var errors = ImmutableArray.CreateBuilder<string>();
        long used = 2;
        if (!failures.IsEmpty)
        {
            used = checked(used + Cost(original!));
            if (used > available) { original = OmittedOriginal; omitted = failures.Length; }
            else
                for (int i = 1; i < failures.Length; i++)
                {
                    long next = checked(used + Cost(failures[i].Error.Message) + (i > 1 ? 1 : 0));
                    if (next > available) { omitted = failures.Length - i; break; }
                    errors.Add(failures[i].Error.Message); used = next;
                }
        }
        return (original, errors.ToImmutable(), new(omitted == 0, omitted, required));
    }
    static int Cost(string text) => JsonSerializer.SerializeToUtf8Bytes(text, RendererPerfManifest.JsonOptions).Length;
}
