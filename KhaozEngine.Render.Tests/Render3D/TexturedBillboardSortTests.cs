using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (GPU-free) coverage of <see cref="Scene3D.SortTexturedBillboardsBackToFront"/>: the reorder-before-upload
    /// step the textured-billboard pass runs so overlapping alpha quads composite far-to-near, plus proof that
    /// additive quads are order-independent (sorting them by depth is harmless and never scrambles their result).
    /// </summary>
    public class TexturedBillboardSortTests
    {
        static readonly Vector3 Eye = Vector3.Zero;
        static readonly Vector3 Forward = new(0, 0, -1);   // looking down -Z: depth grows as z gets more negative

        static Scene3D.TexturedBillboardItem Item(Vector3 center, BillboardBlend blend, float colorR) => new()
        {
            TexIndex = 0,
            Blend = blend,
            Center = center,
            Size = 1f,
            SourceUv = new Vector4(0, 0, 1, 1),
            Color = new Vector4(colorR, 0, 0, 1),
        };

        static List<Scene3D.TexturedBillboardItem> Sort(params Scene3D.TexturedBillboardItem[] items)
        {
            var centers = new List<Vector3>();
            float[] keys = Array.Empty<float>();
            int[] order = Array.Empty<int>();
            var sorted = new List<Scene3D.TexturedBillboardItem>();
            Scene3D.SortTexturedBillboardsBackToFront(items, Eye, Forward, centers, ref keys, ref order, sorted);
            return sorted;
        }

        [Fact]
        public void AlphaBatch_SubmittedFrontToBack_UploadsBackToFront()
        {
            // Worst case for the old submission-order code: near quad queued FIRST, far one LAST.
            var sorted = Sort(
                Item(new Vector3(0, 0, -2), BillboardBlend.Alpha, 0.1f),   // near
                Item(new Vector3(0, 0, -9), BillboardBlend.Alpha, 0.9f),   // far
                Item(new Vector3(0, 0, -5), BillboardBlend.Alpha, 0.5f));  // mid

            // Upload order is far -> mid -> near (identified by the R channel we tagged each depth with).
            Assert.Equal(3, sorted.Count);
            Assert.Equal(0.9f, sorted[0].Color.X, 5);   // far first
            Assert.Equal(0.5f, sorted[1].Color.X, 5);   // mid
            Assert.Equal(0.1f, sorted[2].Color.X, 5);   // near last
        }

        [Fact]
        public void AlreadyBackToFront_IsUnchanged()
        {
            // Far -> near submission is already correct: a stable sort must not permute it.
            var sorted = Sort(
                Item(new Vector3(0, 0, -9), BillboardBlend.Alpha, 0.9f),
                Item(new Vector3(0, 0, -5), BillboardBlend.Alpha, 0.5f),
                Item(new Vector3(0, 0, -2), BillboardBlend.Alpha, 0.1f));

            Assert.Equal(0.9f, sorted[0].Color.X, 5);
            Assert.Equal(0.5f, sorted[1].Color.X, 5);
            Assert.Equal(0.1f, sorted[2].Color.X, 5);
        }

        [Fact]
        public void AdditiveBatch_OrderIndependent_SortIsHarmless()
        {
            // Additive blend (out = src + dst) is commutative, so the composited result is identical for ANY order.
            // The pass sorts additive quads by depth alongside alpha (one code path), which is provably harmless:
            // whatever permutation comes out, every input item is present exactly once. Assert the multiset is
            // preserved (no drop / duplicate), which is all order-independence requires.
            var a = Item(new Vector3(0, 0, -2), BillboardBlend.Additive, 0.1f);
            var b = Item(new Vector3(0, 0, -9), BillboardBlend.Additive, 0.9f);
            var c = Item(new Vector3(0, 0, -5), BillboardBlend.Additive, 0.5f);
            var sorted = Sort(a, b, c);

            var seen = new HashSet<float>();
            foreach (var it in sorted) seen.Add(it.Color.X);
            Assert.Equal(3, sorted.Count);
            Assert.Equal(new HashSet<float> { 0.1f, 0.5f, 0.9f }, seen);
        }

        [Fact]
        public void EqualDepth_KeepsSubmissionOrder_Stable()
        {
            // Two quads at the same forward depth must keep the order they were queued (deterministic blend).
            var sorted = Sort(
                Item(new Vector3(-3, 0, -5), BillboardBlend.Alpha, 0.2f),   // queued first
                Item(new Vector3(3, 0, -5), BillboardBlend.Alpha, 0.8f));   // queued second, same depth

            Assert.Equal(0.2f, sorted[0].Color.X, 5);
            Assert.Equal(0.8f, sorted[1].Color.X, 5);
        }
    }
}
