using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class IrradianceSHTests
{
    [Fact]
    public void UniformEvaluatesToItsIrradianceInEveryDirection()
    {
        var irradiance = new Vector3(.2f, .3f, .4f);
        var sh = IrradianceSH.Uniform(irradiance);

        foreach (var normal in TestNormals())
            AssertClose(irradiance, sh.Evaluate(normal), 1e-5f);
    }

    [Fact]
    public void SkyAndGroundHitsBothPolesAndAveragesTheHorizon()
    {
        var sky = new Vector3(1, .5f, .25f);
        var ground = new Vector3(.1f, .2f, .3f);
        var sh = IrradianceSH.SkyAndGround(sky, ground);

        AssertClose(sky, sh.Evaluate(Vector3.UnitY), 1e-5f);
        AssertClose(ground, sh.Evaluate(-Vector3.UnitY), 1e-5f);
        AssertClose((sky + ground) / 2, sh.Evaluate(Vector3.UnitX), 1e-5f);
    }

    [Fact]
    public void ProjectingUniformRadianceMatchesUniform()
    {
        var radiance = new Vector3(.1f, .2f, .3f);
        var sh = Project(_ => radiance);
        var expected = MathF.PI * radiance;

        foreach (var normal in TestNormals())
            AssertRelativeClose(expected, sh.Evaluate(normal), 1e-3f);
    }

    [Fact]
    public void ProjectingTwoHemispheresMatchesSkyAndGround()
    {
        var sky = new Vector3(1, .5f, .25f);
        var ground = new Vector3(.1f, .2f, .3f);
        var projected = Project(direction => (direction.Y > 0 ? sky : ground) / MathF.PI);
        var analytic = IrradianceSH.SkyAndGround(sky, ground);

        foreach (var normal in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX })
            AssertRelativeClose(analytic.Evaluate(normal), projected.Evaluate(normal), 1e-2f);
    }

    [Fact]
    public void RotateYMatchesProjectingTheRotatedLobe()
    {
        float elevation = MathF.PI / 6;
        const float azimuth = 1.1f;
        var direction = new Vector3(MathF.Cos(elevation), MathF.Sin(elevation), 0);
        var rotatedDirection = new Vector3(MathF.Cos(elevation) * MathF.Cos(azimuth),
            MathF.Sin(elevation), MathF.Cos(elevation) * MathF.Sin(azimuth));
        var original = Project(sample => Lobe(sample, direction));
        var projected = Project(sample => Lobe(sample, rotatedDirection));
        var rotated = original.RotateY(azimuth);

        for (int i = 0; i < IrradianceSH.CoefficientCount; i++)
            AssertClose(projected.Coefficient(i), rotated.Coefficient(i), 1e-4f);
    }

    [Fact]
    public void EvaluateNeverGoesNegative()
    {
        var sh = IrradianceSH.SkyAndGround(Vector3.One, Vector3.Zero)
            + IrradianceSH.Uniform(new Vector3(-.2f));

        Assert.Equal(Vector3.Zero, sh.Evaluate(-Vector3.UnitY));
    }

    [Fact]
    public void TheSpanConstructorRejectsTheWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new IrradianceSH(new Vector3[8]));
    }

    static Vector3[] TestNormals() =>
    [
        Vector3.UnitY,
        -Vector3.UnitY,
        Vector3.UnitX,
        -Vector3.UnitZ,
        Vector3.Normalize(Vector3.One),
    ];

    static Vector3 Lobe(Vector3 sample, Vector3 direction)
        => new(MathF.Pow(MathF.Max(Vector3.Dot(sample, direction), 0), 4));

    static IrradianceSH Project(Func<Vector3, Vector3> radiance)
    {
        const int latitudeCount = 32;
        const int azimuthCount = 64;
        const float solidAngle = 4 * MathF.PI / (latitudeCount * azimuthCount);
        var projection = new IrradianceProjection();

        // Midpoints are uniform in height and azimuth, so every sample covers the same solid angle.
        for (int latitude = 0; latitude < latitudeCount; latitude++)
        {
            float y = 1 - 2 * (latitude + .5f) / latitudeCount;
            float radius = MathF.Sqrt(1 - y * y);
            for (int azimuth = 0; azimuth < azimuthCount; azimuth++)
            {
                float angle = 2 * MathF.PI * (azimuth + .5f) / azimuthCount;
                var direction = new Vector3(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
                projection.Add(direction, radiance(direction), solidAngle);
            }
        }

        return projection.ToIrradiance();
    }

    static void AssertRelativeClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        AssertClose(Vector3.One, actual / expected, tolerance);
    }

    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }
}
