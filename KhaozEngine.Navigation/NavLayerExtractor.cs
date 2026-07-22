using System;
using System.Collections.Generic;

namespace KhaozEngine.Navigation;

/// <summary>
/// Decomposes per-column surface stacks into layer-sized surface fields for the layered overworld
/// bake (see NAV-LAYERED-SURFACES-DESIGN.md). Regions are grown over (cell, surface) nodes with
/// 8-adjacency within the step budget and are single-valued per column by construction (a
/// bridge-to-ground continuum or a spiral ramp splits where it would overlap itself). Growth holds a
/// hard invariant: a region never contains an 8-adjacent pair of cells whose surface rise exceeds the
/// step budget. A candidate surface is claimed only when it stays within the step budget of every
/// surface the region already claimed in a column 8-adjacent to the candidate's, so a gradual ramp
/// climbed step by step can no longer fold back to sit tall-adjacent to the flat ground it left. The
/// merge pass then joins step-adjacent regions whose cell sets do not overlap AND that share no too-tall
/// contact anywhere: a pair that meets within the step budget at one spot but towers over it at another
/// is recorded as forbidden and never merged, so the invariant survives merging. Regions are then
/// assigned greedily to layers such that two regions share a layer only when they have no column overlap
/// AND no 8-adjacency at all, which is what removes the phase-1 rim erosion at feature boundaries.
/// Because no layer ever holds an 8-adjacent too-tall pair, StepMask can never fire on a layered-bake
/// layer: erosion is fully eliminated, and every too-tall contact becomes a layer boundary that
/// <see cref="NavLayerLinks"/> seams with directed stair (or hop) pairs instead of a silently blocked
/// cell. Every scan runs in a fixed order (z, then x, then ascending surface), so the decomposition is
/// deterministic. Internal: the public entry point is <see cref="NavLayerBaker"/>.
/// </summary>
internal static class NavLayerExtractor
{
    // Fixed neighbor order: N, S, E, W, then the four diagonals NE, NW, SE, SW (same as StepMask).
    static readonly (int Dx, int Dz)[] Neighbors =
    {
        (0, -1), (0, 1), (1, 0), (-1, 0),
        (1, -1), (-1, -1), (1, 1), (-1, 1),
    };

    /// <summary>One extracted layer: parallel row-major fields sized width * height, ready to feed
    /// <see cref="NavGrid.FromSurfaces"/>. <see cref="YMin"/>/<see cref="YMax"/> are the min/max
    /// standable surface heights on the layer.</summary>
    internal sealed class Layer
    {
        public required bool[] Standable { get; init; }
        public required float[] Height { get; init; }
        public required float[] Headroom { get; init; }
        public required float YMin { get; init; }
        public required float YMax { get; init; }
    }

    /// <summary>
    /// Runs the full decomposition. <paramref name="columnStart"/> has width * height + 1 prefix
    /// offsets into <paramref name="surfaceHeight"/> / <paramref name="surfaceHeadroom"/> (each
    /// column's surfaces stored ascending). Returns the layers in assignment order. Empty when there
    /// are no surfaces at all.
    /// </summary>
    internal static List<Layer> Extract(
        int width, int height,
        int[] columnStart, float[] surfaceHeight, float[] surfaceHeadroom,
        float stepHeight)
    {
        int cellCount = width * height;
        int nodeCount = surfaceHeight.Length;
        var layers = new List<Layer>();
        if (nodeCount == 0) return layers;

        // Column index of each node, recovered once so the passes below never re-search the prefix.
        var nodeColumn = new int[nodeCount];
        for (int ci = 0; ci < cellCount; ci++)
            for (int s = columnStart[ci]; s < columnStart[ci + 1]; s++)
                nodeColumn[s] = ci;

        int regionCount = GrowRegions(
            width, height, columnStart, surfaceHeight, nodeColumn,
            stepHeight, out int[] regionOf);

        var parent = new int[regionCount];
        for (int r = 0; r < regionCount; r++) parent[r] = r;

        List<int>[] columnsOf = BuildColumnSets(regionCount, regionOf, nodeColumn, nodeCount);
        MergeRegions(
            width, height, columnStart, surfaceHeight, nodeColumn,
            stepHeight, regionOf, parent, columnsOf);

        (int[] layerOfRoot, int layerCount) = AssignLayers(
            width, height, columnStart, regionOf, nodeColumn, nodeCount, parent, columnsOf);

        var standable = new bool[layerCount][];
        var heightField = new float[layerCount][];
        var headroomField = new float[layerCount][];
        var yMin = new float[layerCount];
        var yMax = new float[layerCount];
        for (int l = 0; l < layerCount; l++)
        {
            standable[l] = new bool[cellCount];
            heightField[l] = new float[cellCount];
            headroomField[l] = new float[cellCount];
            yMin[l] = float.PositiveInfinity;
            yMax[l] = float.NegativeInfinity;
        }

        for (int n = 0; n < nodeCount; n++)
        {
            int l = layerOfRoot[Find(parent, regionOf[n])];
            int ci = nodeColumn[n];
            standable[l][ci] = true;
            heightField[l][ci] = surfaceHeight[n];
            headroomField[l][ci] = surfaceHeadroom[n];
            if (surfaceHeight[n] < yMin[l]) yMin[l] = surfaceHeight[n];
            if (surfaceHeight[n] > yMax[l]) yMax[l] = surfaceHeight[n];
        }

        for (int l = 0; l < layerCount; l++)
        {
            layers.Add(new Layer
            {
                Standable = standable[l],
                Height = heightField[l],
                Headroom = headroomField[l],
                YMin = yMin[l],
                YMax = yMax[l],
            });
        }

        return layers;
    }

