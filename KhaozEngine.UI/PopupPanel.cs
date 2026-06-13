using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A single content row in a popup panel. Can be a stat line, a header, or a spacer.
/// </summary>
public readonly record struct PopupRow
{
    /// <summary>Row display type.</summary>
    public PopupRowType Type { get; init; }

    /// <summary>Left-side label text.</summary>
    public string Label { get; init; }

    /// <summary>Right-side value text.</summary>
    public string Value { get; init; }

    /// <summary>Color for the value text.</summary>
    public Color ValueColor { get; init; }

    /// <summary>Optional icon color (rendered as a small filled square before the label).</summary>
    public Color? IconColor { get; init; }

    /// <summary>Creates a stat row with label, value, and optional icon color.</summary>
    public static PopupRow Stat(string label, string value, Color valueColor, Color? iconColor = null)
        => new() { Type = PopupRowType.Stat, Label = label, Value = value, ValueColor = valueColor, IconColor = iconColor };

    /// <summary>Creates a section header row.</summary>
    public static PopupRow Header(string text)
        => new() { Type = PopupRowType.Header, Label = text, Value = "", ValueColor = Color.White };

    /// <summary>Creates a vertical spacer.</summary>
    public static PopupRow Spacer()
        => new() { Type = PopupRowType.Spacer, Label = "", Value = "", ValueColor = Color.Transparent };
}

/// <summary>Types of rows in a popup panel.</summary>
public enum PopupRowType { Stat, Header, Spacer }

/// <summary>
/// Reusable full-screen popup panel with dimmed backdrop, title bar, scrollable content,
/// and a dismiss button. Fully parameterised  -- callers control size, dimming, title,
/// and provide content rows each frame.
///
/// Usage:
/// 1. Create with configuration
/// 2. Call <see cref="SetRows"/> to provide content
/// 3. Call <see cref="Update"/> for scroll + dismiss input
/// 4. Call <see cref="Draw"/> to render
/// </summary>
public sealed class PopupPanel
{
    private readonly VirtualResolution _vr;
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _titleFont;
    private readonly SpriteFont _uiFont;
    private readonly SpriteFont _bodyFont;

    private float _scrollOffset;
    private readonly List<PopupRow> _rows = [];
    private RasterizerState? _scissorRasterizer;
    private Button? _dismissButton;
    private Button? _primaryActionButton;

    // -- Configuration (set before first draw) ---------------------------

    /// <summary>Width as a fraction of viewport width (0.0-1.0). Default 0.85.</summary>
    public float WidthFraction { get; set; } = 0.85f;

    /// <summary>Maximum height as a fraction of viewport height (0.0-1.0). Default 0.85.</summary>
    public float MaxHeightFraction { get; set; } = 0.85f;

    /// <summary>Minimum height in virtual pixels. Panel grows to fit content up to max.</summary>
    public int MinHeight { get; set; } = 150;

    /// <summary>Title bar text.</summary>
    public string Title { get; set; } = "";

    /// <summary>Dismiss button text.</summary>
    public string DismissText { get; set; } = "";

    /// <summary>Whether a primary action button is shown beside the dismiss button.</summary>
    public bool ShowPrimaryAction { get; set; }

    /// <summary>Primary action button text.</summary>
    public string PrimaryActionText { get; set; } = "";

    /// <summary>Whether the primary action button is interactive.</summary>
    public bool PrimaryActionEnabled { get; set; } = true;

    /// <summary>Visual style for the primary action button.</summary>
    public ButtonStyle PrimaryActionStyle { get; set; } = ButtonStyle.Primary;

    /// <summary>True on the frame the primary action button was clicked.</summary>
    public bool WasPrimaryActionClicked { get; private set; }

    /// <summary>Scrim (background dim) opacity (0-1). Default 0.6.</summary>
    public float ScrimOpacity { get; set; } = 0.6f;

    /// <summary>Transition alpha (0 = hidden, 1 = fully visible). Set from GameScreen.TransitionAlpha.</summary>
    public float TransitionAlpha { get; set; } = 1f;

    /// <summary>Height of the title bar in virtual pixels.</summary>
    public int TitleBarHeight { get; set; } = 36;

    /// <summary>Height of the dismiss button area.</summary>
    public int DismissBarHeight { get; set; } = 40;

    /// <summary>Row height for stat lines.</summary>
    public int RowHeight { get; set; } = 24;

    /// <summary>Row height for section headers.</summary>
    public int HeaderRowHeight { get; set; } = 28;

    /// <summary>Spacer height.</summary>
    public int SpacerHeight { get; set; } = 12;

