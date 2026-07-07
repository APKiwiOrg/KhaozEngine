using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure (GPU-free) back-to-front depth sorting for alpha-blended transparent draws. Alpha blending is
    /// order-dependent (each fragment reads the destination it lands on), so overlapping translucent draws must
    /// composite far-to-near for a correct result. The renderers upload their per-frame batches in submission
    /// order, so a scene that queues a near sprite before a far one behind it blends wrong; sorting the batch by
    /// view depth before upload fixes it independent of submission order.
    /// <para>
    /// The key is the signed distance along the camera FORWARD axis: <c>dot(pos - eye, forward)</c>. Using the
    /// forward-axis projection rather than raw Euclidean distance to the eye keeps the order stable when the camera
    /// strafes (a pure sideways move doesn't change any draw's forward depth, so nothing pops), which is what a
    /// depth-buffer comparison also uses. Larger key = farther from the eye. Back-to-front = descending key.
    /// </para>
    /// <para>
    /// Additive blending (out = src + dst) is commutative, so additive batches are order-independent and must NOT
    /// pay the sort cost; callers keep those in submission order and never route them through here (see the beam
    /// pass and the billboard / textured-billboard additive queues).
    /// </para>
    /// Allocation-conscious: <see cref="ComputeOrder"/> reuses caller-owned key/index arrays (grown geometrically,
    /// like the instance-buffer handling in ModelRenderer) and sorts in place with
    /// <see cref="Array.Sort{TKey,TValue}(TKey[],TValue[],int,int)"/>, so a steady-state frame does no per-frame
    /// heap allocation and uses no LINQ or comparer object.
    /// </summary>
    internal static class TransparencySort
    {
        /// <summary>
        /// View depth of <paramref name="worldPos"/>: the signed distance from <paramref name="eye"/> along the
        /// camera <paramref name="forward"/> direction, <c>dot(worldPos - eye, forward)</c>. Larger = farther.
        /// <paramref name="forward"/> is expected unit length (the camera surfaces return a normalized forward), so
        /// this is a true forward-axis depth; a non-unit forward just scales all keys uniformly, which does not
        /// change the resulting order.
        /// </summary>
        public static float ViewDepth(Vector3 worldPos, Vector3 eye, Vector3 forward)
            => Vector3.Dot(worldPos - eye, forward);

        /// <summary>
        /// Fill <paramref name="order"/>[0..count) with the item indices reordered BACK-TO-FRONT (farthest first)
        /// by the view depth of each item's world centre in <paramref name="centers"/>, so the caller can walk
        /// <paramref name="order"/> to emit its batch far-to-near. The sort is STABLE for equal depths (ties keep
        /// submission order), so coplanar / identical-depth draws render deterministically in the order they were
        /// queued. <paramref name="count"/> must be &lt;= <paramref name="centers"/>.Length.
        /// <para>
        /// <paramref name="keyScratch"/> and <paramref name="order"/> are caller-owned reusable buffers; both are
        /// grown to at least <paramref name="count"/> via <see cref="EnsureCapacity"/> (geometric growth) and then
        /// overwritten, so repeated calls with the same buffers do not allocate. Only the first
        /// <paramref name="count"/> entries of each are meaningful on return.
        /// </para>
        /// </summary>
        public static void ComputeOrder(ReadOnlySpan<Vector3> centers, int count, Vector3 eye, Vector3 forward,
            ref float[] keyScratch, ref int[] order)
        {
            EnsureCapacity(ref keyScratch, count);
            EnsureCapacity(ref order, count);
            for (int i = 0; i < count; i++)
            {
                // Negate the forward-axis depth so an ascending numeric sort yields farthest-first (back-to-front):
                // a larger view depth (farther) becomes a smaller (more negative) key.
                keyScratch[i] = -ViewDepth(centers[i], eye, forward);
                order[i] = i;
            }
            StableSortByKey(keyScratch, order, count);
        }

        /// <summary>Sort the first <paramref name="count"/> entries of <paramref name="order"/> by
        /// <paramref name="keys"/> ascending, breaking ties by the original index value so the result is stable
        /// (equal keys keep their queued order). In place; no allocation beyond the caller's buffers.</summary>
        static void StableSortByKey(float[] keys, int[] order, int count)
        {
            // Array.Sort(keys, items) is an unstable introsort. order[i] == i going in, so after the primary sort we
            // re-sort each equal-key run by its index value to recover submission order for ties. Only equal-key
            // runs pay for the fix-up; the common all-distinct-depth case does a single pass and no inner sort.
            Array.Sort(keys, order, 0, count);
            int runStart = 0;
            for (int i = 1; i <= count; i++)
            {
                if (i == count || keys[i] != keys[runStart])
                {
                    if (i - runStart > 1) Array.Sort(order, runStart, i - runStart);
                    runStart = i;
                }
            }
        }

        /// <summary>Grow <paramref name="buffer"/> to at least <paramref name="needed"/> elements, doubling from a
        /// small base so a slowly-growing batch does not reallocate every frame (mirrors the instance-buffer growth
        /// in ModelRenderer). A no-op when the buffer is already large enough. Old contents are discarded (the caller
        /// refills before reading).</summary>
        public static void EnsureCapacity<T>(ref T[] buffer, int needed)
        {
            if (buffer.Length >= needed) return;
            int cap = buffer.Length == 0 ? 64 : buffer.Length;
            while (cap < needed) cap *= 2;
            buffer = new T[cap];
        }
    }
}
