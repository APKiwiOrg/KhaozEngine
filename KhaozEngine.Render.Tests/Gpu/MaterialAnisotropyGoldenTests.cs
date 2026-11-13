using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Anisotropic material sampling (issue #1776): a tiled ground texture seen at a grazing angle keeps its detail
    /// far down the view at the default 16x, where trilinear filtering blurs it toward grey, and a device without
    /// anisotropic filtering falls back to exactly the trilinear image. The golden pins the default look.
    /// <para>
    /// HDR is off so the frame carries the sampled checker without a tonemap, and the ambient is a quarter so the lit
    /// checks never clip. The far band is the upper third of the ground, where a check is a pixel or two deep and the
    /// filters disagree most.
    /// </para>
    /// </summary>
    public sealed class MaterialAnisotropyGoldenTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180;
        const float RepeatsPerMetre = 0.5f;
        const double MinContrastRatio = 1.6;

        static readonly PixelRect FarBand = new(W / 5, H * 30 / 100, W * 4 / 5, H * 48 / 100);

        static byte[] Checker()
        {
            var rgba = new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    byte v = ((x / 8 + y / 8) & 1) == 0 ? (byte)38 : (byte)217;
                    int i = (y * 256 + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                    rgba[i + 3] = 255;
                }
            return rgba;
        }

        // A 24 by 60 m ground quad, both windings so culling cannot hide it.
        static GltfMesh Ground()
        {
            var n = Vector3.UnitY; var c = Vector4.One;
            ModelVertex V(float x, float z) => new(new Vector3(x, 0f, z), n, c, new Vector2(x, z) * RepeatsPerMetre);
            var v = new[] { V(-12f, 0.5f), V(12f, 0.5f), V(12f, 60.5f), V(-12f, 60.5f) };
            return new GltfMesh(v, new ushort[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        }

        static byte[] Capture(int anisotropy)
        {
            MeshHandle ground = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    s.Post.UseSmoothPreset();
                    s.Post.Hdr.Enabled = false;
                    s.Post.TransparentBackground = false;
                    s.Post.AmbientColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                    s.Post.Quality.MaterialAnisotropy = anisotropy;
                    s.CameraOverride = new FlyCamera3D
                    {
                        Position = new Vector3(0f, 1.2f, 0f),
                        Pitch = -0.2f,
                        AspectRatio = (float)W / H,
                    };
                    ground = s.LoadMesh(Ground(), s.LoadTexture(Checker(), 256, 256));
                },
                drawFrame: s => s.Draw(ground, Matrix4x4.Identity),
                frames: 2);
        }

        [GpuFact]
        public void Golden3D_MaterialAnisotropy_KeepsGrazingDetailThatTrilinearBlurs()
        {
            byte[] anisotropic = Capture(16);
            byte[] trilinear = Capture(1);
            bool supported;
            using (GpuDeviceContext context = GpuDeviceContext.CreateHeadless())
                supported = context.Capabilities.SamplerAnisotropy;

            double cA = TemporalAcceptance.LocalContrast(anisotropic, W, H, FarBand);
            double cT = TemporalAcceptance.LocalContrast(trilinear, W, H, FarBand);
            output.WriteLine($"far band local contrast: 16x {cA:0.0000}, trilinear {cT:0.0000}, anisotropy supported {supported}");
            // The grid first, so a bake still writes it when a backend's ratio needs reading.
            GoldenCompare.AssertOrUpdate("scene3d_material_anisotropy", anisotropic, W, H);
            // 16x kept 2.55 times trilinear's far-band contrast on Metal (0.0884 against 0.0346). The floor is that
            // ratio less a margin for the software rasterizers' own anisotropic footprints.
            if (supported)
                Assert.True(cA > MinContrastRatio * cT,
                    $"16x must keep grazing detail trilinear blurs: {cA:0.0000} against {cT:0.0000}");
            else
                Assert.Equal(trilinear, anisotropic);
        }
    }
}
