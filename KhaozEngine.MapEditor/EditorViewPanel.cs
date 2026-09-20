using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>A persistent view button and floating visibility grid independent of the active inspector.</summary>
internal sealed class EditorViewPanel
{
    readonly EditorVisibility _visibility;
    readonly Func<IReadOnlyList<string>> _layerNames;
    string[] _builtLayers = Array.Empty<string>();

    public EditorViewPanel(EditorVisibility visibility, Func<IReadOnlyList<string>> layerNames)
    {
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        _layerNames = layerNames ?? throw new ArgumentNullException(nameof(layerNames));
        Button = new Button(default, MapEditorStrings.Text(MapEditorStrings.View), null!, Toggle)
        {
            Style = GuiStyle.Modern,
        };
        Grid = new PropertyGrid(default) { EditorStyle = GuiStyle.Modern };
        RebuildRows();
    }

    public Button Button { get; }
    public PropertyGrid Grid { get; }
    public bool IsOpen { get; private set; }
    public Rect PanelBounds { get; set; }
    public bool HasActiveEditor => IsOpen && Grid.HasActiveEditor;

    public void Open() { IsOpen = true; Button.Selected = true; SyncLayers(); }
    public void Close() { IsOpen = false; Button.Selected = false; }
    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Update(InputManager input, float dt)
    {
        Button.Update(input.Pointer);
        if (!IsOpen) return;
        SyncLayers();
        Grid.Bounds = PanelBounds;
        Grid.Update(input, dt);
    }

    public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
    {
        Button.Font = font;
        Button.Draw(batch, white);
        if (!IsOpen) return;
        Grid.Bounds = PanelBounds;
        Grid.Draw(batch, white, font);
    }

    void SyncLayers()
    {
        IReadOnlyList<string> names = _layerNames();
        if (Same(names, _builtLayers)) return;
        RebuildRows();
    }

    void RebuildRows()
    {
        IReadOnlyList<string> layers = _layerNames();
        _builtLayers = new string[layers.Count];
        for (int i = 0; i < layers.Count; i++) _builtLayers[i] = layers[i];
        Grid.Rows.Clear();
        Grid.Rows.Add(new HeaderRow(MapEditorStrings.Text(MapEditorStrings.ViewOptions)));
        Grid.Rows.Add(new BoolRow(MapEditorStrings.Text(MapEditorStrings.TerrainOnly),
            () => _visibility.TerrainOnly, value => _visibility.TerrainOnly = value));
        Grid.Rows.Add(new BoolRow(MapEditorStrings.Text(MapEditorStrings.ShowAll),
            () => false, value => { if (value) _visibility.ShowAll(); }));
        Grid.Rows.Add(new HeaderRow(MapEditorStrings.Text(MapEditorStrings.PropCategories)));
        AddCategory(MapEditorStrings.OtherProps, EditorPropCategory.OtherProps);
        AddCategory(MapEditorStrings.Trees, EditorPropCategory.Trees);
        AddCategory(MapEditorStrings.Rocks, EditorPropCategory.Rocks);
        Grid.Rows.Add(new BoolRow(MapEditorStrings.Text(MapEditorStrings.Water),
            () => _visibility.GetGroupChoice(VisibilityGroup.Water),
            value => _visibility.SetGroup(VisibilityGroup.Water, value)));
        Grid.Rows.Add(new HeaderRow(MapEditorStrings.Text(MapEditorStrings.Markers)));
        AddGroup(MapEditorStrings.Spawns, VisibilityGroup.Spawns);
        AddGroup(MapEditorStrings.PlayerSpawns, VisibilityGroup.PlayerSpawns);
        AddGroup(MapEditorStrings.Exclusions, VisibilityGroup.Exclusions);
        AddGroup(MapEditorStrings.ScatterOverrides, VisibilityGroup.ScatterOverrides);
        AddGroup(MapEditorStrings.Regions, VisibilityGroup.Regions);
        AddGroup(MapEditorStrings.FeatureMarkers, VisibilityGroup.FeatureMarkers);
        if (layers.Count == 0) return;
        Grid.Rows.Add(new HeaderRow(MapEditorStrings.Text(MapEditorStrings.ScatterLayers)));
        foreach (string layer in layers)
        {
            string name = layer;
            Grid.Rows.Add(new BoolRow(LocalizedText.Raw(name),
                () => _visibility.GetLayerChoice(name), value => _visibility.SetLayer(name, value)));
        }
    }

    void AddCategory(StringId label, EditorPropCategory category) =>
        Grid.Rows.Add(new BoolRow(MapEditorStrings.Text(label),
            () => _visibility.GetCategoryChoice(category), value => _visibility.SetCategory(category, value)));

    void AddGroup(StringId label, VisibilityGroup group) =>
        Grid.Rows.Add(new BoolRow(MapEditorStrings.Text(label),
            () => _visibility.GetGroupChoice(group), value => _visibility.SetGroup(group, value)));

    static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
