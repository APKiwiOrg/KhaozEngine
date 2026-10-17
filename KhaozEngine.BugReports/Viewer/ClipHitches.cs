using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

// The viewer's hitch rule, computed here so it is testable: a frame is a hitch when its render time is strictly
// over twice the clip's median render time. The median of an even count is the mean of the middle two, taken as
// a + (b - a) / 2 so two huge values cannot overflow to infinity.
internal static class ClipHitches
{
    public static (double Median, IReadOnlyList<int> Hitches) Find(IReadOnlyList<ClipIndexFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) return (0, []);

        double[] sorted = new double[frames.Count];
        for (int i = 0; i < sorted.Length; i++) sorted[i] = frames[i].RenderMilliseconds;
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        double median = sorted.Length % 2 == 1 ? sorted[middle] : sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) / 2;

        var hitches = new List<int>();
        for (int i = 0; i < frames.Count; i++)
            if (frames[i].RenderMilliseconds > 2 * median) hitches.Add(i);
        return (median, hitches);
    }
}
