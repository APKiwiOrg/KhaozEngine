using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Finds the pairs of boxes whose bounds overlap. A sweep over minimum X keeps the active boxes in two
/// structures over compressed Z: a set ordered by low Z, which finds active boxes that start inside the current Z
/// range, and a stabbing tree, which finds active boxes that start below it and reach it. Work is proportional to the
/// pairs whose bounds overlap or touch on both axes, and each one examined is charged as one pair check.</summary>
internal static class MapWaterBoundsSweep
{
    /// <summary>Index pairs whose bounds overlap. With <paramref name="touching"/>, bounds that only touch count as
    /// overlapping. Each pair is reported once with the smaller index first.</summary>
    internal static IEnumerable<(int A, int B)> Pairs(IReadOnlyList<MapWaterPolygons.Bounds> bounds, bool touching,
        MapWaterWorkBudget budget)
    {
        int n = bounds.Count;
        if (n < 2) yield break;
        MapWideRational[] zs = bounds.SelectMany(b => new[] { b.Min.Z, b.Max.Z }).Distinct().Order().ToArray();
        var low = new int[n];
        var high = new int[n];
        for (int i = 0; i < n; i++)
        {
            low[i] = Array.BinarySearch(zs, bounds[i].Min.Z);
            high[i] = Array.BinarySearch(zs, bounds[i].Max.Z);
        }
        int[] entry = Enumerable.Range(0, n).ToArray();
        Array.Sort(entry, (a, b) => Compare(bounds[a].Min.X, a, bounds[b].Min.X, b));
        int[] exit = Enumerable.Range(0, n).ToArray();
        Array.Sort(exit, (a, b) => Compare(bounds[a].Max.X, a, bounds[b].Max.X, b));

        var state = new State[n];
        var byLow = new SortedSet<(int Low, int Index)>();
        var stab = new StabTree(zs.Length);
        int retired = 0;
        foreach (int index in entry)
        {
            MapWaterPolygons.Bounds current = bounds[index];
            // Retire every box that ends before the current one starts. Under the open rule a box that ends exactly
            // there no longer overlaps anything later, including a zero-width box that has not entered yet.
            for (; retired < n; retired++)
            {
                int old = exit[retired];
                int end = bounds[old].Max.X.CompareTo(current.Min.X);
                if (touching ? end >= 0 : end > 0) break;
                if (state[old] == State.Active)
                {
                    byLow.Remove((low[old], old));
                    stab.Update(low[old], high[old], old, add: false);
                }
                state[old] = State.Retired;
            }
            int top = touching ? high[index] : high[index] - 1;
            if (top >= low[index])
                foreach ((int _, int other) in byLow.GetViewBetween((low[index], int.MinValue), (top, int.MaxValue)))
                {
                    budget.Charge(1);
                    if (Overlap(bounds[other], current, touching)) yield return Ordered(other, index);
                }
            foreach (int other in stab.Stab(low[index]))
            {
                budget.Charge(1);
                if (low[other] < low[index] && Overlap(bounds[other], current, touching)) yield return Ordered(other, index);
            }

            // A zero-width box retired before entering still meets the boxes around it, but nothing after it.
            if (state[index] == State.Retired) continue;
            byLow.Add((low[index], index));
            stab.Update(low[index], high[index], index, add: true);
            state[index] = State.Active;
        }
    }

    /// <summary>Every cross pair whose bounds overlap. See the capped overload.</summary>
    internal static IReadOnlyList<(int Left, int Right)> Cross(IReadOnlyList<MapWaterPolygons.Bounds> left,
        IReadOnlyList<MapWaterPolygons.Bounds> right) => Cross(left, right, long.MaxValue)!;

