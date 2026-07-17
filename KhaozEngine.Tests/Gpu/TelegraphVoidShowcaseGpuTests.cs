using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Human-reviewed PNG dumps of the void ground-decal fallback over <see cref="VoidDecalScene"/>. This test locks
    /// no pixels beyond a sanity guard; the committed <c>telegraph_ground_void</c> golden and the pixel A/B pairs in
    /// <see cref="GroundDecalVoidGoldenTests"/> are the automated net. What this exists for is the part no automated
    /// check can do: 11.9.0 established that a green suite is not evidence the look is right, so the dumps are here
    /// to be LOOKED AT.
    /// <para>
    /// Five dumps, each answering one question a reviewer will actually ask:
    /// </para>
    /// <list type="number">
    /// <item><c>_off</c> / <c>_on</c> - the decisive A/B. Same scene, one flag. Off is today's bug (the ring falls off
    /// the island's edge and survives only where it happens to cross the tile's corners); on is the fix, an UNBROKEN
    /// annulus continuing over the void on its own plane.</item>
    /// <item><c>_edge</c> - zoomed on the CAMERA-FACING +X/+Z edge, where the ring crosses a cliff face below its
    /// plane. The ring hangs in FRONT of that cliff, so it must paint straight across it. This is the region an
    /// earlier cut of this feature got backwards, discarding it as "geometry is present" and losing most of the near
    /// arc; the dump exists so that failure is visible at a glance if it ever comes back.</item>
    /// <item><c>_wall</c> - the mirror. A slab standing on the plane where the ring overhangs: the ring is genuinely
    /// behind it and must stay hidden. Together with <c>_edge</c> this is both signs of the depth comparison.</item>
    /// <item><c>_starfield</c> - the same scene with the background pass on, which is the whole reason release 1 had
    /// to land first: the void ring composites OVER the stars. Under the pre-11.9.0 blit the final pass regenerated
    /// the background from the clear colour and would have erased it.</item>
    /// </list>
    /// Gated on KE_GPU_TESTS. Dumps land in KE_PNG_DUMP_DIR (default: the temp dir).
    /// </summary>
    public sealed class TelegraphVoidShowcaseGpuTests
    {
        const int W = VoidDecalScene.W, H = VoidDecalScene.H;

        static string Dump(string name, byte[] rgba)
        {
            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            PngWriter.Save(path, rgba, W, H);
            Assert.True(new FileInfo(path).Length > 0, $"expected a PNG dump at {path}");
            return path;
        }

        static byte[] Render(bool voidFallback, Action<Scene3D>? extraSetup = null)
        {
            MeshHandle island = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene => { island = VoidDecalScene.Setup(scene); extraSetup?.Invoke(scene); },
                drawFrame: scene => VoidDecalScene.Draw(scene, island, voidFallback),
                frames: 2);
        }

        /// <summary>Blue-dominant (ring-fill) pixel count: the coarse "is there a ring at all" signal.</summary>
        static int RingPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 2] > rgba[i] + 25 && rgba[i + 2] > 60) n++;
            return n;
        }

        [GpuFact]
        public void Showcase_void_fallback_off_vs_on_and_the_camera_facing_edge()
        {
            byte[] off = Render(voidFallback: false);
            byte[] on = Render(voidFallback: true);

            Dump("telegraph_ground_void_off", off);
            Dump("telegraph_ground_void_on", on);

            // Guard, so the dumps can never be two identical pictures a reviewer nods at: the fallback must actually
            // add ring pixels. The A/B tests say WHERE; this only says the dumps differ enough to be worth reviewing.
            int gained = RingPixels(on) - RingPixels(off);
            Assert.True(gained > (W * H) / 100,
                $"the on/off dumps must differ substantially or the showcase is vacuous: gained {gained} ring pixels");

            // The camera-facing edge, zoomed. The default iso camera looks from (+x, +y, +z), so the +X/+Z cliffs are
            // the ones facing us. Frame tight on the +X edge crossing at z = 0, at the plane, so the top surface, the
            // cliff the ring crosses in front of, and the void beyond are all in shot.
            byte[] edge = Render(voidFallback: true, extraSetup: s =>
                s.Camera.Frame(new Vector3(VoidDecalScene.TileHalf + 0.7f, VoidDecalScene.PlaneY * 0.5f, 0f),
                    new Vector3(5.5f, 2.5f, 5.5f)));
            Dump("telegraph_ground_void_edge", edge);

            // The mirror: the ring must NOT show through a slab standing in front of it.
            MeshHandle island = default, wall = default;
            byte[] walled = Render3DSnapshot.Capture(W, H,
                setup: scene => { island = VoidDecalScene.Setup(scene); wall = VoidDecalScene.LoadWall(scene); },
                drawFrame: scene => VoidDecalScene.Draw(scene, island, voidFallback: true, wall: wall),
                frames: 2);
            Dump("telegraph_ground_void_wall", walled);

            // The starfield variant: proves release 1's background pass and release 2 compose, which is the sequencing
            // argument made concrete. The ring must survive over the stars rather than be erased by the blit.
            byte[] stars = Render(voidFallback: true, extraSetup: s => s.Post.Background = BackgroundMode.Starfield);
            Dump("telegraph_ground_void_starfield", stars);
            Assert.True(RingPixels(stars) > (W * H) / 100,
                "the void ring must survive over the starfield background, not be erased by the final blit");
        }
    }
}