    /// <summary>
    /// Region growing: BFS from each unassigned node in scan order, connecting to the nearest-height
    /// unassigned surface within <paramref name="stepHeight"/> in each 8-neighbor column, never claiming
    /// a second surface in a column the region already occupies, and never claiming a surface that would
    /// put an 8-adjacent pair with rise beyond <paramref name="stepHeight"/> inside the region (the
    /// growth invariant, enforced by <see cref="ViolatesInvariant"/>). A candidate refused by the
    /// invariant stays unassigned and later seeds or joins another region. <paramref name="regionOf"/>
    /// receives each node's region id and the return value is the region count.
    /// </summary>
    static int GrowRegions(
        int width, int height, int[] columnStart, float[] surfaceHeight, int[] nodeColumn,
        float stepHeight, out int[] regionOf)
    {
        int nodeCount = surfaceHeight.Length;
        int cellCount = width * height;
        regionOf = new int[nodeCount];
        Array.Fill(regionOf, -1);

        // columnClaim[c] stores the id of the most recent region to claim column c, and
        // columnClaimNode[c] the node it claimed there. Only the region currently growing ever reads
        // them, and columnClaimNode[c] is valid exactly when columnClaim[c] equals that region, so
        // neither needs a reset between regions (allocation is the only setup columnClaimNode needs).
        var columnClaim = new int[cellCount];
        Array.Fill(columnClaim, -1);
        var columnClaimNode = new int[cellCount];

        var queue = new Queue<int>();
        int regionCount = 0;

        for (int seed = 0; seed < nodeCount; seed++)
        {
            if (regionOf[seed] != -1) continue;

            int region = regionCount++;
            regionOf[seed] = region;
            columnClaim[nodeColumn[seed]] = region;
            columnClaimNode[nodeColumn[seed]] = seed;
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                int ci = nodeColumn[n];
                int cx = ci % width;
                int cz = ci / width;
                float h = surfaceHeight[n];

                for (int d = 0; d < Neighbors.Length; d++)
                {
                    int nx = cx + Neighbors[d].Dx;
                    int nz = cz + Neighbors[d].Dz;
                    if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;

                    int nc = nz * width + nx;
                    if (columnClaim[nc] == region) continue;

                    // Nearest-height claimable surface in the neighbor column that (a) is within the step
                    // budget of n and (b) keeps the growth invariant: it must stay within the step budget
                    // of every surface this region already claimed in a column 8-adjacent to nc. Clause
                    // (b) is what stops a region ever holding an 8-adjacent pair whose rise exceeds the
                    // budget, which is exactly the contact StepMask would later erode. Strict less-than
                    // keeps the lowest surface on a rise tie, and a refused candidate lowers nothing.
                    int best = -1;
                    float bestRise = float.PositiveInfinity;
                    for (int s = columnStart[nc]; s < columnStart[nc + 1]; s++)
                    {
                        if (regionOf[s] != -1) continue;
                        float rise = MathF.Abs(surfaceHeight[s] - h);
                        if (rise > stepHeight || rise >= bestRise) continue;
                        if (ViolatesInvariant(s, nx, nz, width, height, region, stepHeight,
                                surfaceHeight, columnClaim, columnClaimNode))
                            continue;

                        best = s;
                        bestRise = rise;
                    }

                    if (best != -1)
                    {
                        regionOf[best] = region;
                        columnClaim[nc] = region;
                        columnClaimNode[nc] = best;
                        queue.Enqueue(best);
                    }
                }
            }
        }

