using System;
using System.Numerics;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class DirectionalLocomotionBlendTests
    {
        internal const int ForwardWalk = 0, ForwardRun = 1, BackWalk = 2, LeftWalk = 3, RightWalk = 4, RightRun = 5;
        const float Frame = 1f / 60f;
        const float Coarse = 0.025f; // blendSeconds / 2 is three calls at the default 0.15 s

        // Six clips. Ids are the slot indexes, so a sample's ClipId names its slot.
        internal static DirectionalGaitSet Set() => new(
            new[] { new GaitClip(ForwardWalk, 1.4f, 1.2f, 0.25f), new GaitClip(ForwardRun, 4.0f, 2.4f, 0.25f) },
            new[] { new GaitClip(BackWalk, 1.0f, 0.9f, 0.5f) },
            new[] { new GaitClip(LeftWalk, 1.2f, 0.8f, 0.6f) },
            new[] { new GaitClip(RightWalk, 1.2f, 0.8f, 0.1f), new GaitClip(RightRun, 3.0f, 1.8f, 0.1f) });

        static readonly float[] SyncPhases = { 0.25f, 0.25f, 0.5f, 0.6f, 0.1f, 0.1f };
        static readonly float[] Strides = { 1.2f, 2.4f, 0.9f, 0.8f, 0.8f, 1.8f };

        internal static int Settle(DirectionalLocomotionBlend blend, Vector2 v, Span<GaitSample> samples)
        {
            int n = 0;
            for (int i = 0; i < 10; i++) n = blend.Advance(v, Frame, samples);
            return n;
        }

        static float[] Weights(ReadOnlySpan<GaitSample> samples)
        {
            var w = new float[6];
            foreach (GaitSample s in samples) w[s.ClipId] += s.Weight;
            return w;
        }

        static float[] SettledWeights(Vector2 v)
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, v, samples);
            return Weights(samples.AsSpan(0, n));
        }

        // Clockwise from forward, the body frame's D7 angle.
        static Vector2 AtDegrees(double degrees, float speed)
        {
            double r = degrees * Math.PI / 180d;
            return new Vector2((float)Math.Sin(r), (float)Math.Cos(r)) * speed;
        }

        static float Ratio(float a, float b) => MathF.Max(a / b, b / a);

        // Phases live on a circle, so 0.9999 and 0.0001 are 0.0002 apart.
        internal static float Circular(float a, float b)
        {
            float d = MathF.Abs(a - b) % 1f;
            return MathF.Min(d, 1f - d);
        }

        static float Frac(float x)
        {
            float f = x - MathF.Floor(x);
            return f >= 1f ? 0f : f;
        }

        [Theory]
        [InlineData(0f, 1.4f, ForwardWalk)]
        [InlineData(0f, -1f, BackWalk)]
        [InlineData(-1.2f, 0f, LeftWalk)]
        [InlineData(1.2f, 0f, RightWalk)]
        public void EachCardinalIsOneClip(float x, float y, int clip)
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, new Vector2(x, y), samples);

            Assert.Equal(1, n);
            Assert.Equal(clip, samples[0].ClipId);
            Assert.Equal(1f, samples[0].Weight, 6);
        }

        [Theory]
        [InlineData(1f, 1f, ForwardWalk, RightWalk, 0.4f, 0.6f)]
        [InlineData(-1f, 1f, ForwardWalk, LeftWalk, 0.4f, 0.6f)]
        [InlineData(1f, -1f, BackWalk, RightWalk, 0.4705882f, 0.5294118f)]
        [InlineData(-1f, -1f, BackWalk, LeftWalk, 0.4705882f, 0.5294118f)]
        public void DiagonalsShareByComponentOverStride(float x, float y, int a, int b, float wa, float wb)
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, new Vector2(x, y) / MathF.Sqrt(2f) * 1.2f, samples);

            Assert.Equal(2, n);
            float[] w = Weights(samples.AsSpan(0, n));
            Assert.InRange(w[a], wa - 1e-5f, wa + 1e-5f);
            Assert.InRange(w[b], wb - 1e-5f, wb + 1e-5f);
        }

        [Fact]
        public void ABracketedDiagonalSplitsEachFamilyShareBySpeed()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, AtDegrees(30d, 2.7f), samples);

            Assert.Equal(4, n);
            float[] w = Weights(samples.AsSpan(0, n));
            Assert.InRange(w[ForwardWalk], 0.3055742f - 1e-5f, 0.3055742f + 1e-5f);
            Assert.InRange(w[ForwardRun], 0.3055742f - 1e-5f, 0.3055742f + 1e-5f);
            Assert.InRange(w[RightWalk], 0.0648086f - 1e-5f, 0.0648086f + 1e-5f);
            Assert.InRange(w[RightRun], 0.3240429f - 1e-5f, 0.3240429f + 1e-5f);
        }

        [Fact]
        public void WeightsAreContinuousAroundTheCircle()
        {
            // The share's slope peaks at a cardinal, at the larger stride over the smaller of the two families that
            // meet there. At 1.2 m/s every family plays its slowest member, so the bound is the largest adjacent walk
            // stride ratio times the 0.5 degree step in radians.
            float forward = Strides[ForwardWalk], back = Strides[BackWalk];
            float left = Strides[LeftWalk], right = Strides[RightWalk];
            float ratio = MathF.Max(MathF.Max(Ratio(forward, left), Ratio(forward, right)),
                MathF.Max(Ratio(back, left), Ratio(back, right)));
            float limit = ratio * (0.5f * MathF.PI / 180f) + 1e-6f;
            float[] first = SettledWeights(AtDegrees(0d, 1.2f));
            float[] previous = first;
            bool sawBelow180 = false, sawAbove180 = false;
            for (int i = 1; i <= 720; i++)
            {
                double degrees = i * 0.5d;
                sawBelow180 |= degrees == 179.5d;
                sawAbove180 |= degrees == 180.5d;
                float[] current = i == 720 ? first : SettledWeights(AtDegrees(degrees, 1.2f));
                for (int slot = 0; slot < 6; slot++)
                {
                    float change = MathF.Abs(current[slot] - previous[slot]);
                    Assert.True(change <= limit, $"slot {slot} changed by {change} arriving at {degrees} degrees");
                }

                previous = current;
            }

            Assert.True(sawBelow180 && sawAbove180);
        }

        [Fact]
        public void SpeedBracketsAndClamps()
        {
            float[] slow = SettledWeights(new Vector2(0f, 0.7f));
            Assert.Equal(1f, slow[ForwardWalk], 6);
            Assert.Equal(0f, slow[ForwardRun]);

            float[] mid = SettledWeights(new Vector2(0f, 2.7f));
            Assert.InRange(mid[ForwardWalk], 0.5f - 1e-6f, 0.5f + 1e-6f);
            Assert.InRange(mid[ForwardRun], 0.5f - 1e-6f, 0.5f + 1e-6f);

            float[] fast = SettledWeights(new Vector2(0f, 6.0f));
            Assert.Equal(0f, fast[ForwardWalk]);
            Assert.Equal(1f, fast[ForwardRun], 6);
        }

        [Fact]
        public void WeightsSumToOneAndSteadyStateHasAtMostFour()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, new Vector2(1f, 1f) / MathF.Sqrt(2f) * 2.7f, samples);

            Assert.Equal(4, n);
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += samples[i].Weight;
            Assert.InRange(sum, 1f - 1e-6f, 1f + 1e-6f);
        }

        [Fact]
        public void AdvanceRefusesASpanShorterThanTheClipCount()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            Assert.ThrowsAny<ArgumentException>(() => blend.Advance(Vector2.Zero, Frame, new GaitSample[4]));
        }

        [Fact]
        public void ADiagonalPhaseRateIsTheSumOfComponentOverStride()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            Vector2 v = new Vector2(1f, 1f) / MathF.Sqrt(2f);
            Settle(blend, v, samples);
            float p0 = blend.Phase;

            for (int i = 0; i < 15; i++) blend.Advance(v, Frame, samples);

            Assert.True(Circular(blend.Phase, Frac(p0 + 0.25f * 1.473139f)) <= 1e-5f, $"phase {blend.Phase} from {p0}");
        }

        [Theory]
        [InlineData(45d, 1.2f)]
        [InlineData(135d, 1.2f)]
        [InlineData(315d, 1.2f)]
        [InlineData(30d, 2.7f)]
        public void PlantedFootTravelMatchesTheBodyOnEachAxis(double degrees, float speed)
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            Vector2 v = AtDegrees(degrees, speed);
            Settle(blend, v, samples);
            float p0 = blend.Phase;

            int n = blend.Advance(v, Frame, samples);
            float rate = Frac(blend.Phase - p0) / Frame;

            float along = 0f, across = 0f;
            for (int i = 0; i < n; i++)
            {
                float travel = samples[i].Weight * Strides[samples[i].ClipId] * rate;
                if (samples[i].ClipId is ForwardWalk or ForwardRun or BackWalk) along += travel;
                else across += travel;
            }

            double r = degrees * Math.PI / 180d;
            float wantAlong = speed * (float)Math.Abs(Math.Cos(r)), wantAcross = speed * (float)Math.Abs(Math.Sin(r));
            Assert.True(MathF.Abs(along - wantAlong) <= 1e-3f, $"forward or backward foot {along} against {wantAlong}");
            Assert.True(MathF.Abs(across - wantAcross) <= 1e-3f, $"left or right foot {across} against {wantAcross}");
        }

        [Fact]
        public void PureStrafeReproducesItsOwnStride()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            var v = new Vector2(0.8f, 0f);
            Settle(blend, v, samples);
            float p0 = blend.Phase;

            for (int i = 0; i < 30; i++) blend.Advance(v, Frame, samples);

            Assert.True(Circular(blend.Phase, Frac(p0 + 0.5f)) <= 1e-5f, $"phase {blend.Phase} from {p0}");
        }

        [Fact]
        public void SyncPhasesKeepContactsAligned()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, new Vector2(1f, 1f) / MathF.Sqrt(2f) * 1.2f, samples);

            Assert.Equal(2, n);
            for (int i = 0; i < n; i++)
            {
                float contact = Frac(samples[i].Phase - SyncPhases[samples[i].ClipId]);
                Assert.True(Circular(contact, blend.Phase) <= 1e-6f, $"clip {samples[i].ClipId} at {contact}, blend {blend.Phase}");
            }
        }

        [Fact]
        public void ZeroTravelHoldsThePhase()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            Settle(blend, new Vector2(0f, 1.4f), samples);
            float p0 = blend.Phase;

            for (int i = 0; i < 10; i++) blend.Advance(Vector2.Zero, Frame, samples);

            Assert.Equal(p0, blend.Phase);
        }

        [Fact]
        public void SplitDtIsInvariantWhenSettled()
        {
            Vector2 v = new Vector2(1f, 1f) / MathF.Sqrt(2f) * 2.7f;
            var whole = new DirectionalLocomotionBlend(Set());
            var split = new DirectionalLocomotionBlend(Set());
            var a = new GaitSample[6];
            var b = new GaitSample[6];
            Settle(whole, v, a);
            Settle(split, v, b);

            int na = whole.Advance(v, 1f / 30f, a);
            split.Advance(v, 1f / 60f, b);
            int nb = split.Advance(v, 1f / 60f, b);

            Assert.True(Circular(whole.Phase, split.Phase) <= 1e-6f, $"{whole.Phase} vs {split.Phase}");
            float[] wa = Weights(a.AsSpan(0, na)), wb = Weights(b.AsSpan(0, nb));
            for (int slot = 0; slot < 6; slot++) Assert.InRange(wa[slot] - wb[slot], -1e-6f, 1e-6f);
        }

        [Fact]
        public void ReversalCrossfadesForBlendSeconds()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            Settle(blend, new Vector2(0f, 1.4f), samples);
            var back = new Vector2(0f, -1f);

            int n = 0;
            for (int i = 0; i < 3; i++) n = blend.Advance(back, Coarse, samples);
            float[] half = Weights(samples.AsSpan(0, n));
            Assert.InRange(half[ForwardWalk], 0.5f - 1e-5f, 0.5f + 1e-5f);
            Assert.InRange(half[BackWalk], 0.5f - 1e-5f, 0.5f + 1e-5f);

            for (int i = 0; i < 3; i++) n = blend.Advance(back, Coarse, samples);
            Assert.Equal(1, n);
            Assert.Equal(BackWalk, samples[0].ClipId);
            Assert.Equal(1f, samples[0].Weight, 6);
        }

        [Fact]
        public void StopFadesOutOfTheLastGait()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            Settle(blend, new Vector2(0f, 1.4f), samples);
            Assert.Equal(1f, blend.TravelWeight);

            int n = 0;
            for (int i = 0; i < 3; i++) n = blend.Advance(Vector2.Zero, Coarse, samples);
            Assert.InRange(blend.TravelWeight, 0.5f - 1e-5f, 0.5f + 1e-5f);

            for (int i = 0; i < 3; i++) n = blend.Advance(Vector2.Zero, Coarse, samples);
            Assert.Equal(0f, blend.TravelWeight);
            Assert.Equal(1, n);
            Assert.Equal(ForwardWalk, samples[0].ClipId);
            Assert.Equal(1f, samples[0].Weight, 6);
        }

        [Fact]
        public void StandingStillIsNotMovingEvenWithAZeroMovingSpeed()
        {
            var blend = new DirectionalLocomotionBlend(Set(), movingSpeed: 0f);
            var samples = new GaitSample[6];
            blend.Reset();

            int n = blend.Advance(Vector2.Zero, Coarse, samples);
            Assert.Equal(0f, blend.TravelWeight);
            Assert.Equal(0, n);
        }

        [Theory]
        [InlineData(0f, 0f, 0f, -1f, 0f, 1f)]
        [InlineData(0f, 1f, 0f, 0f, 1f, 0f)]
        [InlineData(MathF.PI / 2f, -1f, 0f, 0f, 0f, 1f)]
        [InlineData(MathF.PI / 2f, 0f, 0f, -1f, 1f, 0f)]
        public void BodyFrameUsesTheCameraYawConvention(float yaw, float vx, float vy, float vz, float x, float y)
        {
            Vector2 body = DirectionalLocomotionBlend.BodyFrame(new Vector3(vx, vy, vz), yaw);
            Assert.InRange(body.X - x, -1e-6f, 1e-6f);
            Assert.InRange(body.Y - y, -1e-6f, 1e-6f);
        }

        [Fact]
        public void InvalidSetsAndArgumentsAreRefused()
        {
            GaitClip[] walk = { new GaitClip(0, 1f, 1f, 0f) };
            Assert.ThrowsAny<ArgumentException>(() => new DirectionalGaitSet(walk, walk, ReadOnlySpan<GaitClip>.Empty, walk));
            Assert.ThrowsAny<ArgumentException>(() => new DirectionalGaitSet(
                new[] { new GaitClip(0, 2f, 1f, 0f), new GaitClip(1, 1f, 1f, 0f) }, walk, walk, walk));
            Assert.ThrowsAny<ArgumentException>(() => new DirectionalGaitSet(walk, new[] { new GaitClip(0, 1f, 1f, 1f) }, walk, walk));
            ArgumentException left = Assert.Throws<ArgumentException>(
                () => new DirectionalGaitSet(walk, walk, new[] { new GaitClip(0, 1f, 0f, 0f) }, walk));
            Assert.Equal("left", left.ParamName);

            var set = new DirectionalGaitSet(walk, walk, walk, walk);
            Assert.ThrowsAny<ArgumentException>(() => new DirectionalLocomotionBlend(set, blendSeconds: 0f));
            var blend = new DirectionalLocomotionBlend(set);
            Assert.ThrowsAny<ArgumentException>(() => blend.Advance(Vector2.UnitY, float.NaN, new GaitSample[4]));
        }

        [Fact]
        public void DuplicateClipIdsAreSeparateSlots()
        {
            GaitClip[] walk = { new GaitClip(7, 1f, 1f, 0f) };
            var set = new DirectionalGaitSet(walk, walk, walk, walk);
            Assert.Equal(4, set.ClipCount);

            var blend = new DirectionalLocomotionBlend(set);
            var samples = new GaitSample[4];
            int n = Settle(blend, new Vector2(1f, 1f), samples);
            Assert.Equal(2, n);
            Assert.Equal(7, samples[0].ClipId);
            Assert.Equal(7, samples[1].ClipId);
            Assert.Equal(1f, samples[0].Weight + samples[1].Weight, 6);
        }
    }
}
