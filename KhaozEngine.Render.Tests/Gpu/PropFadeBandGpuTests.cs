using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the prop draw-distance FADE BAND (issue #44) driven through the REAL
    /// <see cref="PropRenderer.DrawProps(Scene3D, System.Collections.Generic.IReadOnlyList{PropPlacement}, System.Collections.Generic.IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, System.Collections.Generic.IReadOnlyDictionary{string, MeshHandle}, float)"/>
    /// path, not the bare dissolve overload. Pixel-presence, NOT golden (so no new bake and no cross-platform gate):
    /// one prop sits at the origin and the focus point slides out toward the draw radius, so PropRenderer computes a
    /// larger per-distance dissolve each frame and the box loses coverage to noise-discard holes monotonically. This
    /// proves the whole wire - distance to dissolve to the 14.5.0 instanced primitive to the GPU - lands on screen.
    /// The dissolve value itself is exercised headlessly in PropRendererTests; the primitive's own discard is covered
    /// by InstancedDissolveGpuTests. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class PropFadeBandGpuTests
    {
        const int W = 128, H = 128;

        static void Setup(Scene3D scene, out IReadOnlyDictionary<string, MeshHandle> meshes)
        {
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
            scene.Post.AmbientColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
            scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
            meshes = new Dictionary<string, MeshHandle> { ["box"] = box };
        }

        // A pixel is "covered" (box, not background) when any channel clears a small floor - the box is bright grey on
        // a black background, so a discard hole reads as background.
        static int CoveredPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40) n++;
            return n;
        }

        // Draw one prop at the origin, culled + faded against a focus `dist` units away along +X. drawRadius 100 with
        // an 80-unit band means the fade starts at 20: dist 20 is solid, dist 92 is ~0.9 dissolved.
        static int CoveredAtFocusDistance(float dist)
        {
            IReadOnlyDictionary<string, MeshHandle> meshes = null!;
            void DoSetup(Scene3D scene) => Setup(scene, out meshes);
            var placements = new List<PropPlacement> { new PropPlacement("box", 0f, 0f, 0f, 1f, 0f, 0) };
            byte[] rgba = Render3DSnapshot.Capture(W, H, DoSetup,
                drawFrame: scene => scene.DrawProps(placements, meshes, focus: new Vector3(dist, 0f, 0f),
                    drawRadius: 100f, fadeBandWidth: 80f), frames: 1);
            return CoveredPixels(rgba);
        }

        [GpuFact]
        public void Prop_dissolves_more_as_the_focus_nears_the_draw_radius()
        {
            int solid = CoveredAtFocusDistance(20f);   // at the inner edge: no dissolve
            int mid = CoveredAtFocusDistance(60f);     // mid band: ~0.5 dissolve
            int faded = CoveredAtFocusDistance(92f);   // near the radius: ~0.9 dissolve

            Assert.True(solid > 0, "the solid prop should cover a chunk of the frame");
            Assert.True(faded > 0, "the heavily faded prop should still show its surviving fragments");
            // Monotonic thinning with comfortable margins so backend noise rounding cannot flake it.
            Assert.True(mid < solid * 0.9, $"mid-band should thin: solid {solid}, mid {mid}");
            Assert.True(faded < mid * 0.85, $"near-radius should thin further: mid {mid}, faded {faded}");
        }
    }
}
