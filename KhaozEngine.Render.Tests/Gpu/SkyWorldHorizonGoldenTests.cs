using System;
using System.Numerics;
using Xunit;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Gated GPU golden for the world-horizon sky (<see cref="SkyHorizon.World"/>, #396): a sun sitting exactly on
    /// the horizon above a small finite floor, seen by a camera pitched down at it. It pins the three things the
    /// screen-space sky could not do: the horizon is where the view rays go level (not at the floor's edge), the
    /// ground band fills the gap between that edge and the horizon, and the disc loses its lower half to the ground.
    /// A second golden puts two bodies in that sky over a calm sea (#1041). Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class SkyWorldHorizonGoldenTests
    {
        const int W = 480, H = 320;

        static readonly Color Ground = new(0.24f, 0.36f, 0.20f, 1f);

        [GpuFact]
        public void Golden3D_SkyWorldHorizon_SunSetsThroughTheHorizon()
        {
            MeshHandle floor = default, box = default;
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 2.5f, 9f),
                Yaw = MathF.PI + 0.2f,
                Pitch = -0.12f,   // pitched DOWN: the horizon rises above screen centre, clear of the floor's far edge
                AspectRatio = (float)W / H,
            };
            // Dead ahead and exactly level: the camera's forward axis flattened onto the ground plane.
            var forward = new Vector3(-fly.View.M13, -fly.View.M23, -fly.View.M33);
            var sun = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.1f));
                    scene.Post.Starfield = false;
                    scene.CameraOverride = fly;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.Horizon = SkyHorizon.World;
                    scene.Post.Sky.GroundColor = Ground;
                    scene.Post.Sky.HorizonSoftness = 0.004f;
                    scene.Post.Sky.HorizonColor = new Color(0.94f, 0.60f, 0.38f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.26f, 0.24f, 0.44f, 1f);
                    scene.Post.Sky.SunColor = new Color(1f, 0.92f, 0.55f, 1f);
                    scene.Post.Sky.SunRadius = 0.16f;
                    scene.Post.Sky.HaloStrength = 0f;
                    scene.Post.Sky.HaloFalloff = 0.04f;
                    // The disc is pointed by hand so the key light can stay high and the floor's shading stable.
                    scene.Post.Sky.SunDirectionOverride = sun;
                    scene.Post.LightDirection = new Vector3(0.3f, -0.8f, -0.4f);
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    scene.Draw(box, Matrix4x4.CreateTranslation(-2.2f, 0.55f, 0.5f), new Color(0.2f, 0.55f, 0.85f, 1f));
                },
                frames: 2);

            Assert.True(SkyMath.ProjectSunWorldToNdc(fly.View, fly.Projection, sun, out Vector2 sunNdc));
            var sky = new SkySettings { Horizon = SkyHorizon.World };
            SkyHorizonFrame frame = SkyHorizonMath.ResolveFrame(sky, fly.View, fly.Projection);
            Assert.True(frame.Live);
            Assert.Equal(0f, frame.SinElevation(sunNdc), 3);   // a level sun projects onto the horizon line
            Assert.True(sunNdc.Y > 0.1f, $"the pitched-down camera should lift the horizon above centre, got {sunNdc}");

            int sunPx = (int)((sunNdc.X * 0.5f + 0.5f) * W);
            int sunPy = (int)((0.5f - sunNdc.Y * 0.5f) * H);   // top-origin
            int halfDisc = (int)(0.16f * 0.5f * H * 0.5f);      // half the disc radius, in pixels

            var top = AveragePatch(rgba, sunPx, sunPy - halfDisc, 3);
            Assert.True(top.r > 0.8f && top.g > 0.7f && top.b < 0.75f,
                $"the upper half of the disc should be the bright warm sun, got rgb({top.r:0.##},{top.g:0.##},{top.b:0.##})");

            var bottom = AveragePatch(rgba, sunPx, sunPy + halfDisc, 3);
            Assert.True(bottom.g > bottom.r && bottom.r < 0.5f,
                $"the lower half of the disc should be behind the ground band, got rgb({bottom.r:0.##},{bottom.g:0.##},{bottom.b:0.##})");

            // Well clear of the disc, just under the horizon and above the floor's far edge: ground, not sky.
            int sidePx = sunPx > W / 2 ? W / 8 : W - W / 8;
            var gap = AveragePatch(rgba, sidePx, sunPy + halfDisc, 3);
            Assert.True(gap.g > gap.r && gap.g > gap.b,
                $"the gap under the horizon should be the ground band, got rgb({gap.r:0.##},{gap.g:0.##},{gap.b:0.##})");

            GoldenCompare.AssertOrUpdate("scene3d_sky_world_horizon", rgba, W, H);
        }

        // Two bodies in one sky (#1041): a low warm sun as the primary disc and a pale moon as an extra disc, over a
        // calm sea that has to reflect BOTH, each along its own direction. The primary is pointed by override while
        // the key light comes from somewhere else entirely, which is the case the old water block got wrong (it
        // reflected the disc along the key light).
        [GpuFact]
        public void Golden3D_SkyTwoDiscs_BothShowAndBothReflectInWater()
        {
            MeshHandle seabed = default;
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 1.6f, 9f),
                Yaw = MathF.PI,
                Pitch = -0.05f,
                AspectRatio = (float)W / H,
            };
            var forward = new Vector3(-fly.View.M13, -fly.View.M23, -fly.View.M33);
            var right = new Vector3(fly.View.M11, fly.View.M21, fly.View.M31);
            Vector3 flat = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
            Vector3 sun = Vector3.Normalize(flat - right * 0.30f + Vector3.UnitY * 0.14f);
            Vector3 moon = Vector3.Normalize(flat + right * 0.32f + Vector3.UnitY * 0.26f);
            var sunColor = new Color(1f, 0.62f, 0.25f, 1f);
            var moonColor = new Color(0.80f, 0.86f, 1f, 1f);

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    scene.Post.Starfield = false;
                    scene.CameraOverride = fly;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.Horizon = SkyHorizon.World;
                    scene.Post.Sky.GroundColor = new Color(0.10f, 0.16f, 0.24f, 1f);
                    scene.Post.Sky.HorizonSoftness = 0.004f;
                    scene.Post.Sky.HorizonColor = new Color(0.55f, 0.42f, 0.48f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.10f, 0.12f, 0.30f, 1f);
                    scene.Post.Sky.SunColor = sunColor;
                    scene.Post.Sky.SunRadius = 0.10f;
                    scene.Post.Sky.HaloStrength = 0f;
                    scene.Post.Sky.HaloFalloff = 0.04f;
                    scene.Post.Sky.SunDirectionOverride = sun;
                    scene.Post.Sky.ExtraDiscs.Add(new SkyDisc
                    {
                        Direction = moon, Color = moonColor, Radius = 0.07f, HaloStrength = 0f, HaloFalloff = 0.04f,
                    });
                    scene.Post.LightDirection = new Vector3(0.3f, -0.8f, 0.4f);   // NOT where either disc is
                    WaterSceneTuning.ApplyCalmMirror(scene.Post.Water);
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -6f, -20f), new Color(0.10f, 0.12f, 0.14f, 1f));
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: -20f, halfExtentX: 30f));
                },
                frames: 2);

            foreach ((Vector3 dir, Color color, string name) in new[] { (sun, sunColor, "sun"), (moon, moonColor, "moon") })
            {
                (int x, int y) sky = Pixel(fly, dir);
                (int x, int y) sea = Pixel(fly, new Vector3(dir.X, -dir.Y, dir.Z));   // its mirror image in a flat sea
                Assert.True(sea.y > sky.y, $"{name}: the reflection should be below the body on screen");

                // The post chain (HDR tonemap) pulls a bright disc a little short of its authored colour.
                var inSky = AveragePatch(rgba, sky.x, sky.y, 3);
                Assert.True(Distance(inSky, color) < 0.25f,
                    $"{name} disc should be drawn at ({sky.x},{sky.y}), got rgb({inSky.r:0.##},{inSky.g:0.##},{inSky.b:0.##})");

                // The same row of sea at screen centre holds no body. Fresnel keeps a reflection well short of the
                // body's full colour, so the claim is direction, not brightness: the patch has moved a good part of
                // the way from open sea toward the body's colour.
                var inSea = AveragePatch(rgba, sea.x, sea.y, 3);
                var control = AveragePatch(rgba, W / 2, sea.y, 3);
                float open = Distance(control, color), reflected = Distance(inSea, color);
                Assert.True(reflected < open * 0.85f,
                    $"{name} should reflect in the water at ({sea.x},{sea.y}): rgb({inSea.r:0.##},{inSea.g:0.##},{inSea.b:0.##}) against open sea rgb({control.r:0.##},{control.g:0.##},{control.b:0.##})");
            }

            GoldenCompare.AssertOrUpdate("scene3d_sky_two_discs", rgba, W, H);
        }

        static float Distance((float r, float g, float b) p, Color c) =>
            MathF.Sqrt((p.r - c.R) * (p.r - c.R) + (p.g - c.G) * (p.g - c.G) + (p.b - c.B) * (p.b - c.B));

        static (int x, int y) Pixel(FlyCamera3D camera, Vector3 direction)
        {
            Assert.True(SkyMath.ProjectSunWorldToNdc(camera.View, camera.Projection, direction, out Vector2 ndc),
                $"{direction} should project in front of the camera");
            return ((int)((ndc.X * 0.5f + 0.5f) * W), (int)((0.5f - ndc.Y * 0.5f) * H));
        }

        static (float r, float g, float b) AveragePatch(byte[] rgba, int cx, int cy, int half)
        {
            float r = 0f, g = 0f, b = 0f;
            int n = 0;
            for (int y = Math.Max(0, cy - half); y <= Math.Min(H - 1, cy + half); y++)
                for (int x = Math.Max(0, cx - half); x <= Math.Min(W - 1, cx + half); x++)
                {
                    int i = (y * W + x) * 4;
                    r += rgba[i] / 255f;
                    g += rgba[i + 1] / 255f;
                    b += rgba[i + 2] / 255f;
                    n++;
                }
            return n == 0 ? (0f, 0f, 0f) : (r / n, g / n, b / n);
        }
    }
}
