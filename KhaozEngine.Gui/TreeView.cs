using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// One node in a <see cref="TreeView"/>: a <see cref="LocalizedText"/> label, a list of children, an expansion
    /// flag, and a caller-owned <see cref="Tag"/> for identity. Plain data with no widget state, so a host can build
    /// the hierarchy once and mutate <see cref="Expanded"/> / <see cref="Children"/> freely between frames.
    /// </summary>
    public sealed class TreeNode
    {
        /// <summary>Build a node with a resolved-at-draw label and an optional identity tag.</summary>
        public TreeNode(LocalizedText label, object? tag = null)
        {
            Label = label;
            Tag = tag;
        }

        /// <summary>The (lazily resolved) row label. Re-resolves on every draw, so a locale switch takes effect next frame.</summary>
        public LocalizedText Label { get; set; }

        /// <summary>When true, this node's <see cref="Children"/> are part of the visible walk. Leaf nodes ignore it.</summary>
        public bool Expanded { get; set; }

        /// <summary>Caller-owned identity payload (e.g. a scene-object handle). The widget never reads it.</summary>
        public object? Tag { get; set; }

        /// <summary>The child nodes, walked depth-first when <see cref="Expanded"/>. Mutate freely between frames.</summary>
        public List<TreeNode> Children { get; } = new();
    }

    /// <summary>
    /// A scrollable hierarchy view over <see cref="TreeNode"/> roots. Rows are laid out by pure arithmetic (the
    /// <see cref="ScrollablePanel"/> convention): <see cref="VisibleRows"/> is the depth-first walk that skips
    /// collapsed subtrees, and <see cref="RowBounds"/> turns a visible index into a screen rect. A tap whose X falls
    /// in a row's caret zone (the <see cref="Indent"/>-wide band at the node's depth) toggles expansion for a node
    /// with children, while a tap anywhere else in the row selects it. The wheel scrolls when the pointer is over
    /// <see cref="Bounds"/>, clamped to the content. Follows the <see cref="Toggle"/> anatomy: reserves its region on
    /// the pointer first (even when disabled), reads a <see cref="GuiStyle"/> for its colours, and resolves labels at
    /// draw time. Content is clipped to <see cref="Bounds"/> via the <see cref="SpriteBatch"/> scissor.
    /// </summary>
    public sealed class TreeView
    {
        /// <summary>Pixels of horizontal padding between the caret column and the label text.</summary>
        const float LabelPadding = 4f;

        readonly List<(TreeNode Node, int Depth)> _visible = new();

        /// <summary>The view rect the caller owns. Rows lay out downward from its top edge, offset by <see cref="ScrollOffset"/>.</summary>
        public Rect Bounds;

        /// <summary>The top-level nodes. Build the hierarchy by adding roots and their <see cref="TreeNode.Children"/>.</summary>
        public List<TreeNode> Roots { get; } = new();

        /// <summary>Row height in pixels. Default 24.</summary>
        public float RowHeight { get; set; } = 24f;

        /// <summary>Horizontal indent per depth level, and the width of the caret zone at each level. Default 16.</summary>
        public float Indent { get; set; } = 16f;

        /// <summary>Vertical scroll in pixels. Wheel scrolling clamps this to the content, but a direct set is honoured as-is.</summary>
        public float ScrollOffset { get; set; }

        /// <summary>The currently selected node, or null. A body tap sets it; a caret-zone toggle leaves it untouched.</summary>
        public TreeNode? Selected { get; set; }

        /// <summary>When false, <see cref="Update"/> reserves the region on the pointer then ignores all input. Default true.</summary>
        public bool Enabled = true;

        /// <summary>Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Default 1 is a no-op.</summary>
        public float Opacity = 1f;

        /// <summary>Palette knobs read at draw time: <see cref="GuiStyle.SelectedFill"/> for the selected row and
        /// <see cref="GuiStyle.Text"/> for the caret and label. Defaults to <see cref="GuiStyle.Default"/>.</summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>True on the frame a body tap changed the selection intent (mirrors the <see cref="Toggle.WasToggled"/> idiom).</summary>
        public bool WasSelectionChanged { get; private set; }

        /// <summary>True on the frame a caret-zone tap toggled a parent node's expansion.</summary>
        public bool WasExpansionChanged { get; private set; }

        /// <summary>Fired when a body tap selects a node, after <see cref="Selected"/> is updated.</summary>
        public Action<TreeNode>? OnSelected;

        /// <summary>Create a tree view over the given screen rect. Add nodes via <see cref="Roots"/>.</summary>
        public TreeView(Rect bounds) { Bounds = bounds; }

        /// <summary>
        /// Depth-first visible rows (respecting <see cref="TreeNode.Expanded"/>), the layout and hit-test source of
        /// truth. Public so hosts and tests can reason about row order without duplicating the walk. Rebuilt on every
        /// call into a shared cached list, so materialize the result before the next call if you need to keep it.
        /// </summary>
        public IReadOnlyList<(TreeNode Node, int Depth)> VisibleRows()
        {
            _visible.Clear();
            foreach (TreeNode root in Roots) Walk(root, 0);
            return _visible;
        }

        void Walk(TreeNode node, int depth)
        {
            _visible.Add((node, depth));
            if (!node.Expanded) return;
            foreach (TreeNode child in node.Children) Walk(child, depth + 1);
        }

        /// <summary>The screen rect of visible row <paramref name="visibleIndex"/> (pure arithmetic from
        /// <see cref="Bounds"/>, <see cref="RowHeight"/>, and <see cref="ScrollOffset"/>). May lie outside <see cref="Bounds"/>.</summary>
        public Rect RowBounds(int visibleIndex) =>
            new(Bounds.X, Bounds.Y + visibleIndex * RowHeight - ScrollOffset, Bounds.Width, RowHeight);

        /// <summary>
        /// Reserve the region on the pointer, apply wheel scrolling, and hit-test a tap for this frame. A caret-zone
        /// tap on a node with children toggles its expansion, any other in-bounds tap selects the row under it. Returns
        /// true if the selection or an expansion changed. Ignores everything after the region reservation when disabled.
        /// </summary>
        public bool Update(InputManager input)
        {
            WasSelectionChanged = false;
            WasExpansionChanged = false;
            input.BlockInputRegion(Bounds);
            if (!Enabled) return false;

            IReadOnlyList<(TreeNode Node, int Depth)> rows = VisibleRows();

            int notches = input.GetScrollIn(Bounds);
            if (notches != 0)
            {
                float max = MathF.Max(0f, rows.Count * RowHeight - Bounds.Height);
                ScrollOffset = Math.Clamp(ScrollOffset - notches * RowHeight * 3f, 0f, max);
            }

            // A tap must land inside the view to hit a row, which also drops taps in the band a scrolled-away row
            // would occupy past the clipped edge (the release fails Bounds.Contains there).
            if (input.IsTapIn(Bounds))
            {
                Vector2 pos = input.PointerPosition;
                int i = (int)MathF.Floor((pos.Y - Bounds.Y + ScrollOffset) / RowHeight);
                if (i >= 0 && i < rows.Count)
                {
                    (TreeNode node, int depth) = rows[i];
                    float caretStart = Bounds.X + depth * Indent;
                    if (node.Children.Count > 0 && pos.X >= caretStart && pos.X < caretStart + Indent)
                    {
                        node.Expanded = !node.Expanded;
                        WasExpansionChanged = true;
                    }
                    else
                    {
                        Selected = node;
                        WasSelectionChanged = true;
                        OnSelected?.Invoke(node);
                    }
                }
            }

            return WasSelectionChanged || WasExpansionChanged;
        }

        /// <summary>
        /// Draw the visible rows clipped to <see cref="Bounds"/>: a fill under the selected row, a caret chevron for
        /// nodes with children (down when expanded, matching the <see cref="Dropdown"/> chevron idiom), and the
        /// label. <paramref name="white"/> is a 1x1 white texture.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
        {
            IReadOnlyList<(TreeNode Node, int Depth)> rows = VisibleRows();
            batch.SetScissor(Bounds);
            for (int i = 0; i < rows.Count; i++)
            {
                Rect row = RowBounds(i);
                if (row.Bottom <= Bounds.Y || row.Y >= Bounds.Bottom) continue;   // fully scrolled out of view

                (TreeNode node, int depth) = rows[i];
                if (ReferenceEquals(node, Selected))
                    GuiDraw.Fill(batch, white, row, GuiDraw.WithOpacity(Style.SelectedFill, Opacity));

                float caretStart = Bounds.X + depth * Indent;
                if (node.Children.Count > 0)
                {
                    var center = new Vector2(caretStart + Indent * 0.5f, row.Y + RowHeight * 0.5f);
                    GuiDraw.Caret(batch, white, center, halfWidth: 4f, halfHeight: 2f, pointingUp: node.Expanded,
                        thickness: 1.5f, GuiDraw.WithOpacity(Style.Text, Opacity));
                }

                float tx = caretStart + Indent + LabelPadding;
                float ty = row.Y + (RowHeight - font.LineHeight) * 0.5f;
                batch.DrawString(font, node.Label.Resolve(), new Vector2(MathF.Floor(tx), MathF.Floor(ty)),
                    (Color)GuiDraw.WithOpacity(Style.Text, Opacity));
            }
            batch.ClearScissor();
        }
    }
}
