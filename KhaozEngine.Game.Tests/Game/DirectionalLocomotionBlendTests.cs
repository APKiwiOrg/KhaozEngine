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
        [InlineData(1f, 1f, ForwardWalk, RightWalk)]
        [InlineData(-1f, 1f, ForwardWalk, LeftWalk)]
        [InlineData(1f, -1f, BackWalk, RightWalk)]
        [InlineData(-1f, -1f, BackWalk, LeftWalk)]
        public void DiagonalsSplitHalfAndHalf(float x, float y, int a, int b)
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            int n = Settle(blend, new Vector2(x, y) / MathF.Sqrt(2f) * 1.2f, samples);

            Assert.Equal(2, n);
            float[] w = Weights(samples.AsSpan(0, n));
            Assert.InRange(w[a], 0.5f - 1e-6f, 0.5f + 1e-6f);
            Assert.InRange(w[b], 0.5f - 1e-6f, 0.5f + 1e-6f);
        }

        [Fact]
        public void WeightsAreContinuousAroundTheCircle()
        {
            const float limit = 0.5f / 90f + 1e-6f;
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
        public void PhaseAdvancesByDistanceOverBlendedStride()
        {
            var blend = new DirectionalLocomotionBlend(Set());
            var samples = new GaitSample[6];
            Vector2 v = new Vector2(1f, 1f) / MathF.Sqrt(2f);
            Settle(blend, v, samples);
            float p0 = blend.Phase;

            for (int i = 0; i < 15; i++) blend.Advance(v, Frame, samples);

            Assert.True(Circular(blend.Phase, Frac(p0 + 0.25f)) <= 1e-5f, $"phase {blend.Phase} from {p0}");
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
            Assert.ThrowsAny<ArgumentException>(() => new DirectionalGaitSet(walk, walk, new[] { new GaitClip(0, 1f, 0f, 0f) }, walk));

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
