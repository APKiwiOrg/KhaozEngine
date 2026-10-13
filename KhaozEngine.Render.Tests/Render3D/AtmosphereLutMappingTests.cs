using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereLutMappingTests
{
    [Theory]
    [InlineData(.1f, .1f)]
    [InlineData(.5f, .5f)]
    [InlineData(.9f, .9f)]
    public void EveryLutMapping_RoundTrips(float u, float v)
    {
        var uv = new Vector2(u, v);
        var transmittance = AtmosphereLutMapping.TransmittanceParameters(uv, 6360, 6460);
        Assert.InRange(Vector2.Distance(uv, AtmosphereLutMapping.TransmittanceUv(
            transmittance.X, transmittance.Y, 6360, 6460)), 0, 2e-4f);
        var multi = AtmosphereLutMapping.MultiParameters(uv, 100);
        Assert.InRange(Vector2.Distance(uv, AtmosphereLutMapping.MultiUv(multi.X, multi.Y, 100)), 0, 1e-5f);
    }

    [Theory]
    [InlineData(.1f, .1f, 0)]
    [InlineData(.4f, .49f, 0)]
    [InlineData(.7f, .51f, 0)]
    [InlineData(.9f, .9f, 0)]
    [InlineData(.1f, .1f, 2)]
    [InlineData(.4f, .49f, 2)]
    [InlineData(.7f, .51f, 2)]
    [InlineData(.9f, .9f, 2)]
    public void SkyMapping_RoundTripsBothSidesOfTheHorizon(float u, float v, float heightKm)
    {
        var uv = new Vector2(u, v);
        var direction = AtmosphereLutMapping.SkyDirection(uv, 6360 + heightKm, 6360);
        Assert.InRange(Vector2.Distance(uv, AtmosphereLutMapping.SkyUv(direction, 6360 + heightKm, 6360)), 0, 2e-4f);
        Assert.InRange(Math.Abs(direction.Length() - 1), 0, 1e-6f);
    }

    [Fact]
    public void BoundedAxes_UseSpecifiedTexelCentresAndCanonicalOutwardEndpoint()
    {
        Assert.Equal(new Vector2(.5f / 256, .5f / 64), AtmosphereLutMapping.TransmittanceUv(6360, 1, 6360, 6460));
        Assert.Equal(new Vector2(.5f / 256, 63.5f / 64), AtmosphereLutMapping.TransmittanceUv(6460, 1, 6360, 6460));
        Assert.Equal(new Vector2(6360, 1), AtmosphereLutMapping.TransmittanceParameters(new Vector2(.5f / 256, .5f / 64), 6360, 6460));
        Assert.Equal(new Vector2(6460, 1), AtmosphereLutMapping.TransmittanceParameters(new Vector2(.5f / 256, 63.5f / 64), 6360, 6460));
        Assert.Equal(new Vector2(.5f / 32), AtmosphereLutMapping.MultiUv(0, -1, 100));
        Assert.Equal(new Vector2(31.5f / 32), AtmosphereLutMapping.MultiUv(100, 1, 100));
        Assert.Equal(new Vector2(0, -1), AtmosphereLutMapping.MultiParameters(new Vector2(.5f / 32), 100));
        Assert.Equal(new Vector2(100, 1), AtmosphereLutMapping.MultiParameters(new Vector2(31.5f / 32), 100));
        Assert.Equal(.5f / 108, AtmosphereLutMapping.SkyUv(Vector3.UnitY, 6360, 6360).Y);
        Assert.Equal(107.5f / 108, AtmosphereLutMapping.SkyUv(-Vector3.UnitY, 6360, 6360).Y);
        Assert.Equal(53.5f / 108, AtmosphereLutMapping.SkyUv(Vector3.UnitX, 6360, 6360).Y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SkyMapping_PreservesFullAzimuthAndPoles(float heightKm)
    {
        foreach (var direction in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var uv = AtmosphereLutMapping.SkyUv(direction, 6360 + heightKm, 6360);
            Assert.InRange(Vector3.Distance(direction, AtmosphereLutMapping.SkyDirection(uv, 6360 + heightKm, 6360)), 0, 2e-4f);
        }
        Assert.Equal(0, AtmosphereLutMapping.SkyUv(Vector3.UnitX, 6360 + heightKm, 6360).X);
        Assert.Equal(.25f, AtmosphereLutMapping.SkyUv(Vector3.UnitZ, 6360 + heightKm, 6360).X);
        Assert.Equal(.5f, AtmosphereLutMapping.SkyUv(-Vector3.UnitX, 6360 + heightKm, 6360).X);
        Assert.Equal(.75f, AtmosphereLutMapping.SkyUv(-Vector3.UnitZ, 6360 + heightKm, 6360).X);
        var seam = AtmosphereLutMapping.SkyDirection(new Vector2(0, .3f), 6360 + heightKm, 6360);
        Assert.InRange(Vector3.Distance(seam, AtmosphereLutMapping.SkyDirection(new Vector2(1, .3f), 6360 + heightKm, 6360)), 0, 1e-6f);
    }

    [Fact]
    public void Mappings_ClampRootCosineAndTextureDomains()
    {
        Assert.Equal(AtmosphereLutMapping.TransmittanceUv(6360, 1, 6360, 6460),
            AtmosphereLutMapping.TransmittanceUv(6359, 2, 6360, 6460));
        Assert.Equal(new Vector2(6360, 1), AtmosphereLutMapping.TransmittanceParameters(new Vector2(-1, -1), 6360, 6460));
        Assert.Equal(new Vector2(.5f / 32), AtmosphereLutMapping.MultiUv(-1, -2, 100));
        Assert.Equal(new Vector2(100, 1), AtmosphereLutMapping.MultiParameters(new Vector2(2, 2), 100));
        Assert.InRange(Vector3.Distance(Vector3.UnitY, AtmosphereLutMapping.SkyDirection(new Vector2(.2f, -1), 6360, 6360)), 0, 1e-6f);
        Assert.Equal(AtmosphereLutMapping.SkyUv(Vector3.UnitY, 6360, 6360),
            AtmosphereLutMapping.SkyUv(Vector3.UnitY * 7, 6359, 6360));
    }
}
