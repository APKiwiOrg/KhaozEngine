using System.IO;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Snapshot;
using StbImageSharp;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// End-to-end harness check on a real device (skipped unless <c>KE_GPU_TESTS=1</c>): a <see cref="SnapshotRunner"/>
    /// drives one <c>Shot2D</c> and one <c>Shot3D</c> (the latter from <c>KhaozEngine.Snapshot.Render3D</c>) to a
    /// temp dir, and we assert both PNGs were written and decode to the requested size. This is the engine-side
    /// mirror of the acceptance sample.
    /// </summary>
    public sealed class SnapshotHarnessGpuTests
    {
        const int W = 96, H = 64;

        [GpuFact]
        public void Runner_writes_a_2D_and_a_3D_png()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-snaphost-gpu-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var runner = new SnapshotRunner(dir, _ => { });

                string path2d = runner.Shot2D("flat2d", W, H, new Color(0.1f, 0.1f, 0.12f, 1f), ctx =>
                {
                    Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                    ctx.Batch.Begin();
                    ctx.Batch.Draw(white, new Vector4(8, 8, 40, 30), new Color(0.85f, 0.2f, 0.2f, 1f));
                    ctx.Batch.End();
                });

                MeshHandle box = default;
                string path3d = runner.Shot3D("box3d", W, H,
                    setup: scene =>
                    {
                        box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                        scene.Camera.Frame(Vector3.Zero, new Vector3(3f, 3f, 3f));
                    },
                    drawFrame: scene => scene.Draw(box, Matrix4x4.Identity, new Color(0.2f, 0.7f, 0.3f, 1f)),
                    frames: 2);

                Assert.Equal(2, runner.Count);
                foreach (string p in new[] { path2d, path3d })
                {
                    Assert.True(File.Exists(p));
                    ImageResult decoded = ImageResult.FromMemory(File.ReadAllBytes(p), ColorComponents.RedGreenBlueAlpha);
                    Assert.Equal(W, decoded.Width);
                    Assert.Equal(H, decoded.Height);
                }
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }
    }
}
