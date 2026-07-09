using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class TrailGeometryTests
    {
        static readonly Vector3 ViewDir = Vector3.Normalize(new Vector3(0.2f, -0.5f, -1f));

        // A straight trail along +X, oldest-first, uniform width, fading head->tail.
        static TrailSample[] StraightTrail(int n, float halfWidth = 0.25f)
        {
            var s = new TrailSample[n];
            for (int i = 0; i < n; i++)
            {
                float alpha = (float)i / (n - 1);          // tail (i=0) faint, head (i=n-1) full
                s[i] = new TrailSample(new Vector3(i, 0, 0), halfWidth, alpha);
            }
            return s;
        }

        static (List<Vector3> pos, List<Vector2> uv, List<float> a) Fresh() =>
            (new List<Vector3>(), new List<Vector2>(), new List<float>());

        [Fact]
        public void Build_VertexCount_IsSixPerSegment()
        {
            var (pos, uv, a) = Fresh();
            int n = TrailGeometry.Build(StraightTrail(4), ViewDir, pos, uv, a);
            Assert.Equal(6 * 3, n);            // 4 samples => 3 segments => 18 verts
            Assert.Equal(18, pos.Count);
            Assert.Equal(18, uv.Count);
            Assert.Equal(18, a.Count);
        }

        [Fact]
        public void Build_FewerThanTwoSamples_ReturnsZero()
        {
            var (pos, uv, a) = Fresh();
            Assert.Equal(0, TrailGeometry.Build(Array.Empty<TrailSample>(), ViewDir, pos, uv, a));
            Assert.Equal(0, TrailGeometry.Build(StraightTrail(2)[..1], ViewDir, pos, uv, a));
            Assert.Empty(pos);
        }

        [Fact]
        public void Build_CameraFacing_AcrossPerpendicularToTangentAndView()
        {
            var (pos, uv, a) = Fresh();
            TrailGeometry.Build(StraightTrail(3), ViewDir, pos, uv, a);
            // Segment 0: aL=pos[0], aR=pos[1]. across = aR-aL faces the camera (perp to the +X tangent AND view).
            Vector3 across = Vector3.Normalize(pos[1] - pos[0]);
            Assert.Equal(0f, Vector3.Dot(across, Vector3.UnitX), 4);   // perp to tangent
            Assert.Equal(0f, Vector3.Dot(across, ViewDir), 4);        // perp to view => camera-facing
        }

        [Fact]
        public void Build_SharedCornersAtJoint_AreContinuous()
        {
            // A bent trail so the joint miter is non-trivial; the shared sample's corners must coincide
            // between the two segments that meet there (no gap/overlap at the joint).
            var s = new[]
            {
                new TrailSample(new Vector3(0, 0, 0), 0.3f, 0.2f),
                new TrailSample(new Vector3(1, 0, 0), 0.3f, 0.6f),
                new TrailSample(new Vector3(2, 1, 0), 0.3f, 1.0f),
            };
            var (pos, uv, a) = Fresh();
            TrailGeometry.Build(s, ViewDir, pos, uv, a);
            // Segment 0 b-end corners (verts 2 = bL, 4 = bR) == segment 1 a-end corners (verts 6 = aL, 7 = aR).
            Assert.True(Vector3.Distance(pos[2], pos[6]) < 1e-5f);
            Assert.True(Vector3.Distance(pos[4], pos[7]) < 1e-5f);
        }

        [Fact]
        public void Build_Taper_AcrossSpanEqualsTwiceHalfWidthPerSample()
        {
            var s = new[]
            {
                new TrailSample(new Vector3(0, 0, 0), 0.10f, 1f),   // tail: thin
                new TrailSample(new Vector3(1, 0, 0), 0.30f, 1f),
                new TrailSample(new Vector3(2, 0, 0), 0.50f, 1f),   // head: thick
            };
            var (pos, uv, a) = Fresh();
            TrailGeometry.Build(s, ViewDir, pos, uv, a);
            // Segment 0: aL=pos[0]/aR=pos[1] at sample0 (hw 0.10); bL=pos[2]/bR=pos[4] at sample1 (hw 0.30).
            Assert.Equal(0.20f, Vector3.Distance(pos[0], pos[1]), 4);
            Assert.Equal(0.60f, Vector3.Distance(pos[2], pos[4]), 4);
        }

        [Fact]
        public void Build_Fade_PerVertexAlphaFollowsSamplesAndDecreasesTowardTail()
        {
            var (pos, uv, a) = Fresh();
            TrailGeometry.Build(StraightTrail(3), ViewDir, pos, uv, a);
            // Segment 0 spans sample0 (alpha 0.0 tail) -> sample1 (alpha 0.5). The a-end verts carry the tail alpha.
            Assert.Equal(0.0f, a[0], 4);      // aL at tail sample
            Assert.Equal(0.5f, a[2], 4);      // bL at sample1
            // Tail alpha is strictly less than head alpha across the whole strip (fades toward the tail).
            Assert.True(a[0] < a[^1]);
        }

        [Fact]
        public void Build_UvAlong_RunsZeroAtTailToOneAtHead()
        {
            var (pos, uv, a) = Fresh();
            TrailGeometry.Build(StraightTrail(3), ViewDir, pos, uv, a);
            float minV = float.MaxValue, maxV = float.MinValue, minU = float.MaxValue, maxU = float.MinValue;
            foreach (var t in uv)
            {
                minV = MathF.Min(minV, t.Y); maxV = MathF.Max(maxV, t.Y);
                minU = MathF.Min(minU, t.X); maxU = MathF.Max(maxU, t.X);
            }
            Assert.Equal(0f, minV, 5); Assert.Equal(1f, maxV, 5);   // v along [0,1], tail->head
            Assert.Equal(0f, minU, 5); Assert.Equal(1f, maxU, 5);   // u across [0,1]
            Assert.Equal(0f, uv[0].Y, 5);                            // segment 0 a-end is the tail (v=0)
        }

        [Fact]
        public void Build_TwistMode_FacingOverridesCameraFacing()
        {
            // Facing = +Y on a +X trail => across = cross(Facing, tangent) is independent of the view direction.
            var s = new[]
            {
                new TrailSample(new Vector3(0, 0, 0), 0.25f, 1f) { Facing = Vector3.UnitY },
                new TrailSample(new Vector3(1, 0, 0), 0.25f, 1f) { Facing = Vector3.UnitY },
            };
            var (p1, u1, a1) = Fresh();
            var (p2, u2, a2) = Fresh();
            TrailGeometry.Build(s, ViewDir, p1, u1, a1);
            TrailGeometry.Build(s, new Vector3(0, 0, 1), p2, u2, a2);   // different view

            Vector3 across = Vector3.Normalize(p1[1] - p1[0]);
            Assert.Equal(0f, Vector3.Dot(across, Vector3.UnitY), 4);    // perp to Facing
            // Twist ribbon holds its plane regardless of camera: corners identical under a different view dir.
            Assert.True(Vector3.Distance(p1[0], p2[0]) < 1e-5f);
            Assert.True(Vector3.Distance(p1[1], p2[1]) < 1e-5f);
        }

        [Fact]
        public void Build_SharpFold_StaysFinite()
        {
            // A near-180 reversal: sample2 folds back onto sample0, so the interior bisector tangent degenerates.
            var s = new[]
            {
                new TrailSample(new Vector3(0, 0, 0), 0.25f, 0.3f),
                new TrailSample(new Vector3(1, 0, 0), 0.25f, 0.6f),
                new TrailSample(new Vector3(0.0001f, 0, 0), 0.25f, 1f),
            };
            var (pos, uv, a) = Fresh();
            int n = TrailGeometry.Build(s, ViewDir, pos, uv, a);
            Assert.Equal(12, n);
            foreach (var v in pos)
                Assert.False(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z));
        }

        [Fact]
        public void Build_TangentParallelToView_StaysFiniteWithFullWidth()
        {
            // Trail along +Z viewed straight down +Z: cross(view, tangent) degenerates; the fallback stays finite
            // and keeps the full width.
            var s = new[]
            {
                new TrailSample(new Vector3(0, 0, 0), 0.30f, 1f),
                new TrailSample(new Vector3(0, 0, 1), 0.30f, 1f),
            };
            var (pos, uv, a) = Fresh();
            TrailGeometry.Build(s, Vector3.UnitZ, pos, uv, a);
            foreach (var v in pos)
                Assert.False(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z));
            Assert.Equal(0.60f, Vector3.Distance(pos[0], pos[1]), 4);   // full across span preserved
        }
    }
}
