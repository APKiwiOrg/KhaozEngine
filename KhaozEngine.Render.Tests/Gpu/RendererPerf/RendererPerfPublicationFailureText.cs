using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// A bounded string projection only. Actual exception occurrences never travel through this owner.
internal static class RendererPerfPublicationFailureText
{
    internal static RendererPerfReport Project(RendererPerfReport report, string? original, ImmutableArray<string> prior,
        RendererPerfFailureTextEvidence evidence, ImmutableArray<GpuDiagnosticFailure> failures, int available)
    {
        bool token = !evidence.Complete && original == RendererPerfFailureText.OmittedOriginal;
        var prefix = original is null ? [] : new[] { original }.Concat(prior).ToArray();
        var added = failures.Select(f => f.Error.Message).ToArray();
        var strings = prefix.Concat(added).ToArray();
        int oldRetained = prefix.Length - (token ? 1 : 0);
        int oldCount = checked(oldRetained + evidence.OmittedFailures);
        long oldRequired = evidence.Complete ? Required(prefix) : evidence.RequiredUtf8Bytes;
        long required = oldCount == 0 ? Required(added) : oldRequired;
        if (oldCount != 0)
        {
            foreach (string value in added) required = checked(required + Cost(value));
            required = checked(required + Math.Max(0, oldCount + added.Length - 2) - Math.Max(0, oldCount - 2));
        }
        // Keep an ordered prefix. Missing earlier text cannot be hidden by appending later causes.
        int limit = evidence.Complete ? strings.Length : prefix.Length;
        long used = 2;
        int retained = token ? 1 : 0;
        for (int n = retained; n < limit; n++)
        {
            long next = checked(used + Cost(strings[n]) + (n > 1 ? 1 : 0));
            if (next > available) break;
            used = next; retained++;
        }
        int omitted = checked(oldCount + added.Length - (retained - (token ? 1 : 0)));
        return report with
        {
            Assessment = report.Assessment with { ValidForPromotion = false },
            Publication = report.Publication with
            {
                State = RendererPerfPublicationState.Failed,
                ReturnedByRun = true,
                FinalUnitPublished = false,
                OriginalFailure = retained == 0 || token ? RendererPerfFailureText.OmittedOriginal : strings[0],
                Errors = retained <= 1 ? [] : [.. strings.Skip(1).Take(retained - 1)],
                FailureText = new(omitted == 0, omitted, required)
            }
        };
    }
    static long Required(string[] values)
    {
        if (values.Length == 0) return 6;
        long result = 2;
        foreach (string value in values) result = checked(result + Cost(value));
        return checked(result + Math.Max(0, values.Length - 2));
    }
    static int Cost(string value) => JsonSerializer.SerializeToUtf8Bytes(value, RendererPerfManifest.JsonOptions).Length;
}
