using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless pin of the skinning default (issue #15): a new <see cref="Scene3D"/> skins on the GPU, and the CPU
    /// path stays one assignment away. The on-device proof is <c>Render3DGpuSkinningGpuTests.UseGpuSkinning_DefaultsOn</c>.
    /// </summary>
    public sealed class GpuSkinningDefaultTests
    {
        [Fact]
        public void Gpu_skinning_is_the_default()
        {
            Assert.True(Scene3D.DefaultUseGpuSkinning);
            var gd = new FakeGpuDevice();
            IGpuTexture tex = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = gd.Factory.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gd, fb.Outputs);
            Assert.True(scene.UseGpuSkinning, "a new scene must start on the GPU skinning path");
            scene.UseGpuSkinning = false;   // the CPU path stays one assignment away
            Assert.False(scene.UseGpuSkinning);
        }
    }
}
