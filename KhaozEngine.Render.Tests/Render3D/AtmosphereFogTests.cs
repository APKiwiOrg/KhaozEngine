using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereFogTests
{
    static readonly AtmosphereFrame Noon = new(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);

    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    [InlineData(1f)]
    public void BlendModes_DoNotInjectAdditiveHaze(float additivity)
    {
        var t = new Vector3(.25f);
        var haze = new Vector3(.8f, .6f, .4f);
        Assert.Equal(new Vector3(.25f), AtmosphereFog.Compose(Vector3.One, t, haze, true));
        var expected = new Vector3(.125f) + new Vector3(.6f, .45f, .3f) * (1 - additivity);
        Close(expected, AtmosphereFog.ComposePremultiplied(new Vector3(.5f), 1, t, haze, additivity));
        Close(expected * .5f, AtmosphereFog.ComposePremultiplied(new Vector3(.25f), .5f, t, haze, additivity));
        Assert.Equal(Vector3.Zero, AtmosphereFog.ComposePremultiplied(Vector3.Zero, 0, t, haze, additivity));
        Assert.Equal(new Vector3(.5f), AtmosphereFog.ComposePremultiplied(new Vector3(.5f), 1,
            Vector3.One, haze, additivity));
        Assert.Equal(haze * (1 - additivity), AtmosphereFog.ComposePremultiplied(Vector3.One, 1,
            Vector3.Zero, haze, additivity));
        Assert.Equal(Vector3.Zero, AtmosphereFog.ComposePremultiplied(Vector3.Zero, 1, t, haze, 1));
    }

    [Theory]
    [InlineData(TonemapOperator.AcesFilmic, 0f)]
    [InlineData(TonemapOperator.AcesFilmic, .75f)]
    [InlineData(TonemapOperator.Reinhard, 1f)]
    [InlineData(TonemapOperator.Clamp, .5f)]
    public void DisplayBillboard_UsesExistingTonemap(TonemapOperator op, float chroma)
    {
        var haze = new Vector3(.8f, .6f, .4f);
        var hdr = new HdrSettings { Enabled = true, Exposure = 1.7f, Operator = op, ChromaPreservation = chroma };
        var mapped = TonemapMath.Map(haze.X, haze.Y, haze.Z, hdr.Exposure, (int)op, chroma);
        var displayHaze = new Vector3(mapped.R, mapped.G, mapped.B);
        Close(displayHaze, AtmosphereFog.ComposeDisplay(Vector3.Zero, Vector3.Zero, haze, hdr));
        var rgb = new Vector3(.2f, .3f, .4f);
        Close(rgb * .25f + displayHaze * .75f,
            AtmosphereFog.ComposeDisplay(rgb, new Vector3(.25f), haze, hdr));
        Assert.Equal(rgb, AtmosphereFog.ComposeDisplay(rgb, Vector3.One, haze, hdr));
        Assert.Equal(rgb * .25f, AtmosphereFog.ComposeDisplay(rgb, new Vector3(.25f), haze, hdr, true));
        Assert.Equal(Vector3.Zero, AtmosphereFog.ComposeDisplay(Vector3.Zero, new Vector3(.25f), haze, hdr, true));
        hdr.Enabled = false;
        Assert.Equal(new Vector3(1, .6f, 0), AtmosphereFog.ComposeDisplay(Vector3.Zero, Vector3.Zero,
            new Vector3(2, .6f, -1), hdr));
    }

    [Fact]
    public void Fog_HasExactSegmentLimits()
    {
        var s = Earth();
        Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, 0, 0, 0));
        Assert.Equal(Vector3.Zero, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
        s.AerialDistanceScale = 0;
        Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
        Assert.Equal(0d, AtmosphereFog.OpticalDepthTerm(0, 2, 3, 8));
        Assert.Equal(1d, AtmosphereFog.OpticalDepthTerm(1, 0, 0, 8), 10);
        Assert.Equal(8 * (1 - Math.Exp(-1d / 8)), AtmosphereFog.OpticalDepthTerm(1, 0, 1, 8), 10);
        Assert.InRange(Math.Abs(AtmosphereFog.OpticalDepthTerm(1, 2, 2 + 1e-9, 8) - Math.Exp(-2d / 8)), 0, 1e-8);
        var haze = new Vector3(.2f, .3f, .4f);
        Assert.Equal(haze, AtmosphereFog.Compose(Vector3.One, Vector3.Zero, haze));
        Assert.Equal(Vector3.One, AtmosphereFog.Compose(Vector3.One, Vector3.One, haze));
        Assert.Equal(Vector3.Zero, AtmosphereFog.Compose(Vector3.One, Vector3.Zero, haze, additive: true));
        Assert.Equal(new Vector3(.6f, .65f, .7f), AtmosphereFog.Compose(Vector3.One, new Vector3(.5f), haze));
    }

    [Theory]
    [InlineData(3, 0, 12, 8)]
    [InlineData(3, 12, 0, 8)]
    [InlineData(5, 2, 2.0001, 8)]
    [InlineData(5, 2, 1.9999, 8)]
    [InlineData(5, 2, 2.001, 8)]
    [InlineData(5, 2, 1.999, 8)]
    public void OpticalDepth_MatchesIndependentQuadrature(double distance, double start, double end, double scaleHeight)
    {
        // Composite Simpson integration of density, independent of the analytic fog helpers.
        const int intervals = 4096;
        double sum = 0;
        for (int i = 0; i <= intervals; i++)
        {
            double height = start + (end - start) * i / intervals;
            int weight = i == 0 || i == intervals ? 1 : i % 2 == 0 ? 2 : 4;
            sum += weight * Math.Exp(-height / scaleHeight);
        }
        double expected = distance * sum / (3 * intervals);
        Assert.InRange(Math.Abs(expected - AtmosphereFog.OpticalDepthTerm(distance, start, end, scaleHeight)), 0, 1e-10);
    }

    [Fact]
    public void DescendingRays_AvoidOverflowAndRemainReciprocal()
    {
        Assert.Equal(1d, AtmosphereFog.OpticalDepthTerm(1000, 1000, 0, 1), 10);
        Assert.Equal(AtmosphereFog.OpticalDepthTerm(1000, 0, 1000, 1),
            AtmosphereFog.OpticalDepthTerm(1000, 1000, 0, 1));
        var s = Earth();
        Assert.Equal(AtmosphereFog.Transmittance(s, 1e6f, 0, 1e6f),
            AtmosphereFog.Transmittance(s, 1e6f, 1e6f, 0));
        Assert.Equal(Vector3.Zero, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 1e6f, 1e6f));
    }

    [Fact]
    public void InfiniteVacuum_AndZeroChannelsStayTransparent()
    {
        var s = Earth();
        s.RayleighScatteringPerKm = Vector3.Zero;
        s.MieScatteringPerKm = s.MieExtinctionPerKm = 0;
        Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
        s.RayleighScatteringPerKm = new Vector3(0, .1f, 0);
        Assert.Equal(new Vector3(1, 0, 1), AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
        s.HazeDensityPerKm = .1f;
        Assert.Equal(Vector3.Zero, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
    }

    [Fact]
    public void Transmittance_UsesRayleighMieExtinctionAndGreyHazeWithoutOzone()
    {
        var s = Earth();
        s.HazeDensityPerKm = .02f;
        var expected = new Vector3((float)Math.Exp(-(.005802 + .004440 + .02)),
            (float)Math.Exp(-(.013558 + .004440 + .02)), (float)Math.Exp(-(.0331 + .004440 + .02)));
        Close(expected, AtmosphereFog.Transmittance(s, 1000, 0, 0));
        s.RayleighScatteringPerKm = Vector3.Zero;
        s.MieScatteringPerKm = s.MieExtinctionPerKm = s.HazeDensityPerKm = 0;
        s.OzoneAbsorptionPerKm = new Vector3(100);
        Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, 1e9f, 25000, 25000));
    }

    [Fact]
    public void AerialScale_TransformsBothDistanceAndEndpointHeights()
    {
        var s = Earth();
        s.AerialDistanceScale = 20;
        s.HazeDensityPerKm = .1f;
        var actual = AtmosphereFog.Transmittance(s, 1500, 100, 300);
        s.AerialDistanceScale = 1;
        Assert.Equal(AtmosphereFog.Transmittance(s, 30000, 2000, 6000), actual);
        s.WorldUnitsPerKilometre = 1;
        Assert.Equal(actual, AtmosphereFog.Transmittance(s, 30, 2, 6));
    }

    [Fact]
    public void UndergroundHeights_ClampEndpointsBeforeInterpolation()
    {
        var s = Earth();
        Assert.Equal(AtmosphereFog.Transmittance(s, 3000, 0, 0),
            AtmosphereFog.Transmittance(s, 3000, -1000, -2000));
        Assert.Equal(AtmosphereFog.Transmittance(s, 3000, 0, 1000),
            AtmosphereFog.Transmittance(s, 3000, -1000, 1000));
    }

    [Fact]
    public void Fog_ConvergesToHorizon()
    {
        var s = Earth();
        var model = new AtmosphereModel(s);
        Close(model.SkyViewValue(0, new Vector2(0, 53.5f / 108), Noon),
            AtmosphereFog.Shade(s, model, Noon, Vector3.One, Vector3.Zero, new Vector3(1e9f, 0, 0), Vector3.Zero));
    }

    [Fact]
    public void RebaseY_LeavesFogIdentical()
    {
        var s = Earth();
        s.AerialDistanceScale = 20;
        var model = new AtmosphereModel(s);
        var eye = new Vector3(100000, 2000, 100000);
        var fragment = new Vector3(101000, 2000, 100000);
        var origin = new Vector3(100000, 1900, 100000);
        var colour = new Vector3(.2f, .3f, .4f);
        Close(AtmosphereFog.Shade(s, model, Noon, colour, eye, fragment, Vector3.Zero),
            AtmosphereFog.Shade(s, model, Noon, colour, eye - origin, fragment - origin, origin));
    }

    [Theory]
    [InlineData(0, 3, 4)]
    [InlineData(2000, 3, 4)]
    [InlineData(2000, 0, 0)]
    [InlineData(-100, 3, 4)]
    public void DownwardFog_ClampsToGeometricHorizonAndPreservesAzimuth(float heightWorld, float x, float z)
    {
        var s = Earth();
        s.AerialDistanceScale = 20;
        s.HazeDensityPerKm = 100;
        var model = new AtmosphereModel(s);
        float heightKm = Math.Max(heightWorld, 0) / 1000;
        double azimuth = x == 0 && z == 0 ? 0 : Math.Atan2(z, x) / (2 * Math.PI);
        var horizonUv = new Vector2((float)(azimuth - Math.Floor(azimuth)), 53.5f / 108);
        var eye = new Vector3(0, heightWorld, 0);
        var displacement = new Vector3(x, -10, z) * 1e6f;
        Close(model.SkyViewValue(heightKm, horizonUv, Noon),
            AtmosphereFog.Shade(s, model, Noon, Vector3.One, eye, eye + displacement, Vector3.Zero));
    }

    [Fact]
    public void UpwardFog_UsesUnclampedSkyAndUnscaledPhysicalCameraAltitude()
    {
        var s = Earth();
        s.AerialDistanceScale = 20;
        s.RayleighScatteringPerKm = new Vector3(10);
        var model = new AtmosphereModel(s);
        var eye = new Vector3(0, 2000, 0);
        var displacement = new Vector3(3000, 4000, 0);
        var transmission = AtmosphereFog.Transmittance(s, 5000, 2000, 6000);
        var skyUv = AtmosphereLutMapping.SkyUv(new Vector3(.6f, .8f, 0), 6362, 6360);
        var sky = model.SkyViewValue(2, skyUv, Noon);
        var expected = new Vector3(.2f, .3f, .4f) * transmission + sky * (Vector3.One - transmission);
        Close(expected, AtmosphereFog.Shade(s, model, Noon, new Vector3(.2f, .3f, .4f),
            eye, eye + displacement, Vector3.Zero));
    }

    [Fact]
    public void DisabledZeroScaleAndZeroLength_ReturnInputAndAdditiveOnlyAttenuates()
    {
        var s = Earth();
        var model = new AtmosphereModel(s);
        var colour = new Vector3(.2f, .3f, .4f);
        var eye = new Vector3(0, 100, 0);
        var fragment = eye + new Vector3(1000, 0, 0);
        Assert.Equal(colour, AtmosphereFog.Shade(s, model, Noon, colour, eye, eye, Vector3.Zero));
        Assert.Equal(colour * AtmosphereFog.Transmittance(s, 1000, 100, 100),
            AtmosphereFog.Shade(s, model, Noon, colour, eye, fragment, Vector3.Zero, additive: true));
        s.AerialDistanceScale = 0;
        Assert.Equal(colour, AtmosphereFog.Shade(s, model, Noon, colour, eye, fragment, Vector3.Zero));
        s.AerialDistanceScale = 1;
        s.Enabled = false;
        Assert.Equal(colour, AtmosphereFog.Shade(s, model, Noon, colour, eye, fragment, Vector3.Zero));
        Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public void NegativeOrInvalidDistances_AreRejected(float distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.Transmittance(Earth(), distance, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.OpticalDepthTerm(distance, 0, 0, 8));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteHeights_AreRejected(float height)
    {
        var s = Earth();
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.Transmittance(s, 1, height, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.Transmittance(s, 1, 0, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.OpticalDepthTerm(1, height, 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.OpticalDepthTerm(1, 0, height, 8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScaleHeights_AreRejected(double scaleHeight)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.OpticalDepthTerm(1, 0, 0, scaleHeight));

    [Fact]
    public void InvalidSettingsAndPositions_AreRejected()
    {
        var s = Earth();
        var model = new AtmosphereModel(s);
        Assert.Throws<ArgumentNullException>(() => AtmosphereFog.Transmittance(null!, 1, 0, 0));
        var invalid = new Vector3(float.NaN, 0, 0);
        Assert.Throws<ArgumentException>(() => AtmosphereFog.Shade(s, model, Noon, Vector3.One,
            invalid, Vector3.One, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => AtmosphereFog.Shade(s, model, Noon, Vector3.One,
            Vector3.Zero, invalid, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => AtmosphereFog.Shade(s, model, Noon, Vector3.One,
            Vector3.Zero, Vector3.One, invalid));
        s.WorldUnitsPerKilometre = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.Transmittance(s, 1, 0, 0));
    }

    static AtmosphereSettings Earth()
    {
        var settings = AtmosphereSettings.Earth();
        settings.Enabled = true;
        settings.AerialDistanceScale = 1;
        return settings;
    }

    static void Close(Vector3 expected, Vector3 actual)
    {
        Assert.True(float.IsFinite(actual.X) && float.IsFinite(actual.Y) && float.IsFinite(actual.Z));
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-6f);
    }
}
