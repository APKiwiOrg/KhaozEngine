using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class TemporalFollowStopGpuTests
    {
        /// <summary>
        /// The walks whose whole set reads past <see cref="WholeShare"/> or <see cref="DampedWholeShare"/> of its
        /// control's error over it from some start phase of the jitter (<see cref="TemporalFollowPhaseGpuTests"/>),
        /// and the share each may read, in thousandths: a regression bound, the worst over the phases measured and
        /// about a quarter more. The outline's pixels lie within the reconstruction's reach of the avatar's edge,
        /// where a pixel keeps one history, which either holds the edge's anti-aliasing in place or shows the ground
        /// or wall passing under it, and the jitter decides which
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1207">#1207</see>). So what an outline pixel
        /// held before the stop differs from its control's by the phase. A history for the edge's coverage apart
        /// from the surface's is the fix
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1191">#1191</see>).
        /// The inner pixels, farther inside the outline than the reconstruction's reach, read within their control's
        /// error from every phase measured, and within <see cref="DampedInnerShare"/> under the damped camera, 0.008
        /// at most. Keyed by the walk's name on its first phase.
        /// </summary>
        internal static readonly Dictionary<string, int> SweptWholeShares = new(StringComparer.Ordinal)
        {
            ["orthographic, 1 px a frame, Stop, Quality"] = 460,                      // 368 over 18 phases
            ["orthographic, 1 px a frame, Reversal, Quality"] = 436,                  // 349 over 4 phases
            ["orthographic, 1 px a frame, DampedStop, Quality"] = 142,                // 114 over 4 phases
            ["orthographic, 1 px a frame, Stop, a passer at 1 px a frame behind, Quality"] = 297,       // 238 over 4
            ["orthographic, 1 px a frame, Reversal, a passer at 1 px a frame behind, Quality"] = 302,   // 242 over 4
            ["perspective Away at pitch 0.75, 1 px a frame, Stop, Quality"] = 103,    // 83 over 18 phases
            // 35 over 4 phases
            ["perspective Away at pitch 0.75, 1 px a frame, Reversal, a passer at 1 px a frame behind, Native"] = 43,
        };

        // The share of SweptWholeShares for r's walk, or null where it holds none.
        static double? SweptWholeShare(StopRun r)
        {
            int cut = r.Name.IndexOf(", from jitter phase", StringComparison.Ordinal);
            string name = cut < 0 ? r.Name : r.Name[..cut];
            return SweptWholeShares.TryGetValue(name, out int share) ? share / 1000.0 : null;
        }
    }
}
