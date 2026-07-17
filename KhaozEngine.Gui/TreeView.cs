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

        // Drag-and-drop reorder state. `_dragNode` is non-null once a press clears `DragThreshold` and grabs a row.
        // `_dragSiblings` / `_dragFromIndex` pin the sibling list and origin slot. The `_drop*` fields are the live
        // insertion target recomputed each frame (and drawn as the insertion line). `_dragCancelled` latches an
        // Escape / disable abort so the release that ends the same gesture cannot fall through to a tap-select.
        TreeNode? _dragNode;
        List<TreeNode>? _dragSiblings;
        int _dragFromIndex;
        int _dropIndex;
        int _dropRow;
        bool _dropAfter;
        bool _dropValid;
        bool _dragCancelled;

        /// <summary>The view rect the caller owns. Rows lay out downward from its top edge, offset by <see cref="ScrollOffset"/>.</summary>
        public Rect Bounds;

        /// <summary>The top-level nodes. Build the hierarchy by adding roots and their <see cref="TreeNode.Children"/>.</summary>
        public List<TreeNode> Roots { get; } = new();

        /// <summary>Row height in pixels. Default 24.</summary>
        public float RowHeight { get; set; } = 24f;

        /// <summary>Horizontal indent per depth level, and the width of the caret zone at each level. Default 16.</summary>
        public float Indent { get; set; } = 16f;

        /// <summary>
        /// Rows advanced per one full wheel unit. The wheel step is continuous (see <see cref="WheelSpeed"/>), not
        /// rounded to an integer notch count: a <c>ScrollDelta</c> of magnitude 1 (one physical wheel click) moves
        /// this many rows, and a fractional trackpad delta moves the matching fraction. Default 3 matches
        /// <see cref="PropertyGrid.WheelRowsPerNotch"/> for the same side-by-side feel.
        /// </summary>
        public float WheelRowsPerNotch { get; set; } = 3f;

        /// <summary>
        /// Pixels scrolled per one unit of wheel <c>ScrollDelta</c>: <c>RowHeight * WheelRowsPerNotch</c>. Exposed
        /// under the same name and used the same way as <see cref="ScrollablePanel.WheelSpeed"/>
        /// (<c>ScrollOffset -= input.ScrollDelta * WheelSpeed</c>, continuous, no per-notch rounding) so every
        /// scrollable widget in the package shares the idiom.
        /// </summary>
        public float WheelSpeed => RowHeight * WheelRowsPerNotch;

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

        /// <summary>True on the frame a drag-and-drop reorder committed (a valid same-parent drop that moved the row).</summary>
        public bool WasReordered { get; private set; }

        /// <summary>Pixels the pointer must travel from the press origin before a held press becomes a row drag
        /// rather than a tap. Below this the gesture is still a tap (select or caret toggle). Default 6.</summary>
        public float DragThreshold { get; set; } = 6f;

        /// <summary>Fired when a body tap selects a node, after <see cref="Selected"/> is updated.</summary>
        public Action<TreeNode>? OnSelected;

        /// <summary>
        /// Fired when a drag-and-drop reorder commits: the dragged node moves within its parent's sibling list from
        /// <c>oldIndex</c> to <c>newIndex</c>, exactly as a <c>RemoveAt(oldIndex)</c> then <c>Insert(newIndex)</c>
        /// on that list. The widget only reports the move (it does not mutate <see cref="Roots"/> or any
        /// <see cref="TreeNode.Children"/>). The host applies it and rebuilds the tree. Drops are same-parent only,
        /// so both indices address the same sibling list. A no-op drop (<c>newIndex == oldIndex</c>), a cross-parent
        /// drop, or a release off the tree fires nothing.
        /// </summary>
        public Action<TreeNode, int, int>? OnReordered;

        /// <summary>Consulted before a held press ARMS a drag on its press-origin row (only when <see cref="OnReordered"/>
        /// is wired): the row's node is passed in, and returning false blocks the drag outright, so that row never
        /// grabs, never shows the insertion line, and never fires <see cref="OnReordered"/> (a held-then-released
        /// press on it still falls through to the plain tap path). Null (the default) means every row is reorderable,
        /// preserving the pre-predicate behavior. A host wires this when only some rows carry list-order semantics
        /// (e.g. an outline where a few kinds reorder and the rest do not), instead of arming a drag that the drop
        /// handler would only reject after the fact.</summary>
        public Func<TreeNode, bool>? CanReorder;

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

        /// <summary>
        /// The first node (depth-first over <see cref="Roots"/>, regardless of <see cref="TreeNode.Expanded"/> - a
        /// collapsed subtree is still searched) whose <see cref="TreeNode.Tag"/> satisfies <paramref name="predicate"/>,
        /// or null when none match. A host uses this to resolve a caller-owned identity (e.g. an outline reference)
        /// back to the live node after <see cref="Roots"/> is rebuilt from fresh data.
        /// </summary>
        public TreeNode? FindByTag(Func<object?, bool> predicate)
        {
            foreach (TreeNode root in Roots)
            {
                TreeNode? found = FindByTag(root, predicate);
                if (found is not null) return found;
            }
            return null;
        }

        static TreeNode? FindByTag(TreeNode node, Func<object?, bool> predicate)
        {
            if (predicate(node.Tag)) return node;
            foreach (TreeNode child in node.Children)
            {
                TreeNode? found = FindByTag(child, predicate);
                if (found is not null) return found;
            }
            return null;
        }

        /// <summary>The screen rect of visible row <paramref name="visibleIndex"/> (pure arithmetic from
        /// <see cref="Bounds"/>, <see cref="RowHeight"/>, and <see cref="ScrollOffset"/>). May lie outside <see cref="Bounds"/>.</summary>
        public Rect RowBounds(int visibleIndex) =>
            new(Bounds.X, Bounds.Y + visibleIndex * RowHeight - ScrollOffset, Bounds.Width, RowHeight);

        /// <summary>
        /// Bring <paramref name="node"/> into view: expands every collapsed ancestor so it re-joins the visible walk
        /// (a node hidden behind a collapsed parent cannot be "in view" at all), then scrolls the minimal amount
        /// needed so its row sits fully inside <see cref="Bounds"/> - an already-visible row is left untouched.
        /// Clamped to <c>[0, maxScroll]</c> like <see cref="ScrollablePanel.ScrollTo(float)"/>'s clamp idiom, so a
        /// row near the end of a long list cannot scroll past its content. No-op when <paramref name="node"/> is not
        /// reachable from <see cref="Roots"/> at all.
        /// </summary>
        public void ScrollTo(TreeNode node)
        {
            if (!ExpandAncestorsOf(node)) return;

            IReadOnlyList<(TreeNode Node, int Depth)> rows = VisibleRows();
            int index = -1;
            for (int i = 0; i < rows.Count; i++)
                if (ReferenceEquals(rows[i].Node, node)) { index = i; break; }
            if (index < 0) return;   // should be unreachable once every ancestor is expanded, but guard anyway

            float top = index * RowHeight;
            float bottom = top + RowHeight;
            float target = ScrollOffset;
            if (top < target) target = top;
            else if (bottom > target + Bounds.Height) target = bottom - Bounds.Height;

            float max = MathF.Max(0f, rows.Count * RowHeight - Bounds.Height);
            ScrollOffset = Math.Clamp(target, 0f, max);
        }

        // Expand every ancestor of `node` (a depth-first search over Roots), leaving Expanded untouched on any
        // branch that does not lead to it. Returns true when `node` is reachable from Roots at all (a root itself
        // counts, needing no expansion), false when it is not part of this tree.
        bool ExpandAncestorsOf(TreeNode node)
        {
            foreach (TreeNode root in Roots)
                if (ExpandAncestors(root, node)) return true;
            return false;
        }

        static bool ExpandAncestors(TreeNode node, TreeNode target)
        {
            if (ReferenceEquals(node, target)) return true;
            foreach (TreeNode child in node.Children)
            {
                if (ExpandAncestors(child, target))
                {
                    node.Expanded = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Reserve the region on the pointer, apply wheel scrolling, run the drag-and-drop reorder gesture, and
        /// hit-test a tap for this frame. Only armed when <see cref="OnReordered"/> is wired: with no handler the
        /// drag path is fully inert and every held press is a plain tap candidate. When a handler is wired, a held
        /// press whose origin is in the tree becomes a row drag once the pointer clears <see cref="DragThreshold"/>
        /// (grabbing the press-origin row, unless that press landed in a parent's caret zone, which stays reserved
        /// for the expand toggle, or <see cref="CanReorder"/> rejects that row, which blocks the arm outright). The
        /// wheel scrolls whenever the pointer is over the tree, including while a drag
        /// is armed: the drop geometry below (<see cref="RowAt"/>/<see cref="RowBounds"/>) reads the live
        /// <see cref="ScrollOffset"/>, so a reorder drag can scroll a long list mid-gesture instead of freezing.
        /// A valid same-parent release fires <see cref="OnReordered"/>, Escape or an off-tree release cancels.
        /// Otherwise a caret-zone tap toggles expansion and any other in-bounds tap selects the row. Returns true
        /// if the selection, an expansion, or a reorder changed. Ignores everything after the region reservation
        /// when disabled.
        /// </summary>
        public bool Update(InputManager input)
        {
            WasSelectionChanged = false;
            WasExpansionChanged = false;
            WasReordered = false;
            input.BlockInputRegion(Bounds);
            if (!Enabled) { _dragCancelled = _dragNode is not null; AbandonDrag(); return false; }

            IReadOnlyList<(TreeNode Node, int Depth)> rows = VisibleRows();

            if (input.IsPointerJustPressed) _dragCancelled = false;   // a fresh gesture clears the abort latch

            // Escape aborts an in-flight drag outright (no drop) and latches so the release cannot tap-select.
            if (_dragNode is not null && input.IsKeyDown(Key.Escape)) { AbandonDrag(); _dragCancelled = true; return false; }

            // Wheel scrolls whenever the pointer is over the tree, continuous (no per-notch rounding) like
            // ScrollablePanel - including mid-drag, so a long list can scroll while reordering. This runs BEFORE
            // TrackDrag/ComputeDrop below, so the same frame's drop geometry resolves against the just-updated
            // ScrollOffset rather than a stale one.
            if (input.IsPointerIn(Bounds) && input.ScrollDelta != 0f)
            {
                float max = MathF.Max(0f, rows.Count * RowHeight - Bounds.Height);
                ScrollOffset = Math.Clamp(ScrollOffset - input.ScrollDelta * WheelSpeed, 0f, max);
            }

            // A held press whose origin is in the tree is a (potential) drag, never a tap: arm and track it here,
            // committing nothing until release. Gated on a reorder handler being wired: with none, this path
            // stays fully inert (never arms, never draws the insertion line) so a held-then-released press falls
            // through to the tap check below exactly as it did before the drag feature existed.
            if (OnReordered is not null && input.IsDragStartIn(Bounds)) { TrackDrag(input, rows); return false; }

            // Release of an armed drag: the release position is the authoritative drop slot, so an off-tree or
            // cross-parent release cancels and a valid slot that actually moves the row fires OnReordered.
            if (input.IsPointerJustReleased && _dragNode is not null)
            {
                ComputeDrop(input.PointerPosition, rows);
                if (_dropValid && _dropIndex != _dragFromIndex)
                {
                    OnReordered?.Invoke(_dragNode, _dragFromIndex, _dropIndex);
                    WasReordered = true;
                }
                AbandonDrag();
                return WasReordered;
            }

            // Idle tap: the caret-vs-body split. Must land inside the view to hit a row, which also drops taps in
            // the band a scrolled-away row would occupy past the clipped edge (the release fails Bounds.Contains
            // there). Suppressed for the rest of a gesture that Escape aborted.
            if (!_dragCancelled && input.IsTapIn(Bounds))
            {
                Vector2 pos = input.PointerPosition;
                int i = RowAt(pos.Y, rows.Count);
                if (i >= 0)
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

            return WasSelectionChanged || WasExpansionChanged || WasReordered;
        }

        // The visible-row index at design-space Y, or -1 when it falls outside the row range (scrolled/clipped away).
        int RowAt(float y, int count)
        {
            int i = (int)MathF.Floor((y - Bounds.Y + ScrollOffset) / RowHeight);
            return (i >= 0 && i < count) ? i : -1;
        }

        // Arm the drag once the pointer clears the threshold (grabbing the press-origin row, unless that press
        // began in a parent's caret zone), then recompute the live drop slot from the current pointer.
        void TrackDrag(InputManager input, IReadOnlyList<(TreeNode Node, int Depth)> rows)
        {
            Vector2 pos = input.PointerPosition;
            Vector2 origin = input.PressOrigin;

            if (_dragNode is null)
            {
                if ((pos - origin).Length() < DragThreshold) return;
                int srcRow = RowAt(origin.Y, rows.Count);
                if (srcRow < 0) return;
                (TreeNode node, int depth) = rows[srcRow];
                float caretStart = Bounds.X + depth * Indent;
                if (node.Children.Count > 0 && origin.X >= caretStart && origin.X < caretStart + Indent) return;   // caret gesture, not a drag
                if (CanReorder is not null && !CanReorder(node)) return;   // this row is not reorderable: never arm
                if (!TryFindSiblings(node, out List<TreeNode>? siblings, out _dragFromIndex)) return;
                _dragSiblings = siblings;
                _dragNode = node;
            }

            ComputeDrop(pos, rows);
        }

        // The insertion slot for the current pointer, constrained to the dragged node's own sibling list (the
        // same-parent rule). Sets `_dropValid` false when the pointer is off the tree or over a non-sibling row.
        void ComputeDrop(Vector2 pos, IReadOnlyList<(TreeNode Node, int Depth)> rows)
        {
            _dropValid = false;
            if (!Bounds.Contains(pos)) return;
            int over = RowAt(pos.Y, rows.Count);
            if (over < 0) return;

            int sib = _dragSiblings!.IndexOf(rows[over].Node);
            if (sib < 0) return;   // over a row in a different parent: reject (indicator hidden)

            Rect r = RowBounds(over);
            bool after = pos.Y - r.Y >= RowHeight * 0.5f;
            int raw = sib + (after ? 1 : 0);
            // Translate the raw slot in the original list to the RemoveAt(from)+Insert(to) target: any slot past
            // the dragged node shifts down one once it is pulled out.
            _dropIndex = raw > _dragFromIndex ? raw - 1 : raw;
            _dropRow = over;
            _dropAfter = after;
            _dropValid = true;
        }

        // Locate the sibling list a node lives in (its parent's Children, or Roots for a top-level node) and its
        // index there. Depth-first over the same walk the layout uses.
        bool TryFindSiblings(TreeNode node, out List<TreeNode>? siblings, out int index)
        {
            index = Roots.IndexOf(node);
            if (index >= 0) { siblings = Roots; return true; }
            foreach (TreeNode root in Roots)
                if (FindInChildren(root, node, out siblings, out index)) return true;
            siblings = null;
            index = -1;
            return false;
        }

        static bool FindInChildren(TreeNode parent, TreeNode target, out List<TreeNode>? siblings, out int index)
        {
            index = parent.Children.IndexOf(target);
            if (index >= 0) { siblings = parent.Children; return true; }
            foreach (TreeNode child in parent.Children)
                if (FindInChildren(child, target, out siblings, out index)) return true;
            siblings = null;
            index = -1;
            return false;
        }

        void AbandonDrag()
        {
            _dragNode = null;
            _dragSiblings = null;
            _dropValid = false;
        }

        /// <summary>
        /// Draw the visible rows clipped to <see cref="Bounds"/>: a fill under the selected row, a caret chevron for
        /// nodes with children (up when expanded, matching the <see cref="Dropdown"/> chevron idiom), and the
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
                    GuiDraw.FillStyled(batch, white, row, Style, GuiDraw.WithOpacity(Style.SelectedFill, Opacity),
                        GuiDraw.WithOpacity(Style.SelectedBorder, Opacity));

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

            // The drag insertion line: a thin bar at the boundary of the target row (its bottom edge when dropping
            // after, its top edge when before). Only shown for a valid same-parent target.
            if (_dragNode is not null && _dropValid)
            {
                Rect target = RowBounds(_dropRow);
                float y = _dropAfter ? target.Bottom : target.Y;
                GuiDraw.Fill(batch, white, new Rect(Bounds.X, y - 1f, Bounds.Width, 2f),
                    GuiDraw.WithOpacity(Style.Text, Opacity));
            }
            batch.ClearScissor();
        }
    }
}
