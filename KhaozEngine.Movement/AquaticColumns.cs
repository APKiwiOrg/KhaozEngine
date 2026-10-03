using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>The column view an aquatic profile reads. Each swim-deep column trades its surfaces below the water for one
/// float surface where the body rests while swimming. Every other column is the captured column.</summary>
internal static class AquaticColumns
{
    /// <summary>Derives the aquatic view of <paramref name="captured"/> for one body. For a column with water at
    /// <c>W</c>, <c>s</c> is the highest surface below <c>W</c> and <c>u</c> the lowest at or above it. The column is
    /// swim-deep when there is no <c>s</c>, or its depth reaches the enter fraction, and the float height
    /// <c>f = W - Submersion x H</c> lies above <c>s</c> and below <c>W</c>. A float at <c>W</c>, from a zero
    /// submersion fraction, would share its height with a kept surface at <c>W</c>, so it is never emitted. A
    /// swim-deep column replaces every surface below <c>W</c> with one float surface at <c>f</c> carrying the water's
    /// areas. Its headroom is <c>s</c>'s headroom less the rise to
    /// <c>f</c>, clamped at zero, or, without <c>s</c>, the gap up to <c>u</c> or infinity. Surfaces at or above
    /// <c>W</c> are kept. Single precision in this order, so every build derives the same bits. The result carries the
    /// captured water entries.</summary>
    internal static PhysicsNavColumns Derive(PhysicsNavColumns captured, in MoveTuning tuning)
    {
        ReadOnlySpan<PhysicsNavWater> water = captured.Water;
        int width = captured.Width, cells = width * captured.Height;
        var starts = new int[cells + 1];
        var surfaces = new PhysicsNavSurface[captured.SurfaceCount + water.Length];
        var floats = new bool[surfaces.Length];
        float bodyHeight = 2f * tuning.CapsuleHalfHeight;
        int stored = 0, next = 0;
        for (int cell = 0; cell < cells; cell++)
        {
            starts[cell] = stored;
            ReadOnlySpan<PhysicsNavSurface> column = captured.GetColumn(cell % width, cell / width);
            int kept = 0;
            if (next < water.Length && water[next].Cell == cell)
            {
                PhysicsNavWater entry = water[next++];
                float w = entry.SurfaceY;
                int below = 0;
                while (below < column.Length && column[below].Height < w) below++;
                float f = w - tuning.SwimSurfaceSubmersionFraction * bodyHeight;
                bool deep = f < w && (below == 0 ||
                    ((w - column[below - 1].Height) / bodyHeight >= tuning.SwimEnterDepthFraction && f > column[below - 1].Height));
                if (deep)
                {
                    float headroom;
                    if (below > 0)
                    {
                        PhysicsNavSurface s = column[below - 1];
                        headroom = MathF.Max(0f, s.Headroom - (f - s.Height));
                    }
                    else
                    {
                        headroom = column.Length > 0 ? column[0].Height - f : float.PositiveInfinity;
                    }
                    floats[stored] = true;
                    surfaces[stored++] = new PhysicsNavSurface(f, headroom, entry.Areas);
                    kept = below;
                }
            }
            for (int i = kept; i < column.Length; i++) surfaces[stored++] = column[i];
        }
        starts[cells] = stored;
        return PhysicsNavColumns.OwnDerived(captured.Options, width, captured.Height, starts,
            surfaces[..stored], water.ToArray(), floats[..stored]);
    }
}
