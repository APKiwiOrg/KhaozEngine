using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU image regression for a <see cref="ParticleAttractor"/>-driven effect: EssenceMotes (attracted SoftGlow
    /// motes plus IgnoreAttractor Wisp haze) stepped toward a fixed target and compared to a committed reference
    /// grid. Locks the attracted-motion, kill-radius-absorb, and max-speed-clamp math end to end through the
    /// modern particle pass. Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    /// </summary>
    public sealed class GoldenParticleAttractorTests
    {
        const int W = 480, H = 320;

        // EssenceMotes (attracted SoftGlow motes + IgnoreAttractor Wisp haze) driven by a ParticleAttractor:
        // stepped 108 frames at 1/60s toward a fixed target, re-assigning the attractor every step (a fixed
        // target is fine for a golden, the per-frame re-assign exercises the same call path a moving target
        // would). Locks the attracted-motion, kill-radius-absorb, and max-speed-clamp math end to end through
        // the modern particle pass at a frame mid-drain: motes strung out along the origin-to-target arc, some
        // already absorbed.
        [GpuFact]
        public void Golden3D_ParticlesAttractor()
        {
            MeshHandle floor = default;

            VfxPreset preset = VfxPresets.EssenceMotes;
            var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 1, seed: 7);
            player.Play(Vector3.Zero, Vector3.UnitY);

            var target = new Vector3(1.6f, 0.9f, 0f);
            const float dt = 1f / 60f;
            for (int i = 0; i < 108; i++)
            {
                player.Attractor = new ParticleAttractor
                {
                    Target = target,
                    Strength = 26f,
                    StrengthCurve = ParticleCurve.EaseIn,
                    KillRadius = 0.18f,
                    MaxSpeed = 6f,
                };
                player.Update(dt);
            }

            var looks = new ParticleLook[preset.Looks.Count];
            for (int k = 0; k < looks.Length; k++) looks[k] = preset.Looks[k];

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f));
                    scene.Post.Starfield = false;   // flat background so the drained stream reads cleanly
                    scene.Post.Outline = true;      // pinned explicit, matching the other 3D goldens
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
                    // Frames both the play origin and the attractor target, with headroom for the wide spread
                    // the drain produces (some motes overshoot the target before the kill radius catches them).
                    scene.Camera.Frame(new Vector3(1.0f, 0.5f, 0f), new Vector3(4.4f, 3.0f, 2.6f));
                    scene.EffectTimeSeconds = 0f;   // frozen time => deterministic noise/flicker terms
                },
                drawFrame: scene =>
                {
                    // Dark floor: gives the additive motes contrast, matching the other particle goldens.
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.10f, 0.10f, 0.13f, 1f));
                    scene.DrawEffect(player, looks);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_particles_attractor", rgba, W, H);
        }
    }
}
