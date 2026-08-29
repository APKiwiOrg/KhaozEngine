using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU image regression for the per-entity silhouette (the inverted-hull highlight): a greybox box drawn
    /// with a crimson silhouette behind it, so the golden moves if the hull push, the front-face cull, the
    /// flat colour or the depth interaction regresses. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class SilhouetteGoldenTests
    {
        const int W = 480, H = 320;

        [GpuFact]
        public void Golden3D_Silhouette_BoxHull()
        {
            MeshHandle box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
                    scene.Camera.Frame(new Vector3(0f, 0.5f, 0f), new Vector3(3f, 3f, 3f));
                    box = scene.LoadMesh(GreyboxMeshResolver.Box(
                        new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f),
                        new Vector4(0.55f, 0.6f, 0.65f, 1f)));
                },
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity);
                    scene.DrawMeshSilhouette(box, Matrix4x4.Identity,
                        new Color(0.9f, 0.25f, 0.2f, 1f), widthMetres: 0.06f);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("silhouette_box", rgba, W, H);
        }

        [GpuFact]
        public void Begin_clears_the_silhouette_queue()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);

            MeshHandle box = preview.Scene.LoadMesh(GreyboxMeshResolver.Box(
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f),
                new Vector4(0.5f, 0.5f, 0.5f, 1f)));
            preview.Scene.DrawMeshSilhouette(box, Matrix4x4.Identity, new Color(1f, 0f, 0f, 1f), 0.05f);
            preview.Scene.DrawMeshSilhouette(box, Matrix4x4.CreateTranslation(2f, 0f, 0f),
                new Color(0f, 1f, 0f, 1f), 0.05f);
            Assert.Equal(2, preview.Scene.SilhouetteDrawCount);

            preview.Capture(_ => { }); // Begin() clears, then the empty draw adds nothing
            Assert.Equal(0, preview.Scene.SilhouetteDrawCount);
        }
    }
}
