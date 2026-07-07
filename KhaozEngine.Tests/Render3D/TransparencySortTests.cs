using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (GPU-free) coverage of <see cref="TransparencySort"/>: the view-depth key math and the back-to-front
    /// index ordering that Scene3D applies to its alpha-blended transparent batches before uploading them, so
    /// overlapping translucent draws composite far-to-near regardless of submission order. Additive batches skip the
    /// sort entirely (order-independent), which the Scene3D-level queue tests assert; here we lock the sort math.
    /// </summary>
    public class TransparencySortTests
    {
        // A camera looking down -Z from the origin (the classic GL forward): depth grows as world Z gets MORE
        // negative, i.e. dot(pos - eye, forward) with forward = (0,0,-1).
        static readonly Vector3 Eye = Vector3.Zero;
        static readonly Vector3 Forward = new(0, 0, -1);

        [Fact]
        public void ViewDepth_IsSignedDistanceAlongForward()
        {
            // A point 5 units down -Z is 5 in front of the eye; a point at +Z is behind (negative depth).
            Assert.Equal(5f, TransparencySort.ViewDepth(new Vector3(0, 0, -5), Eye, Forward), 5);
            Assert.Equal(-3f, TransparencySort.ViewDepth(new Vector3(0, 0, 3), Eye, Forward), 5);
            // Lateral offset does not change forward-axis depth (strafe stability): only the forward component counts.
            Assert.Equal(5f, TransparencySort.ViewDepth(new Vector3(100, -40, -5), Eye, Forward), 5);
        }

        [Fact]
        public void ViewDepth_UsesForwardProjection_NotEuclideanDistance()
        {
            // Two points at the same forward depth (z = -5) but very different Euclidean distance to the eye must
            // read the SAME depth, so a strafe that changes distance-to-eye never reorders them.
            float near = TransparencySort.ViewDepth(new Vector3(0.1f, 0, -5), Eye, Forward);
            float far = TransparencySort.ViewDepth(new Vector3(50f, 0, -5), Eye, Forward);
            Assert.Equal(near, far, 5);
        }

        [Fact]
        public void ComputeOrder_SortsBackToFront_FarthestFirst()
        {
            // Three billboards at z = -2 (near), -8 (far), -5 (mid), queued near-first (worst case for the old code).
            var centers = new[]
            {
                new Vector3(0, 0, -2),   // 0: near
                new Vector3(0, 0, -8),   // 1: far
                new Vector3(0, 0, -5),   // 2: mid
            };
            float[] keys = Array.Empty<float>();
            int[] order = Array.Empty<int>();
            TransparencySort.ComputeOrder(centers, centers.Length, Eye, Forward, ref keys, ref order);

            // Back-to-front: far (1), mid (2), near (0).
            Assert.Equal(new[] { 1, 2, 0 }, new[] { order[0], order[1], order[2] });
        }

        [Fact]
        public void ComputeOrder_TiesKeepSubmissionOrder_Stable()
        {
            // Four items, two pairs at equal depth. The equal-depth items must keep the order they were queued.
            var centers = new[]
            {
                new Vector3(1, 0, -5),   // 0: depth 5 (pair A)
                new Vector3(9, 0, -5),   // 1: depth 5 (pair A) - queued after 0
                new Vector3(2, 0, -9),   // 2: depth 9 (pair B, farther)
                new Vector3(7, 0, -9),   // 3: depth 9 (pair B) - queued after 2
            };
            float[] keys = Array.Empty<float>();
            int[] order = Array.Empty<int>();
            TransparencySort.ComputeOrder(centers, centers.Length, Eye, Forward, ref keys, ref order);

            // Farther pair first, and within each equal-depth pair submission order (2 before 3, then 0 before 1).
            Assert.Equal(new[] { 2, 3, 0, 1 }, new[] { order[0], order[1], order[2], order[3] });
        }

        [Fact]
        public void ComputeOrder_ReusesBuffers_NoReallocWhenLargeEnough()
        {
            var centers = new[] { new Vector3(0, 0, -2), new Vector3(0, 0, -8) };
            float[] keys = new float[16];
            int[] order = new int[16];
            float[] keysRef = keys;
            int[] orderRef = order;

            TransparencySort.ComputeOrder(centers, centers.Length, Eye, Forward, ref keys, ref order);

            // The pre-sized buffers were reused in place (no growth, so the same array instances came back).
            Assert.Same(keysRef, keys);
            Assert.Same(orderRef, order);
            Assert.Equal(new[] { 1, 0 }, new[] { order[0], order[1] });
        }

        [Fact]
        public void EnsureCapacity_GrowsGeometrically_FromEmpty()
        {
            float[] buf = Array.Empty<float>();
            TransparencySort.EnsureCapacity(ref buf, 1);
            Assert.True(buf.Length >= 64);   // grows from a small base, not to exactly 1

            float[] prev = buf;
            TransparencySort.EnsureCapacity(ref buf, buf.Length);   // already big enough: no realloc
            Assert.Same(prev, buf);

            TransparencySort.EnsureCapacity(ref buf, buf.Length + 1);   // one over: doubles
            Assert.True(buf.Length >= prev.Length * 2);
        }

        [Fact]
        public void ComputeOrder_HonoursCameraForward_ReversedView()
        {
            // Camera now looks down +Z, so depth grows with +Z. The item at +8 is farthest and must come first.
            Vector3 fwd = new(0, 0, 1);
            var centers = new[]
            {
                new Vector3(0, 0, 2),
                new Vector3(0, 0, 8),
                new Vector3(0, 0, 5),
            };
            float[] keys = Array.Empty<float>();
            int[] order = Array.Empty<int>();
            TransparencySort.ComputeOrder(centers, centers.Length, Eye, fwd, ref keys, ref order);
            Assert.Equal(new[] { 1, 2, 0 }, new[] { order[0], order[1], order[2] });
        }
    }
}
