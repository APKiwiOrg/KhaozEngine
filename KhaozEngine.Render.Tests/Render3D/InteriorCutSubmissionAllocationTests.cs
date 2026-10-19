using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class InteriorCutSubmissionAllocationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WarmedOptedInSubmissionsDoNotAllocate(bool empty, bool silhouette)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = empty
            ? InteriorHeightFieldBuilder.Build([], new InteriorWindow(new Vector2(-2), .25f, 32), InteriorEncoding.For(-1, 3))
            : InteriorCutShadowTests.Field();
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        var near = new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(1, 0, 0)) { CutInsideInteriors = true };
        var far = new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(20, 0, 0)) { CutInsideInteriors = true };
        void Submit()
        {
            if (silhouette)
            {
                scene.DrawMeshSilhouette(mesh, near.World, Color.White, .05f, true);
                scene.DrawMeshSilhouette(mesh, far.World, Color.White, .05f, true);
            }
            else
            {
                scene.Draw(in near);
                scene.Draw(in far);
            }
        }
        scene.Begin();
        Submit();
        scene.Begin();
        Submit();
        scene.Begin();

        long allocated = AllocationMeasurement.Measure("unchanged opted-in placements", Submit);

        Assert.Equal(0L, allocated);
        if (!silhouette)
        {
            Assert.Equal(2, scene.QueuedInstancesForTests.Count);
            Assert.Equal(!empty, scene.QueuedInstancesForTests[0].CutInsideInteriors);
            Assert.False(scene.QueuedInstancesForTests[1].CutInsideInteriors);
        }
    }
}
