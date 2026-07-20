using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Pixel-presence GPU coverage of the particle attractor (issue-driven EssenceMotes preset drained toward a
    /// fixed <see cref="ParticleAttractor"/> target), mirroring <see cref="InstancedDissolveGpuTests"/>'s
    /// capture-and-threshold conventions. Not golden: it plays the same EssenceMotes/attractor recipe as
    /// <c>Golden3D_ParticlesAttractor</c> to different step counts and asserts on luminance-weighted pixel mass
    /// instead of a committed grid, so the thresholds stay backend-agnostic. Each fact also dumps a PNG of every
    /// frame it renders (same <see cref="PngWriter"/> pattern as the other showcase/reject GpuFacts) so the drain
    /// can be eyeballed without re-running. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class ParticleAttractorGpuTests
    {
        const int W = 480, H = 320;
        const float Dt = 1f / 60f;
        static readonly Vector3 Target = new(1.6f, 0.9f, 0f);

        // Same seed/target/strength/curve/kill-radius/max-speed recipe as Golden3D_ParticlesAttractor.
        static (ParticleEffectPlayer Player, ParticleLook[] Looks) NewPlayer()
        {
            VfxPreset preset = VfxPresets.EssenceMotes;
            var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 1, seed: 7);
            player.Play(Vector3.Zero, Vector3.UnitY);

            var looks = new ParticleLook[preset.Looks.Count];
            for (int k = 0; k < looks.Length; k++) looks[k] = preset.Looks[k];
            return (player, looks);
        }

        static void StepAttractor(ParticleEffectPlayer player, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                player.Attractor = new ParticleAttractor
                {
                    Target = Target,
                    Strength = 26f,
                    StrengthCurve = ParticleCurve.EaseIn,
                    KillRadius = 0.18f,
                    MaxSpeed = 6f,
                };
                player.Update(Dt);
            }
        }

        // Same camera framing as Golden3D_ParticlesAttractor, so a screen-space comparison between two
        // captures of the same player stays meaningful (both frames use an identical camera).
        static (byte[] Rgba, IsoCamera3D Camera) Render(ParticleEffectPlayer player, ParticleLook[] looks)
        {
            MeshHandle floor = default;
            IsoCamera3D cam = null!;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
                    scene.Camera.Frame(new Vector3(1.0f, 0.5f, 0f), new Vector3(4.4f, 3.0f, 2.6f));
                    scene.EffectTimeSeconds = 0f;
                    cam = scene.Camera;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.10f, 0.10f, 0.13f, 1f));
                    scene.DrawEffect(player, looks);
                },
                frames: 1);
            return (rgba, cam);
        }

        static string DumpPng(byte[] rgba, string name)
        {
            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            PngWriter.Save(path, rgba, W, H);
            Assert.True(new FileInfo(path).Length > 0, $"empty png at {path}");
            return path;
        }

        // Luminance-weighted centroid of every pixel whose luma clears the threshold, plus how many pixels
        // cleared it.
        static (Vector2 Centroid, int Count) BrightMass(byte[] rgba, float threshold = 0.25f)
        {
            double sumX = 0, sumY = 0, sumLuma = 0;
            int count = 0;
            int i = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++, i += 4)
                {
                    float luma = 0.2126f * (rgba[i] / 255f) + 0.7152f * (rgba[i + 1] / 255f) + 0.0722f * (rgba[i + 2] / 255f);
                    if (luma <= threshold)
                    {
                        continue;
                    }
                    count++;
                    sumX += x * luma;
                    sumY += y * luma;
                    sumLuma += luma;
                }
            }
            Vector2 centroid = sumLuma > 0 ? new Vector2((float)(sumX / sumLuma), (float)(sumY / sumLuma)) : Vector2.Zero;
            return (centroid, count);
        }

        [GpuFact]
        public void Attractor_LatePixelMass_ShiftsTowardTarget()
        {
            (ParticleEffectPlayer player, ParticleLook[] looks) = NewPlayer();

            StepAttractor(player, 20);
            (byte[] rgba20, _) = Render(player, looks);
            DumpPng(rgba20, "attractor_late_t020");

            StepAttractor(player, 108 - 20);
            (byte[] rgba108, IsoCamera3D cam108) = Render(player, looks);
            DumpPng(rgba108, "attractor_late_t108");

            Assert.True(cam108.WorldToScreen(Target, W, H, out Vector2 targetScreen),
                "the attractor target should project inside the frame");

            (Vector2 centroid20, int count20) = BrightMass(rgba20);
            (Vector2 centroid108, int count108) = BrightMass(rgba108);
            Assert.True(count20 > 0, "step-20 frame should show visible mote pixels");
            Assert.True(count108 > 0, "step-108 frame should show visible mote pixels");

            float d0 = Vector2.Distance(centroid20, targetScreen);
            float d108 = Vector2.Distance(centroid108, targetScreen);
            Assert.True(d0 > 0f, "the step-20 centroid should not already sit on the target");
            float closedFraction = (d0 - d108) / d0;
            Assert.True(closedFraction > 0.25f,
                $"expected the bright-pixel centroid to close > 25% of the gap to the target: d0={d0} d108={d108} closed={closedFraction:P1}");
        }

        [GpuFact]
        public void Attractor_AbsorbDrainsPixels()
        {
            (ParticleEffectPlayer player, ParticleLook[] looks) = NewPlayer();

            StepAttractor(player, 108);
            (byte[] rgba108, _) = Render(player, looks);
            DumpPng(rgba108, "attractor_absorb_t108");

            StepAttractor(player, 400 - 108);
            (byte[] rgba400, _) = Render(player, looks);
            DumpPng(rgba400, "attractor_absorb_t400");

            (_, int count108) = BrightMass(rgba108);
            (_, int count400) = BrightMass(rgba400);
            Assert.True(count108 > 0, "step-108 frame should show visible mote pixels before the drain empties it");
            Assert.True(count400 < count108 * 0.2f,
                $"expected the drain to visibly empty the frame: step108={count108} step400={count400}");
        }
    }
}