    /// <summary>Padding inside the content area.</summary>
    public int ContentPadding { get; set; } = 12;

    /// <summary>Icon size (small square before label).</summary>
    public int IconSize { get; set; } = 10;

    // -- Computed bounds -------------------------------------------------

    private Rectangle GetPanelRect()
    {
        int panelWidth = (int)(_vr.Width * WidthFraction);
        int maxHeight = (int)(_vr.Height * MaxHeightFraction);
        int contentWidth = Math.Max(1, panelWidth - ContentPadding * 2);
        int contentHeight = MeasureContentHeight(contentWidth);
        int totalHeight = TitleBarHeight + contentHeight + DismissBarHeight + ContentPadding * 2;
        totalHeight = Math.Clamp(totalHeight, MinHeight, maxHeight);

        int x = (_vr.Width - panelWidth) / 2;
        int y = (int)((_vr.Height - totalHeight) * 0.4f); // slightly above center
        return new Rectangle(x, y, panelWidth, totalHeight);
    }

    private Rectangle GetContentRect(Rectangle panel)
    {
        int top = panel.Y + TitleBarHeight + ContentPadding;
        int bottom = panel.Bottom - DismissBarHeight - ContentPadding;
        return new Rectangle(
            panel.X + ContentPadding,
            top,
            panel.Width - ContentPadding * 2,
            Math.Max(0, bottom - top));
    }

    private Rectangle GetDismissRect(Rectangle panel)
    {
        const int buttonWidth = 130;
        int buttonHeight = 30;
        int bx = panel.X + panel.Width / 2 - buttonWidth / 2;
        int by = panel.Bottom - DismissBarHeight + (DismissBarHeight - buttonHeight) / 2;
        return new Rectangle(bx, by, buttonWidth, buttonHeight);
    }

    private Rectangle GetPrimaryActionRect(Rectangle panel)
    {
        if (!ShowPrimaryAction)
            return Rectangle.Empty;

        const int buttonHeight = 30;
        const int buttonGap = 10;
        int availableWidth = panel.Width - ContentPadding * 2;
        int buttonWidth = Math.Min(130, (availableWidth - buttonGap) / 2);
        int totalWidth = buttonWidth * 2 + buttonGap;
        int bx = panel.X + panel.Width / 2 - totalWidth / 2;
        int by = panel.Bottom - DismissBarHeight + (DismissBarHeight - buttonHeight) / 2;
        return new Rectangle(bx + buttonWidth + buttonGap, by, buttonWidth, buttonHeight);
    }

    private Rectangle GetFooterDismissRect(Rectangle panel)
    {
        if (!ShowPrimaryAction)
            return GetDismissRect(panel);

        const int buttonHeight = 30;
        const int buttonGap = 10;
        int availableWidth = panel.Width - ContentPadding * 2;
        int buttonWidth = Math.Min(130, (availableWidth - buttonGap) / 2);
        int totalWidth = buttonWidth * 2 + buttonGap;
        int bx = panel.X + panel.Width / 2 - totalWidth / 2;
        int by = panel.Bottom - DismissBarHeight + (DismissBarHeight - buttonHeight) / 2;
        return new Rectangle(bx, by, buttonWidth, buttonHeight);
    }

    /// <summary>Creates a new PopupPanel.</summary>
    public PopupPanel(VirtualResolution vr, PrimitiveRenderer renderer, InputManager input,
        SpriteFont titleFont, SpriteFont uiFont, SpriteFont bodyFont)
    {
        _vr = vr;
        _renderer = renderer;
        _input = input;
        _titleFont = titleFont;
        _uiFont = uiFont;
        _dismissButton = new Button(ButtonStyle.Primary, uiFont, renderer, input);
        _primaryActionButton = new Button(ButtonStyle.Primary, uiFont, renderer, input);
        _bodyFont = bodyFont;
    }

