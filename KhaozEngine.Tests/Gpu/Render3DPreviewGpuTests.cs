using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Render3DPreview renders a 3D model into a sampleable Texture2D on the SAME live device (no separate headless
    // device, no CPU roundtrip - unlike Render3DSnapshot). This exercises that path on a headless device and reads
    // the result back to assert (a) the model is present in the centre and (b) the background composites
    // transparently (alpha 0), which a coarse RGB golden grid cannot see. Skipped unless KE_GPU_TESTS=1.
    public sealed class Render3DPreviewGpuTests
    {
        const int W = 128, H = 128;

        [GpuFact]
        public void Capture_renders_model_to_a_sampleable_texture_with_transparent_background()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            using var preview = new Render3DPreview(gd, W, H);
            Assert.Equal(W, preview.Width);
            Assert.Equal(H, preview.Height);
            Assert.True(preview.Scene.Post.TransparentBackground, "preview defaults to a transparent background");
            Assert.False(preview.Scene.Post.Starfield, "preview defaults to starfield off");

            // Load the preview mesh ONCE (the whole point - no per-frame re-upload), frame the camera to it.
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));
            preview.Scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));

            // Caller drives rotation by passing a world matrix per frame.
            Texture2D tex = preview.Capture(scene =>
                scene.Draw(box, Matrix4x4.CreateRotationY(0.7f), new Color(0.85f, 0.35f, 0.2f, 1f)));

            Assert.Equal(W, tex.Width);
            Assert.Equal(H, tex.Height);
            // Capturing again returns the SAME wrapper (target reused, no per-frame allocation).
            Texture2D again = preview.Capture(scene =>
                scene.Draw(box, Matrix4x4.CreateRotationY(1.1f), new Color(0.85f, 0.35f, 0.2f, 1f)));
            Assert.Same(tex, again);

            byte[] rgba = GpuReadback.ToRgba(gd, tex.Handle, W, H);

            // Centre: the model fills the middle of the framed view -> opaque and not black.
            int c = ((H / 2) * W + (W / 2)) * 4;
            Assert.True(rgba[c + 3] > 200, $"centre should be opaque (model), got a={rgba[c + 3]}");
            Assert.True(rgba[c + 0] + rgba[c + 1] + rgba[c + 2] > 40,
                $"centre should carry model colour, got rgb=({rgba[c]},{rgba[c + 1]},{rgba[c + 2]})");

            // Corner: empty background -> transparent (alpha 0), so it composites cleanly into a 2D panel.
            int k = (3 * W + 3) * 4; // a few pixels in from the top-left
            Assert.True(rgba[k + 3] < 40, $"corner should be transparent background, got a={rgba[k + 3]}");
        }
    }
}
