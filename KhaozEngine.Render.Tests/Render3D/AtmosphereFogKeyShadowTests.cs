using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Key-only fog in-scatter and equal-weight shadow taps, KhaozEngine #1411.</summary>
public sealed class AtmosphereFogKeyShadowTests
{
    [Fact]
    public void FullVisibility_PreservesShadeExactlyAcrossTheHorizon()
    {
        var settings = Settings();
        var model = new AtmosphereModel(settings);
        var frame = Frame();
        var eye = new Vector3(0, 2, 0);
        const float heightKm = .002f;
        double radius = settings.PlanetRadiusKm + (double)heightKm;
        var scattering = AtmosphereFogScattering.Evaluate(settings, model, frame, heightKm);
        foreach (float azimuth in new[] { 0f, MathF.PI / 2, MathF.PI })
            foreach (float elevation in new[] { -.8f, -.02f, 0f, .1f, .8f })
            {
                var view = Direction(azimuth, elevation);
                var original = AtmosphereFog.Shade(settings, model, frame, Vector3.Zero, eye,
                    eye + view * 10000, Vector3.Zero);
                var actualView = Vector3.Normalize(eye + view * 10000 - eye);
                var skyUv = AtmosphereFogSampling.SkyUv(actualView, radius, settings.PlanetRadiusKm);
                var phase = actualView.Y >= 0 ? Vector3.One : scattering.Ratio(actualView, radius, settings.PlanetRadiusKm);
                var haze = model.SkyViewValue(heightKm, skyUv, frame, phase);
                var transmission = AtmosphereFog.Transmittance(settings, 10000, eye.Y, (eye + view * 10000).Y);
                var unoccluded = haze * (Vector3.One - transmission);
                Assert.InRange(Vector3.Distance(unoccluded, original), 0, unoccluded.Length() * 1e-6f);
                Assert.Equal(original, AtmosphereFog.Shade(settings, model, frame, Vector3.Zero, eye,
                    eye + view * 10000, Vector3.Zero, keyVisibility: 1));
            }
    }

    [Fact]
    public void Occlusion_ScalesOnlyTheKeyShareAndLeavesAmbientFog()
    {
        var settings = Settings();
        var model = new AtmosphereModel(settings);
        var frame = Frame();
        var eye = new Vector3(0, 2, 0);
        Vector3 Haze(float azimuth, float visibility, AtmosphereFrame state) => AtmosphereFog.Shade(settings,
            model, state, Vector3.Zero, eye, eye + Direction(azimuth, -.02f) * 10000, Vector3.Zero,
            keyVisibility: visibility);
        var open = Haze(0, 1, frame);
        var blocked = Haze(0, 0, frame);
        var half = Haze(0, .5f, frame);
        Assert.InRange(Vector3.Distance((open + blocked) * .5f, half), 0, open.Length() * 1e-6f);
        Assert.True(blocked.X > 0 && blocked.Y > 0 && blocked.Z > 0);
        Assert.True(Luma(blocked) / Luma(open) < Luma(Haze(MathF.PI, 0, frame)) / Luma(Haze(MathF.PI, 1, frame)));
        Assert.Equal(Haze(0, 1, frame with { KeySource = KeyLightSource.None }),
            Haze(0, 0, frame with { KeySource = KeyLightSource.None }));
    }

