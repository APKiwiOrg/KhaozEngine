using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

// Independent test-only scalar oracle. Depth inputs are already linear.
internal static class TemporalVisibilityReference
{
    internal static (bool Valid, float Change) Compare(VisibilityCase c)
    {
        if (!c.Ready || c.Background || c.PreviousUv.X < 0 || c.PreviousUv.Y < 0
            || c.PreviousUv.X > 1 || c.PreviousUv.Y > 1) return (false, 0);
        float current = 0, count = 0, previous = 0, weights = 0;
        float centreDepth = c.Depth[c.CentreY * c.Width + c.CentreX];
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                int ax = Math.Clamp(c.CentreX + x, 0, c.Width - 1);
                int ay = Math.Clamp(c.CentreY + y, 0, c.Height - 1);
                int a = ay * c.Width + ax;
                if (!(MathF.Abs(c.Depth[a] - centreDepth) <= 0.02f * MathF.Max(centreDepth, 1e-5f))) continue;
                current += c.Current[a];
                count++;
                Vector2 size = new(c.Width, c.Height);
                Vector2 h = c.PreviousUv + (new Vector2(ax, ay) + new Vector2(0.5f) - c.Jitter) / size
                    - c.Uv + c.PreviousJitter / size;
                Vector2 p = h * size - new Vector2(0.5f);
                int bx = (int)MathF.Floor(p.X), by = (int)MathF.Floor(p.Y);
                float fx = p.X - bx, fy = p.Y - by;
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                    {
                        int b = Math.Clamp(by + j, 0, c.Height - 1) * c.Width + Math.Clamp(bx + i, 0, c.Width - 1);
                        float weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                        if (!(MathF.Abs(c.PreviousDepth[b] - c.ExpectedDepth) <= 0.02f * MathF.Max(c.ExpectedDepth, 1e-5f))) continue;
                        previous += c.Previous[b] * weight;
                        weights += weight;
                    }
            }
        return count > 0 && weights > 0 ? (true, current / count - previous / weights) : (false, 0);
    }

    internal static IEnumerable<VisibilityCase> Cases()
    {
        foreach (float value in new[] { MathF.BitDecrement(0.1f), 0.1f, MathF.BitIncrement(0.1f), -0.1f, -MathF.BitIncrement(0.1f) })
        {
            var c = new VisibilityCase($"threshold {value:R}", value, 0);
            Array.Fill(c.Depth, 2);
            c.Depth[5] = 1;
            c.Want = value;
            yield return c;
        }
        yield return new("rise", 0.75f, 0.25f) { Want = 0.5f };
        yield return new("fall", 0.25f, 0.75f) { Want = -0.5f };
        var boundary = new VisibilityCase("current depth exactly two percent", 0, 0);
        boundary.Depth[4] = 1.02f; boundary.Current[4] = 0.9f;
        boundary.Depth[6] = MathF.BitIncrement(1.02f); boundary.Current[6] = 1;
        boundary.Want = 0.1125f;
        yield return boundary;
        var corners = new VisibilityCase("reject corners before interpolation", 0.5f, 1) { PreviousJitter = new(0.5f), Want = 0.25f };
        Array.Fill(corners.Depth, 2); corners.Depth[5] = 1;
        Array.Fill(corners.PreviousDepth, 2); corners.PreviousDepth[5] = 1; corners.Previous[5] = 0.25f;
        yield return corners;
        var previousBoundary = new VisibilityCase("history depth exactly two percent", 0.5f, 0) { Want = 0.25f };
        Array.Fill(previousBoundary.Depth, 2); previousBoundary.Depth[5] = 1;
        Array.Fill(previousBoundary.PreviousDepth, 2); previousBoundary.PreviousDepth[5] = 1.02f;
        previousBoundary.Previous[5] = 0.25f;
        yield return previousBoundary;
        var aggregate = new VisibilityCase("aggregate history weights", 0.75f, 0) { PreviousJitter = new(0.5f, 0), Want = 0.55f };
        Array.Fill(aggregate.Depth, 2); aggregate.Depth[5] = aggregate.Depth[6] = 1;
        Array.Fill(aggregate.PreviousDepth, 2); aggregate.PreviousDepth[5] = aggregate.PreviousDepth[6] = 1;
        aggregate.Previous[5] = 0.6f;
        yield return aggregate;
        var empty = new VisibilityCase("empty current", 1, 0) { Valid = false };
        empty.Depth[5] = float.NaN;
        yield return empty;
        foreach (VisibilityCase c in NonFiniteCases()) yield return c;
        var unmatched = new VisibilityCase("empty history and unmatched depth", 1, 0) { Valid = false };
        Array.Fill(unmatched.PreviousDepth, 2);
        yield return unmatched;
        var zeroWeight = new VisibilityCase("qualified history with zero weights", 1, 0) { Valid = false };
        Array.Fill(zeroWeight.Depth, 2); zeroWeight.Depth[5] = 1;
        Array.Fill(zeroWeight.PreviousDepth, 2); zeroWeight.PreviousDepth[6] = 1;
        yield return zeroWeight;
        var border = new VisibilityCase("clamped duplicates", 0, 0) { CentreX = 0, CentreY = 0, Uv = new(0.125f), PreviousUv = new(0.125f), Want = 4f / 9f };
        border.Current[0] = 1;
        yield return border;
        yield return new("offscreen", 1, 0) { PreviousUv = new(-0.01f, 0.5f), Valid = false };
        yield return new("background", 1, 0) { Background = true, Valid = false };
        yield return new("unseeded", 1, 0) { Ready = false, Valid = false };
        foreach ((string name, Vector2 motion, Vector2 jitter, Vector2 prior, float want) in new[]
        {
            ("motion", new Vector2(0.0625f, 0), Vector2.Zero, Vector2.Zero, 0.59375f),
            ("previous jitter", Vector2.Zero, Vector2.Zero, new Vector2(0.5f, 0), 0.5f),
            ("current jitter", Vector2.Zero, new Vector2(0.5f, 0), Vector2.Zero, 0.625f),
            ("motion and jitters", new Vector2(0.0625f, 0), new Vector2(0.25f, 0), new Vector2(0.5f, 0), 0.5625f),
        })
        {
            var c = new VisibilityCase(name, 0.75f, 0) { PreviousUv = new Vector2(0.375f) - motion, Jitter = jitter, PreviousJitter = prior, Want = want };
            Array.Fill(c.Depth, 2); c.Depth[5] = 1;
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) c.Previous[y * 4 + x] = x * 0.125f + y * 0.0625f;
            yield return c;
        }
    }

    internal static IEnumerable<VisibilityCase> NonFiniteCases()
    {
        foreach (float depth in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var centre = new VisibilityCase($"non-finite centre {depth}", 1, 0) { Valid = false };
            centre.Depth[5] = depth;
            yield return centre;
            var neighbour = new VisibilityCase($"non-finite current neighbour {depth}", 0, 0);
            neighbour.Depth[4] = depth; neighbour.Current[4] = 1;
            yield return neighbour;
            yield return new($"non-finite expected history {depth}", 1, 0) { ExpectedDepth = depth, Valid = false };
            var emptyHistory = new VisibilityCase($"non-finite empty history {depth}", 1, 0) { Valid = false };
            Array.Fill(emptyHistory.PreviousDepth, depth);
            yield return emptyHistory;
            var mixedHistory = new VisibilityCase($"non-finite mixed history {depth}", 0.5f, 1)
            {
                PreviousJitter = new(0.5f), Want = 0.25f,
            };
            Array.Fill(mixedHistory.Depth, 2); mixedHistory.Depth[5] = 1;
            Array.Fill(mixedHistory.PreviousDepth, depth); mixedHistory.PreviousDepth[5] = 1;
            mixedHistory.Previous[5] = 0.25f;
            yield return mixedHistory;
        }
    }

}

internal sealed class VisibilityCase
{
    internal readonly string Name;
    internal readonly int Width = 4, Height = 4;
    internal readonly float[] Current = new float[16], Previous = new float[16], Depth = new float[16], PreviousDepth = new float[16];
    internal Vector2 Uv = new(0.375f), PreviousUv = new(0.375f), Jitter, PreviousJitter;
    internal int CentreX = 1, CentreY = 1;
    internal float ExpectedDepth = 1, Want;
    internal bool Ready = true, Background, Valid = true;
    internal VisibilityCase(string name, float current, float previous)
    {
        Name = name;
        Array.Fill(Current, current); Array.Fill(Previous, previous);
        Array.Fill(Depth, 1); Array.Fill(PreviousDepth, 1);
    }
}
