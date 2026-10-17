using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    internal readonly record struct TemporalReviewFigures(double Error, double Change, double Temporal, double Reference)
    {
        public override string ToString() => $"{Error:0.000000}|{Change:0.000000}|{Temporal:0.000000}|{Reference:0.000000}";
    }

    /// <summary>Measurement checkpoint for the v21.2.0 comparison. The final bounds use these hosted figures.</summary>
    internal static class TemporalReviewBaseline
    {
        public static void Check(string kind, string name, TemporalReviewFigures figures, List<string> failures)
        {
            if (!double.IsFinite(figures.Error) || !double.IsFinite(figures.Change)
                || !double.IsFinite(figures.Temporal) || figures.Error < 0 || figures.Change < 0 || figures.Temporal < 0)
                failures.Add($"Invalid measurement for {kind} {name}: {figures}");
        }
    }
}
