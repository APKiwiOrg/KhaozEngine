using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class TemporalFollowPhaseGpuTests
    {
        /// <summary>Every cell of a table within its bound from every start phase run. The bounds are the follow
        /// facts' own, set from the worst over the phases (#1207), so a cell over one is a regression, not a
        /// phase.</summary>
        static void Hold(IReadOnlyList<PhaseSweep> sweeps, string family)
        {
            string[] over = sweeps.Where(s => s.Over > 0)
                .Select(s => $"{s.Cell}: worst {s.Worst} at phase {s.WorstPhase}, {s.Over} of {s.Phases.Length} "
                    + $"phases over {(s.AtLeast ? s.Bounds.Max() : s.Bounds.Min())}")
                .ToArray();
            Assert.True(over.Length == 0, $"{family} over its bound from some start phase: {string.Join(". ", over)}");
        }
    }
}
