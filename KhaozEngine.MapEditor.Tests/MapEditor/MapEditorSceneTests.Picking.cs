using System;
using System.Diagnostics;
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

    // A viewport click runs the visibility filter once per placement, so the filter has to stay constant-time per
    // element. Resolving each placement's kit through an id lookup made one click quadratic: about half a second on
    // a 17,000-placement island. Linear work here is around a millisecond, so the bound leaves two orders of
    // magnitude for a loaded machine while still failing the quadratic shape.
    [Fact]
    public void ViewportPick_StaysLinearAcrossALargeAuthoredDocument()
    {
        const int Count = 20_000;
        var scene = new FieldDocScene(() =>
        {
            MapDocument doc = ValidDoc();
            for (int i = 0; i < Count; i++)
                doc.Placements.Add(new MapPlacement { Id = $"baked-{i}", Kind = "oak", X = 1000f + i, Z = 1000f, Y = 0f });
            doc.Placements.Add(new MapPlacement { Id = "target", Kind = "oak", X = 0f, Z = 0f, Y = 0f });
            return doc;
        });
        scene.Init(null!, null!, null!, new MapEditorOptions
        {
            ResolvePropCategory = _ => EditorPropCategory.Trees,
        });
        new SceneManager().Push(scene);

        double best = double.MaxValue;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            scene.Document.Selection.Clear();
            var watch = Stopwatch.StartNew();
            scene.Controller.Update(DownwardPress(0f, 0f));
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            scene.Controller.Update(new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY,
                pointerReleased: true, dt: 0.016f));
        }

        Assert.Equal("target", scene.Document.Selection.Id);
        Assert.True(best < 150.0, $"A viewport pick over {Count} placements took {best:F1} ms.");
    }

    [Fact]
    public void HiddenPlacementCategory_StillSuppressesPickAfterReload()
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
