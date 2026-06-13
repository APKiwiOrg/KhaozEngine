using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A small semi-transparent floating panel that displays stat lines.
/// Reusable  -- callers provide the stat entries each frame.
/// Can be positioned at any anchor in the game area.
/// </summary>
public sealed class FloatingStatsPanel
{
    private readonly PrimitiveRenderer _renderer;
    private readonly SpriteFont _font;
    private readonly List<StatEntry> _entries = [];

    /// <summary>Horizontal padding inside the panel.</summary>
    public int PaddingX { get; set; } = 6;

    /// <summary>Vertical padding inside the panel.</summary>
    public int PaddingY { get; set; } = 4;

    /// <summary>Vertical spacing between stat lines.</summary>
    public int LineSpacing { get; set; } = 2;

    /// <summary>Background color (with alpha).</summary>
    public Color BackgroundColor { get; set; } = new(0, 0, 0, 140);

    /// <summary>Border color.</summary>
    public Color BorderColor { get; set; } = new(50, 50, 60);

    /// <summary>
    /// Creates a new FloatingStatsPanel.
    /// </summary>
    public FloatingStatsPanel(PrimitiveRenderer renderer, SpriteFont font)
    {
        _renderer = renderer;
        _font = font;
    }

    /// <summary>
    /// Clears all stat entries. Call at the start of each frame before adding entries.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
    }

    /// <summary>
    /// Adds a stat line to display.
    /// </summary>
    /// <param name="label">The stat label (e.g., "DPS").</param>
    /// <param name="value">The stat value (e.g., "902.1").</param>
    /// <param name="color">Color for the value text.</param>
    public void Add(string label, string value, Color color)
    {
        _entries.Add(new StatEntry(label, value, color));
    }

    /// <summary>
    /// Draws the panel at the given position (top-left corner of the panel).
    /// Must be called inside an active SpriteBatch.Begin/End.
    /// </summary>
    /// <param name="spriteBatch">Active SpriteBatch.</param>
    /// <param name="position">Top-left corner position in virtual coordinates.</param>
    public void Draw(SpriteBatch spriteBatch, Vector2 position)
    {
        if (_entries.Count == 0) return;

        // Measure content
        float lineHeight = _font.LineSpacing;
        float maxWidth = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            StatEntry entry = _entries[i];
            float width = _font.MeasureString($"{entry.Label}: {entry.Value}").X;
            if (width > maxWidth) maxWidth = width;
        }

        int panelWidth = (int)maxWidth + PaddingX * 2;
        int panelHeight = (int)(lineHeight * _entries.Count + LineSpacing * (_entries.Count - 1)) + PaddingY * 2;

        var bounds = new Rectangle((int)position.X, (int)position.Y, panelWidth, panelHeight);

        // Background + border
        _renderer.DrawFilledRect(spriteBatch, bounds, BackgroundColor);
        _renderer.DrawRect(spriteBatch, bounds, BorderColor, 1);

        // Stat lines  -- all positions snapped to integers for sharp text
        int posX = (int)position.X;
        int posY = (int)position.Y;
        int y = posY + PaddingY;
        for (int i = 0; i < _entries.Count; i++)
        {
            StatEntry entry = _entries[i];
            TextHelper.Draw(spriteBatch, _font, $"{entry.Label}: ", posX + PaddingX, y, new Color(160, 160, 170));

            int labelWidth = (int)_font.MeasureString($"{entry.Label}: ").X;
            TextHelper.Draw(spriteBatch, _font, entry.Value, posX + PaddingX + labelWidth, y, entry.Color);

            y += (int)lineHeight + LineSpacing;
        }
    }

    private readonly record struct StatEntry(string Label, string Value, Color Color);
}
