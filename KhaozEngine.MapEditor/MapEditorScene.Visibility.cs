using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    const float ViewButtonWidth = 80f;
    const float ViewPanelWidth = 300f;

    void BuildLayersInspector()
    {
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Groups")));
        foreach (VisibilityGroup group in Enum.GetValues<VisibilityGroup>())
        {
            VisibilityGroup captured = group;
            _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(GroupLabel(captured)),
                () => _visibility.GetGroupChoice(captured), value => _visibility.SetGroup(captured, value),
                LocalizedText.Raw("Shows or hides this marker group without changing the document.")));
        }
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("Textured props"),
            () => _options.TexturedProps,
            value => { _options.TexturedProps = value; InvalidateViewportKitMeshes(); RebuildWorldForVisibility(); },
            LocalizedText.Raw("Switches between textured and flat prop meshes in the editor viewport.")));
        foreach (MapScatterLayer layer in _document.Doc.ScatterLayers)
        {
            string name = layer.Name;
            _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(name),
                () => _visibility.GetLayerChoice(name), value => _visibility.SetLayer(name, value),
                LocalizedText.Raw("Shows or hides this retained scatter layer without rebuilding it.")));
        }
    }

    static string GroupLabel(VisibilityGroup group) => group switch
    {
        VisibilityGroup.FeatureMarkers => "Feature markers",
        VisibilityGroup.ScatterOverrides => "Scatter overrides",
        VisibilityGroup.PlayerSpawns => "Player spawns",
        _ => group.ToString(),
    };

    protected virtual void RebuildWorldForVisibility()
    {
        if (!_viewport.IsBuilt) return;
        _viewport.Rebuild(_document.Doc, _document.Registry);
        _controller.Field = _viewport.Field;
    }

    protected virtual void InvalidateViewportKitMeshes() => _viewport.InvalidateKitMeshes();

    bool ElementVisible(SelectionKind kind, string id)
    {
        if (!_visibility.IsElementVisible(kind, id)) return false;
        return kind != SelectionKind.Placement || Placement(id) is not { } placement
            || PropKindVisible(placement.Kind);
    }

    bool PropKindVisible(string kitId) =>
        _visibility.GetCategory(_viewport.PropCategoryOf(kitId));

    IReadOnlyList<string> ScatterLayerNames()
    {
        var names = new string[_document.Doc.ScatterLayers.Count];
        for (int i = 0; i < names.Length; i++) names[i] = _document.Doc.ScatterLayers[i].Name;
        return names;
    }

    Rect ViewPanelBounds(ChromeLayout layout) => new(
        MathF.Max(layout.Viewport.X, layout.Inspector.X - ViewPanelWidth - ToolbarGap),
        layout.Viewport.Y + ToolbarGap,
        MathF.Min(ViewPanelWidth, layout.Viewport.Width),
        MathF.Max(0f, layout.Viewport.Height - ToolbarGap * 2f));

    void UpdateViewPanel(float dt, ChromeLayout layout, Rect buttonBounds)
    {
        _viewPanel.Button.Bounds = buttonBounds;
        _viewPanel.PanelBounds = ViewPanelBounds(layout);
        _viewPanel.Update(_ui, dt);
    }

    void DrawViewPanel(SpriteBatch batch, SpriteFont font, ChromeLayout layout, Rect buttonBounds)
    {
        _viewPanel.Button.Bounds = buttonBounds;
        _viewPanel.PanelBounds = ViewPanelBounds(layout);
        if (_viewPanel.IsOpen) FillPanel(batch, _viewPanel.PanelBounds, PanelBackground);
        _viewPanel.Draw(batch, _white, font);
    }
}
