using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Camera-relative rendering, camera half: the three engine cameras' <see cref="IRenderOriginAware"/>
    /// implementation, and the two projections that would break silently without it.
    /// <para>
    /// <c>WorldToScreen</c> and <c>ScreenToRay</c> are the landmine here. Both take or return ABSOLUTE world points
    /// while going through a now render-relative <c>ViewProjection</c>, so missing the conversion produces a picking
    /// error equal to the whole render origin: at 100 km, picking simply stops working, and no rendered golden shows
    /// it because the image is perfect. These are headless, which is exactly why they are worth having.
    /// </para>
    /// </summary>
    public sealed class RenderOriginCameraTests
    {
        const int W = 800, H = 600;
        // 100 km out, the offset the whole design is sized against, and its quantized frame anchor.
        static readonly Vector3 Far = new(100_000f, 0f, 100_000f);
        static Vector3 FarAnchor => WorldFrame.Nearest(Far).Anchor;

        static IIsoCamera3D[] CamerasAt(Vector3 target, Vector3 renderOrigin)
        {
            var iso = new IsoCamera3D { Target = target, AspectRatio = (float)W / H, FarPlane = 4000f, RenderOrigin = renderOrigin };
            // Placed 18 m back and 6 m up, pitched down onto the target, so the probe below sits near frame centre.
            var fly = new FlyCamera3D
            {
                Position = target + new Vector3(0f, 6f, -18f),
                Pitch = -MathF.Atan2(6f, 18f),
                AspectRatio = (float)W / H,
                RenderOrigin = renderOrigin,
            };
            var follow = new FollowCamera3D { AspectRatio = (float)W / H, RenderOrigin = renderOrigin };
            follow.Warp(target);
            return new IIsoCamera3D[] { iso, fly, follow };
        }

        [Fact]
        public void A_zero_render_origin_leaves_every_camera_matrix_bit_identical()
        {
            // The opt-out, and the reason the whole existing golden suite is unaffected wherever the quantized eye
            // lands on zero: `p - Vector3.Zero` is p, bit for bit, so the shifted path IS the old path there.
            foreach (IIsoCamera3D cam in CamerasAt(new Vector3(3f, 0f, -7f), Vector3.Zero))
            {
                var aware = Assert.IsAssignableFrom<IRenderOriginAware>(cam);
                Assert.Equal(Vector3.Zero, aware.RenderOrigin);
                AssertMatrixBitIdentical(aware.AbsoluteViewProjection, cam.ViewProjection);
            }
        }

        [Fact]
        public void The_absolute_view_projection_ignores_the_render_origin()
        {
            // The CPU-side spatial paths (frustum culling, cascade fitting, caster classification) read this one, so
            // it must be independent of whatever origin the GPU path is using this frame.
            Vector3 target = new(240f, 0f, -310f);
            IIsoCamera3D[] shifted = CamerasAt(target, WorldFrame.Nearest(target).Anchor);
            IIsoCamera3D[] absolute = CamerasAt(target, Vector3.Zero);
            for (int i = 0; i < shifted.Length; i++)
            {
                var a = (IRenderOriginAware)shifted[i];
                Assert.NotEqual(Vector3.Zero, a.RenderOrigin);
                AssertMatrixBitIdentical(absolute[i].ViewProjection, a.AbsoluteViewProjection);
                Assert.NotEqual(absolute[i].ViewProjection, shifted[i].ViewProjection);   // the GPU path really did shift
            }
        }

        [Fact]
        public void World_to_screen_and_screen_to_ray_round_trip_at_a_hundred_kilometres()
        {
            // Test 19a. Project a point near the camera, unproject the pixel, and land back on it. The tolerance is
            // in METRES of world position: a missed conversion lands the ray a whole render origin away (1e5 m), so
            // this fails by 7 orders of magnitude rather than marginally.
            foreach (IIsoCamera3D cam in CamerasAt(Far, FarAnchor))
            {
                Vector3 probe = Far + new Vector3(1.5f, 0.75f, -2.25f);
                Assert.True(cam.WorldToScreen(probe, W, H, out Vector2 pixel),
                    $"{cam.GetType().Name} failed to project a point 3 m from its own target");
                Assert.InRange(pixel.X, 0f, W);
                Assert.InRange(pixel.Y, 0f, H);

                Ray ray = ScreenToRay(cam, pixel);
                Vector3 hit = ClosestPointOnRay(ray, probe);
                Assert.True((hit - probe).Length() < 0.05f,
                    $"{cam.GetType().Name} round-tripped {probe} to {hit}, {(hit - probe).Length()} m away");
            }
        }

        [Fact]
        public void The_render_origin_is_what_makes_the_round_trip_precise_at_range()
        {
            // The same round trip through the ABSOLUTE path at the same distance, which is what a consumer camera
            // that does not implement IRenderOriginAware still gets. Asserting the shifted path is strictly better
            // is what stops this feature quietly becoming a no-op: without the eye-side subtraction the projection
            // is a difference of two ~1e5 float32 values and the surviving precision is metres, not millimetres.
            Vector3 probe = Far + new Vector3(1.5f, 0.75f, -2.25f);
            float shifted = RoundTripError(CamerasAt(Far, FarAnchor)[1], probe);      // FlyCamera3D, perspective
            float absolute = RoundTripError(CamerasAt(Far, Vector3.Zero)[1], probe);
            Assert.True(shifted < absolute * 0.25f,
                $"the render-origin round trip ({shifted} m) is not meaningfully better than the absolute one ({absolute} m)");
        }

        [Fact]
        public void A_point_behind_the_camera_is_still_rejected_with_an_origin_in_force()
        {
            // The near/far rejection is a clip-space test, and the shift must not turn it into a "yes" that draws a
            // nameplate for something behind the player.
            foreach (IIsoCamera3D cam in CamerasAt(Far, FarAnchor))
            {
                Vector3 behind = Far - cam.Forward * 5_000f;
                Assert.False(cam.WorldToScreen(behind, W, H, out Vector2 pixel),
                    $"{cam.GetType().Name} projected a point behind itself");
                Assert.Equal(default, pixel);
            }
        }

        [Fact]
        public void Screen_to_ground_returns_an_absolute_world_point()
        {
            // ScreenToGround rides on ScreenToRay, so it inherits the add-back. The plane it solves against is an
            // ABSOLUTE height, which is only meaningful if the ray is absolute too.
            var cam = new FlyCamera3D
            {
                Position = Far + new Vector3(0f, 20f, 0f),
                Pitch = -MathF.PI / 4f,
                AspectRatio = (float)W / H,
                RenderOrigin = FarAnchor,
            };
            Vector3 ground = cam.ScreenToGround(new Vector2(W / 2f, H / 2f), W, H, groundY: 0f);
            Assert.True(MathF.Abs(ground.Y) < 0.01f, $"expected the y=0 plane, got y={ground.Y}");
            Assert.True(MathF.Abs(ground.X - Far.X) < 200f && MathF.Abs(ground.Z - Far.Z) < 200f,
                $"expected a point near the camera at {Far}, got {ground}: the ray is still frame-local");
        }

        static float RoundTripError(IIsoCamera3D cam, Vector3 probe)
        {
            Assert.True(cam.WorldToScreen(probe, W, H, out Vector2 pixel));
            return (ClosestPointOnRay(ScreenToRay(cam, pixel), probe) - probe).Length();
        }

        static Ray ScreenToRay(IIsoCamera3D cam, Vector2 pixel) => cam switch
        {
            IsoCamera3D iso => iso.ScreenToRay(pixel, W, H),
            FlyCamera3D fly => fly.ScreenToRay(pixel, W, H),
            FollowCamera3D follow => follow.ScreenToRay(pixel, W, H),
            _ => throw new InvalidOperationException("unexpected camera type"),
        };

        // The ray is a line, not a point, so the round trip compares the probe against the nearest point on it.
        static Vector3 ClosestPointOnRay(Ray ray, Vector3 p)
        {
            Vector3 d = ray.Direction;
            float len2 = d.LengthSquared();
            if (len2 < 1e-12f) return ray.Origin;
            return ray.Origin + d * (Vector3.Dot(p - ray.Origin, d) / len2);
        }

        static void AssertMatrixBitIdentical(Matrix4x4 expected, Matrix4x4 actual)
        {
            for (int r = 1; r <= 4; r++)
                for (int c = 1; c <= 4; c++)
                {
                    float e = Element(expected, r, c), a = Element(actual, r, c);
                    Assert.Equal(BitConverter.SingleToUInt32Bits(e), BitConverter.SingleToUInt32Bits(a));
                }
        }

        static float Element(in Matrix4x4 m, int row, int col) => (row, col) switch
        {
            (1, 1) => m.M11, (1, 2) => m.M12, (1, 3) => m.M13, (1, 4) => m.M14,
            (2, 1) => m.M21, (2, 2) => m.M22, (2, 3) => m.M23, (2, 4) => m.M24,
            (3, 1) => m.M31, (3, 2) => m.M32, (3, 3) => m.M33, (3, 4) => m.M34,
            (4, 1) => m.M41, (4, 2) => m.M42, (4, 3) => m.M43, _ => m.M44,
        };
    }
}
