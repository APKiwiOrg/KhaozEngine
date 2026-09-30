using System;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>The editor chrome's construction, widget step, and toolbar layout.</summary>
public partial class MapEditorScene
{
    // ---- chrome wiring -----------------------------------------------------------------------------------

    void BuildChrome()
    {
        _toolbar = new TabBar(ToolLabels);
        _outline = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _inspector = new PropertyGrid(default) { EditorStyle = GuiStyle.Modern };
        _outline.OnSelected = OnOutlineSelected;
        _outline.OnReordered = OnOutlineReordered;
        _outline.CanReorder = OutlineNodeIsReorderable;

        _paletteFilter = new TextInput(default) { PlaceholderContent = LocalizedText.Raw("Filter kits...") };
        _paletteTree = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _paletteTree.OnSelected = OnPaletteSelected;

        _spawnFilter = new TextInput(default) { PlaceholderContent = LocalizedText.Raw("Filter spawns...") };
        _spawnList = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _spawnList.OnSelected = OnSpawnSelected;

        _featureList = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _featureList.OnSelected = OnFeatureTypeSelected;

        // The toolbar Save button (decision 4). Font and Bounds are set per frame (no SpriteFont resolves at
        // chrome-build time, the TabBar pattern). The label is re-synced each chrome step in UpdateChrome.
        _saveButton = new Button(default, LocalizedText.Raw("Save"), null!, () => SaveDocument())
        {
            Style = GuiStyle.Modern,
        };
        BuildDungeonChrome();
        _viewPanel = new EditorViewPanel(_visibility, ScatterLayerNames);
    }

    void UpdateWidgets(float dt)
    {
        UiViewport? ui = Manager!.UiViewport;
        if (ui is null) return;
        UpdateGuiInput(ui);
        ChromeLayout L = ComputeLayout(ui.Width, ui.Height);

        (Rect tabsRect, Rect viewRect, Rect generateRect, Rect saveRect) = SplitToolbar(L.Toolbar);
        _toolbar.Bounds = tabsRect;
        if (_toolbar.Update(_ui.Pointer)) _controller.Mode = (EditorToolMode)_toolbar.ActiveIndex;

        _saveButton.Bounds = saveRect;
        _saveButton.Update(_ui.Pointer);
        UpdateDungeonButton(generateRect);
        UpdateViewPanel(dt, L, viewRect);

        _outline.Bounds = L.Outline;
        _outline.Update(_ui);

        _inspector.Bounds = L.Inspector;
        _inspector.Update(_ui, dt);

        if (FeatureMode)
        {
            _featureList.Bounds = L.Palette;
            _featureList.Update(_ui);
        }
        else if (BottomPanelVisible)
        {
            (Rect filterRect, Rect bodyRect) = SplitPaletteRegion(L.Palette);
            if (SpawnMode)
            {
                _spawnFilter.Bounds = filterRect;
                _spawnFilter.Update(_ui.Pointer, Manager.Input, dt);
                _spawnList.Bounds = bodyRect;
                _spawnList.Update(_ui);
            }
            else
            {
                _paletteFilter.Bounds = filterRect;
                _paletteFilter.Update(_ui.Pointer, Manager.Input, dt);
                _paletteTree.Bounds = bodyRect;
                _paletteTree.Update(_ui);
            }
        }
        RefreshPalettes();
    }

    // Splits the toolbar between tabs, View, the optional dungeon action, and Save. The dungeon action takes
    // no width without a game-supplied kit map, so existing editors keep their original tab space.
    // Pure math, so the split is asserted headless.
    (Rect Tabs, Rect View, Rect Generate, Rect Save) SplitToolbar(Rect toolbar)
    {
        float saveW = MathF.Min(SaveButtonWidth, MathF.Max(0f, toolbar.Width - ToolbarGap * 2f));
        var save = new Rect(toolbar.Right - saveW - ToolbarGap,
            toolbar.Y + (toolbar.Height - SaveButtonHeight) * 0.5f, saveW, SaveButtonHeight);
        float generateW = _generateDungeonButton is null ? 0f :
            MathF.Min(GenerateButtonWidth, MathF.Max(0f, save.X - toolbar.X - ToolbarGap * 2f));
        var generate = generateW <= 0f ? default :
            new Rect(save.X - generateW - ToolbarGap, save.Y, generateW, save.Height);
        float rightOfView = generateW <= 0f ? save.X : generate.X;
        float viewW = MathF.Min(ViewButtonWidth, MathF.Max(0f, rightOfView - toolbar.X - ToolbarGap * 2f));
        var view = new Rect(rightOfView - viewW - ToolbarGap, save.Y, viewW, save.Height);
        var tabs = new Rect(toolbar.X, toolbar.Y, MathF.Max(0f, view.X - toolbar.X - ToolbarGap), toolbar.Height);
        return (tabs, view, generate, save);
    }

}