    /// <summary>Sets the content rows to display. Call before Update/Draw each frame.</summary>
    public void SetRows(List<PopupRow> rows)
    {
        bool changed = _rows.Count != rows.Count;
        if (!changed)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (_rows[i] != rows[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return;

        _rows.Clear();
        _rows.AddRange(rows);
        _scrollOffset = 0;
    }

    /// <summary>
    /// Handles scroll and dismiss input. Returns true if the dismiss button was tapped.
    /// </summary>
    public bool Update()
    {
        WasPrimaryActionClicked = false;

        Rectangle panel = GetPanelRect();
        Rectangle content = GetContentRect(panel);
        Rectangle dismiss = GetFooterDismissRect(panel);

        // Scroll
        int scrollDelta = _input.GetScrollIn(content);
        if (scrollDelta != 0)
        {
            _scrollOffset -= scrollDelta * 0.3f;
            ClampScroll(content);
        }

        Vector2 dragDelta = _input.GetDragDelta(content);
        if (dragDelta != Vector2.Zero)
        {
            _scrollOffset -= dragDelta.Y;
            ClampScroll(content);
        }

        // Dismiss button with hover/pressed states
        _dismissButton!.Text = DismissText;
        _dismissButton.Update(dismiss);

        if (ShowPrimaryAction)
        {
            Rectangle primaryAction = GetPrimaryActionRect(panel);
            _primaryActionButton!.Text = PrimaryActionText;
            _primaryActionButton.Style = PrimaryActionStyle;
            _primaryActionButton.Enabled = PrimaryActionEnabled;
            _primaryActionButton.Update(primaryAction);
            WasPrimaryActionClicked = _primaryActionButton.WasClicked;
        }

        return _dismissButton.WasClicked;
    }

    /// <summary>Draws the full popup (scrim, panel, title, content, dismiss button).</summary>
    public void Draw(SpriteBatch spriteBatch, GraphicsDevice graphicsDevice)
    {
        float alpha = TransitionAlpha;
        Rectangle panel = GetPanelRect();
        Rectangle content = GetContentRect(panel);
        Rectangle dismiss = GetFooterDismissRect(panel);

        // Scrim
        _renderer.DrawFilledRect(spriteBatch, new Rectangle(0, 0, _vr.Width, _vr.Height),
            new Color(0, 0, 0) * (ScrimOpacity * alpha));

        // Panel background
        _renderer.DrawFilledRect(spriteBatch, panel, new Color(16, 18, 28, (int)(240 * alpha)));
        _renderer.DrawRect(spriteBatch, panel, new Color(60, 65, 85) * alpha, 1);

        // Title bar
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(panel.X, panel.Y, panel.Width, TitleBarHeight),
            new Color(22, 25, 38) * alpha);
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(panel.X, panel.Y + TitleBarHeight - 1, panel.Width, 1),
            new Color(50, 55, 70) * alpha);

        Rectangle titleRect = new(panel.X, panel.Y, panel.Width, TitleBarHeight);
        TextHelper.DrawCenteredInRect(spriteBatch, _titleFont, Title, titleRect, Color.White, alpha);

        // Dismiss button
        _dismissButton!.Text = DismissText;
        _dismissButton.Draw(spriteBatch, dismiss, alpha);

        if (ShowPrimaryAction)
        {
            Rectangle primaryAction = GetPrimaryActionRect(panel);
            _primaryActionButton!.Text = PrimaryActionText;
            _primaryActionButton.Style = PrimaryActionStyle;
            _primaryActionButton.Enabled = PrimaryActionEnabled;
            _primaryActionButton.Draw(spriteBatch, primaryAction, alpha);
        }

        // Divider above dismiss
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(panel.X + 1, panel.Bottom - DismissBarHeight, panel.Width - 2, 1),
            new Color(40, 45, 60) * alpha);

        // Content  -- scissor clipped
        spriteBatch.End();

        _scissorRasterizer ??= new RasterizerState { ScissorTestEnable = true };
        int scissorW = Math.Max(0, (int)(content.Width * _vr.Scale));
        int scissorH = Math.Max(0, (int)(content.Height * _vr.Scale));
        graphicsDevice.ScissorRectangle = new Rectangle(
            (int)(content.X * _vr.Scale), (int)(content.Y * _vr.Scale),
            scissorW, scissorH);

        spriteBatch.Begin(samplerState: SamplerState.PointClamp,
            rasterizerState: _scissorRasterizer, transformMatrix: _vr.ScaleMatrix);

        DrawContent(spriteBatch, content, alpha);

        spriteBatch.End();

