using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Regression net for the flat-ground ring depth-fade bug: a <see cref="ParticleOrientation.FlatGround"/> ring
    /// (a shockwave nova) lies coplanar with the floor, so the soft depth fade used to reconstruct that same floor
    /// behind it and erase the near/far arcs at a grazing camera angle, leaving the ring "only partially visible".
    /// The fix skips the depth fade for flat-ground sprites. This asserts the ring is a WHOLE ellipse: lit pixels
    /// exist at BOTH the near edge (closest to the camera, lowest on screen, the arc that used to vanish) and the
    /// far edge. Camera + stage mirror <c>RoomVfx</c>. Not a byte golden (deliberately no "Golden" in the name) so
    /// it reads as gross-geometry behaviour, robust across backends.
    /// </summary>
    public sealed class FlatGroundRingFadeTests
    {
        const int W = 960, H = 540;
        const float Lift = 0.09f;      // RoomVfx nova-ring OriginOffset
        const float RingSize = 1.7f;   // ~ full expansion
        const float BandR = 0.70f * RingSize;   // ring mask band radius in world units (shader draws the annulus at d~0.70)

        [GpuFact]
        public void FlatGround_ring_renders_whole_ellipse_not_partial_arc()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.TransparentBackground = false;
            preview.Scene.Post.Starfield = false;
            preview.Scene.Post.BackgroundColor = new Color(0.11f, 0.12f, 0.15f, 1f);
            preview.Scene.Post.Bloom.Enabled = true;

            var cam = new FollowCamera3D { Target = new Vector3(0f, 1f, 0f) };
            cam.Distance = 10f;
            cam.AspectRatio = (float)W / H;
            preview.Scene.CameraOverride = cam;

            MeshHandle ground = preview.Scene.LoadMesh(MeshPrimitives.Plane(40f, 40f, 4, 4));
            byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
            {
                s.Draw(ground, Matrix4x4.Identity, new Color(0.16f, 0.17f, 0.20f, 1f));
                s.DrawParticle(new ParticleSprite
                {
                    Position = new Vector3(0f, Lift, 0f),
                    Size = RingSize,
                    Color = new Color(1f, 0.9f, 0.7f, 0.9f),
                    Shape = ParticleShape.Ring,
                    ShapeParam = 0.15f,
                    Blend = BillboardBlend.Additive,
                    Orientation = ParticleOrientation.FlatGround,
                });
            }).Handle, W, H);

            // Near edge projects lowest on screen (+Z is toward the camera at yaw 0), far edge highest.
            Assert.True(cam.WorldToScreen(new Vector3(0f, Lift, +BandR), W, H, out Vector2 near), "near edge behind camera");
            Assert.True(cam.WorldToScreen(new Vector3(0f, Lift, -BandR), W, H, out Vector2 far), "far edge behind camera");

            int nearLit = LitPixelsNear(px, near, radius: 22);
            int farLit = LitPixelsNear(px, far, radius: 22);

            // Both arcs must be present. Pre-fix, the near arc was fully erased by the depth fade (nearLit ~ 0).
            Assert.True(farLit > 20, $"far ring arc missing (farLit={farLit}) - ring not rendering at all?");
            Assert.True(nearLit > 20, $"near ring arc erased (nearLit={nearLit}) - flat-ground depth-fade regression");
        }

        // Count bright (ring) pixels within a screen-space radius of a projected point. The additive warm ring reads
        // far brighter than the ~(41,43,51) floor; threshold well above it.
        static int LitPixelsNear(byte[] rgba, Vector2 center, int radius)
        {
            int cx = (int)MathF.Round(center.X), cy = (int)MathF.Round(center.Y);
            int r2 = radius * radius, count = 0;
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(H - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(W - 1, cx + radius); x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) continue;
                int i = (y * W + x) * 4;
                if (rgba[i] > 110 && rgba[i + 1] > 95) count++;   // warm-bright ring pixel
            }
            return count;
        }
    }
}
