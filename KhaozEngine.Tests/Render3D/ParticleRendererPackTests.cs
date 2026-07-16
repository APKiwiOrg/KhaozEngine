using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of the pure per-instance packing for the modern particle pass. The GPU-side
    /// look is covered by the particle showcase dumps and the particles golden.</summary>
    public sealed class ParticleRendererPackTests
    {
        static ParticleSprite Sprite() => new()
        {
            Position = new Vector3(1f, 2f, 3f),
            Velocity = new Vector3(4f, 5f, 6f),
            Size = 0.75f,
            Rotation = 1.25f,
            Color = new Color(0.9f, 0.5f, 0.25f, 0.8f),
            Shape = ParticleShape.Wisp,
            ShapeParam = 0.4f,
            LifeNorm = 0.6f,
            Seed = 0.33f,
            Stretch = 2f,
            Blend = BillboardBlend.Alpha,
        };

        [Fact]
        public void PackInstance_MapsEveryField()
        {
            ParticleRenderer.ParticleInstance p = ParticleRenderer.PackInstance(Sprite());

            Assert.Equal(new Vector4(1f, 2f, 3f, 0.75f), p.CenterSize);
            Assert.Equal(new Vector4(4f, 5f, 6f, 1.25f), p.VelocityRot);
            Assert.Equal((Vector4)new Color(0.9f, 0.5f, 0.25f, 0.8f), p.Color);
            Assert.Equal((int)ParticleShape.Wisp, p.Shape.X);
            Assert.Equal(0.4f, p.Shape.Y);
            Assert.Equal(0.6f, p.Shape.Z);
            Assert.Equal(0.33f, p.Shape.W);
            Assert.Equal(2f, p.Extra.X);
            // Defaults: camera-facing orientation, soft-fade scale 0 packs as the neutral 1.
            Assert.Equal(0f, p.Extra.Z);
            Assert.Equal(1f, p.Extra.W);
        }

        [Fact]
        public void PackInstance_OrientationAndFadeScale_Lanes()
        {
            ParticleSprite s = Sprite();
            s.Orientation = ParticleOrientation.FlatGround;
            s.SoftFadeScale = 0.12f;

            ParticleRenderer.ParticleInstance p = ParticleRenderer.PackInstance(s);
            Assert.Equal(1f, p.Extra.Z);
            Assert.Equal(0.12f, p.Extra.W);
        }

        [Fact]
        public void PackInstance_AdditivityLane_FollowsBlend()
        {
            ParticleSprite s = Sprite();

            s.Blend = BillboardBlend.Alpha;
            Assert.Equal(0f, ParticleRenderer.PackInstance(s).Extra.Y);

            s.Blend = BillboardBlend.Additive;
            Assert.Equal(1f, ParticleRenderer.PackInstance(s).Extra.Y);
        }

        [Fact]
        public void PackInstance_ShapeIds_MatchEnumValues()
        {
            ParticleSprite s = Sprite();
            foreach (ParticleShape shape in new[]
            {
                ParticleShape.SoftGlow, ParticleShape.Ember, ParticleShape.Spark,
                ParticleShape.Wisp, ParticleShape.Ring, ParticleShape.Star,
            })
            {
                s.Shape = shape;
                Assert.Equal((byte)shape, ParticleRenderer.PackInstance(s).Shape.X);
            }
        }

        // Mirror the fragment shader's IFlip.w decode (grid + quantized motion strength) so the tests pin the exact
        // round-trip the GPU relies on.
        static (int cols, int rows, float mstr) UnpackFlipGrid(float packed)
        {
            int cols = (int)(packed % 256f);
            int rows = (int)(MathF.Floor(packed / 256f) % 256f);
            float mstr = MathF.Floor(packed / 65536f) / 64f;
            return (cols, rows, mstr);
        }

        [Fact]
        public void PackFlipGrid_RoundTripsColsRows_AcrossFullRange()
        {
            // Every field must stay exact through float32, including the corners of the 1..255 grid range.
            foreach (int cols in new[] { 1, 2, 7, 16, 128, 254, 255 })
                foreach (int rows in new[] { 1, 2, 8, 16, 200, 255 })
                {
                    float packed = ParticleRenderer.PackFlipGrid(cols, rows, 1f);
                    (int dc, int dr, _) = UnpackFlipGrid(packed);
                    Assert.Equal(cols, dc);
                    Assert.Equal(rows, dr);
                }
        }

        [Fact]
        public void PackFlipGrid_QuantizesMotionStrength_ToSixtyFourth()
        {
            // 0 and 1 land exactly. 4 is capped at the top byte (255) so the whole packed value stays <= 2^24-1 and
            // every field stays exact in float32, decoding to within one 1/64 quantum of 4.
            (_, _, float m0) = UnpackFlipGrid(ParticleRenderer.PackFlipGrid(255, 255, 0f));
            Assert.Equal(0f, m0);

            (_, _, float m1) = UnpackFlipGrid(ParticleRenderer.PackFlipGrid(255, 255, 1f));
            Assert.Equal(1f, m1);

            (int dc, int dr, float m4) = UnpackFlipGrid(ParticleRenderer.PackFlipGrid(255, 255, 4f));
            Assert.Equal(255, dc);   // grid survives even at the max-strength corner
            Assert.Equal(255, dr);
            Assert.InRange(m4, 4f - 1f / 64f, 4f);
        }

        [Fact]
        public void ResolveFrames_Loop_WrapsFrameBAcrossTheSeam()
        {
            (float fa, float fb, float blend) = ParticleRenderer.ResolveFrames(15.25f, 16, loop: true);
            Assert.Equal(15f, fa);
            Assert.Equal(0f, fb);
            Assert.Equal(0.25f, blend, 5);
        }

        [Fact]
        public void ResolveFrames_Loop_MidSheet()
        {
            (float fa, float fb, float blend) = ParticleRenderer.ResolveFrames(2.5f, 16, loop: true);
            Assert.Equal(2f, fa);
            Assert.Equal(3f, fb);
            Assert.Equal(0.5f, blend, 5);
        }

        [Fact]
        public void ResolveFrames_OneShot_ClampsAtLastFrame_BlendZero()
        {
            (float fa, float fb, float blend) = ParticleRenderer.ResolveFrames(15.25f, 16, loop: false);
            Assert.Equal(15f, fa);
            Assert.Equal(15f, fb);   // no next cell past the sheet, so frameB pins to the last too
            Assert.Equal(0f, blend);
        }

        [Fact]
        public void ResolveFrames_OneShot_MidSheetBlends()
        {
            (float fa, float fb, float blend) = ParticleRenderer.ResolveFrames(2.5f, 16, loop: false);
            Assert.Equal(2f, fa);
            Assert.Equal(3f, fb);
            Assert.Equal(0.5f, blend, 5);
        }

        [Fact]
        public void PackInstance_Procedural_PacksZeroFlipLane()
        {
            // A sprite with no flipbook must pack Flip = (0,0,0,0): the shader reads w <= 0.5 as "procedural" and
            // discards the atlas taps, so the procedural output stays byte-identical.
            Assert.Equal(Vector4.Zero, ParticleRenderer.PackInstance(Sprite()).Flip);
        }

        [Fact]
        public void PackInstance_ActiveFlipbook_PacksResolvedFramesAndGrid()
        {
            ParticleSprite s = Sprite();
            s.Flipbook = new ParticleFlipbook(new Scene3D.TextureHandle(0), 4, 4, MotionStrength: 1f, Loop: true);
            s.FlipbookFrame = 15.25f;   // 4x4 = 16 frames, wraps

            ParticleRenderer.ParticleInstance p = ParticleRenderer.PackInstance(s);
            (float fa, float fb, float blend) = ParticleRenderer.ResolveFrames(15.25f, 16, loop: true);
            Assert.Equal(fa, p.Flip.X);
            Assert.Equal(fb, p.Flip.Y);
            Assert.Equal(blend, p.Flip.Z, 5);
            Assert.Equal(ParticleRenderer.PackFlipGrid(4, 4, 1f), p.Flip.W);
        }

        // A procedural (no-flipbook) sprite carries the dummy atlas pair (-1, -1).
        static ParticleSprite Procedural() => Sprite();

        // A flipbook sprite on the atlas at the given list index (no motion sheet unless mvIndex >= 0).
        static ParticleSprite Flip(int atlasIndex, int mvIndex = -1)
        {
            ParticleSprite s = Sprite();
            Scene3D.TextureHandle mv = mvIndex >= 0 ? new Scene3D.TextureHandle(mvIndex) : default;
            s.Flipbook = new ParticleFlipbook(new Scene3D.TextureHandle(atlasIndex), 4, 4, mv);
            return s;
        }

        static ParticleRenderer.ParticleRun[] Runs(params ParticleSprite[] sorted)
        {
            var buf = new ParticleRenderer.ParticleRun[sorted.Length];
            int n = ParticleRenderer.BuildRuns(sorted, buf);
            return buf[..n];
        }

        [Fact]
        public void BuildRuns_AllProcedural_IsOneDummyRun()
        {
            ParticleRenderer.ParticleRun[] runs = Runs(Procedural(), Procedural(), Procedural());
            Assert.Single(runs);
            Assert.Equal(new ParticleRenderer.ParticleRun(-1, -1, 0, 3), runs[0]);
        }

        [Fact]
        public void BuildRuns_Empty_IsNoRuns()
        {
            Assert.Empty(Runs());
        }

        [Fact]
        public void BuildRuns_InterleavedAtlasProcAtlas_SplitsThreePreservingOrder()
        {
            // A / proc / A must NOT merge the two A runs: reordering across the procedural run would break the
            // global back-to-front sort.
            ParticleRenderer.ParticleRun[] runs = Runs(Flip(5), Procedural(), Flip(5));
            Assert.Equal(3, runs.Length);
            Assert.Equal(new ParticleRenderer.ParticleRun(5, -1, 0, 1), runs[0]);
            Assert.Equal(new ParticleRenderer.ParticleRun(-1, -1, 1, 1), runs[1]);
            Assert.Equal(new ParticleRenderer.ParticleRun(5, -1, 2, 1), runs[2]);
        }

        [Fact]
        public void BuildRuns_AdjacentSameAtlas_Merge()
        {
            ParticleRenderer.ParticleRun[] runs = Runs(Flip(5), Flip(5), Flip(2), Flip(2), Flip(2));
            Assert.Equal(2, runs.Length);
            Assert.Equal(new ParticleRenderer.ParticleRun(5, -1, 0, 2), runs[0]);
            Assert.Equal(new ParticleRenderer.ParticleRun(2, -1, 2, 3), runs[1]);
        }

        [Fact]
        public void BuildRuns_DifferentMotionSheet_SameAtlas_Splits()
        {
            // Same atlas but different motion pairing is a different key, so the runs split.
            ParticleRenderer.ParticleRun[] runs = Runs(Flip(5, mvIndex: 1), Flip(5, mvIndex: 3));
            Assert.Equal(2, runs.Length);
            Assert.Equal(new ParticleRenderer.ParticleRun(5, 1, 0, 1), runs[0]);
            Assert.Equal(new ParticleRenderer.ParticleRun(5, 3, 1, 1), runs[1]);
        }
    }
}
