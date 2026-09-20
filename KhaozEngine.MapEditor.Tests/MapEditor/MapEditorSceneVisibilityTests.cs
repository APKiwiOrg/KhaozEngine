using System.Linq;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public partial class MapEditorSceneTests
{
    [Fact]
    public void ViewPanel_TogglesWhileInspectorAndSculptStayActiveWithoutMutationOrRebuild()
    {
        var options = new MapEditorOptions { ResolvePropCategory = _ => EditorPropCategory.Trees };
        RebuildSpyDocScene scene = PushRebuildSpyDocScene(() =>
        {
            MapDocument doc = ValidDoc();
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "forest" });
            doc.CompanionLayers.Add(new MapCompanionLayer { Name = "ferns", HostLayer = "forest" });
            doc.Placements.Add(new MapPlacement { Id = "oak-1", Kind = "oak", X = 0f, Z = 0f });
            return doc;
        }, options);
        string hash = MapDocumentHash.OfWorld(scene.Document.Doc);
        var authored = new[]
        {
            new EditorPlacement("oak-1", new PropPlacement("oak", 0f, 0f, 0f, 1f, 0f, 0)),
        };
        scene.Document.Selection.Set(SelectionKind.Placement, "oak-1");
        PropertyRow inspectorRow = scene.Inspector.Rows[0];

        TapButton(scene.ViewPanel.Button);
        Assert.True(scene.ViewPanel.IsOpen);
        Assert.Same(inspectorRow, scene.Inspector.Rows[0]);
        TapBool(scene.ViewPanel.Grid.Rows.OfType<BoolRow>().Single(r => r.Label.Resolve() == "Authored props"));

        Assert.False(scene.Visibility.GetGroup(VisibilityGroup.Placements));
        Assert.True(scene.Visibility.GetCategory(EditorPropCategory.Trees));
        Assert.True(scene.Visibility.GetLayer("forest"));
        Assert.Empty(ViewportWorld.FilterVisiblePlacements(authored, scene.Visibility));
        Assert.False(scene.Visibility.IsElementVisible(SelectionKind.Placement, "oak-1"));
        Assert.Equal(GizmoAffordance.None, scene.Controller.TryGizmo(out _));
        Assert.Equal(0, scene.Rebuilds);
        Assert.Equal(hash, MapDocumentHash.OfWorld(scene.Document.Doc));
        Assert.False(scene.Document.History.CanUndo);

        scene.Controller.Mode = EditorToolMode.SculptTerrain;
        scene.OnUpdate(0f);
        Assert.True(scene.ViewPanel.IsOpen);
        Assert.Contains(scene.Inspector.Rows.OfType<HeaderRow>(), row => row.Label.Resolve() == "Terrain Sculpt");
        TapBool(scene.ViewPanel.Grid.Rows.OfType<BoolRow>().Single(r => r.Label.Resolve() == "Terrain Only"));
        Assert.True(scene.Visibility.TerrainOnly);
        TapBool(scene.ViewPanel.Grid.Rows.OfType<BoolRow>().Single(r => r.Label.Resolve() == "Terrain Only"));
        Assert.False(scene.Visibility.TerrainOnly);
        Assert.False(scene.Visibility.GetGroup(VisibilityGroup.Placements));
        Assert.True(scene.Visibility.GetCategory(EditorPropCategory.Trees));
        Assert.True(scene.Visibility.GetLayer("forest"));

        TapBool(scene.ViewPanel.Grid.Rows.OfType<BoolRow>().Single(r => r.Label.Resolve() == "Show All"));
        Assert.True(scene.Visibility.GetGroup(VisibilityGroup.Placements));
        Assert.True(scene.Visibility.GetCategory(EditorPropCategory.Trees));
        Assert.True(scene.Visibility.GetLayer("forest"));
        Assert.Single(ViewportWorld.FilterVisiblePlacements(authored, scene.Visibility));
        Assert.True(scene.Visibility.IsElementVisible(SelectionKind.Placement, "oak-1"));
        Assert.Equal(hash, MapDocumentHash.OfWorld(scene.Document.Doc));
        Assert.False(scene.Document.History.CanUndo);
        Assert.Equal(0, scene.Rebuilds);
    }

    [Fact]
    public void HiddenPlacementCategory_SuppressesPickAndExistingSelectionGizmo()
    {
        var scene = new FieldDocScene(() =>
        {
            MapDocument doc = ValidDoc();
            doc.Placements.Add(new MapPlacement { Id = "oak-1", Kind = "oak", X = 0f, Z = 0f });
            return doc;
        });
        scene.Init(null!, null!, null!, new MapEditorOptions
        {
            ResolvePropCategory = kit => kit == "oak" ? EditorPropCategory.Trees : EditorPropCategory.OtherProps,
        });
        new KhaozEngine.Game.SceneManager().Push(scene);
        scene.Document.Selection.Set(SelectionKind.Placement, "oak-1");

        Assert.NotEqual(GizmoAffordance.None, scene.Controller.TryGizmo(out _));
        scene.Visibility.SetCategory(EditorPropCategory.Trees, false);
        Assert.Equal(GizmoAffordance.None, scene.Controller.TryGizmo(out _));

        scene.Document.Selection.Clear();
        scene.Controller.Update(new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY,
            pointerPressed: true, pointerDown: true, dt: 0.016f));
        Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);
    }

    void TapButton(Button button)
    {
        var ui = new KhaozEngine.Windowing.InputManager();
        button.Bounds = new KhaozEngine.Primitives.Rect(0f, 0f, 80f, 28f);
        var at = new Vector2(40f, 14f);
        ui.Update(MouseFrame(at, false)); button.Update(ui.Pointer);
        ui.Update(MouseFrame(at, true)); button.Update(ui.Pointer);
        ui.Update(MouseFrame(at, false)); button.Update(ui.Pointer);
    }
}
