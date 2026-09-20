using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public partial class MapEditorSceneTests
{
    static MapDocument ScatterBakeDoc()
    {
        MapDocument doc = ValidDoc();
        for (int i = 0; i < 40; i++)
            doc.Placements.Add(new MapPlacement { Id = $"grass-{i}", Kind = "grass", X = i, Z = 0f });
        doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 9f });
        return doc;
    }

    [Fact]
    public void Outline_SelectingInsideACollapsedKitGroup_HighlightsTheGroupWithoutOpeningIt()
    {
        DocScene scene = PushDocScene(ScatterBakeDoc);
        TreeNode grass = CategoryChild(scene.Outline, "Placements", 0);
        Assert.Equal("grass (40)", grass.Label.Resolve());
        Assert.False(grass.Expanded);

        scene.Document.Selection.Set(SelectionKind.Placement, "grass-17");

        Assert.Same(grass, scene.Outline.Selected);
        Assert.False(grass.Expanded);

        grass.Expanded = true;
        scene.Document.Selection.Set(SelectionKind.Placement, "grass-18");
        Assert.Same(grass.Children[18], scene.Outline.Selected);
    }

    [Fact]
    public void Outline_TappingAKitGroupTogglesItAndKeepsTheSelection()
    {
        DocScene scene = PushDocScene(ScatterBakeDoc);
        TreeView outline = scene.Outline;
        outline.Bounds = new Rect(0f, 0f, 240f, 400f);   // before the select, whose scroll-into-view reads it
        scene.Document.Selection.Set(SelectionKind.Placement, "hut");
        TreeNode grass = CategoryChild(outline, "Placements", 0);

        TapTree(outline, new InputManager(), RowCenter(outline, grass));

        Assert.True(grass.Expanded);
        Assert.Equal("hut", scene.Document.Selection.Id);
    }

    [Fact]
    public void Outline_KitGroupExpansionSurvivesADocumentEdit()
    {
        DocScene scene = PushDocScene(ScatterBakeDoc);
        CategoryChild(scene.Outline, "Placements", 0).Expanded = true;

        scene.Document.Execute(new MovePlacementCommand("hut", 3f, 3f, null));

        TreeNode rebuilt = CategoryChild(scene.Outline, "Placements", 0);
        Assert.True(rebuilt.Expanded);
        Assert.Equal(40, rebuilt.Children.Count);
    }
}
