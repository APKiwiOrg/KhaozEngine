using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the dynamic point-light path (<see cref="Scene3D.AddLight"/>). Renders the SAME box with
    /// the global key/fill/ambient lights dimmed to near-black, so any illumination is attributable to the point
    /// lights, and reads the result back. Backend-agnostic (asserts relative brightening, not absolute pixel
    /// values, so it needs no per-backend committed golden). Also asserts colour tinting, the host budget clamp
    /// (over-MaxPointLights renders without crashing), and that <see cref="Scene3D.Begin"/> clears the queue.
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class PointLightGpuTests
    {
        const int W = 128, H = 128;

        // Dim the global lights so the lit term is dominated by whatever point lights are added.
        static void DarkenGlobals(Scene3D scene)
        {
            scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.AmbientColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
        }

        static long CentreBrightness(byte[] rgba)
        {
            // Average the central 16x16 block so we sample the box surface, not an edge.
            long sum = 0;
            for (int y = H / 2 - 8; y < H / 2 + 8; y++)
                for (int x = W / 2 - 8; x < W / 2 + 8; x++)
                {
                    int i = (y * W + x) * 4;
                    sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
                }
            return sum;
        }

        static (long r, long g, long b) CentreChannels(byte[] rgba)
        {
            long r = 0, g = 0, b = 0;
            for (int y = H / 2 - 8; y < H / 2 + 8; y++)
                for (int x = W / 2 - 8; x < W / 2 + 8; x++)
                {
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2];
                }
            return (r, g, b);
        }

        [GpuFact]
        public void PointLight_brightens_a_mesh_vs_no_lights()
        {
            MeshHandle box = default;
            void Setup(Scene3D scene)
            {
                box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                DarkenGlobals(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
            }

            byte[] dark = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene => scene.Draw(box, Matrix4x4.Identity), frames: 1);

            byte[] lit = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity);
                    // White light in the camera-facing octant, close, bright: lights the visible faces.
                    scene.AddLight(new Vector3(2f, 2f, 2f), new Color(1f, 1f, 1f, 1f), radius: 8f, intensity: 4f);
                }, frames: 1);

            long darkB = CentreBrightness(dark);
            long litB = CentreBrightness(lit);
            Assert.True(litB > darkB * 3, $"point light should clearly brighten the mesh: dark={darkB} lit={litB}");
        }

        [GpuFact]
        public void PointLight_colour_tints_the_mesh()
        {
            MeshHandle box = default;
            void Setup(Scene3D scene)
            {
                box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                DarkenGlobals(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
            }

            byte[] red = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity);
                    scene.AddLight(new Vector3(2f, 2f, 2f), new Color(1f, 0f, 0f, 1f), radius: 8f, intensity: 4f);
                }, frames: 1);

            var (r, g, b) = CentreChannels(red);
            Assert.True(r > g * 3 && r > b * 3, $"red point light should tint the mesh red: r={r} g={g} b={b}");
        }

        [GpuFact]
        public void OverBudget_lights_render_without_crashing()
        {
            MeshHandle box = default;
            byte[] lit = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    DarkenGlobals(scene);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity);
                    // Queue well over the per-frame budget; the renderer clamps to MaxPointLights.
                    for (int i = 0; i < Scene3D.MaxPointLights + 12; i++)
                        scene.AddLight(new Vector3(2f, 2f, 2f), new Color(1f, 1f, 1f, 1f), radius: 8f, intensity: 1f);
                }, frames: 1);

            Assert.True(CentreBrightness(lit) > 0, "over-budget lights should still light the mesh");
        }

        [GpuFact]
        public void Begin_clears_the_point_light_queue()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);

            preview.Scene.AddLight(Vector3.Zero, new Color(1f, 1f, 1f, 1f), 4f, 2f);
            preview.Scene.AddLight(Vector3.One, new Color(1f, 1f, 1f, 1f), 4f, 2f);
            Assert.Equal(2, preview.Scene.LightCount);

            // Capture calls Scene.Begin() first (clearing the queue), then the empty draw adds nothing.
            preview.Capture(_ => { });
            Assert.Equal(0, preview.Scene.LightCount);
        }
    }
}
