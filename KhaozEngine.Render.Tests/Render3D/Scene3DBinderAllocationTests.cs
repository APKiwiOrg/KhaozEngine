using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class Scene3DBinderAllocationTests
{
    [Fact]
    public void WarmedSceneSubmit_IsAllocationFreeAndPreservesEntityDraws()
    {
        var world = new World();
        Entity first = Spawn(world, 5, new Vector3(1f, 2f, 3f));
        var tint = new Color(10, 20, 30, 255);
        var material = Material.Shiny(0.6f, 64f);
        Entity second = Spawn(world, 6, new Vector3(4f, 5f, 6f), tint, material);
        world.Set(world.Spawn(), new Transform3D { Position = new Vector3(99f) });
        world.Set(world.Spawn(), new MeshInstance { Mesh = new MeshHandle(99) });
        using var harness = new MotionTestScene();

        for (int warm = 0; warm < 16; warm++)
        {
            harness.Scene.Begin();
            Scene3DBinder.Submit(world, harness.Scene);
        }

        world.Set(first, new Transform3D { Position = new Vector3(10f, 2f, 3f), Scale = new Vector3(2f) });
        harness.Scene.Begin();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Scene3DBinder.Submit(world, harness.Scene);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var queued = harness.Scene.QueuedInstancesForTests;
        Assert.Equal(2, queued.Count);
        AssertDraw(queued[0], first, 5,
            Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(10f, 2f, 3f), Color.White, default);
        AssertDraw(queued[1], second, 6, Matrix4x4.CreateTranslation(4f, 5f, 6f), tint, material);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void SceneSubmit_UsesTheCurrentWorldAndSceneAcrossAlternatingCalls()
    {
        var firstWorld = new World();
        Entity first = Spawn(firstWorld, 5, new Vector3(1f, 2f, 3f));
        var secondWorld = new World();
        Entity second = Spawn(secondWorld, 6, new Vector3(4f, 5f, 6f));
        using var firstScene = new MotionTestScene();
        using var secondScene = new MotionTestScene();

        firstScene.Scene.Begin();
        Scene3DBinder.Submit(firstWorld, firstScene.Scene);
        secondScene.Scene.Begin();
        Scene3DBinder.Submit(secondWorld, secondScene.Scene);
        firstScene.Scene.Begin();
        Scene3DBinder.Submit(secondWorld, firstScene.Scene);
        secondScene.Scene.Begin();
        Scene3DBinder.Submit(firstWorld, secondScene.Scene);

        AssertDraw(Assert.Single(firstScene.Scene.QueuedInstancesForTests), second, 6,
            Matrix4x4.CreateTranslation(4f, 5f, 6f), Color.White, default);
        AssertDraw(Assert.Single(secondScene.Scene.QueuedInstancesForTests), first, 5,
            Matrix4x4.CreateTranslation(1f, 2f, 3f), Color.White, default);
    }

    static Entity Spawn(World world, int mesh, Vector3 position, Color tint = default, Material material = default)
    {
        Entity entity = world.Spawn();
        world.Set(entity, new Transform3D { Position = position });
        world.Set(entity, new MeshInstance { Mesh = new MeshHandle(mesh), Tint = tint, Material = material });
        return entity;
    }

    static void AssertDraw(SceneInstances.Instance actual, Entity entity, int mesh, Matrix4x4 model,
        Color tint, Material material)
    {
        Assert.Equal(new MeshHandle(mesh), actual.Mesh);
        Assert.Equal(model, actual.World);
        Assert.Equal(tint, actual.Tint);
        Assert.Equal(material, actual.Material);
        Assert.Equal(Scene3DBinder.MotionKeyOf(entity), actual.Motion);
    }
}
