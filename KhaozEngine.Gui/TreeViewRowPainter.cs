using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>Paints the content after a tree row's caret while preserving the default localized label path.</summary>
internal static class TreeViewRowPainter
{
    const float LabelPadding = 4f;

    internal static void Paint(TreeView tree, SpriteBatch batch, SpriteFont font, TreeNode node,
        Rect row, float caretStart, bool selected)
    {
        float tx = caretStart + tree.Indent + LabelPadding;
        if (tree.DrawRow is { } painter)
        {
            float left = MathF.Max(tx, tree.Bounds.X);
            float top = MathF.Max(row.Y, tree.Bounds.Y);
            float right = MathF.Min(row.Right, tree.Bounds.Right);
            float bottom = MathF.Min(row.Bottom, tree.Bounds.Bottom);
            if (right <= left || bottom <= top) return;

            var content = new Rect(left, top, right - left, bottom - top);
            batch.SetScissor(content);
            try { painter(batch, content, node, selected); }
            finally { batch.ClearScissor(); }
            return;
        }

        float ty = GuiDraw.CenteredTextY(row.Y, tree.RowHeight, font.LineHeight, tree.TextScale);
        batch.DrawString(font, node.Label.Resolve(), new Vector2(MathF.Floor(tx), MathF.Floor(ty)),
            (Color)GuiDraw.WithOpacity(tree.Style.Text, tree.Opacity), tree.TextScale);
    }
}
