using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    public class FootPlantTests
    {
        const float Epsilon = 1e-5f;

        [Fact]
        public void Measure_DistanceDrivenWalkKeepsStanceFeetPlanted()
        {
            var fixture = new InspectionFixture();

            FootPlantReport report = FootPlant.Measure(
                fixture.WalkClip,
                fixture.Skeleton,
                new[] { "Foot.L", "Foot.R" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres: 2f,
                samples: 4);

            AssertNear(0f, report.MinSoleHeight);
            AssertNear(0f, report.MinSolePhase);
            AssertNear(0f, report.MaxStanceSlide);
        }

        [Fact]
        public void Measure_ReportsFootThroughFloorAtFirstMinimumPhase()
        {
            var fixture = new InspectionFixture();
            AnimationClip clip = FootClip(
                new[] { 0f, 0.5f, 1f },
                new[]
                {
                    new Vector3(0f, -0.5f, 0.15f),
                    new Vector3(0f, -0.75f, 0.15f),
                    new Vector3(0f, -0.5f, 0.15f),
                });

            FootPlantReport report = FootPlant.Measure(
                clip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres: 0f,
                samples: 4);

            AssertNear(-0.25f, report.MinSoleHeight);
            AssertNear(0.5f, report.MinSolePhase);
        }

        [Fact]
        public void Measure_AllStanceRunAnchorsSlideAtSampleZero()
        {
            var fixture = new InspectionFixture();
            AnimationClip clip = FootClip(
                new[] { 0f, 0.25f, 0.5f, 0.75f, 1f },
                new[]
                {
                    new Vector3(0f, -0.5f, 0.15f),
                    new Vector3(1f, -0.5f, 0.15f),
                    new Vector3(2f, -0.5f, 0.15f),
                    new Vector3(4f, -0.5f, 0.15f),
                    new Vector3(0f, -0.5f, 0.15f),
                });

            FootPlantReport report = FootPlant.Measure(
                clip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres: 0f,
                samples: 4);

            AssertNear(4f, report.MaxStanceSlide);
        }

        [Fact]
        public void Measure_JoinsStanceRunAcrossLoopBoundary()
        {
            var fixture = new InspectionFixture();
            AnimationClip clip = FootClip(
                new[] { 0f, 0.25f, 0.5f, 0.75f, 1f },
                new[]
                {
                    new Vector3(1f, -0.5f, 0.15f),
                    new Vector3(1f, -0.2f, 0.15f),
                    new Vector3(0f, -0.2f, 0.15f),
                    new Vector3(0f, -0.5f, 0.15f),
                    new Vector3(1f, -0.5f, 0.15f),
                });

            FootPlantReport report = FootPlant.Measure(
                clip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres: 0f,
                samples: 4);

            AssertNear(1f, report.MaxStanceSlide);
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Measure_RejectsInvalidStride(float strideMetres)
        {
            var fixture = new InspectionFixture();

            Assert.Throws<System.ArgumentOutOfRangeException>(() => FootPlant.Measure(
                fixture.WalkClip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres,
                samples: 4));
        }

        [Fact]
        public void Measure_RejectsFewerThanTwoSamples()
        {
            var fixture = new InspectionFixture();

            Assert.Throws<System.ArgumentOutOfRangeException>(() => FootPlant.Measure(
                fixture.WalkClip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres: 0f,
                samples: 1));
        }

        [Fact]
        public void Measure_RejectsNonFiniteGeometryInputs()
        {
            var fixture = new InspectionFixture();

            Assert.Throws<System.ArgumentOutOfRangeException>(() => FootPlant.Measure(
                fixture.WalkClip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                new Vector3(float.NaN, 0f, 0f),
                groundHeight: 0f,
                strideMetres: 0f,
                samples: 4));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => FootPlant.Measure(
                fixture.WalkClip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: float.NaN,
                strideMetres: 0f,
                samples: 4));
        }

        [Fact]
        public void Measure_RejectsNonFiniteSampledSole()
        {
            var fixture = new InspectionFixture();
            AnimationClip clip = FootClip(
                new[] { 0f, 1f },
                new[]
                {
                    new Vector3(0f, float.NaN, 0f),
                    new Vector3(0f, float.NaN, 0f),
                });

            Assert.Throws<System.ArgumentException>(() => FootPlant.Measure(
                clip,
                fixture.Skeleton,
                new[] { "Foot.L" },
                Vector3.Zero,
                groundHeight: 0f,
                strideMetres: 0f,
                samples: 4));
        }

        static AnimationClip FootClip(float[] times, Vector3[] values) =>
            new(
                "foot",
                1f,
                new List<JointTrack>
                {
                    new(103)
                    {
                        Translation = new Vector3Track(times, values, InterpolationMode.Linear),
                    },
                });

        static void AssertNear(float expected, float actual) =>
            Assert.InRange(System.MathF.Abs(expected - actual), 0f, Epsilon);
    }
}
