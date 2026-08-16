using System;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>High-level factories that READ the document and build the command that expresses the edit. Nothing
/// here mutates: the returned command is handed to <see cref="TileEditingDocument.Execute"/>, which is what
/// keeps every path through the tool undoable and the collision map in step.</summary>
public static partial class TileEditOps
{
    /// <summary>Raises (or lowers, with a negative delta) every corner of the rect. With
    /// <paramref name="falloff"/> at 0 the whole rect moves by the flat delta. Above 0 the delta is scaled by
    /// 1 - falloff * (distance / halfExtent) clamped into 0..1, where distance is the Chebyshev distance from
    /// the rect centre in corner units and halfExtent is the distance from that centre out to the rect's
    /// OUTERMOST ring, half of the larger dimension less one. That makes falloff 1 fade a square brush to
    /// exactly nothing on its edge ring and 0.5 fade it to half there. A rect one corner wide or tall has no
    /// extent to fade across, so every corner keeps the full delta whatever the falloff.</summary>
    public static SetCornerHeightsCommand Raise(TileWorldDocument doc, TileRect cornerRect, int plane, int deltaCm,
        float falloff = 0f)
    {
        ArgumentNullException.ThrowIfNull(doc);
        short[] cm = ReadCorners(doc, cornerRect, plane);
        if (cm.Length == 0) return new SetCornerHeightsCommand(cornerRect, plane, cm);
        float centreX = (cornerRect.X + cornerRect.X1 - 1) * 0.5f;
        float centreZ = (cornerRect.Z + cornerRect.Z1 - 1) * 0.5f;
        // Rings, not dimensions: a 5 wide rect reaches 2 corners out from its centre, not 2.5, and dividing by
        // the half-dimension would leave the edge ring at a fifth of the delta on a falloff of 1.
        float halfExtent = (Math.Max(cornerRect.Width, cornerRect.Height) - 1) * 0.5f;
        int i = 0;
        for (int z = cornerRect.Z; z < cornerRect.Z1; z++)
            for (int x = cornerRect.X; x < cornerRect.X1; x++, i++)
            {
                float weight = 1f;
                if (falloff > 0f && halfExtent > 0f)
                {
                    float distance = Math.Max(Math.Abs(x - centreX), Math.Abs(z - centreZ));
                    weight = Math.Clamp(1f - falloff * (distance / halfExtent), 0f, 1f);
                }
                // Widened before the add, so a delta near the int ceiling saturates at the short bounds instead
                // of wrapping the sum and landing the brush at the bottom of the world.
                cm[i] = ClampCm(cm[i] + (long)MathF.Round(deltaCm * weight, MidpointRounding.AwayFromZero));
            }
        return new SetCornerHeightsCommand(cornerRect, plane, cm);
    }

    /// <summary>Levels every corner of the rect to <paramref name="toCm"/>, or to the rounded average of the
    /// corners as they stand when it is null (half a centimetre rounds away from zero).</summary>
    public static SetCornerHeightsCommand Flatten(TileWorldDocument doc, TileRect cornerRect, int plane, short? toCm)
    {
        ArgumentNullException.ThrowIfNull(doc);
        short[] cm = ReadCorners(doc, cornerRect, plane);
        short target = 0;
        if (toCm is short given) target = given;
        else if (cm.Length > 0)
        {
            long sum = 0;
            foreach (short v in cm) sum += v;
            target = ClampCm((long)Math.Round(sum / (double)cm.Length, MidpointRounding.AwayFromZero));
        }
        for (int i = 0; i < cm.Length; i++) cm[i] = target;
        return new SetCornerHeightsCommand(cornerRect, plane, cm);
    }

    /// <summary>Runs an iterated 3 by 3 box blur over the corner rect. Each pass averages the nine corners
    /// around each one, taking the neighbours that fall outside the rect from the document (they are read
    /// fresh, never written, so the smoothed patch blends into the terrain around it instead of stepping off
    /// it), and rounds back to whole centimetres before the next pass runs. Takes 1 to 64 iterations: the
    /// result has long since converged by 64, and the ceiling stops a mistyped count from walking the rect
    /// billions of times inside a tool call that cannot be cancelled.</summary>
    public static SetCornerHeightsCommand Smooth(TileWorldDocument doc, TileRect cornerRect, int plane, int iterations)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, 64);
        short[] current = ReadCorners(doc, cornerRect, plane);
        if (current.Length == 0) return new SetCornerHeightsCommand(cornerRect, plane, current);
        short[] next = new short[current.Length];
        for (int pass = 0; pass < iterations; pass++)
        {
            int i = 0;
            for (int z = cornerRect.Z; z < cornerRect.Z1; z++)
                for (int x = cornerRect.X; x < cornerRect.X1; x++, i++)
                    next[i] = Blur(doc, cornerRect, plane, current, x, z);
            // Double buffered on purpose: a pass that wrote in place would feed its own half-blurred output to
            // the corners it visits later, which skews the result along the walk order rather than symmetrically.
            (current, next) = (next, current);
        }
        return new SetCornerHeightsCommand(cornerRect, plane, current);
    }

    /// <summary>The plain write of one height per corner, the command every other factory here ends up
    /// building. The document is not read, the parameter is there so a caller can swap one op of this family
    /// for another without reshaping the call.</summary>
    public static SetCornerHeightsCommand SetHeights(TileWorldDocument doc, TileRect cornerRect, int plane, short[] cm)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return new SetCornerHeightsCommand(cornerRect, plane, cm);
    }

    // One corner's blurred value: the nine-corner average, taken from the working buffer inside the rect and
    // from the document outside it.
    static short Blur(TileWorldDocument doc, TileRect cornerRect, int plane, short[] current, int x, int z)
    {
        int sum = 0;
        for (int nz = z - 1; nz <= z + 1; nz++)
            for (int nx = x - 1; nx <= x + 1; nx++)
                sum += cornerRect.Contains(nx, nz)
                    ? current[(nz - cornerRect.Z) * cornerRect.Width + (nx - cornerRect.X)]
                    : doc.CornerHeightCm(nx, nz, plane);
        return ClampCm((long)Math.Round(sum / 9.0, MidpointRounding.AwayFromZero));
    }

    // The corner rect's current heights, row-major with z outer, which is the order every command and factory
    // in this family indexes its value array in.
    static short[] ReadCorners(TileWorldDocument doc, TileRect cornerRect, int plane)
    {
        if (cornerRect.IsEmpty) return Array.Empty<short>();
        var cm = new short[cornerRect.Width * cornerRect.Height];
        int i = 0;
        for (int z = cornerRect.Z; z < cornerRect.Z1; z++)
            for (int x = cornerRect.X; x < cornerRect.X1; x++, i++)
                cm[i] = doc.CornerHeightCm(x, z, plane);
        return cm;
    }

    // Heights are centimetres in a short, so a raise that would overflow the lattice saturates at its bounds
    // instead of wrapping the terrain from its ceiling to its floor. Takes a long, so the arithmetic feeding it
    // has room to overshoot the short range (and the int range) before it gets here.
    static short ClampCm(long cm) => (short)Math.Clamp(cm, short.MinValue, short.MaxValue);
}
