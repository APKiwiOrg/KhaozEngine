using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereScatteringTests
{
    [Fact]
    public void Vacuum_HasNoScatteredRadiance()
    {
        var vacuum = Vacuum();
        var v = new AtmosphereModel(vacuum);
        Assert.Equal(Vector3.Zero, v.SkyRadiance(0, Vector3.UnitY, Vector3.UnitY));
        Assert.Equal(Vector3.Zero, v.MultiScattering(0, 1));
        Assert.Equal(Vector3.Zero, v.SingleScattering(new Vector3(0, 6360, 0), Vector3.UnitY, Vector3.UnitY));
        Assert.Equal(Vector3.One, v.Tables.SampleTransmittance(6360, 1));
    }

    [Fact]
    public void Scattering_IsPositiveAndForwardMieIsBrighter()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var sky = m.SkyRadiance(.002f, Vector3.UnitY, Vector3.UnitY);
        AssertFiniteNonNegative(sky);
        Assert.True(sky.Z > 0);
        var multi = m.MultiScattering(1, .5f);
        AssertFiniteNonNegative(multi);
        Assert.True(multi.X > 0);
        Assert.True(AtmosphereModel.MiePhase(1, .8f) > AtmosphereModel.MiePhase(-1, .8f));

        var single = m.SingleScattering(new Vector3(0, 6360.002f, 0), Vector3.UnitY, Vector3.UnitY);
        Assert.True(sky.X > single.X && sky.Y > single.Y && sky.Z > single.Z);
    }

    [Fact]
    public void IndependentMoonAzimuth_IsNotFolded()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var moon = Vector3.Normalize(new Vector3(1, .1f, 0));
        var mirrored = Vector3.Normalize(new Vector3(-1, .1f, 0));
        Assert.True(m.SkyRadiance(.002f, moon, moon).Length() >
            m.SkyRadiance(.002f, mirrored, moon).Length() * 1.2f);
    }

    [Fact]
    public void SingleScattering_MatchesAnIndependentHomogeneousVerticalColumn()
    {
        var settings = Vacuum();
        settings.PlanetRadiusKm = 100;
        settings.AtmosphereHeightKm = 10;
        settings.RayleighScaleHeightKm = float.MaxValue;
        settings.RayleighScatteringPerKm = new Vector3(.1f, .2f, .3f);
        var m = new AtmosphereModel(settings);

        // In a homogeneous column, view and source transmission multiply to exp(-sigma * 10).
        // Integrating the source over 10 km gives sigma * 10 * exp(-sigma * 10) * 3 / (8 * pi).
        var expected = new Vector3(.04391237f, .03230892f, .01782868f);
        var actual = m.SingleScattering(new Vector3(0, 100, 0), Vector3.UnitY, Vector3.UnitY);
        Assert.InRange(Vector3.Distance(expected, actual), 0, .0002f);
    }

    [Fact]
    public void RayleighPhase_HasTheNormalizedAnalyticValues()
    {
        Assert.InRange(Math.Abs(AtmosphereModel.RayleighPhase(0) - 3 / (16 * Math.PI)), 0, 1e-6);
        Assert.InRange(Math.Abs(AtmosphereModel.RayleighPhase(1) - 3 / (8 * Math.PI)), 0, 1e-6);
        Assert.InRange(Math.Abs(PhaseIntegral(AtmosphereModel.RayleighPhase) - 1), 0, .002);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.8f)]
    [InlineData(-.8f)]
    public void MiePhase_IntegratesToOne(float g)
        => Assert.InRange(Math.Abs(PhaseIntegral(mu => AtmosphereModel.MiePhase(mu, g)) - 1), 0, .002);

    [Fact]
    public void SegmentIntegral_HandlesZeroMixedAndTinyExtinction()
    {
        var source = new Vector3(2, 3, 4);
        Assert.Equal(source * 2, AtmosphereModel.SegmentIntegral(source, Vector3.Zero, 2));
        var mixed = AtmosphereModel.SegmentIntegral(source, new Vector3(0, .5f, 1), 2);
        Assert.InRange(Vector3.Distance(new Vector3(4, 3.7927234f, 3.458659f), mixed), 0, 1e-6f);
        Assert.InRange(Vector3.Distance(source * 2,
            AtmosphereModel.SegmentIntegral(source, new Vector3(1e-12f), 2)), 0, 1e-6f);
    }

    [Fact]
    public void PlanetShadow_BlocksTheDirectSourceAndViewStopsAtGround()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        Assert.Equal(Vector3.Zero,
            m.SingleScattering(new Vector3(0, 6360.002f, 0), Vector3.UnitY, -Vector3.UnitY));
        Assert.Equal(Vector3.Zero,
            m.SingleScattering(new Vector3(0, 6359, 0), Vector3.UnitY, Vector3.UnitY));
        Assert.Equal(Vector3.Zero,
            m.SingleScattering(new Vector3(0, 6461, 0), Vector3.UnitY, Vector3.UnitY));

        var vacuum = Vacuum();
        vacuum.GroundAlbedo = .3f;
        var v = new AtmosphereModel(vacuum);
        var ground = v.SingleScattering(new Vector3(0, 6361, 0), -Vector3.UnitY, Vector3.UnitY);
        Assert.InRange(Vector3.Distance(new Vector3(.09549297f), ground), 0, 1e-6f);
        Assert.True(v.MultiScattering(1, 1).X > 0);
    }

    [Fact]
    public void Tables_BilinearlySampleTransportAtBoundariesAndBetweenTexels()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var tables = m.Tables;
        Assert.Equal(256 * 64, tables.Transmittance.Length);
        Assert.Equal(32 * 32, tables.MultiScattering.Length);
        Assert.InRange(Vector3.Distance(new Vector3(.940325f, .867331f, .762439f),
            tables.SampleTransmittance(6360, 1)), 0, .002f);
        Assert.Equal(Vector3.One, tables.SampleTransmittance(6460, 1));

        var uv = new Vector2(80f / 256, 20f / 64);
        var parameters = AtmosphereLutMapping.TransmittanceParameters(uv, 6360, 6460);
        var transmittance = tables.Transmittance;
        var expected = (transmittance[19 * 256 + 79] + transmittance[19 * 256 + 80]
            + transmittance[20 * 256 + 79] + transmittance[20 * 256 + 80]) * .25f;
        Assert.InRange(Vector3.Distance(expected, tables.SampleTransmittance(parameters.X, parameters.Y)), 0, 1e-5f);

        var multi = tables.MultiScattering;
        var multiExpected = (multi[10 * 32 + 8] + multi[10 * 32 + 9]
            + multi[11 * 32 + 8] + multi[11 * 32 + 9]) * .25f;
        Assert.InRange(Vector3.Distance(multiExpected, tables.SampleMulti(100 * 10.5f / 31, 2 * 8.5f / 31 - 1)), 0, 1e-6f);
        foreach (var value in transmittance) AssertFiniteNonNegative(value);
        foreach (var value in multi) AssertFiniteNonNegative(value);
    }

    [Fact]
    public void SettingsUpdates_RebuildOnlyAfterAnAcceptedChange()
    {
        var settings = AtmosphereSettings.Earth();
        var m = new AtmosphereModel(settings);
        Assert.Equal(0, m.BuildCount);
        var original = m.MultiScattering(1, .5f);
        Assert.Equal(1, m.BuildCount);
        var originalTables = m.Tables;
        Assert.False(m.UpdateSettings(settings.Clone()));
        Assert.Same(originalTables, m.Tables);
        settings.RayleighScatteringPerKm *= 2;
        Assert.Equal(original, m.MultiScattering(1, .5f));
        Assert.Equal(1, m.BuildCount);
        Assert.True(m.UpdateSettings(settings));
        Assert.Equal(1, m.BuildCount);
        Assert.NotEqual(original, m.MultiScattering(1, .5f));
        Assert.Equal(2, m.BuildCount);
        Assert.NotSame(originalTables, m.Tables);
        Assert.Equal(original, originalTables.SampleMulti(1, .5f));
        Assert.False(m.UpdateSettings(settings.Clone()));
        settings.MieAnisotropy = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => m.UpdateSettings(settings));
        _ = m.SkyRadiance(1, Vector3.UnitY, Vector3.UnitY);
        Assert.Equal(2, m.BuildCount);
    }

    [Fact]
    public void Scattering_RejectsInvalidInputsBeforeBuildingAndNormalizesDirections()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SkyRadiance(float.NaN, Vector3.UnitY, Vector3.UnitY));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.MultiScattering(0, float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => m.SkyRadiance(0, Vector3.Zero, Vector3.UnitY));
        Assert.Throws<ArgumentException>(() => m.SkyRadiance(0, Vector3.UnitY, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => m.SingleScattering(new Vector3(float.NaN), Vector3.UnitY, Vector3.UnitY));
        Assert.Equal(0, m.BuildCount);
        var expected = m.SkyRadiance(1, Vector3.UnitY, Vector3.UnitY);
        Assert.Equal(expected, m.SkyRadiance(1, Vector3.UnitY * 10, Vector3.UnitY * float.MaxValue));
    }

    static AtmosphereSettings Vacuum()
    {
        var vacuum = AtmosphereSettings.Earth();
        vacuum.RayleighScatteringPerKm = vacuum.OzoneAbsorptionPerKm = Vector3.Zero;
        vacuum.MieScatteringPerKm = vacuum.MieExtinctionPerKm = vacuum.GroundAlbedo = 0;
        return vacuum;
    }

    static double PhaseIntegral(Func<float, float> phase)
    {
        const int cells = 16384;
        double sum = 0;
        for (int i = 0; i < cells; i++) sum += phase((float)(-1 + 2 * (i + .5) / cells));
        return sum * (4 * Math.PI / cells);
    }

    static void AssertFiniteNonNegative(Vector3 value)
    {
        Assert.True(float.IsFinite(value.X) && value.X >= 0);
        Assert.True(float.IsFinite(value.Y) && value.Y >= 0);
        Assert.True(float.IsFinite(value.Z) && value.Z >= 0);
    }
}
