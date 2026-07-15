using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The always-on 3D draw counters (Scene3D.LastFrameStats): draw calls / instances / estimated triangles / buffer
    // upload bytes over the geometry passes. Needs a real device (the counters are finalized inside RenderInternal),
    // so gated behind KE_GPU_TESTS=1. Reads the stats through the frames:2 capture (drawFrame sees the prior frame's
    // finalized totals, since the counters reset+accumulate inside the render pass).
    public sealed class Scene3DFrameStatsTests
    {
        const int W = 320, H = 240;

        static RenderFrameStats RenderGrid(int half)
        {
            var captured = default(RenderFrameStats);
            MeshHandle box = default;
            Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(0.6f));
                    scene.FrustumCulling = false;   // every instance drawn, so the counts are deterministic
                    scene.Post.Starfield = false;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(6f, 5f, 6f));
                },
                drawFrame: scene =>
                {
                    for (int gx = -half; gx <= half; gx++)
                        for (int gz = -half; gz <= half; gz++)
                            scene.Draw(box, Matrix4x4.CreateTranslation(gx * 2f, 0f, gz * 2f),
                                new Color(0.8f, 0.5f, 0.2f, 1f));
                    captured = scene.LastFrameStats;   // frame 2 sees frame 1's finalized totals
                },
                frames: 2);
            return captured;
        }

        [GpuFact]
        public void Geometry_passes_populate_draw_instance_and_triangle_counters()
        {
            RenderFrameStats s = RenderGrid(half: 1);   // a 3x3 grid = 9 boxes
            const int boxes = 9;
            const int triPerBox = 12;                   // a unit box = 12 triangles (36 indices)

            Assert.True(s.DrawCalls >= 1, "expected at least one geometry draw call");
            Assert.True(s.Instances >= boxes, $"expected >= {boxes} instances, got {s.Instances}");
            Assert.True(s.Triangles >= (long)boxes * triPerBox,
                $"expected >= {boxes * triPerBox} triangles, got {s.Triangles}");
            Assert.True(s.BufferUpdateBytes > 0, "expected per-frame instance upload bytes");
        }

        [GpuFact]
        public void More_instances_report_more_work()
        {
            RenderFrameStats small = RenderGrid(half: 1);   // 9 boxes
            RenderFrameStats large = RenderGrid(half: 2);   // 25 boxes

            Assert.True(large.Instances > small.Instances, "a denser grid must report more instances");
            Assert.True(large.Triangles > small.Triangles, "a denser grid must report more triangles");
            Assert.True(large.BufferUpdateBytes > small.BufferUpdateBytes, "a denser grid uploads more instance bytes");
        }
    }
}
