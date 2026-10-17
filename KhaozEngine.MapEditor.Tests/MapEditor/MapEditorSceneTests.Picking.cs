using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public partial class MapEditorSceneTests
{
    static EditorFrameInput DownwardPress(float x, float z) =>
        new(new Vector3(x, 100f, z), -Vector3.UnitY, pointerPressed: true, pointerDown: true, dt: 0.016f);

    // A viewport click runs the pick filters once per placement, so each filter has to resolve nothing from the
    // document. Resolving each placement's kit through an id lookup once made one click quadratic: about half a
    // second on a 17,000-placement island. The scene and the tool controller, which own the pick filters, read the
    // document through EditorDocument.Doc, so a filter that searches it reads Doc once per placement. A linear pick
    // reads it a fixed number of times whatever the placement count. A read count rather than a stopwatch, so a
    // loaded machine cannot fail it (#1444).
    [Fact]
    public void ViewportPick_StaysLinearAcrossALargeAuthoredDocument()
    {
        long small = DocumentReadsForOnePick(200);
        long large = DocumentReadsForOnePick(20_000);

        Assert.True(small > 0, "The read probe did not see the pick reach the document.");
        Assert.Equal(small, large);
    }

    // Document reads during one Select-mode press over count far placements plus one target under the ray.
    static long DocumentReadsForOnePick(int count)
    {
        var scene = new FieldDocScene(() =>
        {
            MapDocument doc = ValidDoc();
            for (int i = 0; i < count; i++)
                doc.Placements.Add(new MapPlacement { Id = $"baked-{i}", Kind = "oak", X = 1000f + i, Z = 1000f, Y = 0f });
            doc.Placements.Add(new MapPlacement { Id = "target", Kind = "oak", X = 0f, Z = 0f, Y = 0f });
            return doc;
        });
        scene.Init(null!, null!, null!, new MapEditorOptions
        {
            ResolvePropCategory = _ => EditorPropCategory.Trees,
        });
        new SceneManager().Push(scene);
        scene.Document.Selection.Clear();

        long before = scene.Document.DocReads;
        scene.Controller.Update(DownwardPress(0f, 0f));
        long reads = scene.Document.DocReads - before;

        Assert.Equal("target", scene.Document.Selection.Id);
        return reads;
    }

    [Fact]
    public void Reload_keeps_hidden_prop_category_out_of_draw_and_picking()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ke-reload-pick-{Guid.NewGuid():N}.json");
        try
        {
            var doc = new MapDocument
            {
                Id = "reload-pick",
                Bounds = new MapBounds { MinX = -20f, MinZ = -20f, MaxX = 20f, MaxZ = 20f },
            };
            doc.Placements.Add(new MapPlacement { Id = "oak-1", Kind = "oak", X = 0f, Z = 0f, Y = 0f });
            MapDocumentFile.Save(doc, path, MapDocRegistry.CreateDefault());

            var scene = new ReloadScene();
            scene.Init(null!, null!, null!, new MapEditorOptions
            {
                DocumentPath = path,
                ResolvePropCategory = kit => kit == "oak" ? EditorPropCategory.Trees : EditorPropCategory.OtherProps,
            });
            new SceneManager().Push(scene);
            scene.Visibility.SetCategory(EditorPropCategory.Trees, false);

            Assert.True(scene.ReloadDocument());
            ViewportWorld viewport = Assert.IsType<ViewportWorld>(scene.ReloadViewport);
            Assert.Equal(EditorPropCategory.Trees, viewport.PropCategoryOf("oak"));
            Assert.False(viewport.PropKindVisible("oak"));
            scene.Controller.Field = new TerrainField(new TerrainConfig { GentleAmplitude = 0f });
            scene.Controller.Update(DownwardPress(0f, 0f));

            Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
