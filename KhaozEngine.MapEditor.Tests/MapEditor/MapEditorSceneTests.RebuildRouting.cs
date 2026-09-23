using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Game;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>
    /// Which rebuild seam <see cref="MapEditorScene.CheckWorldRebuild"/> dispatches an edit to, driven through a
    /// real editing gesture rather than a command's own <c>DirtyRegion</c> getter. In its own file because the
    /// test class itself is at its file-size baseline and may not grow.
    /// </summary>
    public partial class MapEditorSceneTests
    {
        // Logs every rebuild seam the dispatch reaches. PropsSucceeds false stands in for a viewport that declines
        // the props-only refresh (not built, or a layer list that changes the sink's layer shape).
        sealed class RoutingScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public bool PropsSucceeds = true;
            public readonly List<string> Log = new();
            public RoutingScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() => Controller.Field =
                new TerrainField(new TerrainConfig { GentleAmplitude = 0f });
            protected override void TeardownWorld() { }
            protected override bool RefreshWorldProps(RectArea dirty) { Log.Add("props"); return PropsSucceeds; }
            protected override bool PartialRebuildWorld(RectArea dirty) { Log.Add("partial"); return true; }
            protected override bool RebuildWorld() { Log.Add("full"); return true; }
            public void RunRebuildCheck(float dt) => CheckWorldRebuild(dt);
        }

        static RoutingScene PushRoutingScene(Func<MapDocument> factory, bool propsSucceeds = true)
        {
            var scene = new RoutingScene(factory) { PropsSucceeds = propsSucceeds };
            scene.Init(null!, null!, null!, new MapEditorOptions { GestureRebuildInterval = 0.25f });
            new SceneManager().Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            scene.Log.Clear();
            return scene;
        }

        // A minimal document holding one disc of the given kind at the origin, so a gizmo drag on it reuses the exact
        // press/drag geometry EditorToolTests.ShapeDrag_MovesCenterThroughCommand already verifies (a +X arrow
        // grab at (0.6, 100, 0) on a disc CenterX=0 CenterZ=0 Radius=5).
        static MapDocument RoutingDoc(Action<MapDocument> add)
        {
            var doc = new MapDocument { Id = "rebuild-routing", Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f } };
            add(doc);
            return doc;
        }

        static DiscShapeDoc OriginDisc() => new() { CenterX = 0f, CenterZ = 0f, Radius = 5f };

        // Grab the +X translate arrow on the selection's gizmo, drag five frames with a rebuild check after each,
        // then release and check once more. The release frame lands the gesture's final edit too, so a drag makes
        // six rebuild checks that have work to do.
        static void DragFiveFrames(RoutingScene scene, Action<EditorDocument> eachFrame)
        {
            scene.Controller.Update(new EditorFrameInput(new Vector3(0.6f, 100f, 0f), ThrottleDown,
                pointerPressed: true, pointerDown: true, dt: 0.016f));
            Assert.True(scene.Controller.IsDragging);
            for (int i = 0; i < 5; i++)
            {
                scene.Controller.Update(new EditorFrameInput(new Vector3(1.6f + i, 100f, 0f), ThrottleDown, pointerDown: true, dt: 0.016f));
                eachFrame(scene.Document);
                scene.RunRebuildCheck(0.1f);
                Assert.False(scene.Document.WorldRebuildPending);
            }
            scene.Controller.Update(new EditorFrameInput(new Vector3(5.6f, 100f, 0f), ThrottleDown, pointerReleased: true, dt: 0.016f));
            Assert.False(scene.Controller.IsDragging);
            scene.RunRebuildCheck(0.01f);
            Assert.Equal(1, scene.Document.History.UndoDepth);
        }

        // ---- exclusion and scatter-override gizmo drags: the props-only partial ------------------------

        [Fact]
        public void ExclusionGizmoDrag_TakesThePropsOnlyPartial_EveryFrame()
        {
            RoutingScene scene = PushRoutingScene(() => RoutingDoc(d => d.Exclusions.Add(new MapExclusion { Shape = OriginDisc() })));
            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            DragFiveFrames(scene, doc =>
            {
                Assert.True(doc.PendingLayerConfigRefresh);
                Assert.False(doc.PendingFieldChange);
                Assert.NotNull(doc.PendingRebuildRegion);
            });

            // Never throttled and never a re-mesh: one props-only refresh per frame that edited the shape.
            Assert.Equal(6, scene.Log.Count(s => s == "props"));
            Assert.DoesNotContain("partial", scene.Log);
            Assert.DoesNotContain("full", scene.Log);
            var disc = Assert.IsType<DiscShapeDoc>(scene.Document.Doc.Exclusions[0].Shape);
            Assert.True(disc.CenterX > 0f);

            // Undo and redo of the whole drag take the same path.
            scene.Log.Clear();
            Assert.True(scene.Document.Undo());
            scene.RunRebuildCheck(0.01f);
            Assert.True(scene.Document.Redo());
            scene.RunRebuildCheck(0.01f);
            Assert.Equal(new[] { "props", "props" }, scene.Log);
        }

        [Fact]
        public void ScatterOverrideGizmoDrag_TakesThePropsOnlyPartial_EveryFrame()
        {
            RoutingScene scene = PushRoutingScene(() => RoutingDoc(d => d.ScatterOverrides.Add(
                new MapScatterOverrideDoc { Shape = OriginDisc(), DensityMultiplier = 0.2f })));
            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");

            DragFiveFrames(scene, doc => Assert.False(doc.PendingFieldChange));

            Assert.Equal(6, scene.Log.Count(s => s == "props"));
            Assert.DoesNotContain("partial", scene.Log);
            Assert.DoesNotContain("full", scene.Log);
        }

        [Fact]
        public void FeatureGizmoDrag_KeepsTheRemeshingPartial()
        {
            RoutingScene scene = PushRoutingScene(() => RoutingDoc(d => d.Terrain.Features.Add(
                new FlattenFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, TargetHeight = 1f })));
            scene.Document.Selection.Set(SelectionKind.Feature, "0");

            DragFiveFrames(scene, doc => Assert.True(doc.PendingFieldChange));

            Assert.Equal(6, scene.Log.Count(s => s == "partial"));
            Assert.DoesNotContain("props", scene.Log);
            Assert.DoesNotContain("full", scene.Log);
        }

        [Fact]
        public void DeclinedPropsRefresh_FallsBackToTheRemeshingPartial()
        {
            RoutingScene scene = PushRoutingScene(
                () => RoutingDoc(d => d.Exclusions.Add(new MapExclusion { Shape = OriginDisc() })), propsSucceeds: false);
            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            DragFiveFrames(scene, _ => { });

            Assert.Equal(Enumerable.Repeat(new[] { "props", "partial" }, 6).SelectMany(p => p), scene.Log);
        }

        [Fact]
        public void ExclusionEdit_BatchedWithAFeatureEdit_Remeshes()
        {
            RoutingScene scene = PushRoutingScene(() => RoutingDoc(d => d.Exclusions.Add(new MapExclusion { Shape = OriginDisc() })));

            scene.Document.Execute(new EditExclusionShapeCommand(0,
                new DiscShapeDoc { CenterX = 2f, CenterZ = 0f, Radius = 5f }, scene.Document.Doc.Exclusions[0].Shape!));
            scene.Document.Execute(new AddFeatureCommand(new FlattenFeatureDoc { CenterX = 3f, CenterZ = 0f, Radius = 4f }));
            Assert.True(scene.Document.PendingFieldChange);
            scene.RunRebuildCheck(0.01f);

            Assert.Equal(new[] { "partial" }, scene.Log);
        }
    }
}
