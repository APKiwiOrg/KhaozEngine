using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Human-reviewed PNG showcase of the modern particle pass: every <see cref="ParticleShape"/> across blend,
    /// param, and life variations, plus velocity stretch and the soft depth fade, over a dark floor so additive
    /// sprites read. Modeled on <see cref="TelegraphShowcaseGpuTests"/> (scene setup / readback / PNG dump).
    /// This test does not itself lock pixels beyond smoke asserts: it is the visual-iteration surface, and
    /// deliberately does NOT carry "Golden" in its name (the cross-backend matrix filter). The byte-exact net is
    /// Golden3D_ParticlesModern.
    /// </summary>
    public sealed class ParticleShowcaseGpuTests
    {
        const int W = 960, H = 540;
        const float Spacing = 3.2f;

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static readonly ParticleShape[] Shapes =
        {
            ParticleShape.SoftGlow, ParticleShape.Ember, ParticleShape.Spark,
            ParticleShape.Wisp, ParticleShape.Ring, ParticleShape.Star,
        };

        // Per-shape showcase tints: warm fire family for glow/ember/spark, cool smoke, frost ring, arcane star.
        static readonly Color[] Tints =
        {
            new(1.0f, 0.72f, 0.35f, 0.95f),
            new(1.0f, 0.45f, 0.15f, 1.0f),
            new(1.0f, 0.85f, 0.45f, 1.0f),
            new(0.62f, 0.64f, 0.70f, 0.85f),
            new(0.55f, 0.85f, 1.0f, 0.95f),
            new(0.75f, 0.55f, 1.0f, 1.0f),
        };

        static Vector3 Cell(int col, int row) =>
            new((col - 2.5f) * Spacing, 1.15f, (row - 1.5f) * Spacing);

        static ParticleSprite Sprite(int col, int row, float param, float life, BillboardBlend blend) => new()
        {
            Position = Cell(col, row),
            Size = 1.05f,
            Color = Tints[col],
            Shape = Shapes[col],
            ShapeParam = param,
            LifeNorm = life,
            Seed = 0.137f + 0.61f * col + 0.29f * row,
            Blend = blend,
        };

        void DrawScene(Scene3D s, MeshHandle floor)
        {
            // Dark neutral floor so additive sprites read (the telegraph-showcase pattern).
            s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.10f, 0.10f, 0.13f, 1f));

            for (int col = 0; col < Shapes.Length; col++)
            {
                // Row 0: canonical additive look. Row 1: alpha compositing. Row 2: param pushed high.
                // Row 3: late life (wisp erosion, others stable).
                s.DrawParticle(Sprite(col, 0, 0.35f, 0.35f, BillboardBlend.Additive));
                s.DrawParticle(Sprite(col, 1, 0.35f, 0.35f, BillboardBlend.Alpha));
                s.DrawParticle(Sprite(col, 2, 0.9f, 0.35f, BillboardBlend.Additive));
                s.DrawParticle(Sprite(col, 3, 0.35f, 0.85f, BillboardBlend.Additive));
            }

        }

        void DrawBehaviors(Scene3D s, MeshHandle floor)
        {
            s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.10f, 0.10f, 0.13f, 1f));

            // Velocity stretch: three sparks with rising speed, elongating along on-screen motion.
            for (int i = 0; i < 3; i++)
            {
                ParticleSprite spark = Sprite(2, 0, 0.5f, 0.3f, BillboardBlend.Additive);
                spark.Position = new Vector3(-6.5f + i * 2.2f, 2.9f, -2f);
                spark.Velocity = new Vector3(2.5f + 3.5f * i, 1.5f, 0f);
                spark.Stretch = 0.35f;
                spark.Size = 0.55f;
                s.DrawParticle(spark);
            }

            // Soft depth fade: two identical glows, one floating free, one half-sunk into the floor. The sunk
            // one's lower half must fade smoothly at the surface instead of clipping hard.
            ParticleSprite free = Sprite(0, 0, 0.35f, 0.3f, BillboardBlend.Additive);
            free.Position = new Vector3(2.2f, 2.9f, -2f);
            free.Size = 1.3f;
            s.DrawParticle(free);
            ParticleSprite sunk = free;
            sunk.Position = new Vector3(5.6f, 0.1f, -2f);
            s.DrawParticle(sunk);

            // Mixed-blend cluster: alpha smoke behind additive ember and glow, overlapping. Premultiplied
            // single-stream compositing must interleave them without additive halos punching through smoke.
            ParticleSprite smoke = Sprite(3, 0, 0.4f, 0.45f, BillboardBlend.Alpha);
            smoke.Position = new Vector3(-3.4f, 1.6f, 2.2f);
            smoke.Size = 1.5f;
            s.DrawParticle(smoke);
            ParticleSprite ember = Sprite(1, 0, 0.35f, 0.3f, BillboardBlend.Additive);
            ember.Position = new Vector3(-3.9f, 1.3f, 3.0f);
            ember.Size = 0.7f;
            s.DrawParticle(ember);
            ParticleSprite glow = Sprite(0, 0, 0.35f, 0.3f, BillboardBlend.Additive);
            glow.Position = new Vector3(-2.8f, 1.9f, 1.4f);
            glow.Size = 0.9f;
            s.DrawParticle(glow);
        }

        [GpuFact]
        public void Showcase_shape_grid_dumps_at_two_effect_times()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            // Grid spans x in [-8, 8], z in [-4.8, 4.8]. Frame padded bounds so every cell is visible.
            preview.Scene.Camera.Frame(new Vector3(0f, 1.2f, 0f), new Vector3(12.5f, 2.5f, 8f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(30f, 0.1f));

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            preview.Scene.EffectTimeSeconds = 0f;
            byte[] t0 = Read(gd, preview.Capture(s => DrawScene(s, floor)));
            AssertNonBackgroundPixels(t0);
            string png0 = Path.Combine(dir, "particle_showcase_t0.png");
            PngWriter.Save(png0, t0, W, H);
            Assert.True(new FileInfo(png0).Length > 0, $"empty png at {png0}");

            preview.Scene.EffectTimeSeconds = 1.7f;
            byte[] t1 = Read(gd, preview.Capture(s => DrawScene(s, floor)));
            string png1 = Path.Combine(dir, "particle_showcase_t1.png");
            PngWriter.Save(png1, t1, W, H);
            Assert.True(new FileInfo(png1).Length > 0, $"empty png at {png1}");

            // The ember flicker and wisp noise scroll read EffectTimeSeconds, so the two dumps must differ
            // somewhere (proves the animated terms actually render on an otherwise static scene).
            int changed = 0;
            for (int i = 0; i < t0.Length; i++)
                if (t0[i] != t1[i]) { changed++; if (changed > 16) break; }
            Assert.True(changed > 16, "time-animated particle terms did not change any pixels between t0 and t1");
        }

        [GpuFact]
        public void Showcase_behaviors_dump()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Camera.Frame(new Vector3(-0.5f, 1.4f, 0f), new Vector3(9.5f, 3.2f, 5.5f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(30f, 0.1f));

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            preview.Scene.EffectTimeSeconds = 0.8f;
            byte[] px = Read(gd, preview.Capture(s => DrawBehaviors(s, floor)));
            AssertNonBackgroundPixels(px);
            string png = Path.Combine(dir, "particle_behaviors.png");
            PngWriter.Save(png, px, W, H);
            Assert.True(new FileInfo(png).Length > 0, $"empty png at {png}");
        }

        static void AssertNonBackgroundPixels(byte[] rgba)
        {
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 3] > 0 && (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40))
                    return;
            Assert.Fail("showcase rendered no visible particle pixels");
        }
    }
}
