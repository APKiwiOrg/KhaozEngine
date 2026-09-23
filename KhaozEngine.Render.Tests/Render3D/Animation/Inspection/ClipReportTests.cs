using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    [Collection("ClipReportCultureSerial")]
    public class ClipReportTests
    {
        [Fact]
        public void Write_RepeatedCallsMatchExactAuthoredEndpointBytes()
        {
            var fixture = new InspectionFixture();
            AnimationClip clip = fixture.CreateEndpointClip();
            var clips = new[] { clip };
            var phases = new[] { 0f, 1f };
            var positions = new[] { "Hips", "weapon_socket" };

            byte[] first = Encoding.UTF8.GetBytes(ClipReport.Write(fixture.Skeleton, clips, phases, positions));
            byte[] second = Encoding.UTF8.GetBytes(ClipReport.Write(fixture.Skeleton, clips, phases, positions));

            Assert.Equal(Encoding.UTF8.GetBytes(ExpectedEndpointReport), first);
            Assert.Equal(first, second);
        }

        [Fact]
        public void Write_UsesInvariantCulture()
        {
            var fixture = new InspectionFixture();
            CultureInfo priorCulture = CultureInfo.CurrentCulture;
            CultureInfo priorUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

                string report = ClipReport.Write(
                    fixture.Skeleton,
                    new[] { fixture.CreateEndpointClip() },
                    new[] { 0.25f },
                    new[] { "Hips" });

                Assert.Contains("phase\t0.250000\n", report, StringComparison.Ordinal);
                Assert.Contains("position\tHips\t0.000000\t1.000000\t0.500000\n", report, StringComparison.Ordinal);
                Assert.DoesNotContain("0,250000", report, StringComparison.Ordinal);
            }
            finally
            {
                CultureInfo.CurrentCulture = priorCulture;
                CultureInfo.CurrentUICulture = priorUiCulture;
            }
        }

        [Fact]
        public void Write_EscapesNamesAndNormalizesRoundedNegativeZero()
        {
            const string nodeName = "Root\\\t\r\n";
            var skeleton = new Skeleton(
                new[] { -1 },
                new[]
                {
                    new JointPose
                    {
                        Translation = new Vector3(-0.0000001f, 0f, 0f),
                        Rotation = new Quaternion(-0.0000001f, 0f, 0f, 1f),
                        Scale = Vector3.One,
                    },
                },
                new[] { 7 },
                new[] { 0 },
                new[] { nodeName });
            var clip = new AnimationClip("clip\\\t\r\n", 1f, Array.Empty<JointTrack>());

            string report = ClipReport.Write(skeleton, new[] { clip }, new[] { 0f }, new[] { nodeName });

            Assert.Equal(
                "KhaozEngine ClipReport v1\n"
                + "clip\tclip\\\\\\t\\r\\n\n"
                + "phase\t0.000000\n"
                + "rotation\tRoot\\\\\\t\\r\\n\t0.000000\t0.000000\t0.000000\t1.000000\n"
                + "position\tRoot\\\\\\t\\r\\n\t0.000000\t0.000000\t0.000000\n",
                report);
        }

        [Fact]
        public void Write_RotationKeyChangeMovesReport()
        {
            var fixture = new InspectionFixture();
            var changed = new AnimationClip(
                "changed",
                1f,
                new List<JointTrack>
                {
                    new(106)
                    {
                        Rotation = new QuaternionTrack(
                            new[] { 0f, 1f },
                            new[]
                            {
                                Quaternion.Identity,
                                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
                            },
                            InterpolationMode.Linear),
                    },
                });
            var unchanged = new AnimationClip("changed", 1f, Array.Empty<JointTrack>());

            string before = ClipReport.Write(fixture.Skeleton, new[] { unchanged }, new[] { 1f }, Array.Empty<string>());
            string after = ClipReport.Write(fixture.Skeleton, new[] { changed }, new[] { 1f }, Array.Empty<string>());

            Assert.NotEqual(before, after);
            Assert.Contains(
                "rotation\tHand.R\t0.000000\t0.000000\t0.707107\t0.707107\n",
                after,
                StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Write_RejectsNonFinitePhase(float phase)
        {
            var fixture = new InspectionFixture();

            Assert.Throws<ArgumentOutOfRangeException>(() => ClipReport.Write(
                fixture.Skeleton,
                new[] { fixture.WalkClip },
                new[] { phase },
                Array.Empty<string>()));
        }

        [Fact]
        public void Write_RejectsNonFiniteSampledRotationAndPosition()
        {
            var fixture = new InspectionFixture();
            var badRotation = new AnimationClip(
                "bad-rotation",
                1f,
                new List<JointTrack>
                {
                    new(106)
                    {
                        Rotation = new QuaternionTrack(
                            new[] { 0f },
                            new[] { new Quaternion(float.NaN, 0f, 0f, 1f) },
                            InterpolationMode.Step),
                    },
                });
            var badPosition = new AnimationClip(
                "bad-position",
                1f,
                new List<JointTrack>
                {
                    new(101)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f },
                            new[] { new Vector3(float.NaN, 0f, 0f) },
                            InterpolationMode.Step),
                    },
                });

            Assert.Throws<ArgumentException>(() => ClipReport.Write(
                fixture.Skeleton,
                new[] { badRotation },
                new[] { 0f },
                Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => ClipReport.Write(
                fixture.Skeleton,
                new[] { badPosition },
                new[] { 0f },
                new[] { "Hips" }));
        }

        const string ExpectedEndpointReport =
            "KhaozEngine ClipReport v1\n"
            + "clip\tendpoint\n"
            + "phase\t0.000000\n"
            + "rotation\tRoot\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tHips\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tThigh.L\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tFoot.L\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tThigh.R\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tFoot.R\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tHand.R\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tweapon_socket\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "position\tHips\t0.000000\t1.000000\t0.000000\n"
            + "position\tweapon_socket\t0.600000\t1.450000\t0.500000\n"
            + "phase\t1.000000\n"
            + "rotation\tRoot\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tHips\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tThigh.L\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tFoot.L\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tThigh.R\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tFoot.R\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tHand.R\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "rotation\tweapon_socket\t0.000000\t0.000000\t0.000000\t1.000000\n"
            + "position\tHips\t0.000000\t1.000000\t2.000000\n"
            + "position\tweapon_socket\t0.600000\t1.450000\t2.500000\n";
    }

    [CollectionDefinition("ClipReportCultureSerial", DisableParallelization = true)]
    public sealed class ClipReportCultureSerialCollection
    {
    }
}
