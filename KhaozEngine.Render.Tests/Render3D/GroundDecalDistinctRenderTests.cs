using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Regression: each queued ground decal must render with ITS OWN uniform data, not the last decal's. The decal
    /// pass used to overwrite one shared UBO once per decal inside a single command list, so on Veldrid/Metal every
    /// decal in a frame painted with the final decal's params. This renders two decals far apart - one red, one cyan -
    /// onto a flat floor and asserts BOTH colours appear: the shared-UBO bug collapses them to a single colour, so
    /// the missing colour fails the assertion. GPU-gated (KE_GPU_TESTS=1), like the other render goldens.
    /// </summary>
    public sealed class GroundDecalDistinctRenderTests
    {
        const int W = 480, H = 320;

        [GpuFact]
        public void Two_decals_each_render_with_their_own_params()
        {
            MeshHandle floor = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));
                    scene.Camera.Frame(new Vector3(0f, 0.3f, 0.3f), new Vector3(6f, 4.5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));

                    // Left: an opaque-ish RED filled circle.
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Circle, Center = new Vector3(-1.8f, 0f, 0.4f),
                        Size = new Vector4(1.3f, 0, 0, 0),
                        FillColor = new Color(0.95f, 0.1f, 0.05f, 0.92f),
                        OutlineColor = new Color(0.95f, 0.1f, 0.05f, 0.92f),
                        EdgeThickness = 0.08f, FillFraction = 1f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                    // Right: an opaque-ish CYAN filled circle. With the shared-UBO bug both draws use these params,
                    // so the red circle never appears.
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Circle, Center = new Vector3(1.8f, 0f, 0.4f),
                        Size = new Vector4(1.3f, 0, 0, 0),
                        FillColor = new Color(0.05f, 0.9f, 0.95f, 0.92f),
                        OutlineColor = new Color(0.05f, 0.9f, 0.95f, 0.92f),
                        EdgeThickness = 0.08f, FillFraction = 1f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                },
                frames: 2);

            bool hasRed = false, hasCyan = false;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                // Red-dominant: R clearly above both G and B.
                if (r > 120 && r - g > 60 && r - b > 60) hasRed = true;
                // Cyan-dominant: both G and B clearly above R.
                if (g > 120 && b > 120 && g - r > 60 && b - r > 60) hasCyan = true;
                if (hasRed && hasCyan) break;
            }

            Assert.True(hasRed, "red decal missing - both decals rendered with the other decal's UBO data");
            Assert.True(hasCyan, "cyan decal missing - both decals rendered with the other decal's UBO data");
        }
    }
}
