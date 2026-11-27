using System;

namespace KhaozEngine.Attachments;

// Eliminate whole node blocks so all three axes keep their original local order.
internal static class ClothSparseOrdering
{
    internal static int[][] Fill(bool[,] connected, out int[] originalBlocks)
    {
        int count = connected.GetLength(0);
        var active = (bool[,])connected.Clone();
        var degrees = new int[count];
        for (int node = 0; node < count; node++)
            for (int other = 0; other < count; other++)
                if (node != other && active[node, other]) degrees[node]++;

        var ordinal = new int[count];
        var laterBlocks = new int[count][];
        originalBlocks = new int[count];
        for (int column = 0; column < count; column++)
        {
            int node = MinimumDegree(degrees);
            originalBlocks[column] = node;
            ordinal[node] = column;
            var neighbors = new int[degrees[node]];
            laterBlocks[column] = neighbors;
            degrees[node] = int.MaxValue;
            int next = 0;
            for (int other = 0; other < count; other++)
            {
                if (degrees[other] == int.MaxValue || !active[node, other]) continue;
                neighbors[next++] = other;
                degrees[other]--;
            }

            for (int i = 0; i < neighbors.Length; i++)
            for (int j = 0; j < i; j++)
            {
                int a = neighbors[i], b = neighbors[j];
                if (active[a, b]) continue;
                active[a, b] = active[b, a] = true;
                degrees[a]++;
                degrees[b]++;
            }
        }

        foreach (int[] blocks in laterBlocks)
        {
            for (int i = 0; i < blocks.Length; i++) blocks[i] = ordinal[blocks[i]];
            Array.Sort(blocks);
        }
        return laterBlocks;
    }

    private static int MinimumDegree(int[] degrees)
    {
        int best = -1, degree = int.MaxValue;
        for (int node = 0; node < degrees.Length; node++)
        {
            if (degrees[node] >= degree) continue;
            best = node;
            degree = degrees[node];
        }
        return best;
    }
}
