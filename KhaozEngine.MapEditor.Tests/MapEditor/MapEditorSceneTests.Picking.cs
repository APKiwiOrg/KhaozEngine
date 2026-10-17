using System;
using System.IO;
using System.Linq;
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

    // Each filter must be constant-time. Count the maximum lookup-key reads on any one placement, so a scan
    // through a captured document or another collection still shows repeated visits to its placements. The
    // viewport's CPU pick state is ready too, so PlacementDrawnAt cannot short-circuit at the unbuilt guard.
    // Read counts rather than a stopwatch, so a loaded machine cannot fail it (#1444, #1453).
    [Fact]
    public void ViewportPick_StaysLinearAcrossALargeAuthoredDocument()
    {
        var small = ReadsForOnePick(200);
        var large = ReadsForOnePick(20_000);

        Assert.True(small.DocumentReads > 0, "The read probe did not see the pick reach the document.");
        Assert.Equal(small.DocumentReads, large.DocumentReads);
        Assert.True(small.PlacementReads > 0, "The placement lookup probe did not see the pick reach a placement.");
        Assert.True(small.PlacementReads == large.PlacementReads,
            $"Placement lookup reads per pick grew with document size: {small.PlacementReads} versus {large.PlacementReads}.");
    }

    // Reads during one Select-mode press over count far placements plus one target under the ray.
    static (long DocumentReads, long PlacementReads) ReadsForOnePick(int count)
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
        using var viewport = new HeadlessPickViewport(scene);
        Assert.True(scene.Controller.PlacementDrawnAt(0f, 0f));
        Assert.False(scene.Controller.PlacementDrawnAt(5000f, 5000f));
        MapPlacement[] placements = scene.Document.Doc.Placements.ToArray();
        long[] placementReadsBefore = placements.Select(p => p.LookupReads).ToArray();

        long before = scene.Document.DocReads;
        scene.Controller.Update(DownwardPress(0f, 0f));
        long reads = scene.Document.DocReads - before;
        long placementReads = placements.Select((p, i) => p.LookupReads - placementReadsBefore[i]).Max();

        Assert.Equal("target", scene.Document.Selection.Id);
        return (reads, placementReads);
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
