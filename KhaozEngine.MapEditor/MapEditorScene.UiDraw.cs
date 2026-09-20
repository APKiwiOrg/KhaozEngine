using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>The editor's ordered 2D draw pass over the viewport.</summary>
public partial class MapEditorScene
{
    /// <inheritdoc/>
    public override void OnDrawUi(SpriteBatch batch)
    {
        if (!_built || batch is null || _font is null || Manager is null) return;
        UiViewport? ui = Manager.UiViewport;
        if (ui is null) return;
        SpriteFont font = _font.For(ui.DpiScale);
        ChromeLayout L = ComputeLayout(ui.Width, ui.Height);

        FillPanel(batch, L.Toolbar, PanelBackground);
        FillPanel(batch, L.Outline, PanelBackground);
        FillPanel(batch, L.Inspector, PanelBackground);
        FillPanel(batch, L.Status, StatusBackground);

        (Rect tabsRect, Rect viewRect, Rect saveRect) = SplitToolbar(L.Toolbar);
        _toolbar.Bounds = tabsRect;
        _toolbar.Font = font;
        _toolbar.Draw(batch, _white);

        _saveButton.Bounds = saveRect;
        _saveButton.Font = font;
        _saveButton.Draw(batch, _white);

        _outline.Bounds = L.Outline;
        _outline.Draw(batch, _white, font);

        _inspector.Bounds = L.Inspector;
        _inspector.Draw(batch, _white, font);

        DrawPalette(batch, font, L.Palette);
        DrawViewPanel(batch, font, L, viewRect);
        string statusLine = TruncateStatusLine(StatusLine(), L.Status.Width, s => font.Measure(s).X);
        batch.DrawString(font, statusLine,
            new Vector2(MathF.Floor(L.Status.X + StatusTextInset), MathF.Floor(L.Status.Y + (StatusHeight - font.LineHeight) * 0.5f)),
            new Color(0.85f, 0.87f, 0.92f, 1f));
        DrawSculptOverlayLabel(batch, font, ui);

        // Drawn last over ordinary chrome so an inspector row description can escape the grid's scissor.
        DrawInspectorTooltip(batch, font, new Vector2(ui.Width, ui.Height));

        if (_exitDialog is not null)
        {
            _exitDialog.Viewport = new Vector2(ui.Width, ui.Height);
            _exitDialog.TitleFont = font;
            _exitDialog.BodyFont = font;
            _exitDialog.Draw(batch, _white, _ui.Pointer);
        }

        DrawSettingsDialog(batch, font, ui);
    }
}
