using System;

namespace KhaozEngine.Tests.Gpu;

internal readonly record struct TemporalReviewLimits(double Error, double? Change, double Temporal,
    double Reference, double ReferenceTolerance);

internal static partial class TemporalReviewBaseline
{
    internal static TemporalReviewLimits ReportLimits(string kind, string name)
    {
        TemporalReviewFigures before;
        bool found = kind == "rim" ? TemporalShadowFilterBaseline.TryGetValue(TemporalDeviceClass.Name, name, out before)
            : Before.TryGetValue((TemporalDeviceClass.Name, kind, name), out before);
        if (!found) throw new InvalidOperationException("The diagnostic cell has no existing control.");
        return new(Limit(before.Error), kind == "diagonal" ? Limit(before.Change) : null, Limit(before.Temporal),
            before.Reference, ReferenceTolerance);
    }
}
