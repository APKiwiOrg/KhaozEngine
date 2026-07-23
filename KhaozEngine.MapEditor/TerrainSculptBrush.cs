using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The five terrain-sculpt brushes the editor's sculpt tool applies to the document's authored height
/// deltas (T2 of the sculpt program, #271). All operate on the sculpt delta layer, composited over the analytic
/// base by <see cref="TerrainField"/>.</summary>
public enum SculptBrush
{
    /// <summary>Adds height under the brush (delta grows by strength * falloff * dt).</summary>
    Raise,
    /// <summary>Subtracts height under the brush (delta shrinks by strength * falloff * dt).</summary>
    Lower,
    /// <summary>Blends each delta toward its 3x3 neighbourhood mean, softening sculpted bumps and cliffs. Operates
    /// on the delta field, so it never fights the procedural base (unsculpted terrain reads a zero-mean and stays
    /// untouched).</summary>
    Smooth,
    /// <summary>Blends the surface toward the composited height sampled under the initial press point.</summary>
    Flatten,
    /// <summary>Blends the surface toward an inspector-specified absolute world height.</summary>
    SetHeight,
}

/// <summary>GPU-free brush math for the terrain sculpt layer: a pure function from a brush dab (op, world centre,
/// radius, strength, frame dt) to the set of per-cell height-delta writes it produces. Kept free of the document
/// and GPU so the whole sculpt core is headless-testable; the editor's stroke command
/// (<see cref="TerrainSculptStrokeCommand"/>) and tool wire this into the undo layer and the viewport.
///
/// <para>The footprint is a world-space disc of <c>radius</c> world units around the centre, with a smoothstep
/// falloff from 1 at the centre to 0 at the edge (<see cref="Falloff"/>). Strength is meters per stroke-second for
/// raise/lower and a per-second blend rate for smooth/flatten/set-height, so a dab's effect scales with the frame
/// <c>dt</c>: the same (centre, dt) dab sequence is deterministic regardless of frame rate, and an instantaneous
/// click (dt 0) is a no-op (a sculpt brush builds up while held). Cells sit at world
/// (cellX * <c>cellSize</c>, cellZ * <c>cellSize</c>); the footprint is clamped to a <see cref="SculptBounds"/> so
/// the brush never touches a cell whose 32-cell tile would leave the document bounds (which the validating writer
/// refuses).</para></summary>
public static class TerrainSculptBrush
{
    /// <summary>One cell the brush writes: the global sculpt cell and its new absolute height delta (meters).</summary>
    public readonly record struct CellWrite(int CellX, int CellZ, float Delta);

    /// <summary>The circular-footprint falloff: a smoothstep weight that is 1 at the brush centre
    /// (<paramref name="normalizedDistance"/> 0) and eases to 0 at the edge (1), so a stroke fades toward its rim
    /// rather than cutting a hard disc. Clamped outside [0, 1].</summary>
    public static float Falloff(float normalizedDistance)
    {
        if (normalizedDistance <= 0f) return 1f;
        if (normalizedDistance >= 1f) return 0f;
        float s = normalizedDistance * normalizedDistance * (3f - 2f * normalizedDistance);   // smoothstep(0,1,t)
        return 1f - s;
    }

    /// <summary>Computes the per-cell height-delta writes for one brush dab. Reads the current delta field through
    /// <paramref name="currentDelta"/> (global cell -> meters, 0 outside every stored tile) and, for flatten and
    /// set-height, the analytic base height through <paramref name="baseHeight"/> (world x,z -> meters), so those
    /// two brushes target a composited world height (delta = target - base). All writes are computed from the same
    /// pre-dab snapshot the delegates expose, so a dab is order-independent and deterministic. Returns only cells
    /// whose delta actually changes; an empty list when nothing was touched (radius non-positive, dt 0, footprint
    /// off bounds, or every cell already at its target).</summary>
    public static IReadOnlyList<CellWrite> ComputeDab(
        SculptBrush brush, float centerX, float centerZ, float radius, float strength, float dt,
        float setHeight, float flattenTarget, float cellSize, in SculptBounds bounds,
        Func<int, int, float> currentDelta, Func<float, float, float> baseHeight)
    {
        ArgumentNullException.ThrowIfNull(currentDelta);
        ArgumentNullException.ThrowIfNull(baseHeight);
        var writes = new List<CellWrite>();
        if (!(radius > 0f) || !(cellSize > 0f) || dt <= 0f || !bounds.HasArea) return writes;

        int cxMin = Math.Max((int)MathF.Floor((centerX - radius) / cellSize), bounds.MinCellX);
        int cxMax = Math.Min((int)MathF.Ceiling((centerX + radius) / cellSize), bounds.MaxCellX);
        int czMin = Math.Max((int)MathF.Floor((centerZ - radius) / cellSize), bounds.MinCellZ);
        int czMax = Math.Min((int)MathF.Ceiling((centerZ + radius) / cellSize), bounds.MaxCellZ);

        float rInv = 1f / radius;
        for (int cz = czMin; cz <= czMax; cz++)
        {
            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                float wx = cx * cellSize, wz = cz * cellSize;
                float dx = wx - centerX, dz = wz - centerZ;
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist > radius) continue;
                float w = Falloff(dist * rInv);
                if (w <= 0f) continue;

                float cur = currentDelta(cx, cz);
                float next;
                switch (brush)
                {
                    case SculptBrush.Raise:
                        next = cur + strength * w * dt;
                        break;
                    case SculptBrush.Lower:
                        next = cur - strength * w * dt;
                        break;
                    case SculptBrush.Smooth:
                        next = cur + (NeighbourMean(currentDelta, cx, cz) - cur) * Blend(strength, w, dt);
                        break;
                    case SculptBrush.Flatten:
                        next = cur + (flattenTarget - baseHeight(wx, wz) - cur) * Blend(strength, w, dt);
                        break;
                    case SculptBrush.SetHeight:
                        next = cur + (setHeight - baseHeight(wx, wz) - cur) * Blend(strength, w, dt);
                        break;
                    default:
                        continue;
                }

                if (next != cur) writes.Add(new CellWrite(cx, cz, next));
            }
        }
        return writes;
    }

    // The per-application blend fraction for the toward-a-target brushes (smooth/flatten/set-height): the
    // per-second rate scaled by the frame dt and the footprint falloff, clamped to [0, 1] so one dab never
    // overshoots its target.
    static float Blend(float strength, float falloff, float dt) => Math.Clamp(strength * falloff * dt, 0f, 1f);

    // The mean of the current delta over the 3x3 block centred on (cellX, cellZ). Reads through the same pre-dab
    // delegate every write uses, so smoothing is order-independent (all cells blend toward the pre-dab mean).
    static float NeighbourMean(Func<int, int, float> currentDelta, int cellX, int cellZ)
    {
        float sum = 0f;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                sum += currentDelta(cellX + dx, cellZ + dz);
        return sum / 9f;
    }
}
