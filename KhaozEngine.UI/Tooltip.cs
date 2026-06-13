using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A small text line displayed inside a <see cref="Tooltip"/>.
/// </summary>
/// <param name="Text">The text to display.</param>
/// <param name="Color">The color of the text.</param>
public readonly record struct TooltipLine(string Text, Color Color);

/// <summary>
/// Reusable floating tooltip that displays a title and body lines near an
/// anchor point. Subtle bordered dark background, auto-sized to content.
/// Flips below the anchor when there isn't room above (e.g., near the top bar).
///
/// Desktop: shown while the pointer hovers over a trigger area.
/// Mobile: shown on tap, dismissed on tap elsewhere.
///
/// Callers control visibility via <see cref="Show"/> / <see cref="Hide"/>.
/// Call <see cref="Update"/> each frame for auto-dismiss logic, and
/// <see cref="Draw"/> inside an active SpriteBatch.
/// </summary>
public sealed class Tooltip
{
    private readonly PrimitiveRenderer _renderer;
    private readonly SpriteFont _titleFont;
    private readonly SpriteFont _bodyFont;
    private readonly InputManager _input;
    private readonly VirtualResolution _vr;

    private string _title = "";
    private string _titleRight = "";
    private readonly List<TooltipLine> _lines = [];
    private Vector2 _anchor;
    private bool _visible;
    private bool _showedThisFrame;

    /// <summary>Horizontal padding inside the tooltip.</summary>
    public int PaddingX { get; set; } = 10;

    /// <summary>Vertical padding inside the tooltip.</summary>
    public int PaddingY { get; set; } = 8;

    /// <summary>Gap between title and the separator line.</summary>
    public int TitleGap { get; set; } = 5;

    /// <summary>Vertical spacing between body lines.</summary>
    public int LineSpacing { get; set; } = 3;

    /// <summary>Vertical offset from the anchor point.</summary>
    public int AnchorOffsetY { get; set; } = 10;

    /// <summary>Background color.</summary>
    public Color BackgroundColor { get; set; } = new(14, 14, 24, 240);

    /// <summary>Subtle border color.</summary>
    public Color BorderColor { get; set; } = new(60, 65, 80, 200);

    /// <summary>Separator line color between title and body.</summary>
    public Color SeparatorColor { get; set; } = new(50, 55, 70, 160);

    /// <summary>Title text color.</summary>
    public Color TitleColor { get; set; } = new(220, 225, 240);

    /// <summary>True if the tooltip is currently displayed.</summary>
    public bool IsVisible => _visible;

    /// <summary>
    /// Creates a new Tooltip.
    /// </summary>
    /// <param name="renderer">Primitive renderer for background.</param>
    /// <param name="titleFont">Font for the title line.</param>
    /// <param name="bodyFont">Font for body lines.</param>
    /// <param name="input">Input manager for auto-dismiss.</param>
    /// <param name="vr">Virtual resolution for screen-edge clamping.</param>
    public Tooltip(PrimitiveRenderer renderer, SpriteFont titleFont, SpriteFont bodyFont,
        InputManager input, VirtualResolution vr)
    {
        _renderer = renderer;
        _titleFont = titleFont;
        _bodyFont = bodyFont;
        _input = input;
        _vr = vr;
    }

    /// <summary>
    /// Shows the tooltip with the given title and body lines, anchored near a position.
    /// </summary>
    /// <param name="title">Title text (rendered with title font).</param>
    /// <param name="lines">Body lines (rendered with body font).</param>
    /// <param name="anchor">Anchor position in virtual coordinates (tooltip appears above this).</param>
    /// <param name="titleRight">Optional right-aligned text on the title row.</param>
    public void Show(string title, IReadOnlyList<TooltipLine> lines, Vector2 anchor,
        string titleRight = "")
    {
        _title = title;
        _titleRight = titleRight;
        _lines.Clear();
        for (int i = 0; i < lines.Count; i++)
            _lines.Add(lines[i]);
        _anchor = anchor;
        _visible = true;
        _showedThisFrame = true;
    }

    /// <summary>
    /// Hides the tooltip.
    /// </summary>
    public void Hide()
    {
        _visible = false;
    }

