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
