using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Whether a wearer's chains are simulated or settling to their rest hang.</summary>
public enum MotionLevel
{
    Simulated,
    Held,
}

/// <summary>Selects wearers for a shared simulation budget.</summary>
public static class MotionLevels
{
    /// <summary>
    /// Selects the nearest wearers within range. Ties prefer the lower wearer index.
    /// Scratch needs room for the smaller of the wearer count and the budget.
    /// </summary>
    public static int Assign(ReadOnlySpan<Vector3> wearers, Vector3 viewer, float simulateWithin, int maxSimulated,
        Span<int> scratch, Span<MotionLevel> levels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSimulated);
        if (!float.IsFinite(simulateWithin) || simulateWithin < 0)
            throw new ArgumentOutOfRangeException(nameof(simulateWithin));
        if (!ChainMath.IsFinite(viewer))
            throw new ArgumentException("Viewer position must be finite.", nameof(viewer));
        int capacity = Math.Min(maxSimulated, wearers.Length);
        if (scratch.Length < capacity)
            throw new ArgumentException("Scratch must hold the selection budget.", nameof(scratch));
        if (levels.Length != wearers.Length)
            throw new ArgumentException("Supply one output level per wearer.", nameof(levels));

        levels.Fill(MotionLevel.Held);
        if (capacity == 0)
            return 0;

        // A max heap keeps the farthest selected wearer available for replacement.
        var heap = scratch[..capacity];
        double rangeSquared = (double)simulateWithin * simulateWithin;
        int count = 0;
        for (int index = 0; index < wearers.Length; index++)
        {
            double distance = ChainMath.DistanceSquared(wearers[index], viewer);
            if (!(distance <= rangeSquared))
                continue;

            if (count < capacity)
            {
                int child = count++;
                int parent = (child - 1) / 2;
                while (child > 0 && IsFarther(index, heap[parent], wearers, viewer))
                {
                    heap[child] = heap[parent];
                    child = parent;
                    parent = (child - 1) / 2;
                }

                heap[child] = index;
            }
            else if (IsFarther(heap[0], index, wearers, viewer))
            {
                int parent = 0;
                while (parent < count / 2)
                {
                    int child = parent * 2 + 1;
                    if (child + 1 < count && IsFarther(heap[child + 1], heap[child], wearers, viewer))
                        child++;
                    if (!IsFarther(heap[child], index, wearers, viewer))
                        break;

                    heap[parent] = heap[child];
                    parent = child;
                }

                heap[parent] = index;
            }
        }

        foreach (int index in heap[..count])
            levels[index] = MotionLevel.Simulated;
        return count;
    }

    private static bool IsFarther(int first, int second, ReadOnlySpan<Vector3> wearers, Vector3 viewer)
    {
        double firstDistance = ChainMath.DistanceSquared(wearers[first], viewer);
        double secondDistance = ChainMath.DistanceSquared(wearers[second], viewer);
        return firstDistance > secondDistance || (firstDistance == secondDistance && first > second);
    }
}