        // Restore normal batch for caller
        spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: _vr.ScaleMatrix);
    }

    private void DrawContent(SpriteBatch spriteBatch, Rectangle contentRect, float alpha)
    {
        int y = contentRect.Y - (int)_scrollOffset;
        int x = contentRect.X;
        int w = contentRect.Width;

        for (int i = 0; i < _rows.Count; i++)
        {
            PopupRow row = _rows[i];
            int rowH = GetRowHeight(row, w);

            switch (row.Type)
            {
                case PopupRowType.Header:
                    TextHelper.Draw(spriteBatch, _uiFont, row.Label, x, y + 6,
                        new Color(180, 190, 210), alpha);
                    _renderer.DrawFilledRect(spriteBatch,
                        new Rectangle(x, y + rowH - 2, w, 1),
                        new Color(45, 50, 65) * alpha);
                    break;

                case PopupRowType.Stat:
                    int labelX = x;
                    if (row.IconColor.HasValue)
                    {
                        _renderer.DrawFilledRect(spriteBatch,
                            new Rectangle(x, y + (rowH - IconSize) / 2, IconSize, IconSize),
                            row.IconColor.Value * alpha);
                        _renderer.DrawRect(spriteBatch,
                            new Rectangle(x, y + (rowH - IconSize) / 2, IconSize, IconSize),
                            new Color(80, 80, 90) * alpha, 1);
                        labelX = x + IconSize + 5;
                    }

                    if (string.IsNullOrEmpty(row.Value))
                    {
                        List<string> wrappedLines = WrapText(_bodyFont, row.Label, Math.Max(1, w - (labelX - x)));
                        int textHeight = wrappedLines.Count * _bodyFont.LineSpacing;
                        int labelY = y + Math.Max(0, (rowH - textHeight) / 2);
                        Color bodyColor = row.ValueColor == Color.Transparent
                            ? new Color(170, 175, 185)
                            : row.ValueColor;

                        for (int lineIndex = 0; lineIndex < wrappedLines.Count; lineIndex++)
                        {
                            TextHelper.Draw(spriteBatch, _bodyFont, wrappedLines[lineIndex], labelX,
                                labelY + lineIndex * _bodyFont.LineSpacing, bodyColor, alpha);
                        }
                    }
                    else
                    {
                        int labelY = y + (rowH - _bodyFont.LineSpacing) / 2;
                        TextHelper.Draw(spriteBatch, _bodyFont, row.Label, labelX, labelY,
                            new Color(170, 175, 185), alpha);

                        int valueY = y + (rowH - _uiFont.LineSpacing) / 2;
                        TextHelper.DrawRight(spriteBatch, _uiFont, row.Value, x + w, valueY,
                            row.ValueColor, alpha);
                    }
                    break;

                case PopupRowType.Spacer:
                    break;
            }

            y += rowH;
        }
    }

    private int MeasureContentHeight(int contentWidth)
    {
        int total = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            total += GetRowHeight(_rows[i], contentWidth);
        }
        return total;
    }

    private int GetRowHeight(PopupRow row, int contentWidth)
    {
        if (row.Type == PopupRowType.Header)
            return HeaderRowHeight;

        if (row.Type == PopupRowType.Spacer)
            return SpacerHeight;

        if (!string.IsNullOrEmpty(row.Value))
            return RowHeight;

        int wrappedHeight = WrapText(_bodyFont, row.Label, Math.Max(1, contentWidth)).Count * _bodyFont.LineSpacing;
        return Math.Max(RowHeight, wrappedHeight);
    }

    private static List<string> WrapText(SpriteFont font, string text, int maxWidth)
    {
        List<string> lines = [];
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        string normalized = text.Replace("\r", string.Empty);
        string[] paragraphs = normalized.Split('\n');
        for (int paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            string paragraph = paragraphs[paragraphIndex];
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            StringBuilder line = new();
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                string word = words[wordIndex];
                string candidate = line.Length == 0 ? word : $"{line} {word}";
                if (font.MeasureString(candidate).X <= maxWidth)
                {
                    line.Clear();
                    line.Append(candidate);
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }

                if (font.MeasureString(word).X <= maxWidth)
                {
                    line.Append(word);
                    continue;
                }

                int segmentStart = 0;
                while (segmentStart < word.Length)
                {
                    int segmentLength = 1;
                    while (segmentStart + segmentLength <= word.Length)
                    {
                        string segment = word.Substring(segmentStart, segmentLength);
                        if (font.MeasureString(segment).X > maxWidth)
                        {
                            segmentLength--;
                            break;
                        }

                        segmentLength++;
                    }

                    if (segmentStart + segmentLength > word.Length)
                        segmentLength = word.Length - segmentStart;

                    segmentLength = Math.Max(1, segmentLength);
                    lines.Add(word.Substring(segmentStart, segmentLength));
                    segmentStart += segmentLength;
                }
            }

            if (line.Length > 0)
                lines.Add(line.ToString());
        }

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }

    private void ClampScroll(Rectangle contentRect)
    {
        int totalHeight = MeasureContentHeight(contentRect.Width);
        float maxScroll = Math.Max(0, totalHeight - contentRect.Height);
        _scrollOffset = MathHelper.Clamp(_scrollOffset, 0, maxScroll);
    }
}
