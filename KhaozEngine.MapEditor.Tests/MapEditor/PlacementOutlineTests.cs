using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Headless tests for the outline's Placements branch: one group per kit, big groups collapsed, and
/// expansion remembered across the rebuild every document edit triggers.</summary>
public sealed class PlacementOutlineTests
{
    static List<MapPlacement> Placements(params (string Kind, int Count)[] kinds)
    {
        var list = new List<MapPlacement>();
        foreach ((string kind, int count) in kinds)
            for (int i = 0; i < count; i++)
                list.Add(new MapPlacement { Id = $"{kind}-{i}", Kind = kind });
        return list;
    }

    static string Label(TreeNode node) => node.Label.Resolve();

    [Fact]
    public void Rebuild_GroupsByKitInNameOrderWithCounts()
    {
        var outline = new PlacementOutline();

        TreeNode root = outline.Rebuild(Placements(("rock_a", 2), ("grass", 40), ("hut", 1)));

        Assert.Equal("Placements", Label(root));
        Assert.True(root.Expanded);
        Assert.Equal(new[] { "grass (40)", "hut (1)", "rock_a (2)" }, root.Children.Select(Label));
        Assert.Equal(new[] { "rock_a-0", "rock_a-1" }, root.Children[2].Children.Select(Label));
        Assert.Equal(new MapEditorScene.OutlineRef(SelectionKind.Placement, "hut-0"), root.Children[1].Children[0].Tag);
    }

    [Fact]
    public void Rebuild_CollapsesLargeGroupsAndOpensSmallOnes()
    {
        var outline = new PlacementOutline();

        TreeNode root = outline.Rebuild(Placements(
            ("grass", PlacementOutline.AutoExpandLimit + 1), ("hut", PlacementOutline.AutoExpandLimit)));

        Assert.False(root.Children[0].Expanded);
        Assert.True(root.Children[1].Expanded);
    }

    [Fact]
    public void Rebuild_RemembersGroupExpansionAcrossRebuilds()
    {
        var outline = new PlacementOutline();
        List<MapPlacement> placements = Placements(("grass", 40), ("hut", 2));
        TreeNode root = outline.Rebuild(placements);
        root.Children[0].Expanded = true;
        root.Children[1].Expanded = false;

        TreeNode rebuilt = outline.Rebuild(placements);

        Assert.NotSame(root, rebuilt);
        Assert.True(rebuilt.Children[0].Expanded);
        Assert.False(rebuilt.Children[1].Expanded);
    }

    [Fact]
    public void Resolve_ReturnsTheRowWhenItsGroupIsOpen_ElseTheGroup()
    {
        var outline = new PlacementOutline();
        TreeNode root = outline.Rebuild(Placements(("grass", 40), ("hut", 2)));

        Assert.Same(root.Children[0], outline.Resolve("grass-7"));
        Assert.Same(root.Children[1].Children[1], outline.Resolve("hut-1"));
        Assert.Null(outline.Resolve("missing"));

        root.Children[0].Expanded = true;
        Assert.Same(root.Children[0].Children[7], outline.Resolve("grass-7"));
    }

    [Fact]
    public void ToggleGroup_FlipsAGroupRowAndIgnoresEverythingElse()
    {
        var outline = new PlacementOutline();
        TreeNode root = outline.Rebuild(Placements(("grass", 40)));
        TreeNode group = root.Children[0];

        Assert.True(PlacementOutline.ToggleGroup(group));
        Assert.True(group.Expanded);
        Assert.True(PlacementOutline.ToggleGroup(group));
        Assert.False(group.Expanded);
        Assert.False(PlacementOutline.ToggleGroup(group.Children[0]));
        Assert.False(PlacementOutline.ToggleGroup(root));
    }
}