    /// <summary>
    /// Updates auto-dismiss logic. On mobile, dismisses on tap outside the tooltip.
    /// Call once per frame.
    /// </summary>
    public void Update()
    {
        if (!_visible) return;

        // Don't dismiss on the same frame we showed
        if (_showedThisFrame)
        {
            _showedThisFrame = false;
            return;
        }

        // Mobile: dismiss on any tap (the next tap after the one that opened it)
        if (_input.IsMobile && _input.IsReleasedOutside(ComputeBounds()))
        {
            _visible = false;
        }
    }

    /// <summary>
    /// Draws the tooltip if visible. Must be called inside an active SpriteBatch.Begin/End.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch)
    {
        if (!_visible || _lines.Count == 0) return;

        Rectangle bounds = ComputeBounds();

        // Background
        _renderer.DrawFilledRect(spriteBatch, bounds, BackgroundColor);

        // Subtle border
        _renderer.DrawRect(spriteBatch, bounds, BorderColor, 1);

        int x = bounds.X + PaddingX;
        int y = bounds.Y + PaddingY;

        // Title
        if (!string.IsNullOrEmpty(_title))
        {
            TextHelper.Draw(spriteBatch, _titleFont, _title, x, y, TitleColor);
            if (!string.IsNullOrEmpty(_titleRight))
                TextHelper.DrawRight(spriteBatch, _bodyFont, _titleRight,
                    bounds.Right - PaddingX, y + 1, new Color(180, 180, 190));
            y += (int)_titleFont.LineSpacing + TitleGap;

            // Separator line under title
            int sepY = y - TitleGap / 2;
            _renderer.DrawLine(spriteBatch,
                new Vector2(bounds.X + PaddingX, sepY),
                new Vector2(bounds.Right - PaddingX, sepY),
                SeparatorColor, 1);
        }

        // Body lines
        for (int i = 0; i < _lines.Count; i++)
        {
            TextHelper.Draw(spriteBatch, _bodyFont, _lines[i].Text, x, y, _lines[i].Color);
            y += (int)_bodyFont.LineSpacing + LineSpacing;
        }
    }

    private Rectangle ComputeBounds()
    {
        // Measure content width
        float maxWidth = 0;
        if (!string.IsNullOrEmpty(_title))
        {
            float tw = _titleFont.MeasureString(_title).X;
            if (!string.IsNullOrEmpty(_titleRight))
                tw += _bodyFont.MeasureString(_titleRight).X + 12; // gap between title and right text
            if (tw > maxWidth) maxWidth = tw;
        }

        for (int i = 0; i < _lines.Count; i++)
        {
            float lw = _bodyFont.MeasureString(_lines[i].Text).X;
            if (lw > maxWidth) maxWidth = lw;
        }

        int contentWidth = (int)Math.Ceiling(maxWidth);
        int panelWidth = contentWidth + PaddingX * 2;

        // Measure content height
        int contentHeight = 0;
        if (!string.IsNullOrEmpty(_title))
            contentHeight += (int)_titleFont.LineSpacing + TitleGap;

        contentHeight += _lines.Count * ((int)_bodyFont.LineSpacing + LineSpacing);
        if (_lines.Count > 0)
            contentHeight -= LineSpacing; // no trailing spacing

        int panelHeight = contentHeight + PaddingY * 2;

        // Position: try above anchor first
        int px = (int)_anchor.X - panelWidth / 2;
        int py = (int)_anchor.Y - panelHeight - AnchorOffsetY;

        // If it would overlap the top bar, flip below the anchor instead
        if (py < LayoutConstants.TopBarHeight + 4)
        {
            py = (int)_anchor.Y + AnchorOffsetY;
        }

        // Horizontal clamp to viewport
        px = Math.Clamp(px, 4, Math.Max(4, _vr.Width - panelWidth - 4));

        // Vertical clamp (safety net)
        py = Math.Clamp(py, LayoutConstants.TopBarHeight + 2,
            Math.Max(LayoutConstants.TopBarHeight + 2, _vr.Height - panelHeight - 4));

        return new Rectangle(px, py, panelWidth, panelHeight);
    }
}
