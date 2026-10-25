using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class MrtBindingRetirementTests
{
    [Theory]
    [InlineData("decal")]
    [InlineData("particle")]
    [InlineData("distortion")]
    [InlineData("water")]
    public void Effect_bindings_survive_target_replacement_until_gpu_completion(string path)
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var retired = GpuRetireQueue.Create(device);
        using var resources = new RenderResources(device, 32, 16, hdrColor: false, retired: retired);
        using var commands = device.Factory.CreateCommandList();
        using IDisposable renderer = path switch
        {
            "decal" => new GroundDecalRenderer(device, resources.ColorDepthFB.Outputs, retired),
            "particle" => new ParticleRenderer(device, resources.ColorDepthFB.Outputs, retired),
            "distortion" => new DistortionRenderer(device, retired),
            _ => new WaterRenderer(device, resources.ColorDepthFB.Outputs, retired)
        };
        var outgoing = new List<FakeResourceSet>();
        Draw();
        for (int change = 0; change < 2; change++)
        {
            var old = device.Resources.ResourceSets.Where(s => !s.Disposed && s.Resources.Contains(resources.DepthColorTex)).ToArray();
            Assert.NotEmpty(old); outgoing.AddRange(old);
            int waits = device.Waits;
            resources.Resize(48 + change * 16, 32, false, 1, false, false);
            Draw(); retired.BeginFrame();
            Assert.Equal(waits, device.Waits);
            Assert.All(outgoing, set => Assert.False(set.Disposed));
        }
        foreach (var fence in device.Fences) fence.Signaled = true;
        retired.BeginFrame();
        Assert.All(outgoing, set => Assert.True(set.Disposed));
        Assert.Equal(0, device.Resources.LiveSetsNamingAFreedTexture);
        Assert.Equal(0, retired.PendingCount);

        void Draw()
        {
            resources.EnsureDistortion(path == "distortion", 2);
            WaterPlane[] planes = [new(0, 0, 0, 20)];
            var settings = new WaterSettings();
            if (renderer is WaterRenderer preparedWater) preparedWater.PrepareFrame(new FramePrepare(settings, planes, 0));
            commands.Begin();
            switch (renderer)
            {
                case GroundDecalRenderer decals:
                    decals.Draw(commands, resources, Matrix4x4.Identity, 0, GroundDecalQuality.Full, false,
                        GroundDecalRenderer.FramePass.Main, Vector3.Zero,
                        [new GroundDecal { Shape = DecalShape.Circle, Size = new Vector4(2, 0, 0, 0) }]);
                    break;
                case ParticleRenderer particles:
                    particles.Draw(commands, resources, Matrix4x4.Identity, Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
                        0, 0, ParticleQuality.Full, 1, [new ParticleSprite { Size = 1, Color = Color.White }], _ => null);
                    break;
                case DistortionRenderer distortion:
                    distortion.Draw(commands, resources, Matrix4x4.Identity, Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
                        0, 0, DistortionQuality.Full, 1, 2, [new DistortionSprite { Size = 1, Strength = 1 }]);
                    break;
                case WaterRenderer water:
                    water.Draw(commands, resources, planes, Matrix4x4.Identity, Matrix4x4.Identity, -Vector3.UnitY,
                        Color.White, new Vector3(0, 10, 0), settings, new SkySettings(), 0);
                    break;
            }
            commands.End(); device.Submit(commands);
        }
    }
}
