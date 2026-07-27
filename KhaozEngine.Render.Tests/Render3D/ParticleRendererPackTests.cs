using System;
using System.Linq;
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

        [Fact]
        public void PackFlipGrid_RoundTripsColsRows_AcrossFullRange()
        {
            // Every field must stay exact through float32, including the corners of the 1..127 grid range (the cap
            // narrowed from 255 when the two UV flip bits took the top of the lane).
            foreach (int cols in new[] { 1, 2, 7, 16, 64, 126, 127 })
                foreach (int rows in new[] { 1, 2, 8, 16, 100, 127 })
                {
                    float packed = ParticleRenderer.PackFlipGrid(cols, rows, 1f);
                    (int dc, int dr, _, _, _) = DecodeFlipGrid(packed);
                    Assert.Equal(cols, dc);
                    Assert.Equal(rows, dr);
                }
        }

        [Fact]
        public void PackFlipGrid_QuantizesMotionStrength_ToSixtyFourth()
        {
            // 0 and 1 land exactly. 4 is capped at the strength byte (255) so the whole packed value stays <= 2^24-1
            // and every field stays exact in float32, decoding to within one 1/64 quantum of 4.
            (_, _, float m0, _, _) = DecodeFlipGrid(ParticleRenderer.PackFlipGrid(127, 127, 0f));
            Assert.Equal(0f, m0);

            (_, _, float m1, _, _) = DecodeFlipGrid(ParticleRenderer.PackFlipGrid(127, 127, 1f));
            Assert.Equal(1f, m1);

            (int dc, int dr, float m4, _, _) = DecodeFlipGrid(ParticleRenderer.PackFlipGrid(127, 127, 4f));
            Assert.Equal(127, dc);   // grid survives even at the max-strength corner
            Assert.Equal(127, dr);
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

        // Mirror of the fragment shader's decode (ShaderSources.Effects.cs). Round-tripping through this proves the
        // C# encode and the GLSL decode agree field by field, which a golden cannot: a golden only sees the pixels
        // an encode/decode PAIR produces, so a matched pair of bugs would sail past it.
        static (int cols, int rows, float mstr, bool flipU, bool flipV) DecodeFlipGrid(float packed)
        {
            float cols = packed % 128f;
            float rows = MathF.Floor(packed / 128f) % 128f;
            float mstr = MathF.Floor(packed / 16384f) % 256f / 64f;
            float flipU = MathF.Floor(packed / 4194304f) % 2f;
            float flipV = MathF.Floor(packed / 8388608f);
            return ((int)cols, (int)rows, mstr, flipU > 0.5f, flipV > 0.5f);
        }

        [Theory]
        [InlineData(1, 1, 0f, false, false)]
        [InlineData(4, 4, 1f, false, false)]
        [InlineData(4, 4, 1f, false, true)]
        [InlineData(4, 4, 1f, true, false)]
        [InlineData(4, 4, 1f, true, true)]
        [InlineData(8, 16, 0.5f, true, false)]
        [InlineData(127, 127, 3.984375f, true, true)]   // 255/64, the largest strength the byte represents exactly
        [InlineData(127, 1, 0f, false, true)]
        [InlineData(1, 127, 3.25f, true, false)]
        public void PackFlipGrid_RoundTripsEveryFieldIndependently(int cols, int rows, float mstr, bool flipU, bool flipV)
        {
            // 7 + 7 + 8 + 1 + 1 bits in one float32 lane. Every field must survive the trip untouched by its
            // neighbours: a carry into the wrong field is exactly how a packing regression shows up. The strengths
            // here are all exact multiples of the 1/64 quantum, so the round-trip is lossless (the separate
            // quantization test covers the rounding and the 255 cap).
            float packed = ParticleRenderer.PackFlipGrid(cols, rows, mstr, flipU, flipV);
            (int dc, int dr, float dm, bool du, bool dv) = DecodeFlipGrid(packed);

            Assert.Equal(cols, dc);
            Assert.Equal(rows, dr);
            Assert.Equal(mstr, dm, 3);
            Assert.Equal(flipU, du);
            Assert.Equal(flipV, dv);
        }

        [Fact]
        public void PackFlipGrid_MaxIsExactly2Pow24Minus1()
        {
            // The whole layout exists to stay under 2^24, where float32 still counts by ones. Saturating every
            // field must land exactly on the ceiling, never past it.
            float max = ParticleRenderer.PackFlipGrid(127, 127, 4f, flipU: true, flipV: true);
            Assert.Equal(16777215f, max);
            Assert.Equal(max, (float)(double)max);   // representable exactly, no rounding on the way in

            // Over-range inputs clamp to that same ceiling rather than overflowing into a neighbour's bits.
            Assert.Equal(max, ParticleRenderer.PackFlipGrid(9999, 9999, 99f, flipU: true, flipV: true));
        }

        [Fact]
        public void PackFlipGrid_AllFourFlipCombinationsAreDistinct()
        {
            float none = ParticleRenderer.PackFlipGrid(4, 4, 1f, false, false);
            float u = ParticleRenderer.PackFlipGrid(4, 4, 1f, true, false);
            float v = ParticleRenderer.PackFlipGrid(4, 4, 1f, false, true);
            float both = ParticleRenderer.PackFlipGrid(4, 4, 1f, true, true);

            Assert.Equal(4, new[] { none, u, v, both }.Distinct().Count());
            // The flip bits sit above the grid and strength, so they add cleanly and never disturb them.
            Assert.Equal(4194304f, u - none);
            Assert.Equal(8388608f, v - none);
            Assert.Equal(12582912f, both - none);
            foreach (float p in new[] { none, u, v, both })
            {
                (int dc, int dr, float dm, _, _) = DecodeFlipGrid(p);
                Assert.Equal((4, 4, 1f), (dc, dr, dm));
            }
        }

        [Fact]
        public void PackFlipGrid_ClampsGridTo127()
        {
            // The cap narrowed from 255 to 127 to buy the two flip bits. A 128-column sheet is unreachable in
            // practice (8192px at a 64px cell), but the clamp must still hold instead of wrapping into rows.
            (int dc, int dr, _, _, _) = DecodeFlipGrid(ParticleRenderer.PackFlipGrid(200, 300, 0f));
            Assert.Equal(127, dc);
            Assert.Equal(127, dr);

            (int zc, int zr, _, _, _) = DecodeFlipGrid(ParticleRenderer.PackFlipGrid(0, -5, 0f));
            Assert.Equal(1, zc);
            Assert.Equal(1, zr);
        }

        [Fact]
        public void PackFlipGrid_ProceduralSentinelStillHolds()
        {
            // The shader reads w > 0.5 as "this is a flipbook". An active spec always packs at least cols 1, so the
            // sentinel can never collide with the procedural 0 no matter which flips are set.
            Assert.True(ParticleRenderer.PackFlipGrid(1, 1, 0f, false, false) > 0.5f);
            Assert.Equal(Vector4.Zero, ParticleRenderer.PackInstance(Sprite()).Flip);
        }

        [Fact]
        public void PackInstance_FlipbookFlips_ReachThePackedLane()
        {
            // ParticleFlipbook's trailing FlipU/FlipV must actually be carried into IFlip.w, not dropped at the
            // packing seam.
            ParticleSprite s = Sprite();
            s.Flipbook = new ParticleFlipbook(new Scene3D.TextureHandle(0), 4, 4,
                MotionStrength: 1f, Loop: true, FlipU: false, FlipV: true);

            (_, _, _, bool du, bool dv) = DecodeFlipGrid(ParticleRenderer.PackInstance(s).Flip.W);
            Assert.False(du);
            Assert.True(dv);
        }

        [Fact]
        public void ParticleFlipbook_FlipsDefaultToFalse()
        {
            // Purely additive: an existing positional construction site keeps the unflipped behaviour.
            var spec = new ParticleFlipbook(new Scene3D.TextureHandle(0), 4, 4);
            Assert.False(spec.FlipU);
            Assert.False(spec.FlipV);
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
