using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The error a history kept through the turn gives the followed avatar's inner pixels
    /// (<see cref="StopRun.Inner"/>) on the turn frame and each of the 15 after, for each default walk of
    /// <see cref="TemporalFollowStopGpuTests"/>: the resolve's, measured on Metal, when the avatar's own pixels stored
    /// only the moved mark, before the band marked them (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23). Keyed by the
    /// run's name.
    /// </summary>
    internal static class TemporalFollowStopKeptHistory
    {
        internal static readonly IReadOnlyDictionary<string, double[]> InnerErrors = new Dictionary<string, double[]>
        {
            ["orthographic, 1 px a frame, Stop, Native"] =
                new[] { 0.00455, 0.00458, 0.00471, 0.00453, 0.00459, 0.00459, 0.00468, 0.00458,
                    0.00461, 0.00463, 0.00475, 0.00459, 0.00461, 0.00464, 0.00473, 0.00465 },
            ["perspective Away at pitch 0.75, 1 px a frame, Stop, Native"] =
                new[] { 0.01689, 0.01681, 0.01691, 0.01683, 0.01674, 0.01678, 0.01683, 0.01697,
                    0.01688, 0.01687, 0.01697, 0.01681, 0.01679, 0.01686, 0.01691, 0.01701 },
            ["perspective Sideways at pitch 0.75, 1 px a frame, Stop, Native"] =
                new[] { 0.01689, 0.01681, 0.01691, 0.01683, 0.01674, 0.01678, 0.01683, 0.01697,
                    0.01688, 0.01687, 0.01697, 0.01681, 0.01679, 0.01686, 0.01691, 0.01701 },
            ["orthographic, 1 px a frame, Stop, Quality"] =
                new[] { 0.01183, 0.01183, 0.01168, 0.01176, 0.01196, 0.01193, 0.01204, 0.01194,
                    0.01185, 0.01171, 0.01172, 0.01176, 0.01195, 0.01201, 0.01197, 0.01189 },
            ["perspective Away at pitch 0.75, 1 px a frame, Stop, Quality"] =
                new[] { 0.02046, 0.02054, 0.02060, 0.02090, 0.02081, 0.02047, 0.02055, 0.02061,
                    0.02056, 0.02039, 0.02031, 0.02040, 0.02056, 0.02055, 0.01998, 0.02039 },
            ["perspective Sideways at pitch 0.75, 1 px a frame, Stop, Quality"] =
                new[] { 0.02046, 0.02054, 0.02060, 0.02090, 0.02081, 0.02047, 0.02055, 0.02061,
                    0.02056, 0.02039, 0.02031, 0.02040, 0.02056, 0.02055, 0.01998, 0.02039 },
            ["orthographic, 1 px a frame, DampedStop, Native"] =
                new[] { 0.01825, 0.02032, 0.02058, 0.02159, 0.02096, 0.02169, 0.02302, 0.02105,
                    0.02023, 0.02119, 0.02172, 0.02107, 0.02043, 0.01989, 0.02026, 0.01870 },
            ["perspective Away at pitch 0.75, 1 px a frame, DampedStop, Native"] =
                new[] { 0.01918, 0.02055, 0.01810, 0.02174, 0.01819, 0.01972, 0.02295, 0.01898,
                    0.01750, 0.01931, 0.02063, 0.02222, 0.02089, 0.02027, 0.01873, 0.01755 },
            ["perspective Sideways at pitch 0.75, 1 px a frame, DampedStop, Native"] =
                new[] { 0.01881, 0.01908, 0.01983, 0.01952, 0.02043, 0.01855, 0.02014, 0.02016,
                    0.01965, 0.01873, 0.01960, 0.02013, 0.02050, 0.02045, 0.02005, 0.01935 },
            ["orthographic, 1 px a frame, DampedStop, Quality"] =
                new[] { 0.02405, 0.02460, 0.02431, 0.02619, 0.02523, 0.02567, 0.02693, 0.02583,
                    0.02486, 0.02545, 0.02623, 0.02631, 0.02624, 0.02545, 0.02445, 0.02369 },
            ["perspective Away at pitch 0.75, 1 px a frame, DampedStop, Quality"] =
                new[] { 0.02376, 0.02474, 0.02056, 0.02581, 0.02144, 0.02300, 0.02648, 0.02270,
                    0.02041, 0.02298, 0.02480, 0.02589, 0.02567, 0.02494, 0.02268, 0.02141 },
            ["perspective Sideways at pitch 0.75, 1 px a frame, DampedStop, Quality"] =
                new[] { 0.02210, 0.02211, 0.02248, 0.02232, 0.02281, 0.02172, 0.02237, 0.02258,
                    0.02322, 0.02214, 0.02304, 0.02204, 0.02247, 0.02298, 0.02252, 0.02198 },
            ["orthographic, 1 px a frame, Reversal, Native"] =
                new[] { 0.00455, 0.00458, 0.00471, 0.00453, 0.00459, 0.00459, 0.00468, 0.00458,
                    0.00461, 0.00463, 0.00475, 0.00459, 0.00461, 0.00464, 0.00473, 0.00465 },
            ["perspective Away at pitch 0.75, 1 px a frame, Reversal, Native"] =
                new[] { 0.01689, 0.01681, 0.01691, 0.01683, 0.01674, 0.01678, 0.01683, 0.01697,
                    0.01688, 0.01687, 0.01697, 0.01681, 0.01679, 0.01686, 0.01691, 0.01701 },
            ["perspective Sideways at pitch 0.75, 1 px a frame, Reversal, Native"] =
                new[] { 0.01692, 0.01681, 0.01691, 0.01683, 0.01674, 0.01681, 0.01683, 0.01697,
                    0.01688, 0.01687, 0.01697, 0.01681, 0.01679, 0.01686, 0.01693, 0.01701 },
            ["orthographic, 1 px a frame, Reversal, Quality"] =
                new[] { 0.01183, 0.01183, 0.01168, 0.01176, 0.01196, 0.01193, 0.01204, 0.01194,
                    0.01185, 0.01171, 0.01172, 0.01176, 0.01195, 0.01201, 0.01197, 0.01189 },
            ["perspective Away at pitch 0.75, 1 px a frame, Reversal, Quality"] =
                new[] { 0.02046, 0.02054, 0.02060, 0.02090, 0.02081, 0.02047, 0.02055, 0.02061,
                    0.02056, 0.02039, 0.02031, 0.02040, 0.02056, 0.02055, 0.01998, 0.02039 },
            ["perspective Sideways at pitch 0.75, 1 px a frame, Reversal, Quality"] =
                new[] { 0.02046, 0.02054, 0.02060, 0.02090, 0.02081, 0.02047, 0.02055, 0.02061,
                    0.02056, 0.02039, 0.02031, 0.02040, 0.02056, 0.02055, 0.01998, 0.02039 },
        };
    }
}
