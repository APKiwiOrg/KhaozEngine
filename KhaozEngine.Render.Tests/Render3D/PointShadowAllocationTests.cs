using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// THE POINT-SHADOW FRAME METHOD ALLOCATES NOTHING ON A STEADY FRAME, and the reason it is worth a test of its
/// own is that the thing it used to allocate was not obvious: the publish half built a list of live material sets
/// and walked every mesh, skinned mesh, splat and tile-ground material to reach a reference compare, once a frame,
/// to answer a question the frame boundary already answers. Allocating anything here allocates it 60 times a
/// second for the whole life of the scene.
/// <para>
/// Measured over the FAKE device, so nothing about the reading depends on a backend: the fake records nothing and
/// allocates nothing of its own, which leaves the delta as the scene's. The frame is genuinely steady, with two
/// cached static lights whose casters have not moved (so no static row is re-rendered) and one dynamic light that
/// is re-rendered on every call, so the pack, the upload and the caster walk are all inside the measurement
/// rather than skipped by an early return.
/// </para>
/// </summary>
[Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
public sealed class PointShadowAllocationTests
{
    [Fact]
    public void PreparePointShadowsOnASteadyFrameAllocatesNothing()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        // The key light's own atlas is not what this measures, and leaving it off keeps its per-frame work out of
        // the reading.
        using var scene = new Scene3D(device, target.Outputs, new ShadowSettings { Mode = ShadowMode.Off });
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        Vector3 eye = scene.Camera.Eye;

        void Queue()
        {
            scene.Draw(mesh, Matrix4x4.Identity);
            scene.Draw(mesh, Matrix4x4.CreateTranslation(0f, 0f, 2f));
            scene.AddLight(eye + new Vector3(0f, 0f, 10f), Color.White, 6f, 1f, LightShadow.Static(1));
            scene.AddLight(eye + new Vector3(0f, 0f, 14f), Color.White, 6f, 1f, LightShadow.Static(2));
            scene.AddLight(eye + new Vector3(0f, 0f, 12f), Color.White, 6f, 1f, LightShadow.Dynamic);
        }

        // Four whole frames: one to ask for the atlas, one for the boundary to bring it up, and two more to draw
        // both static rows under the shipped rebuild budget. What is left is a frame with nothing static to do.
        for (int i = 0; i < 4; i++)
        {
            scene.Begin();
            Queue();
            scene.PrepareFrame();
            using IGpuCommandList frame = factory.CreateCommandList();
            frame.Begin();
            scene.RenderInternal(frame, 16, 16, target);
            frame.End();
        }

        Assert.Equal(3, scene.PointShadowedLights);
        Assert.Equal(0, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(1, scene.LastShadowPassDiagnostics.PointDynamicRenders);

        // The queued lights and the grouped instances stand until the next Begin, so the method can be called
        // again against exactly the state a steady frame hands it.
        using IGpuCommandList commands = factory.CreateCommandList();
        commands.Begin();
        for (int i = 0; i < 4; i++) scene.PreparePointShadows(commands, eye);   // warm every reused buffer

        // Retries once before failing (see AllocAssert.NoPerCallAllocation) to ride out an unrelated gen-0
        // collision from the rest of the process, per issue #284.
        AllocAssert.NoPerCallAllocation("PreparePointShadows over 20 steady frames", () =>
        {
            for (int i = 0; i < 20; i++) scene.PreparePointShadows(commands, eye);
        });

        commands.End();
        Assert.Equal(3, scene.PointShadowedLights);
        Assert.Equal(0, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
    }
}
