using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>The outline's Placements branch: one group row per kit id ("grass (5524)"), with the placements of
/// that kit beneath it. A baked scatter puts thousands of rows of a few kits in the document, so a flat list buries
/// every other outline category. Groups sort by kit id, a group of more than <see cref="AutoExpandLimit"/> rows
/// starts collapsed while a small hand-placed one starts open, and a toggle away from that default is remembered
/// across the rebuild every document edit triggers (which makes fresh nodes). Headless: it only builds
/// <see cref="TreeNode"/>s.</summary>
internal sealed class PlacementOutline
{
    /// <summary>The largest group that starts expanded.</summary>
    internal const int AutoExpandLimit = 12;

    /// <summary>The tag a kit group row carries, which marks it as a group rather than a selectable element.</summary>
    internal readonly record struct KitGroup(string Kind);

    readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    TreeNode? _root;

    /// <summary>Builds a fresh Placements root from the document's placements, carrying each group's expansion
    /// over from the root this call replaces.</summary>
    internal TreeNode Rebuild(IReadOnlyList<MapPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        RememberExpansion();

        var groups = new SortedDictionary<string, TreeNode>(StringComparer.Ordinal);
        foreach (MapPlacement p in placements)
        {
            if (!groups.TryGetValue(p.Kind, out TreeNode? group))
                groups[p.Kind] = group = new TreeNode(default, new KitGroup(p.Kind));
            group.Children.Add(new TreeNode(LocalizedText.Raw(p.Id),
                new MapEditorScene.OutlineRef(SelectionKind.Placement, p.Id)));
        }

        var root = new TreeNode(LocalizedText.Raw("Placements")) { Expanded = true };
        foreach ((string kind, TreeNode group) in groups)
        {
            group.Label = LocalizedText.Raw($"{kind} ({group.Children.Count})");
            group.Expanded = _expanded.TryGetValue(kind, out bool open) ? open : OpensByDefault(group);
            root.Children.Add(group);
        }
        _root = root;
        return root;
    }

    /// <summary>The outline row that stands for a placement: its own row while its kit group is open, else the
    /// group row, so a viewport pick never unrolls thousands of rows. Null when the id is not in the branch.</summary>
    internal TreeNode? Resolve(string placementId)
    {
        if (_root is null) return null;
        foreach (TreeNode group in _root.Children)
            foreach (TreeNode row in group.Children)
                if (row.Tag is MapEditorScene.OutlineRef r && string.Equals(r.Id, placementId, StringComparison.Ordinal))
                    return group.Expanded ? row : group;
        return null;
    }

    /// <summary>Flips a kit group row's expansion, so a tap anywhere on the row opens it (not only the caret).
    /// False, touching nothing, for any other node.</summary>
    internal static bool ToggleGroup(TreeNode node)
    {
        if (node.Tag is not KitGroup) return false;
        node.Expanded = !node.Expanded;
        return true;
    }

    static bool OpensByDefault(TreeNode group) => group.Children.Count <= AutoExpandLimit;

    // Only a choice that differs from the size default is a choice worth keeping. A group left at its default keeps
    // following its size, so a bake that turns "fern (3)" into "fern (4414)" collapses it.
    void RememberExpansion()
    {
        if (_root is null) return;
        foreach (TreeNode group in _root.Children)
        {
            if (group.Tag is not KitGroup g) continue;
            if (group.Expanded == OpensByDefault(group)) _expanded.Remove(g.Kind);
            else _expanded[g.Kind] = group.Expanded;
        }
    }
}
