using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutPlacementSlotTests
{
    [Fact]
    public void UnchangedSubmissionsCheckContextOnceAndDoNotReadMeshSlots()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = InteriorCutShadowTests.Field();
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        var cache = HighlightTestAccess.Field<InteriorCutRelevanceCache>(scene, "_interiorRelevance");
        void Submit()
        {
            scene.Begin();
            foreach (float x in new[] { 1f, 20f, 40f })
                scene.Draw(new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(x, 0, 0)) { CutInsideInteriors = true });
        }
        Submit();
        int checks = cache.ContextChecks, reads = scene.InteriorMeshReads;

        Submit();

        Assert.Equal(checks + 1, cache.ContextChecks);
        Assert.Equal(reads, scene.InteriorMeshReads);
        Assert.True(scene.QueuedInstancesForTests[0].CutInsideInteriors);
        Assert.False(scene.QueuedInstancesForTests[1].CutInsideInteriors);
        Assert.False(scene.QueuedInstancesForTests[2].CutInsideInteriors);
    }

    [Fact]
    public void WarmSlotsDoNotSurviveAMeshUnloadedBetweenFrames()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = InteriorCutShadowTests.Field();
        MeshHandle old = scene.LoadMesh(MeshPrimitives.Box(.02f));
        var world = Matrix4x4.CreateTranslation(20, 0, 0);
        scene.Begin();
        scene.Draw(new RigidInstanceDraw(old, world) { CutInsideInteriors = true });
        scene.Begin();
        scene.UnloadMesh(old);
        MeshHandle replacement = scene.LoadMesh(MeshPrimitives.Box(100));
        scene.Draw(new RigidInstanceDraw(old, world) { CutInsideInteriors = true });
        scene.Draw(new RigidInstanceDraw(replacement, world) { CutInsideInteriors = true });

        Assert.Equal(old.Index, replacement.Index);
        Assert.NotEqual(old.Generation, replacement.Generation);
        Assert.False(scene.QueuedInstancesForTests[0].CutInsideInteriors);
        Assert.True(scene.QueuedInstancesForTests[1].CutInsideInteriors);
    }

    [Fact]
    public void AReplacementFieldRefreshesEarlierAndLaterSubmissions()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = InteriorCutShadowTests.Field();
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        var draw = new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(1, 0, 0)) { CutInsideInteriors = true };
        scene.Begin();
        scene.Draw(in draw);
        scene.InteriorCut = InteriorHeightFieldBuilder.Build([], new InteriorWindow(new Vector2(-2), .25f, 32), InteriorEncoding.For(-1, 3));
        scene.Draw(in draw);
        rig.Render();

        Assert.Equal(2, scene.QueuedInstancesForTests.Count);
        Assert.All(scene.QueuedInstancesForTests, item => Assert.False(item.CutInsideInteriors));
    }

    [Fact]
    public void SubmissionSlotsMatchBothMeshIdentityComponents()
    {
        var cache = new InteriorCutRelevanceCache();
        InteriorHeightField field = InteriorCutShadowTests.Field();
        Matrix4x4 world = Matrix4x4.CreateTranslation(1, 0, 0);
        MeshBounds bounds = MeshBounds.Empty;
        foreach (var mesh in new[] { new MeshHandle(1, 1), new MeshHandle(2, 1), new MeshHandle(1, 2) })
        {
            cache.BeginFrame();
            Assert.True(cache.MayIntersect(field, default, mesh, world, bounds));
        }
        Assert.Equal(3, cache.Evaluations);
        Assert.Equal(3, cache.Lookups);
        cache.BeginFrame();
        Assert.True(cache.MayIntersect(field, default, new MeshHandle(1, 2), world, bounds));
        Assert.Equal(3, cache.Lookups);
    }

    [Fact]
    public void ChangedTransformsReevaluateOnlyTheirPlacement()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        var cache = HighlightTestAccess.Field<InteriorCutRelevanceCache>(scene, "_interiorRelevance");
        scene.InteriorCut = InteriorCutShadowTests.Field();
        float middle = 20;
        void Submit()
        {
            scene.Begin();
            foreach (float x in new[] { 0f, middle, 40f })
                scene.Draw(new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(x, 0, 0)) { CutInsideInteriors = true });
        }
        Submit();
        Assert.Equal(3, cache.Evaluations);
        int lookups = cache.Lookups;
        Submit();
        Assert.Equal(3, cache.Evaluations);
        Assert.Equal(lookups, cache.Lookups);
        middle++;
        Submit();
        Assert.Equal(4, cache.Evaluations);
        Assert.Equal(lookups + 1, cache.Lookups);
        scene.InteriorCut = InteriorCutShadowTests.Field();
        Submit();
        Assert.Equal(7, cache.Evaluations);
        Submit();
        Assert.Equal(7, cache.Evaluations);
        Assert.Equal(lookups + 4, cache.Lookups);
    }

    [Fact]
    public void SilhouetteSlotsInvalidateOnWidthFieldTransformAndMeshGeneration()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(.02f));
        Matrix4x4 world = Matrix4x4.CreateTranslation(1, 3.02f, 0);
        float width = .001f;
        scene.InteriorCut = InteriorCutShadowTests.Field();
        bool Submit()
        {
            scene.Begin();
            scene.DrawMeshSilhouette(mesh, world, Color.White, width, true);
            var draws = HighlightTestAccess.Field<System.Collections.IList>(scene, "_silhouetteDraws");
            return (bool)draws[0]!.GetType().GetProperty("CutInsideInteriors")!.GetValue(draws[0])!;
        }
        Assert.False(Submit());
        Assert.False(Submit());
        Assert.Equal(1, scene.InteriorMarginEvaluations);
        Assert.Equal(1, scene.InteriorMarginLookups);
        width = .05f;
        Assert.True(Submit());
        Assert.Equal(2, scene.InteriorMarginEvaluations);
        world *= Matrix4x4.CreateTranslation(20, 0, 0);
        Assert.False(Submit());
        Assert.Equal(3, scene.InteriorMarginEvaluations);
        scene.InteriorCut = InteriorCutShadowTests.Field();
        Assert.False(Submit());
        Assert.Equal(4, scene.InteriorMarginEvaluations);
        scene.UnloadMesh(mesh);
        mesh = scene.LoadMesh(MeshPrimitives.Box(100));
        Assert.True(Submit());
        Assert.Equal(5, scene.InteriorMarginEvaluations);
    }
}