        return regionCount;
    }

    /// <summary>
    /// True when claiming surface <paramref name="candidate"/> in the column at
    /// (<paramref name="nx"/>, <paramref name="nz"/>) would put an 8-adjacent pair with rise beyond
    /// <paramref name="stepHeight"/> inside <paramref name="region"/>: some column 8-adjacent to
    /// (nx, nz) that <paramref name="region"/> already claimed carries a claimed surface whose height
    /// differs from the candidate's by more than the step budget. This is the growth invariant's guard.
    /// </summary>
    static bool ViolatesInvariant(
        int candidate, int nx, int nz, int width, int height, int region, float stepHeight,
        float[] surfaceHeight, int[] columnClaim, int[] columnClaimNode)
    {
        float ch = surfaceHeight[candidate];
        for (int d = 0; d < Neighbors.Length; d++)
        {
            int ax = nx + Neighbors[d].Dx;
            int az = nz + Neighbors[d].Dz;
            if (ax < 0 || az < 0 || ax >= width || az >= height) continue;

            int ac = az * width + ax;
            if (columnClaim[ac] != region) continue;
            if (MathF.Abs(ch - surfaceHeight[columnClaimNode[ac]]) > stepHeight) return true;
        }

        return false;
    }

    static List<int>[] BuildColumnSets(int regionCount, int[] regionOf, int[] nodeColumn, int nodeCount)
    {
        var columnsOf = new List<int>[regionCount];
        for (int r = 0; r < regionCount; r++) columnsOf[r] = new List<int>();
        for (int n = 0; n < nodeCount; n++)
            columnsOf[regionOf[n]].Add(nodeColumn[n]);
        return columnsOf;
    }

    /// <summary>
    /// Merges step-adjacent regions whose column sets do not overlap, to fixpoint, while never merging
    /// across a too-tall contact. The discovery pass records two things per inter-region adjacency: a
    /// merge candidate when the rise is within <paramref name="stepHeight"/>, and a forbidden pair when
    /// the rise exceeds it. A merge of two roots proceeds only when their column sets are disjoint AND
    /// neither root group forbids the other, so a pair that is step-adjacent at one spot but
    /// too-tall-adjacent at another is never folded into a single region (which would reintroduce the
    /// erosion the growth invariant removes). Candidate pairs are discovered in scan order and processed
    /// in that order each pass, the smaller root id absorbs the larger, and the forbidden bookkeeping
    /// uses only order-free set operations, so the result is deterministic.
    /// </summary>
    static void MergeRegions(
        int width, int height, int[] columnStart, float[] surfaceHeight, int[] nodeColumn,
        float stepHeight, int[] regionOf, int[] parent, List<int>[] columnsOf)
    {
        var candidates = new List<(int A, int B)>();
        var seen = new HashSet<(int A, int B)>();
        var forbidden = new HashSet<(int A, int B)>();
        int nodeCount = surfaceHeight.Length;

        for (int n = 0; n < nodeCount; n++)
        {
            int ci = nodeColumn[n];
            int cx = ci % width;
            int cz = ci / width;
            float h = surfaceHeight[n];
            int rn = regionOf[n];

            for (int d = 0; d < Neighbors.Length; d++)
            {
                int nx = cx + Neighbors[d].Dx;
                int nz = cz + Neighbors[d].Dz;
                if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;

                int nc = nz * width + nx;
                for (int s = columnStart[nc]; s < columnStart[nc + 1]; s++)
                {
                    int rs = regionOf[s];
                    if (rs == rn) continue;

                    (int A, int B) pair = rn < rs ? (rn, rs) : (rs, rn);
                    if (MathF.Abs(surfaceHeight[s] - h) > stepHeight)
                    {
                        // A too-tall contact between two regions. Merging them would pull the contact
                        // inside one region, reintroducing exactly the erosion the growth invariant
                        // removed, so record the pair as forbidden and never merge across it.
                        forbidden.Add(pair);
                    }
                    else if (seen.Add(pair))
                    {
                        candidates.Add(pair);
                    }
                }
            }
        }

        // Forbidden relations keyed by current union root. Seeded with original region ids (each its
        // own root), then carried across unions: when a root is absorbed, its partners are re-pointed
        // at the surviving root. Membership only, so every decision below is order-independent.
        var forbiddenOf = new Dictionary<int, HashSet<int>>();
        foreach ((int a, int b) in forbidden)
        {
            AddForbidden(forbiddenOf, a, b);
            AddForbidden(forbiddenOf, b, a);
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((int a, int b) in candidates)
            {
                int ra = Find(parent, a);
                int rb = Find(parent, b);
                if (ra == rb) continue;

                int keep = Math.Min(ra, rb);
                int drop = Math.Max(ra, rb);
                if (ColumnsOverlap(columnsOf[keep], columnsOf[drop])) continue;
                if (IsForbidden(forbiddenOf, keep, drop)) continue;

                parent[drop] = keep;
                columnsOf[keep].AddRange(columnsOf[drop]);
                columnsOf[drop] = new List<int>();
                MergeForbidden(forbiddenOf, keep, drop);
                changed = true;
            }
        }
    }

    static void AddForbidden(Dictionary<int, HashSet<int>> forbiddenOf, int from, int to)
    {
        if (!forbiddenOf.TryGetValue(from, out HashSet<int>? set))
        {
            set = new HashSet<int>();
            forbiddenOf[from] = set;
        }

        set.Add(to);
    }

    /// <summary>True when <paramref name="keep"/> and <paramref name="drop"/> may not merge because a
    /// too-tall contact was recorded between their root groups. Both directions are tested, though the
    /// map is kept symmetric, so either alone would suffice.</summary>
    static bool IsForbidden(Dictionary<int, HashSet<int>> forbiddenOf, int keep, int drop)
        => (forbiddenOf.TryGetValue(keep, out HashSet<int>? fk) && fk.Contains(drop))
            || (forbiddenOf.TryGetValue(drop, out HashSet<int>? fd) && fd.Contains(keep));

    /// <summary>
    /// Absorbs <paramref name="drop"/>'s forbidden partners into <paramref name="keep"/> after
    /// union(keep, drop): keep inherits each of drop's forbidden partners, and every such partner has
    /// drop swapped for keep in its own set. Set operations only, so the outcome does not depend on
    /// iteration order. keep never forbids drop here (the merge was already cleared by
    /// <see cref="IsForbidden"/>), so no self-loop is introduced.
    /// </summary>
    static void MergeForbidden(Dictionary<int, HashSet<int>> forbiddenOf, int keep, int drop)
    {
        if (!forbiddenOf.TryGetValue(drop, out HashSet<int>? fd)) return;

        foreach (int x in fd)
        {
            if (x == keep) continue;
            AddForbidden(forbiddenOf, keep, x);
            if (forbiddenOf.TryGetValue(x, out HashSet<int>? fx))
            {
                fx.Remove(drop);
                fx.Add(keep);
            }
        }

        forbiddenOf.Remove(drop);
    }

    /// <summary>
    /// Assigns each merged root to the lowest layer index holding no conflicting root, where a
    /// conflict is column overlap or any 8-adjacency between the two roots' surfaces. Roots are
    /// processed in ascending id order (creation order, hence scan order). Returns each root's layer
    /// and the layer count.
    /// </summary>
    static (int[] LayerOfRoot, int LayerCount) AssignLayers(
        int width, int height, int[] columnStart, int[] regionOf, int[] nodeColumn, int nodeCount,
        int[] parent, List<int>[] columnsOf)
    {
        int regionCount = parent.Length;

        // Root adjacency: any 8-adjacent surface pair between two distinct roots, regardless of rise.
        var adjacent = new HashSet<(int A, int B)>();
        for (int n = 0; n < nodeCount; n++)
        {
            int ci = nodeColumn[n];
            int cx = ci % width;
            int cz = ci / width;
            int rn = Find(parent, regionOf[n]);

            for (int d = 0; d < Neighbors.Length; d++)
            {
                int nx = cx + Neighbors[d].Dx;
                int nz = cz + Neighbors[d].Dz;
                if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;

                int nc = nz * width + nx;
                for (int s = columnStart[nc]; s < columnStart[nc + 1]; s++)
                {
                    int rs = Find(parent, regionOf[s]);
                    if (rs == rn) continue;
                    adjacent.Add(rn < rs ? (rn, rs) : (rs, rn));
                }
            }
        }

        var layerOfRoot = new int[regionCount];
        Array.Fill(layerOfRoot, -1);
        var layerMembers = new List<List<int>>();

        for (int r = 0; r < regionCount; r++)
        {
            if (Find(parent, r) != r) continue;

            int chosen = -1;
            for (int l = 0; l < layerMembers.Count && chosen == -1; l++)
            {
                bool conflict = false;
                foreach (int member in layerMembers[l])
                {
                    if (adjacent.Contains(member < r ? (member, r) : (r, member))
                        || ColumnsOverlap(columnsOf[member], columnsOf[r]))
                    {
                        conflict = true;
                        break;
                    }
                }
                if (!conflict) chosen = l;
            }

            if (chosen == -1)
            {
                chosen = layerMembers.Count;
                layerMembers.Add(new List<int>());
            }

            layerMembers[chosen].Add(r);
            layerOfRoot[r] = chosen;
        }

        return (layerOfRoot, layerMembers.Count);
    }

    static bool ColumnsOverlap(List<int> a, List<int> b)
    {
        List<int> small = a.Count <= b.Count ? a : b;
        List<int> large = ReferenceEquals(small, a) ? b : a;
        var lookup = new HashSet<int>(large);
        foreach (int c in small)
            if (lookup.Contains(c)) return true;
        return false;
    }

    static int Find(int[] parent, int r)
    {
        while (parent[r] != r)
        {
            parent[r] = parent[parent[r]];
            r = parent[r];
        }
        return r;
    }
}
