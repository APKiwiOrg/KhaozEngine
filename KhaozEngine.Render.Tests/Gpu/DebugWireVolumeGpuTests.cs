using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the immediate-mode debug wire volumes
    /// (<see cref="Scene3D.DebugWireSphere"/> / <see cref="Scene3D.DebugWireDome"/> /
    /// <see cref="Scene3D.DebugWireCylinder"/> / <see cref="Scene3D.DebugWireCircle"/>). Verifies the immediate-mode
    /// contract (queued vertices route by <see cref="DebugDepthMode"/> and clear each <see cref="Scene3D.Begin"/>) and
    /// that the default depth-tested mode is actually occluded by scene geometry while the always-on-top mode is not.
    /// Backend-agnostic (relative assertions, no committed golden). Skipped unless KE_GPU_TESTS is set.
    /// </summary>
    public sealed class DebugWireVolumeGpuTests
    {
        const int W = 160, H = 160;

        static void CleanBackground(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.TransparentBackground = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
        }

        [GpuFact]
        public void Wire_volumes_route_by_depth_mode_and_clear_each_frame()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);
            Scene3D scene = preview.Scene;

            scene.Begin();
            // Two segments-32 shapes on the depth-tested stream, one on the always-on-top stream.
            scene.DebugWireSphere(Vector3.Zero, 2f, new Color(1f, 0f, 0f, 1f));                       // depth-tested (default)
            scene.DebugWireCylinder(new Vector3(5, 0, 0), 1f, 2f, new Color(0f, 1f, 0f, 1f));         // depth-tested (default)
            scene.DebugWireDome(new Vector3(-5, 0, 0), 2f, new Color(0f, 0f, 1f, 1f),
                depth: DebugDepthMode.AlwaysOnTop);                                                    // always-on-top

            Assert.True(scene.DepthLineVertexCount > 0, "depth-tested volumes queued nothing");
            Assert.True(scene.LineVertexCount > 0, "always-on-top volume queued nothing");
            // Depth stream carries the sphere + cylinder. Overlay stream carries only the dome.
            Assert.True(scene.DepthLineVertexCount > scene.LineVertexCount);

            // Begin() (inside Capture) clears both streams. The empty draw adds nothing back.
            preview.Capture(_ => { });
            Assert.Equal(0, scene.DepthLineVertexCount);
            Assert.Equal(0, scene.LineVertexCount);
        }

        [GpuFact]
        public void Depth_tested_volume_is_occluded_by_geometry_but_always_on_top_is_not()
        {
            // A big opaque box sits at the origin facing the camera, and a bright wire sphere is drawn at the SAME place
            // so the box front face fully covers it. Depth-tested => the sphere is hidden; always-on-top => it shows.
            GltfMesh box = MeshPrimitives.Box(4f);
            MeshHandle handle = default;
            void Setup(Scene3D scene)
            {
                handle = scene.LoadMesh(box);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(5f, 5f, 5f));
            }

            var red = new Color(1f, 0.05f, 0.05f, 1f);
            byte[] occluded = Render3DSnapshot.Capture(W, H, Setup, drawFrame: scene =>
            {
                scene.Draw(handle, Matrix4x4.Identity);
                scene.DebugWireSphere(Vector3.Zero, 1.6f, red);                              // depth-tested (default)
            }, frames: 1);
            byte[] onTop = Render3DSnapshot.Capture(W, H, Setup, drawFrame: scene =>
            {
                scene.Draw(handle, Matrix4x4.Identity);
                scene.DebugWireSphere(Vector3.Zero, 1.6f, red, depth: DebugDepthMode.AlwaysOnTop);
            }, frames: 1);

            int occludedRed = CountRed(occluded), onTopRed = CountRed(onTop);
            // The always-on-top sphere draws its wire over the box. The depth-tested one is mostly hidden behind it.
            Assert.True(onTopRed > occludedRed * 3,
                $"expected the always-on-top wire to show far more red than the occluded one: onTop={onTopRed} occluded={occludedRed}");
        }

        // Pixels where red clearly dominates (the wire colour), so the box/background don't count.
        static int CountRed(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > 130 && rgba[i] > rgba[i + 1] * 2 && rgba[i] > rgba[i + 2] * 2) n++;
            return n;
        }
    }
}
