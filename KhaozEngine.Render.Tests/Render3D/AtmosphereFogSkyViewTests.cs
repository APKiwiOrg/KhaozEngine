using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereFogSkyViewTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(225)]
    [InlineData(315)]
    public void FinalCellFog_MatchesSkyOnlyTexelsAcrossAzimuthQuadrants(double azimuthDegrees)
    {
        var scene = new FogScene(azimuthDegrees);
        // Include the reported pair explicitly, then cover the entire final easing cell.
        foreach (double degrees in new[] { -.001, 0, .001, .003 })
            AssertSkyOnly(scene, degrees * Math.PI / 180);
        for (int i = 0; i <= 256; i++)
            AssertSkyOnly(scene, scene.CellAngle * i / 256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(225)]
    [InlineData(315)]
    public void FinalCellFog_HasContinuousColourAndSlopeAcrossAzimuthQuadrants(double azimuthDegrees)
    {
        var scene = new FogScene(azimuthDegrees);
        var first = scene.Sample(.001 * Math.PI / 180);
        var second = scene.Sample(.003 * Math.PI / 180);
        float reportedJump = Vector3.Distance(first, second) / second.Length();
        Console.WriteLine($"FOG_FINAL_CELL azimuth={azimuthDegrees} first={first} second={second} relativeJump={reportedJump}");
        Assert.True(reportedJump < .001f, $"The reported pair jumps by {reportedJump} at azimuth {azimuthDegrees}.");

        double step = scene.CellAngle / 256;
        var previous = scene.Sample(-step);
        var previousSlope = Vector3.Zero;
        for (int i = 0; i <= 256; i++)
        {
            var next = scene.Sample(i * step);
            float scale = MathF.Max(next.Length(), .01f);
            float relativeChange = Vector3.Distance(previous, next) / scale;
            var slope = (next - previous) / ((float)step * scale);
            Assert.True(relativeChange < 75 * step,
                $"Fog value jumps by {relativeChange} at cell sample {i}, azimuth {azimuthDegrees}.");
            Assert.True(Vector3.Distance(previousSlope, slope) < 3,
                $"Fog slope jumps from {previousSlope} to {slope} at cell sample {i}, azimuth {azimuthDegrees}.");
            if (i <= 1) Assert.InRange(slope.Length(), 0, .01f);
            previous = next;
            previousSlope = slope;
        }
    }

    static void AssertSkyOnly(FogScene scene, double aboveHorizon)
    {
        var expected = scene.SkyOnlyReference(aboveHorizon);
        var actual = scene.Sample(aboveHorizon);
        float error = Vector3.Distance(expected, actual) / expected.Length();
        Assert.True(error < 2e-5f,
            $"Fog must sample sky-only LUT radiance at azimuth {scene.AzimuthDegrees}, offset {aboveHorizon * 180 / Math.PI} degrees. Expected {expected}, actual {actual}, relative error {error}.");
    }

    sealed class FogScene
    {
        readonly AtmosphereSettings _settings = AtmosphereSettings.Earth();
        readonly AtmosphereModel _model;
        readonly AtmosphereFrame _frame;
        readonly double _azimuth;
        readonly double _radius;
        readonly double _horizon;
        readonly Vector3 _eye = new(0, 2, 0);

        internal double AzimuthDegrees { get; }
        internal double CellAngle { get; }

        internal FogScene(double azimuthDegrees)
        {
            AzimuthDegrees = azimuthDegrees;
            _azimuth = azimuthDegrees * Math.PI / 180;
            _settings.Enabled = true;
            _settings.HazeDensityPerKm = 100;
            _settings.HazeScaleHeightKm = 100;
            _model = new AtmosphereModel(_settings);
            var sun = Direction(2 * Math.PI / 180, _azimuth);
            _frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
            _radius = _settings.PlanetRadiusKm + (double)_settings.HeightKm(2);
            _horizon = -Math.Acos(_settings.PlanetRadiusKm / _radius);
            CellAngle = (Math.PI / 2 - _horizon) / (53 * 53);
        }

        internal Vector3 Sample(double aboveHorizon)
            => AtmosphereFog.Shade(_settings, _model, _frame, Vector3.Zero, _eye,
                _eye + Direction(_horizon + aboveHorizon, _azimuth) * 100000, Vector3.Zero);

        internal Vector3 SkyOnlyReference(double aboveHorizon)
        {
            // Independent texture contract: 192 periodic columns, sky rows 0 through 53,
            // texel-centred bilinear filtering and the specified final-cell polynomial.
            double t = Math.Max(aboveHorizon, 0) / CellAngle;
            double weight = t < 1 ? t * t * t * (3.5 - 2.5 * t) : Math.Sqrt(t);
            double x = AzimuthDegrees / 360 * 192 - .5;
            int left = (int)Math.Floor(x);
            float fx = (float)(x - left), fy = (float)(1 - weight);
            var lower = Vector3.Lerp(Texel(left, 52), Texel(left + 1, 52), fx);
            var upper = Vector3.Lerp(Texel(left, 53), Texel(left + 1, 53), fx);
            var radiance = Vector3.Lerp(lower, upper, fy);
            return Vector3.Max(radiance * (_settings.SunIlluminance * MathF.PI),
                _settings.NightSkyFloor * _settings.NightTint);
        }

        Vector3 Texel(int column, int row)
        {
            double azimuth = 2 * Math.PI * (column + .5) / 192;
            double elevation = _horizon + CellAngle * (53 - row) * (53 - row);
            // The independent oracle integrates a double ray just inside the visible sky.
            // This negligible angular margin avoids retesting a numerically rounded tangent.
            elevation += 1e-10;
            double horizontal = Math.Cos(elevation);
            var view = (horizontal * Math.Cos(azimuth), Math.Sin(elevation), horizontal * Math.Sin(azimuth));
            double length = Math.Sqrt((double)_frame.SunDirection.X * _frame.SunDirection.X
                + (double)_frame.SunDirection.Y * _frame.SunDirection.Y + (double)_frame.SunDirection.Z * _frame.SunDirection.Z);
            var light = (_frame.SunDirection.X / length, _frame.SunDirection.Y / length, _frame.SunDirection.Z / length);
            return _model.IntegrateScattering((0, _radius, 0), view, light, _model.Tables, 32,
                isotropic: false, includeMultiScattering: true).Radiance;
        }

        static Vector3 Direction(double elevation, double azimuth)
            => new((float)(Math.Cos(elevation) * Math.Cos(azimuth)), (float)Math.Sin(elevation),
                (float)(Math.Cos(elevation) * Math.Sin(azimuth)));
    }
}
