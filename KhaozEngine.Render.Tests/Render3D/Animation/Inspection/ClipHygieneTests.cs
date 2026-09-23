using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    public class ClipHygieneTests
    {
        [Fact]
        public void Check_GeneratedWalkPassesDeclaredPolicy()
        {
            var fixture = new InspectionFixture();
            var translated = new HashSet<string>(StringComparer.Ordinal) { "Foot.L", "Foot.R", "Hand.R" };
            var animated = new HashSet<string>(translated, StringComparer.Ordinal);

            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(
                fixture.WalkClip,
                fixture.Skeleton,
                new ClipHygieneOptions(translated, animated, Looping: true, MinKeysPerSecond: 1f));

            Assert.Empty(findings);
        }

        [Fact]
        public void Check_BrokenTracksReturnExactFindingsInDeterministicOrder()
        {
            var fixture = new InspectionFixture();
            var footTranslation = new Vector3(0f, -0.5f, 0.15f);
            var tracks = new List<JointTrack>
            {
                new(901),
                new(103)
                {
                    Translation = new Vector3Track(
                        new[] { 0f, 0.5f, 0.5f },
                        new[] { footTranslation, footTranslation, footTranslation },
                        InterpolationMode.Linear),
                    Rotation = new QuaternionTrack(
                        new[] { 0f, 1f },
                        new[] { Quaternion.Identity, new Quaternion(0f, 0f, 0f, 2f) },
                        InterpolationMode.Linear),
                    Scale = new Vector3Track(
                        new[] { 0f, 1f },
                        new[] { Vector3.One, new Vector3(2f) },
                        InterpolationMode.Linear),
                },
                new(101)
                {
                    Translation = new Vector3Track(
                        new[] { 0f, 1f },
                        new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 1f) },
                        InterpolationMode.Linear),
                },
                new(900)
                {
                    Scale = new Vector3Track(
                        new[] { 0f, 1f },
                        new[] { Vector3.One, Vector3.One },
                        InterpolationMode.Linear),
                },
            };
            var clip = new AnimationClip("broken", 1f, tracks);
            var translated = new HashSet<string>(StringComparer.Ordinal);
            var animated = new HashSet<string>(StringComparer.Ordinal) { "Foot.L" };

            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(
                clip,
                fixture.Skeleton,
                new ClipHygieneOptions(translated, animated, Looping: true, MinKeysPerSecond: 2f));

            Assert.Equal(
                new[]
                {
                    new ClipHygieneFinding("allowed-node", "Hips", "track outside AllowedNodes"),
                    new ClipHygieneFinding("translation", "Hips", "translation outside TranslationAllowed"),
                    new ClipHygieneFinding("key-density", "Hips", "translation keys-per-second=1.000000 minimum=2.000000"),
                    new ClipHygieneFinding("translation", "Foot.L", "translation outside TranslationAllowed"),
                    new ClipHygieneFinding("scale", "Foot.L", "scale channel present"),
                    new ClipHygieneFinding("rotation-unit", "Foot.L", "rotation key=1 time=1.000000 length-squared=4.000000"),
                    new ClipHygieneFinding("key-times", "Foot.L", "translation key=2 time=0.500000 previous=0.500000 not-increasing"),
                    new ClipHygieneFinding("key-density", "Foot.L", "rotation keys-per-second=1.000000 minimum=2.000000"),
                    new ClipHygieneFinding("key-density", "Foot.L", "scale keys-per-second=1.000000 minimum=2.000000"),
                    new ClipHygieneFinding("unknown-node", string.Empty, "logical-index=900"),
                    new ClipHygieneFinding("scale", string.Empty, "scale channel present"),
                    new ClipHygieneFinding("key-density", string.Empty, "scale keys-per-second=1.000000 minimum=2.000000"),
                    new ClipHygieneFinding("unknown-node", string.Empty, "logical-index=901"),
                    new ClipHygieneFinding("loop", "Hips", "mismatch=translation"),
                    new ClipHygieneFinding("loop", "Foot.L", "mismatch=scale"),
                },
                findings);
        }

        [Fact]
        public void Check_NonFiniteKeyTimeUsesCanonicalDetail()
        {
            var fixture = new InspectionFixture();
            var clip = new AnimationClip(
                "non-finite-time",
                1f,
                new List<JointTrack>
                {
                    new(103)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f, float.NaN, 1f },
                            new[] { Vector3.Zero, Vector3.Zero, Vector3.Zero },
                            InterpolationMode.Linear),
                    },
                });

            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(
                clip,
                fixture.Skeleton,
                Options(translationAllowed: "Foot.L"));

            Assert.Equal(
                new[] { new ClipHygieneFinding("key-times", "Foot.L", "translation key=1 time=NaN non-finite") },
                findings);
        }

        [Fact]
        public void Check_NonPositiveDurationMakesPresentDensityUndefined()
        {
            var fixture = new InspectionFixture();
            var clip = new AnimationClip(
                "zero-duration",
                0f,
                new List<JointTrack>
                {
                    new(103)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f, 1f },
                            new[] { Vector3.Zero, Vector3.Zero },
                            InterpolationMode.Linear),
                    },
                });

            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(
                clip,
                fixture.Skeleton,
                Options(translationAllowed: "Foot.L", minKeysPerSecond: 1f));

            Assert.Equal(
                new[]
                {
                    new ClipHygieneFinding(
                        "key-density",
                        "Foot.L",
                        "translation keys-per-second=undefined duration=0.000000 minimum=1.000000"),
                },
                findings);
        }

        [Fact]
        public void Check_LoopTreatsNegatedQuaternionAsSameOrientation()
        {
            var fixture = new InspectionFixture();
            var clip = new AnimationClip(
                "negated-loop",
                1f,
                new List<JointTrack>
                {
                    new(106)
                    {
                        Rotation = new QuaternionTrack(
                            new[] { 0f, 1f },
                            new[] { Quaternion.Identity, new Quaternion(0f, 0f, 0f, -1f) },
                            InterpolationMode.Linear),
                    },
                });

            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(
                clip,
                fixture.Skeleton,
                Options(looping: true));

            Assert.Empty(findings);
        }

        [Fact]
        public void Check_UsesOrdinalNamesEvenWhenCallerSetsIgnoreCase()
        {
            var fixture = new InspectionFixture();
            var translated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "foot.l" };
            var animated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "foot.l" };
            var clip = new AnimationClip(
                "case-policy",
                1f,
                new List<JointTrack>
                {
                    new(103)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f, 1f },
                            new[] { Vector3.Zero, Vector3.Zero },
                            InterpolationMode.Linear),
                    },
                });

            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(
                clip,
                fixture.Skeleton,
                new ClipHygieneOptions(translated, animated, Looping: false, MinKeysPerSecond: 0f));

            Assert.Equal(
                new[]
                {
                    new ClipHygieneFinding("allowed-node", "Foot.L", "track outside AllowedNodes"),
                    new ClipHygieneFinding("translation", "Foot.L", "translation outside TranslationAllowed"),
                },
                findings);
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Check_RejectsInvalidMinimumDensity(float minimum)
        {
            var fixture = new InspectionFixture();

            Assert.Throws<ArgumentOutOfRangeException>(() => ClipHygiene.Check(
                fixture.WalkClip,
                fixture.Skeleton,
                Options(minKeysPerSecond: minimum)));
        }

        static ClipHygieneOptions Options(
            string? translationAllowed = null,
            bool looping = false,
            float minKeysPerSecond = 0f)
        {
            var translated = new HashSet<string>(StringComparer.Ordinal);
            if (translationAllowed is not null) translated.Add(translationAllowed);
            return new ClipHygieneOptions(translated, AllowedNodes: null, looping, minKeysPerSecond);
        }
    }
}
