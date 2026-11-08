using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class RigidPointShadowAllocationTests
{
    [Fact]
    public void WarmMovingRigidSchedulingAllocatesNothing()
    {
        using var rig = new RigidPointShadowRig();
        rig.Warm();
        using IGpuCommandList commands = rig.Factory.CreateCommandList();
        commands.Begin();
        rig.Scene.PreparePointShadows(commands, rig.Scene.Camera.Eye);

        AllocAssert.NoPerCallAllocation("Warm moving rigid point-shadow scheduling", () =>
            rig.Scene.PreparePointShadows(commands, rig.Scene.Camera.Eye));

        commands.End();
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(12, rig.Scene.LastShadowPassDiagnostics.PointStaticTransientRigidDrawCalls);
    }
}
