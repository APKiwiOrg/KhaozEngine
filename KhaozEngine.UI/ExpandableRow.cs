using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Reusable expandable row component for scrollable panels.
/// Renders a summary row with a +/- indicator that can be tapped
/// to reveal a detail row beneath it. Works with any content
/// rendered via callbacks.
///
/// Usage:
/// 1. Create with font, renderer, input
/// 2. Call <see cref="Update"/> each frame with row bounds
/// 3. Call <see cref="DrawSummary"/> to render the summary row
/// 4. If <see cref="IsExpanded"/>, call <see cref="DrawDetail"/> for the detail area
/// 5. Use <see cref="TotalHeight"/> for layout calculations
/// </summary>
public sealed class ExpandableRow
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _font;

    /// <summary>Whether the detail row is currently visible.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Height of each row (summary and detail use the same height).</summary>
    public int RowHeight { get; set; } = 32;

    /// <summary>True on the frame the expanded state changed.</summary>
    public bool WasToggled { get; private set; }

    /// <summary>
    /// Total height consumed by this component (summary + detail if expanded).
    /// </summary>
    public int TotalHeight => IsExpanded ? RowHeight * 2 : RowHeight;

    /// <summary>The bounds of the summary row, set during <see cref="Update"/>.</summary>
    public Rectangle SummaryBounds { get; private set; }

    /// <summary>The bounds of the detail row (only valid when expanded), set during <see cref="Update"/>.</summary>
    public Rectangle DetailBounds { get; private set; }

    /// <summary>
    /// Creates a new ExpandableRow.
    /// </summary>
    public ExpandableRow(SpriteFont font, PrimitiveRenderer renderer, InputManager input)
    {
        _font = font;
        _renderer = renderer;
        _input = input;
    }

    /// <summary>
    /// Updates the expand/collapse state. Call each frame with the summary row bounds.
    /// </summary>
    /// <param name="summaryBounds">Bounds for the summary row.</param>
    public void Update(Rectangle summaryBounds)
    {
        WasToggled = false;
        SummaryBounds = summaryBounds;
        DetailBounds = new Rectangle(summaryBounds.X, summaryBounds.Y + RowHeight,
            summaryBounds.Width, RowHeight);

        if (_input.IsTapIn(summaryBounds))
        {
            IsExpanded = !IsExpanded;
            WasToggled = true;
        }
    }

    /// <summary>
    /// Draws the +/- expand indicator on the right side of the summary row.
    /// Call this after drawing your own summary content.
    /// </summary>
    public void DrawExpandIndicator(SpriteBatch spriteBatch, float alpha = 1f)
    {
        string indicator = IsExpanded ? "-" : "+";
        Color color = new Color(120, 120, 130) * alpha;
        int textY = SummaryBounds.Y + (RowHeight - _font.LineSpacing) / 2;
        TextHelper.DrawRight(spriteBatch, _font, indicator,
            SummaryBounds.Right, textY, color);
    }
}
