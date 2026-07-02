using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the translucent unlit overlay-mesh pass
    /// (<see cref="Scene3D.DrawOverlayMesh(MeshHandle,System.Numerics.Matrix4x4)"/>). Smoke test: a collision-shape
    /// overlay mesh drawn over an empty scene must change pixels (something rendered), and its own vertex colour must
    /// dominate the centre. Backend-agnostic (relative assertions, no committed golden). Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class OverlayMeshRendererGpuTests
    {
        const int W = 128, H = 128;

        static void CleanBackground(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new KhaozEngine.Primitives.Color(0f, 0f, 0f, 1f);
        }

        static (long r, long g, long b) Centre(byte[] rgba)
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
        public void Overlay_mesh_draw_changes_pixels_over_an_empty_scene()
        {
            var palette = new CollisionOverlayPalette();
            GltfMesh proxy = CollisionShapeMesh.Build(new BoxShape(Vector3.One), palette);

            MeshHandle handle = default;
            void Setup(Scene3D scene)
            {
                handle = scene.LoadMesh(proxy);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(3.5f, 3.5f, 3.5f));
            }

            byte[] empty = Render3DSnapshot.Capture(W, H, Setup, drawFrame: _ => { }, frames: 1);
            byte[] withOverlay = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene => scene.DrawOverlayMesh(handle, Matrix4x4.Identity), frames: 1);

            // The overlay changed at least one pixel versus the empty scene.
            bool changed = false;
            for (int i = 0; i < empty.Length; i++)
                if (empty[i] != withOverlay[i]) { changed = true; break; }
            Assert.True(changed, "DrawOverlayMesh should change pixels over an empty scene");

            // The proxy's own vertex colour reaches the framebuffer at the centre (brighter than the empty background).
            var (er, eg, eb) = Centre(empty);
            var (or, og, ob) = Centre(withOverlay);
            Assert.True(or + og + ob > er + eg + eb, $"overlay centre should be brighter than empty: overlay=({or},{og},{ob}) empty=({er},{eg},{eb})");
        }

        [GpuFact]
        public void Begin_clears_the_overlay_mesh_queue()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);

            var palette = new CollisionOverlayPalette();
            MeshHandle proxy = preview.Scene.LoadMesh(CollisionShapeMesh.Build(new BoxShape(Vector3.One), palette));
            preview.Scene.DrawOverlayMesh(proxy, Matrix4x4.Identity);
            preview.Scene.DrawOverlayMesh(proxy, Matrix4x4.CreateTranslation(2f, 0f, 0f));
            Assert.Equal(2, preview.Scene.OverlayMeshDrawCount);

            preview.Capture(_ => { }); // Begin() clears, then the empty draw adds nothing
            Assert.Equal(0, preview.Scene.OverlayMeshDrawCount);
        }
    }
}
