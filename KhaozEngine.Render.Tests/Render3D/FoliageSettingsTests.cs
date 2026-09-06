using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageSettingsTests
{
    [Fact]
    public void ConstructedSettingsAreValidAndLeaveWindDisabled()
    {
        var settings = new FoliageRenderSettings();

        settings.Validate();

        Assert.Equal(40f, settings.DrawRadius);
        Assert.Null(settings.DensityRadius);
        Assert.Equal(1f, settings.QualityDensity);
        Assert.Equal(.35f, settings.DistantDensity);
        Assert.Equal(8f, settings.FadeBandWidth);
        Assert.Equal(1f, settings.InstanceFadeBandWidth);
        Assert.Equal(Vector2.UnitX, settings.WindDirection);
        Assert.Equal(0f, settings.WindStrength);
        Assert.Equal(1.8f, settings.WindSpeed);
        Assert.Equal(.35f, settings.WindSpatialFrequency);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DistancesAndWindRatesRejectInvalidValues(float value)
    {
        FoliageRenderSettings[] invalid =
        [
            new() { DrawRadius = value },
            new() { DensityRadius = value },
            new() { FadeBandWidth = value },
            new() { InstanceFadeBandWidth = value },
            new() { WindSpeed = value },
            new() { WindSpatialFrequency = value },
        ];

        foreach (FoliageRenderSettings settings in invalid)
            Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void DensitiesAndWindStrengthStayWithinOneFullBlade(float value)
    {
        Assert.ThrowsAny<ArgumentException>(new FoliageRenderSettings { QualityDensity = value }.Validate);
        Assert.ThrowsAny<ArgumentException>(new FoliageRenderSettings { DistantDensity = value }.Validate);
        Assert.ThrowsAny<ArgumentException>(new FoliageRenderSettings { WindStrength = value }.Validate);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.NegativeInfinity)]
    public void WindDirectionMustBeFinite(float x, float z) =>
        Assert.ThrowsAny<ArgumentException>(new FoliageRenderSettings
        {
            WindDirection = new Vector2(x, z),
        }.Validate);

    [Fact]
    public void ZeroDistancesAndStationaryWindAreValid()
    {
        new FoliageRenderSettings
        {
            DrawRadius = 0f,
            DensityRadius = 0f,
            FadeBandWidth = 0f,
            InstanceFadeBandWidth = 0f,
            QualityDensity = 0f,
            DistantDensity = 1f,
            WindDirection = Vector2.Zero,
            WindStrength = 1f,
            WindSpeed = 0f,
            WindSpatialFrequency = 0f,
        }.Validate();
    }

    [Fact]
    public void InteractorsRejectNonFiniteOrOutOfRangeInputs()
    {
        FoliageInteractor[] invalid =
        [
            new(new Vector3(float.NaN, 0f, 0f), 1f),
            new(new Vector3(0f, float.PositiveInfinity, 0f), 1f),
            new(new Vector3(0f, 0f, float.NegativeInfinity), 1f),
            new(Vector3.Zero, -1f),
            new(Vector3.Zero, float.PositiveInfinity),
            new(Vector3.Zero, float.NaN),
            new(Vector3.Zero, 1f, -.1f),
            new(Vector3.Zero, 1f, 1.1f),
            new(Vector3.Zero, 1f, float.NaN),
        ];

        foreach (FoliageInteractor interactor in invalid)
            Assert.ThrowsAny<ArgumentException>(interactor.Validate);
    }

    [Fact]
    public void InteractorsAllowDisabledRadiusAndStrength()
    {
        new FoliageInteractor(Vector3.One, 0f).Validate();
        new FoliageInteractor(Vector3.One, 2f, 0f).Validate();
    }
}
