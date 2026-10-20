using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageInteriorCutReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_equipment_rejects_a_changed_interior_cut_flag(bool outline)
    {
        using var fixture = new CoverageSceneFixture();
        fixture.Begin();
        var frozen = new RigidInstanceDraw[1];
        Assert.True(fixture.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, fixture.Support,
            [new RigidInstanceDraw(fixture.RigidHandle, Matrix4x4.Identity)], frozen, out var failure), failure.ToString());
        var changed = frozen[0] with { CutInsideInteriors = true };
        if (outline)
        {
            var group = fixture.Scene.BeginMeshOutline(KhaozEngine.Primitives.Color.White, 1);
            Assert.Throws<InvalidOperationException>(() => fixture.Scene.DrawMeshOutline(group, changed));
            fixture.Scene.DrawMeshOutline(group, frozen[0]);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Scene.Draw(changed));
            fixture.Scene.Draw(frozen[0]);
        }
    }
}
