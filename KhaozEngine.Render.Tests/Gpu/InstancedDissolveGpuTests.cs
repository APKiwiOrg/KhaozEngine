using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the rigid/instanced per-instance dissolve (issue #253, the <see cref="Scene3D.Draw(MeshHandle,
    /// Matrix4x4, Color, Material, float, float, Color)"/> overload). Pixel-presence, NOT golden: it renders a box
    /// through the SAME instanced pipeline as every plain draw and asserts (a) a half-dissolved box has visible
    /// discard holes (fewer covered pixels than the solid box) plus edge-coloured pixels, and (b) the new overload
    /// at dissolve 0 reads back byte-identical to the plain material draw (the gated shader term is inert). No
    /// committed golden and no bake - backend-agnostic thresholds. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class InstancedDissolveGpuTests
    {
        const int W = 128, H = 128;

        // Frame a white box, lit flat by a strong white ambient so every visible face reads well above the
        // background regardless of key/fill direction, and a black background so a discard hole is unambiguous.
        static void Setup(Scene3D scene, out MeshHandle box)
        {
            MeshHandle b = scene.LoadMesh(MeshPrimitives.Box(1.4f));
            scene.Post.AmbientColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
            scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
            box = b;
        }

        // A pixel is "covered" (box, not background) when any channel clears a small floor - the box is bright grey,
        // the background is black.
        static int CoveredPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40) n++;
            return n;
        }

        // A pixel is "edge-coloured" when green clearly dominates red and blue - the emissive edge is pure green and
        // the box itself is neutral grey, so only the dissolve edge band trips this.
        static int GreenEdgePixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 1] > 90 && rgba[i + 1] > rgba[i] + 40 && rgba[i + 1] > rgba[i + 2] + 40) n++;
            return n;
        }

        [GpuFact]
        public void HalfDissolve_makes_holes_and_shows_the_edge_colour()
        {
            MeshHandle box = default;
            void DoSetup(Scene3D scene) => Setup(scene, out box);

            byte[] solid = Render3DSnapshot.Capture(W, H, DoSetup,
                drawFrame: scene => scene.Draw(box, Matrix4x4.Identity, Color.White, Material.None), frames: 1);

            var edge = new Color(0f, 1f, 0f, 1f);
            byte[] dissolved = Render3DSnapshot.Capture(W, H, DoSetup,
                drawFrame: scene => scene.Draw(box, Matrix4x4.Identity, Color.White, Material.None,
                    dissolve: 0.5f, edgeWidth: 0.12f, edgeColor: edge), frames: 1);

            int solidCovered = CoveredPixels(solid);
            int dissolvedCovered = CoveredPixels(dissolved);
            Assert.True(solidCovered > 0, "the solid box should cover a chunk of the frame");
            // ~half the noise is below threshold 0.5, so the dissolved box loses a clear fraction of its coverage to
            // discard holes. Use a comfortable margin (< 85% of solid) so backend noise rounding cannot flake it.
            Assert.True(dissolvedCovered < solidCovered * 0.85,
                $"dissolve should punch holes: solid covered {solidCovered}, dissolved covered {dissolvedCovered}");
            Assert.True(dissolvedCovered > 0, "the dissolved box should still show its surviving fragments");

            // The solid box is neutral grey (no edge), the dissolved box has a green emissive edge band.
            Assert.Equal(0, GreenEdgePixels(solid));
            Assert.True(GreenEdgePixels(dissolved) > 0, "the dissolve edge band should paint green edge pixels");
        }

        [GpuFact]
        public void Dissolve_zero_through_new_overload_matches_plain_material_draw()
        {
            MeshHandle box = default;
            void DoSetup(Scene3D scene) => Setup(scene, out box);
            var mat = new Material(new Color(0.2f, 0.05f, 0.3f, 1f), 0.4f, 48f);   // exercise the emissive/spec packing

            byte[] plain = Render3DSnapshot.Capture(W, H, DoSetup,
                drawFrame: scene => scene.Draw(box, Matrix4x4.Identity, Color.White, mat), frames: 1);

            // dissolve 0 through the new overload, with a bright edge colour that MUST be ignored because the gate is
            // threshold > 0 - so the readback has to be byte-for-byte identical to the plain material draw.
            byte[] zeroDissolve = Render3DSnapshot.Capture(W, H, DoSetup,
                drawFrame: scene => scene.Draw(box, Matrix4x4.Identity, Color.White, mat,
                    dissolve: 0f, edgeWidth: 0.2f, edgeColor: new Color(1f, 0f, 1f, 1f)), frames: 1);

            Assert.Equal(plain.Length, zeroDissolve.Length);
            Assert.True(plain.AsSpan().SequenceEqual(zeroDissolve),
                "dissolve 0 through the new overload must render byte-identical to the plain material draw");
        }
    }
}
