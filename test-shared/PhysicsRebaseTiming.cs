using System;
using System.Text;

namespace KhaozEngine.Tests.Physics;

internal readonly record struct PhysicsRebaseTimingResult(double MedianRatio, string Summary)
{
    internal bool Passed => double.IsFinite(MedianRatio) && MedianRatio <= 3d;
}

/// <summary>Pure reporting and verdict logic, tested without running a local timing workload.</summary>
internal static class PhysicsRebaseTiming
{
    internal static PhysicsRebaseTimingResult Summarize(double[] steps, double[] rebases)
    {
        if (steps.Length != 5 || rebases.Length != 5)
            throw new ArgumentException("Exactly five interleaved step and rebase samples are required.");
        var ratios = new double[5];
        var text = new StringBuilder("## Physics rebase performance\n\n");
        text.AppendLine("25 terrain meshes, 2,000 prop statics and 200 dynamics. One step and two rebases per sample.");
        text.AppendLine();
        text.AppendLine("| Sample | Step ms | Rebase ms | Rebase / step |");
        text.AppendLine("| --- | ---: | ---: | ---: |");
        for (int i = 0; i < ratios.Length; i++)
        {
            if (!double.IsFinite(steps[i]) || steps[i] <= 0 || !double.IsFinite(rebases[i]) || rebases[i] < 0)
                throw new ArgumentException("Samples must be finite with positive step and nonnegative rebase durations.");
            ratios[i] = rebases[i] / steps[i];
            text.AppendLine(FormattableString.Invariant($"| {i + 1} | {steps[i] * 1000:F6} | {rebases[i] * 1000:F6} | {ratios[i]:F6}x |"));
        }
        Array.Sort(ratios);
        double median = ratios[2];
        text.AppendLine();
        text.AppendLine(FormattableString.Invariant($"Median paired ratio {median:F6}x. Bound 3x. Values above 3x fail this job."));
        return new(median, text.ToString());
    }
}