    [Theory]
    [InlineData(KeyLightSource.Sun, .5f)]
    [InlineData(KeyLightSource.Moon, .625f)]
    [InlineData(KeyLightSource.None, 1f)]
    public void KeyOcclusion_KeepsTheOtherBodyAndMultipleScattering(KeyLightSource source, float expected)
    {
        // At mu=0 this Rayleigh coefficient yields unit phase. Total is 2 + 3*.5 + .5 = 4.
        var scattering = new AtmosphereFogScattering(new Vector3(16 * MathF.PI / 3), 0, new Vector3(.5f), .8f,
            Vector3.UnitX, new Vector3(2), -Vector3.UnitX, new Vector3(3), .5f, source);
        var occlusion = scattering.KeyOcclusion(Vector3.UnitY, 6360, 6360, 0);
        Assert.InRange(Vector3.Distance(new Vector3(expected), occlusion), 0, 1e-6f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    public void Shade_RejectsInvalidVisibility(float visibility)
    {
        var settings = Settings();
        var model = new AtmosphereModel(settings);
        var frame = Frame();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereFog.Shade(settings, model, frame,
            Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector3.Zero, keyVisibility: visibility));
        Assert.Equal("keyVisibility", error.ParamName);
        foreach (float boundary in new[] { 0f, 1f })
            AtmosphereFog.Shade(settings, model, frame, Vector3.Zero, Vector3.Zero, Vector3.UnitX,
                Vector3.Zero, keyVisibility: boundary);
    }

    [Theory]
    [InlineData(0, .2)]
    [InlineData(80, .2)]
    [InlineData(10000, .2)]
    [InlineData(0, .9)]
    [InlineData(80, .9)]
    [InlineData(0, 1)]
    [InlineData(80, 1)]
    [InlineData(0, .9999995)]
    public void TapDistance_PlacesOrderedTapsAtEqualInScatterShares(double onset, double transmission)
    {
        const double distance = 1200;
        double previous = 0;
        for (int i = 0; i < 8; i++)
        {
            double u = (i + .5) / 8;
            double tap = AtmosphereFogKeyShadow.TapDistance(distance, onset, transmission, u);
            Assert.InRange(tap, 0, distance);
            Assert.True(tap > previous);
            previous = tap;
            // Integrate the grey model independently at the returned distance, including the onset's inverse.
            double fraction = (tap * tap / (tap + onset)) / (distance * distance / (distance + onset));
            double share = transmission == 1 ? fraction : (1 - Math.Exp(Math.Log(transmission) * fraction)) / (1 - transmission);
            Assert.InRange(Math.Abs(share - u), 0, 1e-6);
            if (onset == 0 && transmission >= .999999)
                Assert.InRange(Math.Abs(tap - u * distance), 0, 1e-9);
        }
    }

    [Theory]
    [InlineData(0, 1f, KeyLightSource.Sun, 0f, 1f)]
    [InlineData(8, 0f, KeyLightSource.Sun, 0f, 1f)]
    [InlineData(8, 1f, KeyLightSource.None, 0f, 1f)]
    [InlineData(8, .6f, KeyLightSource.Sun, 0f, .4f)]
    [InlineData(8, .9f, KeyLightSource.Sun, 1f, 1f)]
    public void Visibility_RespectsDisablementStrengthAndExactClearTaps(int samples, float strength,
        KeyLightSource key, float depth, float expected)
    {
        float actual = AtmosphereFogKeyShadow.Visibility(Vector3.Zero, new Vector3(0, 0, .8f), new Vector3(.5f),
            0, samples, key, new[] { Matrix4x4.Identity }, new Vector4(1, strength, 0, 0),
            new Vector4(.001f, 1, .1f, 0), _ => depth);
        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void Visibility_UsesAtlasColumnsAndFadesOnlyShadowedOutermostTaps()
    {
        var matrices = new[] { Matrix4x4.CreateTranslation(4, 0, 0), Matrix4x4.Identity };
        float Visibility(float depth, float border, Vector3 camera) => AtmosphereFogKeyShadow.Visibility(camera,
            camera + new Vector3(0, 0, .8f), new Vector3(.5f), 0, 8, KeyLightSource.Sun, matrices,
            new Vector4(2, 1, 0, 0), new Vector4(.001f, 1, border, 0), uv => uv.X > .5f ? depth : 1);
        Assert.Equal(0f, Visibility(0, .1f, Vector3.Zero));
        Assert.Equal(.5f, Visibility(0, 1, Vector3.Zero), 6);
        Assert.Equal(1f, Visibility(1, 1, Vector3.Zero));
        Assert.Equal(1f, Visibility(0, .1f, Vector3.UnitX * 10));
    }

    static AtmosphereSettings Settings() => new()
    {
        Enabled = true,
        HazeDensityPerKm = 100,
        HazeScaleHeightKm = 100,
        NightSkyFloor = 0,
    };

    static AtmosphereFrame Frame() => new(Direction(0, .1f), Direction(MathF.PI, .3f), .04f, Vector3.One);

    static Vector3 Direction(float azimuth, float elevation) => new(MathF.Cos(azimuth) * MathF.Cos(elevation),
        MathF.Sin(elevation), MathF.Sin(azimuth) * MathF.Cos(elevation));

    static float Luma(Vector3 c) => .2126f * c.X + .7152f * c.Y + .0722f * c.Z;
}
