using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of the per-cascade shadow caster cull: the sphere-vs-cascade test itself
    /// (<see cref="ShadowCascadeCull"/>) and the span splitter that consumes it
    /// (<see cref="Scene3D.BuildCascadeSpans"/>). No GPU, so the two load-bearing contracts are pinned here rather
    /// than inferred from a rendered frame: XY culling is exact for a directional light, and the NEAR plane is never
    /// a cull plane (the 17.13.0 pancaking contract, issue #394).
    /// </summary>
    public sealed class ShadowCascadeCullTests
    {
        const int Resolution = 2048;

        // A cascade fitted around the origin with a 20 m radius, lit from a 35 degree sun (the Ruinborne-shaped
        // case): the same call Scene3D.FitCascade makes.
        static ShadowCascadeCull Cascade(float radius = 20f, Vector3 focus = default, float elevationDegrees = 35f)
        {
            float e = elevationDegrees * MathF.PI / 180f;
            var dir = Vector3.Normalize(new Vector3(MathF.Cos(e), -MathF.Sin(e), 0.2f));
            Matrix4x4 vp = ShadowMapMath.BuildLightViewProj(dir, focus, radius, Resolution);
            return ShadowCascadeCull.FromLightViewProj(vp, Resolution);
        }

        // The light-space axes of the same fit, so a test can push a point a known distance ACROSS the map or ALONG
        // the light ray without re-deriving the basis.
        static (Vector3 Right, Vector3 Up, Vector3 Along) Basis(float elevationDegrees = 35f)
        {
            float e = elevationDegrees * MathF.PI / 180f;
            var dir = Vector3.Normalize(new Vector3(MathF.Cos(e), -MathF.Sin(e), 0.2f));
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dir));
            Vector3 up = Vector3.Normalize(Vector3.Cross(dir, right));
            return (right, up, dir);
        }

        [Fact]
        public void Light_matrix_is_affine_so_the_clip_w_is_always_one()
        {
            // The cull's whole arithmetic assumes an ORTHOGRAPHIC light fit (clip.w == 1), which is what lets a world
            // radius scale into a clip radius by a column length. Pin it at the source rather than trusting it.
            Matrix4x4 vp = ShadowMapMath.BuildLightViewProj(new Vector3(0.4f, -0.9f, 0.2f), new Vector3(30f, 5f, -12f), 25f, Resolution);
            Assert.Equal(0f, vp.M14);
            Assert.Equal(0f, vp.M24);
            Assert.Equal(0f, vp.M34);
            Assert.Equal(1f, vp.M44);
        }

        [Fact]
        public void A_caster_at_the_focus_is_kept()
        {
            Assert.True(Cascade().Intersects(Vector3.Zero, 1f));
        }

        [Fact]
        public void A_caster_far_across_the_map_in_light_space_xy_is_culled()
        {
            var (right, up, _) = Basis();
            ShadowCascadeCull cull = Cascade(radius: 20f);
            Assert.False(cull.Intersects(right * 200f, 1f));
            Assert.False(cull.Intersects(up * 200f, 1f));
            Assert.False(cull.Intersects(right * -200f + up * 60f, 1f));
        }

        [Fact]
        public void A_caster_straddling_the_xy_edge_is_kept()
        {
            var (right, _, _) = Basis();
            ShadowCascadeCull cull = Cascade(radius: 20f);
            // Centre 3 m outside the 20 m half-extent, but a 5 m radius reaches back in.
            Assert.True(cull.Intersects(right * 23f, 5f));
            // Same centre with a small radius does not reach, so it goes.
            Assert.False(cull.Intersects(right * 23f, 0.5f));
        }

        [Fact]
        public void The_margin_keeps_a_caster_exactly_on_the_edge()
        {
            var (right, _, _) = Basis();
            const float R = 20f;
            ShadowCascadeCull cull = Cascade(radius: R);
            // Exactly at the half-extent, zero radius: inside by the margin, never a rounding coin-flip.
            Assert.True(cull.Intersects(right * R, 0f));
            // The margin is texel-sized, so it is small: a caster a metre out with no radius is still culled.
            Assert.False(cull.Intersects(right * (R + 1f), 0f));
        }

        [Fact]
        public void A_caster_far_up_light_of_the_near_plane_is_never_culled()
        {
            // THE contract. At a grazing sun a tall caster sits many cascade radii up-light of the ground it shades,
            // in front of the light's near plane, and the depth pass PANCAKES it to the near plane instead of
            // clipping it (issue #394). Culling it on the near plane would delete exactly that shadow. Walk a caster
            // a long way up-light and assert it survives at every step.
            var (_, _, along) = Basis();
            ShadowCascadeCull cull = Cascade(radius: 20f);
            for (float d = 0f; d <= 400f; d += 25f)
                Assert.True(cull.Intersects(-along * d, 1f),
                    $"a caster {d} m up-light of the focus must never be culled (the pancaking contract)");
        }

        [Fact]
        public void A_caster_far_down_light_past_the_far_plane_is_culled()
        {
            // The mirror case, which IS safe: the rasterizer clips depth past 1 identically, so dropping it on the
            // CPU changes nothing. The fit spans 4r of depth from the eye at 2r up-light.
            var (_, _, along) = Basis();
            ShadowCascadeCull cull = Cascade(radius: 20f);
            Assert.True(cull.Intersects(along * 10f, 1f));      // still inside the depth range
            Assert.False(cull.Intersects(along * 400f, 1f));    // far past the far plane
        }

        [Fact]
        public void Culling_follows_the_cascade_focus()
        {
            // A caster 100 m out is outside a cascade fitted at the origin and inside one fitted around it: the
            // fall-through the whole design depends on (cascade 0 rejects, a wider or further cascade keeps).
            var (right, _, _) = Basis();
            Vector3 far = right * 100f;
            Assert.False(Cascade(radius: 20f).Intersects(far, 1f));
            Assert.True(Cascade(radius: 20f, focus: far).Intersects(far, 1f));
            Assert.True(Cascade(radius: 150f).Intersects(far, 1f));   // a wide outer cascade reaches it too
        }

        [Fact]
        public void Margin_scales_with_resolution()
        {
            Assert.Equal(2f * ShadowCascadeCull.MarginTexels / 1024f, ShadowCascadeCull.ClipMargin(1024), 6);
            Assert.Equal(2f * ShadowCascadeCull.MarginTexels / 2048f, ShadowCascadeCull.ClipMargin(2048), 6);
            Assert.True(ShadowCascadeCull.ClipMargin(0) > 0f, "a degenerate resolution must not produce a zero or negative margin");
        }

        // ---- the span splitter -------------------------------------------------------------------------------

        // Mask one cascade's worth of spheres, then split. Exercises both halves of the split the way the renderer
        // chains them: ComputeCascadeMasks writes the per-instance bits, BuildCascadeSpans reads one bit.
        static List<Scene3D.ShadowCasterSpan> Split(List<Scene3D.ShadowCasterSpan> source, List<Vector4> spheres,
            ShadowCascadeCull cull, int mergeGap)
        {
            var masks = new byte[spheres.Count];
            Span<ShadowCascadeCull> culls = stackalloc ShadowCascadeCull[1];
            culls[0] = cull;
            Scene3D.ComputeCascadeMasks(CollectionsMarshal.AsSpan(spheres), culls, 1, masks);
            var dst = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.BuildCascadeSpans(CollectionsMarshal.AsSpan(source), masks, 0, mergeGap, dst);
            return dst;
        }

        // One span of n instances, alternating in/out by the caller's predicate, laid out along the light-space
        // right axis so "out" really is out on XY.
        static (List<Scene3D.ShadowCasterSpan> Spans, List<Vector4> Spheres) Scene(int n, Func<int, bool> inside)
        {
            var (right, _, _) = Basis();
            var spans = new List<Scene3D.ShadowCasterSpan> { new(3, 1, 0, (uint)n, ShadowCastKind.Opaque) };
            var spheres = new List<Vector4>();
            for (int i = 0; i < n; i++)
            {
                Vector3 p = inside(i) ? Vector3.Zero : right * 500f;
                spheres.Add(new Vector4(p, 0.5f));
            }
            return (spans, spheres);
        }

        [Fact]
        public void Everything_inside_yields_the_original_span()
        {
            var (spans, spheres) = Scene(64, _ => true);
            List<Scene3D.ShadowCasterSpan> dst = Split(spans, spheres, Cascade(), mergeGap: 0);
            Assert.Single(dst);
            Assert.Equal(spans[0], dst[0]);
        }

        [Fact]
        public void Everything_outside_yields_nothing()
        {
            var (spans, spheres) = Scene(64, _ => false);
            Assert.Empty(Split(spans, spheres, Cascade(), mergeGap: 0));
        }

        [Fact]
        public void An_exact_split_emits_only_the_kept_runs()
        {
            // Slots 2..4 and 9 are inside, nothing else is.
            var (spans, spheres) = Scene(12, i => (i >= 2 && i <= 4) || i == 9);
            List<Scene3D.ShadowCasterSpan> dst = Split(spans, spheres, Cascade(), mergeGap: 0);
            Assert.Equal(2, dst.Count);
            Assert.Equal(new Scene3D.ShadowCasterSpan(3, 1, 2, 3, ShadowCastKind.Opaque), dst[0]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(3, 1, 9, 1, ShadowCastKind.Opaque), dst[1]);
        }

        [Fact]
        public void The_merge_gap_draws_through_a_small_hole_instead_of_splitting()
        {
            var (spans, spheres) = Scene(12, i => (i >= 2 && i <= 4) || i == 9);
            // Gap 4 covers the 4 rejected slots between them, so one draw spans 2..9.
            List<Scene3D.ShadowCasterSpan> dst = Split(spans, spheres, Cascade(), mergeGap: 4);
            Assert.Single(dst);
            Assert.Equal(new Scene3D.ShadowCasterSpan(3, 1, 2, 8, ShadowCastKind.Opaque), dst[0]);
            // Gap 3 is one short, so it splits.
            Assert.Equal(2, Split(spans, spheres, Cascade(), mergeGap: 3).Count);
        }

        [Fact]
        public void Merging_never_extends_past_the_last_kept_instance()
        {
            // Trailing rejects must not ride along on the merge gap: the span ends at slot 4.
            var (spans, spheres) = Scene(12, i => i >= 2 && i <= 4);
            List<Scene3D.ShadowCasterSpan> dst = Split(spans, spheres, Cascade(), mergeGap: 64);
            Assert.Single(dst);
            Assert.Equal(new Scene3D.ShadowCasterSpan(3, 1, 2, 3, ShadowCastKind.Opaque), dst[0]);
        }

        [Fact]
        public void Each_source_span_keeps_its_own_mesh_and_kind()
        {
            var (right, _, _) = Basis();
            var spans = new List<Scene3D.ShadowCasterSpan>
            {
                new(3, 1, 0, 2, ShadowCastKind.Opaque),
                new(8, 2, 2, 2, ShadowCastKind.Dissolving),
                new(9, 4, 4, 2, ShadowCastKind.DissolvingInverted),
            };
            var spheres = new List<Vector4>();
            // Keep the first of each pair, drop the second.
            for (int i = 0; i < 6; i++)
                spheres.Add(new Vector4(i % 2 == 0 ? Vector3.Zero : right * 500f, 0.5f));
            List<Scene3D.ShadowCasterSpan> dst = Split(spans, spheres, Cascade(), mergeGap: 0);
            Assert.Equal(3, dst.Count);
            Assert.Equal(new Scene3D.ShadowCasterSpan(3, 1, 0, 1, ShadowCastKind.Opaque), dst[0]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(8, 2, 2, 1, ShadowCastKind.Dissolving), dst[1]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(9, 4, 4, 1, ShadowCastKind.DissolvingInverted), dst[2]);
        }

        [Fact]
        public void A_short_mask_array_keeps_the_caster_rather_than_dropping_it()
        {
            // Defensive: a mismatched mask array is a bug, and the safe direction is to draw the caster (the
            // pre-cull behaviour) rather than silently lose a shadow.
            var spans = new List<Scene3D.ShadowCasterSpan> { new(3, 1, 0, 4, ShadowCastKind.Opaque) };
            var dst = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.BuildCascadeSpans(CollectionsMarshal.AsSpan(spans), ReadOnlySpan<byte>.Empty, 0, 0, dst);
            Assert.Single(dst);
            Assert.Equal(spans[0], dst[0]);
        }

        [Fact]
        public void One_mask_pass_sets_a_bit_per_cascade()
        {
            // The renderer tests every cascade in a single pass over the spheres, so the bit layout is load-bearing:
            // bit c must be cascade c. A caster at the origin reaches both cascades here, one 100 m out only the
            // wide one, and one 400 m out neither.
            var (right, _, _) = Basis();
            Span<ShadowCascadeCull> culls = stackalloc ShadowCascadeCull[2];
            culls[0] = Cascade(radius: 20f);
            culls[1] = Cascade(radius: 150f);
            var spheres = new List<Vector4>
            {
                new(Vector3.Zero, 0.5f),
                new(right * 100f, 0.5f),
                new(right * 400f, 0.5f),
            };
            var masks = new byte[spheres.Count];
            Scene3D.ComputeCascadeMasks(CollectionsMarshal.AsSpan(spheres), culls, 2, masks);
            Assert.Equal(0b11, masks[0]);
            Assert.Equal(0b10, masks[1]);
            Assert.Equal(0b00, masks[2]);
        }

        [Fact]
        public void Splitting_is_allocation_stable_across_frames()
        {
            // The splitter runs per cascade per dirty frame, so it reuses the caller's list. Refilling it must not
            // grow capacity once it has settled.
            var (spans, spheres) = Scene(256, i => i % 8 < 3);
            var masks = new byte[spheres.Count];
            Span<ShadowCascadeCull> culls = stackalloc ShadowCascadeCull[1];
            culls[0] = Cascade();
            Scene3D.ComputeCascadeMasks(CollectionsMarshal.AsSpan(spheres), culls, 1, masks);
            var dst = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.BuildCascadeSpans(CollectionsMarshal.AsSpan(spans), masks, 0, 0, dst);
            int capacity = dst.Capacity;
            for (int f = 0; f < 8; f++) Scene3D.BuildCascadeSpans(CollectionsMarshal.AsSpan(spans), masks, 0, 0, dst);
            Assert.Equal(capacity, dst.Capacity);
        }
    }
}
