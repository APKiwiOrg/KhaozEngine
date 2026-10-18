using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.TileWorld;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutRelevanceTests
{
    [Fact]
    public void RelevanceCacheTracksFieldAndTransform()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = InteriorCutShadowTests.Field();
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        Matrix4x4 world = Matrix4x4.Identity;
        void Submit()
        {
            scene.Begin();
            scene.Draw(new RigidInstanceDraw(mesh, world) { CutInsideInteriors = true });
            scene.DrawMeshOutlineDissolved(scene.BeginMeshOutline(Color.White, 1f), mesh, world, 0, false, true);
            scene.DrawMeshSilhouette(mesh, world, Color.White, .05f, true);
        }
        Submit();
        Assert.Equal(1, Evaluations(scene));
        Submit();
        Submit();
        Assert.Equal(1, Evaluations(scene));
        scene.Begin();
        scene.Begin();
        Submit();
        Assert.Equal(1, Evaluations(scene));
        scene.InteriorCut = InteriorCutShadowTests.Field();
        Submit();
        Assert.Equal(2, Evaluations(scene));
        world = Matrix4x4.CreateTranslation(10, 0, 0);
        Submit();
        Assert.Equal(3, Evaluations(scene));
        Assert.False(Assert.Single(scene.QueuedInstancesForTests).CutInsideInteriors);
        // A rebase also changes represented predicate boundaries, so it invalidates the cached answer.
        scene.RenderOrigin = new Vector3(4096, 128, -4096);
        Submit();
        Assert.Equal(4, Evaluations(scene));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyReachingInstancesCarryTheFlag(bool selectFar)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.RequestShadowMapLayout(256, 1);
        scene.FrustumCulling = false;
        scene.ShadowCascadeCulling = false;
        TileObject near = doc.AddObject("tree", 11, 10, 0, 0);
        TileObject far = doc.AddObject("tree", 40, 40, 0, 0);
        var dispatcher = new ManualTileBuildDispatcher();
        var options = new TileWorldViewOptions
        {
            PropDrawRadius = 192f,
            PropLayers = [new TilePropLayerDefinition
            {
                Id = "trees", ArchetypeIds = new HashSet<string> { "tree" }, DrawRadius = 192f,
                LodDistance = 0, CutInsideInteriors = true,
            }],
        };
        using var view = new TileWorldView(new Scene3DTileWorldScene(scene), doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(), options, null, dispatcher)
        { InteriorMeshes = new InteriorTestMeshes() };
        view.LoadRegion(default);
        view.SettleForCapture(default);
        scene.Begin();
        view.Draw(Vector3.Zero);
        InteriorTestWorker.Run(dispatcher);
        TileObject selected = selectFar ? far : near;
        view.SetOutlinedObject(selected.Id, Color.White);
        view.SetSilhouettedObject(selected.Id, Color.White);
        scene.Begin();
        view.Draw(Vector3.Zero);
        Assert.NotNull(view.AppliedInteriorCut);

        MeshHandle treeMesh = Assert.Single(scene.QueuedInstancesForTests, i =>
            Math.Abs(i.World.M41 - 40.5f) < .01f && Math.Abs(i.World.M43 + 40.5f) < .01f).Mesh;
        var trees = scene.QueuedInstancesForTests.Where(i => i.Mesh.Equals(treeMesh)).ToArray();
        Assert.Equal(2, trees.Length);
        Assert.All(trees, tree => Assert.Equal(tree.World.M41 < 20, tree.CutInsideInteriors));
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        Scene3D.GroupInstances(trees, data, runs);
        Assert.Equal(new[] { 1f, 0f }, data.Select(i => i.SpecParams.W));
        Assert.Equal(ShadowCastKind.Dissolving, Scene3D.ClassifyCaster(trees[0]));
        Assert.Equal(ShadowCastKind.Opaque, Scene3D.ClassifyCaster(trees[1]));
        Assert.Equal(ShadowDepthVariant.Opaque, ShadowDepthSelection.ForRigidSpan(Scene3D.ClassifyCaster(trees[1]), false));

        rig.Render();
        var pointSpans = scene.DebugPointCasterSpans(new Vector3(25, 3, -25), 100f);
        Assert.Equal(new[] { ShadowCastKind.Dissolving, ShadowCastKind.Opaque },
            pointSpans.Where(span => span.Index == treeMesh.Index).Select(span => span.Kind));
        var cascadeSpans = HighlightTestAccess.Field<List<Scene3D.ShadowCasterSpan>[]>(scene, "_cascadeCasterSpans");
        Assert.Equal(new[] { ShadowCastKind.Dissolving, ShadowCastKind.Opaque },
            cascadeSpans[0].Where(span => span.Index == treeMesh.Index).Select(span => span.Kind));
        Assert.Equal(!selectFar, OutlineCut(scene));
        Assert.Equal(!selectFar, SilhouetteCut(scene));
    }

    [Fact]
    public void TemporarilyAbsentPlacementsKeepTheirCachedAnswer()
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = InteriorCutShadowTests.Field();
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        void Submit(float x)
        {
            scene.Begin();
            scene.Draw(new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(x, 0, 0)) { CutInsideInteriors = true });
        }
        Submit(0);
        Submit(20);
        Submit(20);
        Submit(0);
        Assert.Equal(2, Evaluations(scene));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestedCutsFollowAFieldReplacedAfterSubmission(bool enableLate)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        InteriorHeightField field = InteriorCutShadowTests.Field();
        scene.InteriorCut = enableLate ? null : field;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box());
        scene.Begin();
        scene.Draw(new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true });
        scene.DrawMeshOutlineDissolved(scene.BeginMeshOutline(Color.White, 1), mesh, Matrix4x4.Identity, 0, false, true);
        scene.DrawMeshSilhouette(mesh, Matrix4x4.Identity, Color.White, .05f, true);
        scene.InteriorCut = enableLate ? field : null;

        rig.Render();

        Assert.Equal(enableLate, Assert.Single(scene.QueuedInstancesForTests).CutInsideInteriors);
        Assert.Equal(enableLate ? 1f : 0f, Assert.Single(HighlightTestAccess.Field<List<ModelRenderer.InstanceData>>(
            scene, "_instanceData")).SpecParams.W);
        Assert.Equal(enableLate, OutlineCut(scene));
        Assert.Equal(enableLate, SilhouetteCut(scene));
    }

    [Fact]
    public void NoFieldOrMissKeepsTheOriginalSubmission()
    {
        using var rig = new MotionTestScene();
        MeshHandle mesh = rig.Scene.LoadMesh(MeshPrimitives.Box());
        var parts = new Dictionary<string, MeshHandle> { ["tree"] = mesh };
        PropPlacement[] placements = [new("tree", 20, 0, 0, 1, .3f, 0)];
        SceneInstances.Instance Submit(InteriorHeightField? field, bool optedIn)
        {
            rig.Scene.InteriorCut = field;
            rig.Scene.Begin();
            rig.Scene.DrawProps(placements, parts, Vector3.Zero, 100f, 0f, cutInsideInteriors: optedIn);
            return Assert.Single(rig.Scene.QueuedInstancesForTests);
        }
        SceneInstances.Instance baseline = Submit(null, false);
        void SameSubmission(SceneInstances.Instance other)
        {
            foreach (PropertyInfo property in typeof(SceneInstances.Instance).GetProperties())
                Assert.Equal(property.GetValue(baseline), property.GetValue(other));
            Assert.Equal(Scene3D.ClassifyCaster(baseline), Scene3D.ClassifyCaster(other));
        }
        SameSubmission(Submit(null, true));
        SameSubmission(Submit(InteriorCutShadowTests.Field(), false));
        SameSubmission(Submit(InteriorCutShadowTests.Field(), true));
    }

    static bool SilhouetteCut(Scene3D scene) => CutOf(Assert.Single(
        HighlightTestAccess.Field<IList>(scene, "_silhouetteDraws").Cast<object>()));

    static bool CutOf(object draw) => (bool)draw.GetType().GetProperty("CutInsideInteriors")!.GetValue(draw)!;

    static bool OutlineCut(Scene3D scene)
    {
        object group = Assert.Single(HighlightTestAccess.Field<IList>(scene, "_meshOutlineGroups").Cast<object>());
        var parts = (IList)group.GetType().GetProperty("Parts")!.GetValue(group)!;
        object part = Assert.Single(parts.Cast<object>());
        return CutOf(part);
    }

    static int Evaluations(Scene3D scene) =>
        HighlightTestAccess.Field<InteriorCutRelevanceCache>(scene, "_interiorRelevance").Evaluations;
}
