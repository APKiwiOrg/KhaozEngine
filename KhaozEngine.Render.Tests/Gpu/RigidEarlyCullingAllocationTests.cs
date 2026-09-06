using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class RigidEarlyCullingAllocationTests
{
    [GpuFact]
    public void WarmedEarlyRejectionReusesItsMaskWithoutAllocating()
    {
        using var fixture = new RigidEarlyCullingScene();
        Scene3D scene = fixture.Scene;
        scene.RenderOrigin = Vector3.Zero;
        scene.Begin();
        for (int i = 0; i < 4; i++)
            scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(0f, 0f, 0.5f), Color.White, Material.None, false);
        for (int i = 0; i < 4096; i++)
            scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(1000f + i, 0f, 0f), Color.White, Material.None, false);
        FrustumPlanes frustum = FrustumPlanes.Extract(Matrix4x4.Identity);
        for (int i = 0; i < 20; i++) scene.CullOptedOutInstances(frustum);

        AllocAssert.NoPerCallAllocation("early rejection of 4096 offscreen opt-outs", () =>
        {
            for (int i = 0; i < 20; i++) scene.CullOptedOutInstances(frustum);
        });

        var retained = scene.CullOptedOutInstances(frustum);
        Assert.Equal(4100, retained.Length);
        for (int i = 0; i < retained.Length; i++) Assert.Equal(i < 4, retained[i]);
    }
}
