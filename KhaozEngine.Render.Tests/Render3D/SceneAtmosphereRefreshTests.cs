using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SceneAtmosphereRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AltitudeRefresh_PreservesCallerSkyAndDrawsCustomDisc(bool applyCycle)
    {
        using var rig = new HeadlessSceneRig();
        var scene = rig.Scene;
        var post = scene.Post;
        post.Atmosphere.Enabled = true;
        if (applyCycle)
            SunCycle.Apply(SunCycle.Evaluate(.5f, new SunCycleSettings { NightKey = NightKeyMode.None }), post);
        var camera = new FlyCamera3D { Position = new(0, 2000, 0) };
        scene.CameraOverride = camera;
        post.LightDirection = new(.1f, -.9f, .3f);
        post.FillLightDirection = Vector3.UnitZ;
        post.Sky.HorizonColor = new(.3f, .2f, .1f);
        post.Sky.ZenithColor = new(.4f, .3f, .2f);
        post.Sky.GroundColor = new(.5f, .4f, .3f);
        post.Sky.SunColor = new(.6f, .5f, .4f);
        post.Sky.SunEnabled = false;
        post.Sky.SunDirectionOverride = Vector3.UnitX;
        var disc = new SkyDisc
        {
            Direction = Vector3.UnitZ,
            Color = new(.9f, .2f, .4f, .6f),
            Radius = .02f,
            HaloStrength = .3f,
            HaloFalloff = .4f,
        };
        post.Sky.ExtraDiscs.Add(disc);
        var sky = (post.Sky.HorizonColor, post.Sky.ZenithColor, post.Sky.GroundColor,
            post.Sky.SunColor, post.Sky.SunEnabled, post.Sky.SunDirectionOverride);
        var model = post.ResolveAtmosphereModel();
        var frame = AtmosphereRenderer.ResolveFrame(post, model);
        var groundLighting = model.EvaluateLighting(0, frame);
        var lighting = model.EvaluateLighting(post.Atmosphere.HeightKm(camera.Eye.Y), frame);
        Assert.NotEqual(groundLighting.Key, lighting.Key);
        using var recording = new RecordingGpuCommandList(new NullGpuCommandList())
        {
            CapturePayloads = true,
            UniformWindowsOfSet = set => Assert.IsType<FakeResourceSet>(set).Resources.OfType<IGpuBuffer>()
                .Select(buffer => new UniformWindow(buffer, 0, buffer.SizeInBytes, false)).ToArray(),
        };

        rig.Frame(commands: recording);

        Assert.Equal(disc, Assert.Single(post.Sky.ExtraDiscs));
        Assert.Equal(sky, (post.Sky.HorizonColor, post.Sky.ZenithColor, post.Sky.GroundColor,
            post.Sky.SunColor, post.Sky.SunEnabled, post.Sky.SunDirectionOverride));
        Assert.Equal(new Vector3(.1f, -.9f, .3f), post.LightDirection);
        Assert.Equal(Vector3.UnitZ, post.FillLightDirection);
        Assert.Equal(ToColor(lighting.Key), post.LightColor);
        Assert.Equal(ToColor(lighting.Ambient), post.AmbientColor);
        Assert.Equal(ToColor(lighting.Fill), post.FillLightColor);
        Assert.Equal(ToColor(lighting.Horizon), post.Water.HorizonColor);
        var upload = Assert.Single(recording.Uploads, upload => upload.Buffer.SizeInBytes == SkyRenderer.UboBytes);
        var ubo = MemoryMarshal.Read<SkyRenderer.SkyUbo>(upload.Data);
        Assert.Equal(1f, ubo.Res.W);
        Assert.Equal((Vector4)disc.Color, ubo.DiscColor[0]);
        Assert.Equal(new Vector4(disc.Direction, disc.Radius), ubo.DiscDirection[0]);
        Assert.Contains(recording.Reads, read => ReferenceEquals(read.Buffer, upload.Buffer)
            && read.DrawOrdinal == upload.DrawsBefore && read.Bytes == SkyRenderer.UboBytes);
    }

    static Color ToColor(Vector3 rgb) => new(rgb.X, rgb.Y, rgb.Z, 1f);
}
