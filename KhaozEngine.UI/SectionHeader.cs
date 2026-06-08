using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Static helper for drawing section header dividers in scrollable lists.
/// Renders a label on the left with a horizontal rule extending to the right edge.
/// </summary>
public static class SectionHeader
{
    private const int LabelInset = 2;
    private const int LineGap = 10;

    /// <summary>
    /// Draws a section header: left-aligned label text with a thin horizontal
    /// line from the label's right edge to the row's right edge.
    /// </summary>
    public static void Draw(SpriteBatch sb, SpriteFont font, PrimitiveRenderer renderer,
        string label, int x, int y, int width, int height, float alpha = 1f)
    {
        Color labelColor = new Color(120, 125, 140) * alpha;
        int labelY = y + (height - font.LineSpacing) / 2;
        TextHelper.Draw(sb, font, label, x + LabelInset, labelY, labelColor);

        int labelWidth = (int)font.MeasureString(label).X;
        int lineX = x + LabelInset + labelWidth + LineGap;
        int lineY = y + height / 2;
        int lineWidth = width - LabelInset - labelWidth - LineGap;

        if (lineWidth > 0)
        {
            renderer.DrawFilledRect(sb, new Rectangle(lineX, lineY, lineWidth, 1),
                new Color(40, 43, 55) * alpha);
        }
    }
}
