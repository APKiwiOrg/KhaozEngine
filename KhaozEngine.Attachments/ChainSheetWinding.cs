using System.Collections.Generic;

namespace KhaozEngine.Attachments;

// Wind each trail consistently across its two-face chain edges. A junction ends a
// trail because three or more faces cannot all have opposite directions on one edge.
internal sealed class ChainSheetWinding
{
    private readonly Dictionary<(int, int), int> _starts = [];

    internal ChainSheetWinding(ChainRig rig, IReadOnlyList<ChainWidthBounds.Pair> pairs)
    {
        var adjacent = new SortedDictionary<int, List<int>>();
        foreach (var pair in pairs)
        {
            var first = pair.First;
            var second = pair.Second;
            if (first.Particle - first.Root != rig.Chains[first.Chain].Joints.Count)
                continue;

            Add(first.Chain, second.Chain);
            Add(second.Chain, first.Chain);
        }

        foreach (var neighbours in adjacent.Values)
            neighbours.Sort();

        // Start open trails at stable endpoints, then handle closed cycles.
        foreach (var (chain, neighbours) in adjacent)
        {
            if (neighbours.Count != 2)
                foreach (int neighbour in neighbours)
                    Walk(chain, neighbour);
        }
        foreach (var (chain, neighbours) in adjacent)
            foreach (int neighbour in neighbours)
                Walk(chain, neighbour);

        void Add(int chain, int neighbour)
        {
            if (!adjacent.TryGetValue(chain, out var neighbours))
                adjacent.Add(chain, neighbours = []);
            neighbours.Add(neighbour);
        }

        void Walk(int from, int to)
        {
            while (_starts.TryAdd(Key(from, to), from))
            {
                var next = adjacent[to];
                if (next.Count != 2)
                    return;
                (from, to) = (to, next[0] == from ? next[1] : next[0]);
            }
        }
    }

    internal ChainWidthBounds.Pair Orient(in ChainWidthBounds.Pair pair) =>
        _starts[Key(pair.First.Chain, pair.Second.Chain)] == pair.First.Chain
            ? pair : new(pair.Second, pair.First, pair.Stretch);

    private static (int, int) Key(int first, int second) => first < second ? (first, second) : (second, first);
}
