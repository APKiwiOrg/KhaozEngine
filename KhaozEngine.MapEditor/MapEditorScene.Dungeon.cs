using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Dungeon;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>The optional dungeon authoring action and its modal lifecycle.</summary>
public partial class MapEditorScene
{
    private const float GenerateButtonWidth = 116f;
    private Button? _generateDungeonButton;
    private DungeonGenerationDialog? _dungeonDialog;

    internal Button? GenerateDungeonButton => _generateDungeonButton;
    internal DungeonGenerationDialog? DungeonDialog => _dungeonDialog;

    private void BuildDungeonChrome()
    {
        _generateDungeonButton = _options.DungeonKit is null ? null :
            new Button(default, LocalizedText.Raw("Dungeon"), null!, OpenDungeonDialog)
            {
                Style = GuiStyle.Modern,
            };
    }

    private void UpdateDungeonButton(Rect bounds)
    {
        if (_generateDungeonButton is null) return;
        _generateDungeonButton.Bounds = bounds;
        _generateDungeonButton.Update(_ui.Pointer);
    }

    private void DrawDungeonButton(SpriteBatch batch, SpriteFont font, Rect bounds)
    {
        if (_generateDungeonButton is null) return;
        _generateDungeonButton.Bounds = bounds;
        _generateDungeonButton.Font = font;
        _generateDungeonButton.Draw(batch, _white);
    }

    internal void OpenDungeonDialog()
    {
        if (_options.DungeonKit is null || _dungeonDialog is not null ||
            _exitDialog is not null || _settingsDialog is not null) return;
        _document.SealGesture();
        try
        {
            _dungeonDialog = new DungeonGenerationDialog(_options.DungeonPreset ?? new DungeonConfig(),
                DungeonPlotCentre());
        }
        catch (DungeonJsonException ex)
        {
            _statusText = "Dungeon preset invalid. " + ex.Message;
            return;
        }
        catch (ArgumentException ex)
        {
            _statusText = "Dungeon preset invalid. " + ex.Message;
            return;
        }
        _statusText = "";
    }

    private Vector3 DungeonPlotCentre()
    {
        MapBounds bounds = _document.Doc.Bounds;
        float x = bounds.MinX + (bounds.MaxX - bounds.MinX) * 0.5f;
        float z = bounds.MinZ + (bounds.MaxZ - bounds.MinZ) * 0.5f;
        TerrainField? field = _controller.Field ?? _viewport.Field;
        UiViewport? ui = Manager?.UiViewport;
        if (ui is not null && field is not null)
        {
            InputState input = Manager!.Input;
            int width = input.Width > 0 ? input.Width : 1;
            int height = input.Height > 0 ? input.Height : 1;
            Ray ray = _camera.ScreenToRay(new Vector2(width * 0.5f, height * 0.5f), width, height);
            Vector3 direction = ray.Direction.LengthSquared() > 1e-12f
                ? Vector3.Normalize(ray.Direction)
                : _camera.Forward;
            float distance = MathF.Max(_camera.FarPlane, 25f);
            if (EditorPicking.PickTerrain(field, ray.Origin, direction, distance, out Vector3 hit)) return hit;
        }
        return new Vector3(x, field?.SampleHeight(x, z) ?? 0f, z);
    }

    private void UpdateDungeonDialog(float dt)
    {
        DungeonGenerationDialog dialog = _dungeonDialog!;
        dialog.HandleKeys(Manager!.Input);
        UiViewport? ui = Manager.UiViewport;
        if (ui is not null)
        {
            _ui.Update(Manager.Input, ui);
            dialog.Update(_ui, new Vector2(ui.Width, ui.Height), dt);
        }

        if (dialog.ConsumeGenerateRequest()) TryGenerateDungeon();
        if (dialog.CloseRequested) _dungeonDialog = null;
    }

    internal void TryGenerateDungeon()
    {
        DungeonGenerationDialog? dialog = _dungeonDialog;
        if (dialog is null || _options.DungeonKit is null) return;
        if (!dialog.TryBuild(out DungeonConfig config, out ulong seed, out DungeonPlotTransform plot)) return;

        try
        {
            _document.Execute(new GenerateDungeonCommand(config, seed, _options.DungeonKit, plot,
                loadedWindow: _window));
        }
        catch (ArgumentException ex)
        {
            dialog.ShowError(ex.Message);
            return;
        }
        catch (InvalidOperationException ex)
        {
            dialog.ShowError(ex.Message);
            return;
        }
        catch (DungeonJsonException ex)
        {
            dialog.ShowError(ex.Message);
            return;
        }

        _statusText = $"Generated dungeon from seed {seed}";
        _dungeonDialog = null;
    }

    private void DrawDungeonDialog(SpriteBatch batch, SpriteFont font, UiViewport ui) =>
        _dungeonDialog?.Draw(batch, _white, font, new Vector2(ui.Width, ui.Height));
}
