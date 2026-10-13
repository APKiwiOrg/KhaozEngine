using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SceneAtmosphereCycleTests
{
    [Fact]
    public void Reenable_UsesTheLatestDisabledCycleWithoutResolvingDisabledSettings()
    {
        using var rig = new HeadlessSceneRig();
        var post = rig.Scene.Post;
        var cycle = new SunCycleSettings { LatitudeDegrees = 0, SolarDeclinationDegrees = 0, NightKey = NightKeyMode.None };
        post.Atmosphere.Enabled = true;
        SunCycle.Apply(SunCycle.Evaluate(.5f, cycle), post);
        var noonColor = post.LightColor;
        Assert.True(noonColor.R > 0);
        var model = post.ResolveAtmosphereModel();
        int transportBuilds = model.BuildCount, lightingBuilds = model.LightingBuildCount;
        int integrations = model.ScatteringIntegrationCount;
        float extinction = post.Atmosphere.MieExtinctionPerKm;
        post.Atmosphere.Enabled = false;
        post.Atmosphere.MieExtinctionPerKm = -1;
        var midnight = SunCycle.Evaluate(0, cycle);

        SunCycle.Apply(midnight, post, new(0, float.NaN, 0));

        Assert.Equal(transportBuilds, model.BuildCount);
        Assert.Equal(lightingBuilds, model.LightingBuildCount);
        Assert.Equal(integrations, model.ScatteringIntegrationCount);
        post.Atmosphere.MieExtinctionPerKm = extinction;
        post.Atmosphere.Enabled = true;
        using var recording = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        rig.Frame(commands: recording);

        Assert.Equal(Color.Black, post.LightColor);
        Assert.NotEqual(noonColor, post.LightColor);
        Assert.Equal(midnight.LightDirection, post.LightDirection);
        Assert.Equal(midnight, post.AtmosphereCycle);
        var upload = Assert.Single(recording.Uploads,
            upload => upload.Buffer.SizeInBytes == AtmosphereRenderer.UboBytes);
        var ubo = MemoryMarshal.Read<AtmosphereRenderer.AtmosphereUbo>(upload.Data);
        Assert.Equal(-1f, ubo.Sun.Y);
        Assert.Equal(0f, ubo.Moon.W);
    }
}
