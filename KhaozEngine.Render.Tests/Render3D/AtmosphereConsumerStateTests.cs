using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereConsumerStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyAndSurfaceUploads_CarryThisFramesEnableFlag(bool enabled)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Sky.Enabled = true;
        rig.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        rig.Scene.Post.Atmosphere.Enabled = !enabled;
        rig.Frame();
        rig.Scene.Post.Atmosphere = new AtmosphereSettings { Enabled = enabled };
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        rig.Frame(commands: commands);

        var sky = Assert.Single(commands.Uploads, upload => upload.Buffer.SizeInBytes == SkyRenderer.UboBytes);
        Assert.Equal(enabled ? 1f : 0f, MemoryMarshal.Read<SkyRenderer.SkyUbo>(sky.Data).Horizon.W);
        var surface = Assert.Single(commands.Uploads, upload => upload.Buffer.SizeInBytes == ModelRenderer.UboBytes);
        Assert.Equal(enabled ? 1f : 0f,
            MemoryMarshal.Read<Vector4>(surface.Data.AsSpan((int)ModelRenderer.RenderOriginOffset)).W);
    }
}
