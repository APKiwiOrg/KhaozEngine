using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class AtmosphereIrradianceAllocationTests : IClassFixture<AtmosphereLightingFixture>
{
    readonly AtmosphereModel _earth;

    public AtmosphereIrradianceAllocationTests(AtmosphereLightingFixture fixture) => _earth = fixture.Earth;

    [Fact]
    public void EvaluatingChangingFramesAllocatesNothing()
    {
        EvaluateChangingFrames();
        AllocAssert.NoPerCallAllocation("Changing atmosphere irradiance frames", EvaluateChangingFrames);
    }

    void EvaluateChangingFrames()
    {
        for (int i = 0; i < 200; i++)
        {
            float elevation = (.1f + i * .003f);
            float azimuth = i * .02f;
            var sun = new Vector3(MathF.Cos(elevation) * MathF.Cos(azimuth), MathF.Sin(elevation),
                MathF.Cos(elevation) * MathF.Sin(azimuth));
            _ = _earth.EvaluateIrradiance(.7f, new AtmosphereFrame(sun, -sun, .04f, Vector3.One));
        }
    }
}
