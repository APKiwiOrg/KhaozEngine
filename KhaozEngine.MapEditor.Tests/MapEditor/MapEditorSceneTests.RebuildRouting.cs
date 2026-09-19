using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Game;
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
        // ---- exclusion gizmo drag: captured config refresh plus bounded invalidation -------------------

        // A minimal document holding one disc exclusion at the origin, so a gizmo drag on it reuses the exact
        // press/drag geometry EditorToolTests.ShapeDrag_MovesCenterThroughCommand already verifies (a +X arrow
        // grab at (0.6, 100, 0) on a DiscShapeDoc CenterX=0 CenterZ=0 Radius=5).
        static MapDocument SampleWithExclusion()
        {
            var doc = new MapDocument { Id = "exclusion-throttle", Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f } };
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            return doc;
        }

        [Fact]
        public void ExclusionGizmoDrag_RoutesToBoundedPartialWithLayerRefresh()
        {
            var scene = new ThrottleScene(SampleWithExclusion) { PartialSucceeds = true };
            scene.Init(null!, null!, null!, new MapEditorOptions { GestureRebuildInterval = 0.25f });
            new SceneManager().Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            scene.Log.Clear();

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            // Grab the +X translate arrow on the shape-center gizmo.
            scene.Controller.Update(new EditorFrameInput(new Vector3(0.6f, 100f, 0f), ThrottleDown,
                pointerPressed: true, pointerDown: true, dt: 0.016f));
            Assert.True(scene.Controller.IsDragging);

            // Every drag frame can take the bounded path because the real viewport refreshes configs first.
            for (int i = 0; i < 5; i++)
            {
                scene.Controller.Update(new EditorFrameInput(new Vector3(1.6f + i, 100f, 0f), ThrottleDown, pointerDown: true, dt: 0.016f));
                Assert.True(scene.Document.PendingLayerConfigRefresh);
                scene.RunRebuildCheck(0.1f);
            }

            Assert.Equal(5, scene.Log.Count(s => s == "partial"));
            Assert.DoesNotContain("full", scene.Log);
            Assert.False(scene.Document.WorldRebuildPending);

            // Releasing seals the gesture into one coalesced undo step (drag coalescing), and the shape actually
            // moved: this is a real edit, not just a rebuild-routing no-op.
            scene.Controller.Update(new EditorFrameInput(new Vector3(5.6f, 100f, 0f), ThrottleDown, pointerReleased: true, dt: 0.016f));
            Assert.False(scene.Controller.IsDragging);
            scene.RunRebuildCheck(0.01f);

            Assert.Equal(6, scene.Log.Count(s => s == "partial"));
            Assert.DoesNotContain("full", scene.Log);
            Assert.False(scene.Document.WorldRebuildPending);
            Assert.Equal(1, scene.Document.History.UndoDepth);
            var disc = Assert.IsType<DiscShapeDoc>(scene.Document.Doc.Exclusions[0].Shape);
            Assert.True(disc.CenterX > 0f);
        }
    }
}
