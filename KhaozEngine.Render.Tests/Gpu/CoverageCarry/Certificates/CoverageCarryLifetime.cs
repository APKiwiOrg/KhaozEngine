using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoverageCarryReuseScenario(int Frames, int RunsWithReuse, double MinimumRemainingSeconds,
    double MedianRemainingSeconds, int AlreadyExpiredOnCompletion, int ReenteredOnCompletion,
    int UnobservedCompletions, int RightCensoredCompletions);
internal sealed record CoverageCarryLifetime(string Motion, int Hz, int Samples, int CoveredFrames,
    int CompleteRuns, int RightCensoredFrames, int MinimumFrames, double MedianFrames, double MinimumSeconds,
    double MedianSeconds, double ExpiriesPerActorSecond, int SynchronizedEvents, int MaximumSynchronizedActors,
    CoverageCarryReuseScenario[] Scenarios, bool[] Admitted);

internal static class CoverageCarryReplay
{
    // Four seconds per rate, one pass. These are explicitly synthetic extensions of F0's two poses, not private
    // game clips. Fixed world amplitudes exercise tiny accumulation, reversal, loop wrap and three sampler jumps.
    internal static CoverageCarryLifetime Run(CoverageCarryCell cell, int hz)
    {
        var flags = new bool[4 * hz];
        for (int frame = 0; frame < flags.Length; frame++)
        {
            double time = frame / (double)hz;
            double jumped = time + (time >= .75 ? .1 : 0) + (time >= 1.75 ? .25 : 0) + (time >= 2.75 ? 1 : 0);
            double phase = (time < 2 ? jumped : 4 - jumped) % 4;
            if (phase < 0) phase += 4;
            double excursion = phase < 1 ? .000001 * Math.Sin(phase * Math.Tau)
                : phase < 2 ? .004 * (phase - 1) : phase < 3 ? .004 * (3 - phase)
                : .00005 * Math.Sin(4 * Math.Tau * (phase - 3));
            var palette = (Matrix4x4[])cell.Center.Clone();
            // Keep the root and its attached equipment fixed. The hip alone deforms, matching F0's actual
            // attachment relationship. The wider six-coefficient domain is independently falsified elsewhere.
            for (int bone = 1; bone < 2; bone++)
            {
                palette[bone].M41 += (float)(.000001 * Math.Sin(time * 2.3 + bone));
                palette[bone].M42 += (float)(.000001 * Math.Cos(time * 1.7 + bone));
                palette[bone].M43 += (float)(excursion + .000001 * Math.Sin(time * 3.1 + bone));
            }
            flags[frame] = CoverageCarryCellBinding.Admits(cell, palette, cell.Commands);
        }
        return Summarize(cell.Motion, hz, flags);
    }

    internal static CoverageCarryLifetime Alternating(CoverageCarryCell[] cells, int hz)
    {
        var flags = new bool[4 * hz];
        for (int frame = 0; frame < flags.Length; frame++)
        {
            var c = cells[frame % 2];
            // E1's actual alternating palette AND root-attached equipment. This checks an existing selected
            // anchor, with no refresh/atlas switch masquerading as a long reuse lifetime.
            flags[frame] = CoverageCarryCellBinding.Admits(cells[0], c.Center, c.Commands);
        }
        return Summarize("E1 alternating, lifted anchor", hz, flags);
    }

    internal static CoverageCarryLifetime Summarize(string motion, int hz, bool[] flags)
    {
        var complete = new List<(int Start, int Frames)>(); int length = 0, start = 0;
        for (int frame = 0; frame < flags.Length; frame++)
        {
            if (flags[frame]) { if (length == 0) start = frame; length++; }
            else if (length > 0) { complete.Add((start, length)); length = 0; }
        }
        static double Median(IEnumerable<int> sequence)
        {
            var a = sequence.Order().ToArray();
            return a.Length == 0 ? 0 : (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2.0;
        }
        var scenarios = new[] { 4, 8, 16 }.Select(delay =>
        {
            var remaining = new List<int>(); int expired = 0, reentered = 0, unobserved = 0, censored = 0;
            foreach (var run in complete)
            {
                int completion = run.Start + delay;
                if (completion >= flags.Length) { unobserved++; continue; }
                if (!flags[completion]) { expired++; remaining.Add(0); continue; }
                if (completion >= run.Start + run.Frames) reentered++;
                int end = completion;
                while (end < flags.Length && flags[end]) end++;
                if (end == flags.Length) { censored++; continue; }
                remaining.Add(end - completion);
            }
            return new CoverageCarryReuseScenario(delay, complete.Count(n => n.Frames - 1 >= delay),
                remaining.Count == 0 ? 0 : remaining.Min() / (double)hz, Median(remaining) / hz,
                expired, reentered, unobserved, censored);
        }).ToArray();
        int minimum = complete.Count == 0 ? 0 : complete.Min(n => n.Frames); double median = Median(complete.Select(n => n.Frames));
        return new(motion, hz, flags.Length, flags.Count(x => x), complete.Count, length, minimum, median,
            minimum / (double)hz, median / hz, complete.Count / (flags.Length / (double)hz), complete.Count,
            complete.Count > 0 ? 32 : 0, scenarios, flags);
    }
}