    /// <summary>Pairs of one left and one right box whose bounds overlap with positive extent on both axes, sorted by
    /// left index and then right index. Boxes of one list are never compared with each other. Work is proportional to
    /// the boxes plus the cross pairs whose bounds overlap or touch. Callers charge each returned candidate before its
    /// exact test, so the comparisons here stay uncharged. Every decision is an exact comparison. A caller that charges
    /// every returned pair passes its remaining allowance as <paramref name="cap"/>. More pairs than that return null,
    /// with no partial result, before the list grows past the allowance.</summary>
    internal static IReadOnlyList<(int Left, int Right)>? Cross(IReadOnlyList<MapWaterPolygons.Bounds> left,
        IReadOnlyList<MapWaterPolygons.Bounds> right, long cap)
    {
        var result = new List<(int Left, int Right)>();
        int n = left.Count, total = n + right.Count;
        if (n == 0 || right.Count == 0) return result;
        MapWaterPolygons.Bounds At(int i) => i < n ? left[i] : right[i - n];
        MapWideRational[] zs = Enumerable.Range(0, total).SelectMany(i => new[] { At(i).Min.Z, At(i).Max.Z }).Distinct().Order().ToArray();
        var low = new int[total];
        var high = new int[total];
        for (int i = 0; i < total; i++)
        {
            low[i] = Array.BinarySearch(zs, At(i).Min.Z);
            high[i] = Array.BinarySearch(zs, At(i).Max.Z);
        }
        int[] entry = Enumerable.Range(0, total).ToArray();
        Array.Sort(entry, (a, b) => Compare(At(a).Min.X, a, At(b).Min.X, b));
        int[] exit = Enumerable.Range(0, total).ToArray();
        Array.Sort(exit, (a, b) => Compare(At(a).Max.X, a, At(b).Max.X, b));

        var state = new State[total];
        var byLow = new[] { new SortedSet<(int Low, int Index)>(), new SortedSet<(int Low, int Index)>() };
        var stab = new[] { new StabTree(zs.Length), new StabTree(zs.Length) };
        int retired = 0;
        foreach (int index in entry)
        {
            MapWaterPolygons.Bounds current = At(index);
            // Retire every box that ends at or before the current start. It cannot overlap anything that enters later.
            for (; retired < total; retired++)
            {
                int old = exit[retired];
                if (At(old).Max.X.CompareTo(current.Min.X) > 0) break;
                if (state[old] == State.Active)
                {
                    int list = old < n ? 0 : 1;
                    byLow[list].Remove((low[old], old));
                    stab[list].Update(low[old], high[old], old, add: false);
                }
                state[old] = State.Retired;
            }
            int side = index < n ? 0 : 1, other = 1 - side;
            // Active boxes of the other list that start inside the current Z range, then those that start below it.
            if (high[index] - 1 >= low[index])
                foreach ((int _, int candidate) in byLow[other].GetViewBetween((low[index], int.MinValue), (high[index] - 1, int.MaxValue)))
                    if (At(candidate).OverlapsOpen(current) && !Add(candidate, index)) return null;
            foreach (int candidate in stab[other].Stab(low[index]))
                if (low[candidate] < low[index] && At(candidate).OverlapsOpen(current) && !Add(candidate, index)) return null;
            if (state[index] == State.Retired) continue;
            byLow[side].Add((low[index], index));
            stab[side].Update(low[index], high[index], index, add: true);
            state[index] = State.Active;
        }
        result.Sort();
        return result;

        bool Add(int a, int b)
        {
            if (result.Count >= cap) return false;
            result.Add(a < n ? (a, b - n) : (b, a - n));
            return true;
        }
    }

    enum State : byte { Pending, Active, Retired }

    static int Compare(MapWideRational a, int ia, MapWideRational b, int ib)
    {
        int order = a.CompareTo(b);
        return order != 0 ? order : ia.CompareTo(ib);
    }

    static bool Overlap(MapWaterPolygons.Bounds a, MapWaterPolygons.Bounds b, bool touching) =>
        touching ? a.OverlapsClosed(b) : a.OverlapsOpen(b);

    static (int A, int B) Ordered(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>A segment tree over compressed Z points. An interval is stored at its canonical nodes, so the
    /// intervals holding a point are exactly those on the path from that point's leaf to the root.</summary>
    sealed class StabTree
    {
        readonly int _size;
        readonly HashSet<int>?[] _nodes;

        public StabTree(int points)
        {
            _size = 1;
            while (_size < points) _size <<= 1;
            _nodes = new HashSet<int>?[2 * _size];
        }

        public void Update(int low, int high, int item, bool add)
        {
            for (int l = low + _size, r = high + _size + 1; l < r; l >>= 1, r >>= 1)
            {
                if ((l & 1) == 1) Touch(l++, item, add);
                if ((r & 1) == 1) Touch(--r, item, add);
            }
        }

        public IEnumerable<int> Stab(int point)
        {
            for (int node = point + _size; node >= 1; node >>= 1)
                if (_nodes[node] is { } items)
                    foreach (int item in items) yield return item;
        }

        void Touch(int node, int item, bool add)
        {
            if (add) (_nodes[node] ??= new HashSet<int>()).Add(item);
            else _nodes[node]!.Remove(item);
        }
    }
}
