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
