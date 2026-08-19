using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage of the retained <see cref="TreeView"/>: the depth-first <see cref="TreeView.VisibleRows"/>
    /// walk (collapsed subtrees skipped), the caret-zone-vs-body tap split (toggle expansion for parents, select
    /// otherwise), wheel scroll clamping, and the scrolled/clipped hit-test edges. No texture or font drawing
    /// (Update only computes interaction).
    /// </summary>
    public class TreeViewTests
    {
        // Tree area X 0..200 (width 200), Y 0..120 (5 rows tall at RowHeight 24).
        static readonly Rect Area = new(0, 0, 200, 120);

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests. Both
        // frame builders below share it: they drive one logical mouse.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down, float scroll = 0f)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(b);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, edgePressed, pos, Vector2.Zero, scroll, 960, 540, mouseReleased: edgeReleased);
        }

        // Fixture: roots A (children A1, A2 where A2 has child A2a) and B.
        // Access via Roots: A=Roots[0], B=Roots[1], A1=A.Children[0], A2=A.Children[1], A2a=A2.Children[0].
        static TreeView NewTree()
        {
            var a = new TreeNode(LocalizedText.Raw("A"));
            a.Children.Add(new TreeNode(LocalizedText.Raw("A1")));
            var a2 = new TreeNode(LocalizedText.Raw("A2"));
            a2.Children.Add(new TreeNode(LocalizedText.Raw("A2a")));
            a.Children.Add(a2);

            var tree = new TreeView(Area);
            tree.Roots.Add(a);
            tree.Roots.Add(new TreeNode(LocalizedText.Raw("B")));
            return tree;
        }

        // A press-origin tap (press and release both at `at`), the way the pointer fires taps.
        void Tap(TreeView tree, InputManager input, Vector2 at)
        {
            input.Update(Frame(at, false)); tree.Update(input);
            input.Update(Frame(at, true)); tree.Update(input);
            input.Update(Frame(at, false)); tree.Update(input);
        }

        [Fact]
        public void VisibleRows_RespectsExpansion()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0], b = tree.Roots[1];
            TreeNode a1 = a.Children[0], a2 = a.Children[1], a2a = a2.Children[0];

            // Collapsed: only the two roots are visible.
            Assert.Equal(new[] { a, b }, tree.VisibleRows().Select(r => r.Node).ToArray());

            // Expand A: its children appear, but A2 stays collapsed so A2a is hidden.
            a.Expanded = true;
            Assert.Equal(new[] { a, a1, a2, b }, tree.VisibleRows().Select(r => r.Node).ToArray());

            // Expand A2 too: A2a joins the walk at depth 2.
            a2.Expanded = true;
            var rows = tree.VisibleRows().ToArray();
            Assert.Equal(new[] { a, a1, a2, a2a, b }, rows.Select(r => r.Node).ToArray());
            Assert.Equal(new[] { 0, 1, 1, 2, 0 }, rows.Select(r => r.Depth).ToArray());
        }

        // VisibleRows is documented as a shared cached list rebuilt on every call: the SAME instance is handed back
        // each time (no per-call allocation), so a caller that wants to keep a result across a later call must
        // materialize it (ToArray/ToList) first, or the earlier reference's content changes out from under it.
        [Fact]
        public void VisibleRows_IsASharedListThatMustBeMaterializedBeforeTheNextCall()
        {
            var tree = NewTree();   // A collapsed (children A1/A2 hidden), B
            IReadOnlyList<(TreeNode Node, int Depth)> first = tree.VisibleRows();
            Assert.Equal(2, first.Count);   // A, B

            tree.Roots[0].Expanded = true;
            IReadOnlyList<(TreeNode Node, int Depth)> second = tree.VisibleRows();

            Assert.Same(first, second);     // the exact same list instance, not a fresh allocation
            Assert.Equal(4, first.Count);   // and `first`'s content changed out from under the earlier reference
            Assert.Equal(4, second.Count);
        }

        [Fact]
        public void TapOnRowBody_Selects()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            int fired = 0;
            TreeNode? got = null;
            tree.OnSelected = n => { fired++; got = n; };

            var input = new InputManager();
            Tap(tree, input, new Vector2(100, 12));   // middle of row 0, x well past the caret zone

            Assert.Same(a, tree.Selected);
            Assert.True(tree.WasSelectionChanged);
            Assert.False(tree.WasExpansionChanged);
            Assert.Equal(1, fired);
            Assert.Same(a, got);
        }

        [Fact]
        public void TapOnCaretZone_TogglesParent_KeepsSelection()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0], b = tree.Roots[1];
            tree.Selected = b;

            var input = new InputManager();
            Tap(tree, input, new Vector2(8, 12));   // x=8 is inside the depth-0 caret zone on row A

            Assert.True(a.Expanded);                 // flipped false -> true
            Assert.True(tree.WasExpansionChanged);
            Assert.Same(b, tree.Selected);           // selection untouched
            Assert.False(tree.WasSelectionChanged);
        }

        [Fact]
        public void TapOnCaretZone_OfLeaf_Selects()
        {
            var tree = NewTree();
            TreeNode b = tree.Roots[1];

            var input = new InputManager();
            Tap(tree, input, new Vector2(8, 36));   // caret-zone x, but row 1 (B) is a leaf -> selects

            Assert.Same(b, tree.Selected);
            Assert.True(tree.WasSelectionChanged);
            Assert.False(tree.WasExpansionChanged);
        }

        // The caret-zone band is offset by `depth * Indent`, not fixed to the depth-0 column: a tap that would land
        // OUTSIDE the depth-0 caret zone but inside a deeper row's own caret column must still toggle that row,
        // not fall through to a body-tap selection.
        [Fact]
        public void TapOnCaretZone_AtDepthGreaterThanZero_TogglesCorrectNode()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0], a2 = a.Children[1];
            a.Expanded = true;   // rows: A(0, depth0), A1(1, depth1), A2(2, depth1), B(3, depth0)

            var input = new InputManager();
            // Row 2 (A2) spans Y 48..72 at RowHeight 24, and its depth-1 caret column is X [Indent, 2*Indent) = [16,32).
            // X=8 (the depth-0 caret column) would miss this row's caret zone entirely if depth were ignored.
            Tap(tree, input, new Vector2(24, 60));

            Assert.True(a2.Expanded);
            Assert.True(tree.WasExpansionChanged);
            Assert.False(tree.WasSelectionChanged);
        }

        [Fact]
        public void Wheel_ScrollsAndClamps()
        {
            var tree = new TreeView(Area);
            for (int i = 0; i < 20; i++)
                tree.Roots.Add(new TreeNode(LocalizedText.Raw("R" + i)));
            // content = 20 * 24 = 480 px in a 120 px view -> max scroll 360.

            var input = new InputManager();

            // Scroll down a lot (wheel down = negative delta), pointer parked inside the view.
            input.Update(Frame(new Vector2(100, 60), false, -100f));
            tree.Update(input);
            Assert.Equal(360f, tree.ScrollOffset, 3);

            // Scroll up a lot: clamps back to the top.
            input.Update(Frame(new Vector2(100, 60), false, 100f));
            tree.Update(input);
            Assert.Equal(0f, tree.ScrollOffset, 3);
        }

        // The wheel step is continuous (ScrollDelta * WheelSpeed), not rounded to an integer notch count: a
        // fractional delta moves the matching fraction of a whole notch's distance.
        [Fact]
        public void Wheel_ScrollsContinuously_NoNotchRounding()
        {
            var tree = new TreeView(Area);
            for (int i = 0; i < 20; i++) tree.Roots.Add(new TreeNode(LocalizedText.Raw("R" + i)));

            var input = new InputManager();
            input.Update(Frame(new Vector2(100, 60), false, -0.5f));   // half a wheel unit down
            tree.Update(input);
            Assert.Equal(3f * tree.RowHeight * 0.5f, tree.ScrollOffset, 3);   // half of the whole-unit 72, not 0 or 72

            input.Update(Frame(new Vector2(100, 60), false, -0.25f));  // another quarter unit
            tree.Update(input);
            Assert.Equal(3f * tree.RowHeight * 0.75f, tree.ScrollOffset, 3);
        }

        // WheelSpeed is exposed under the same name/idiom as ScrollablePanel.WheelSpeed, computed from
        // RowHeight * WheelRowsPerNotch.
        [Fact]
        public void WheelSpeed_MatchesRowHeightTimesWheelRowsPerNotch()
        {
            var tree = new TreeView(Area) { RowHeight = 20f, WheelRowsPerNotch = 4f };
            Assert.Equal(80f, tree.WheelSpeed, 3);
        }

        // RowBounds is pure arithmetic from Bounds, RowHeight, and ScrollOffset - pinned directly rather than only
        // observed indirectly through hit-testing or Draw.
        [Fact]
        public void RowBounds_IsPureArithmeticFromBoundsRowHeightAndScrollOffset()
        {
            var tree = new TreeView(new Rect(10, 20, 200, 120)) { RowHeight = 24f };
            Assert.Equal(new Rect(10, 20, 200, 24), tree.RowBounds(0));
            Assert.Equal(new Rect(10, 44, 200, 24), tree.RowBounds(1));

            tree.ScrollOffset = 30f;
            Assert.Equal(new Rect(10, -10, 200, 24), tree.RowBounds(0));   // scrolled above the top: may lie outside Bounds
            Assert.Equal(new Rect(10, 14, 200, 24), tree.RowBounds(1));
        }

        [Fact]
        public void ScrolledRow_HitTestFollowsOffset()
        {
            var tree = NewTree();
            TreeNode b = tree.Roots[1];
            tree.ScrollOffset = 24f;   // one row scrolled off the top

            var input = new InputManager();
            Tap(tree, input, new Vector2(100, 6));   // the top band now shows the second row

            Assert.Same(b, tree.Selected);
        }

        [Fact]
        public void RowOutsideBounds_DoesNotHitTest()
        {
            var tree = new TreeView(Area);
            for (int i = 0; i < 10; i++)
                tree.Roots.Add(new TreeNode(LocalizedText.Raw("R" + i)));
            // Row 6 sits at y [144,168] without clipping, below Bounds.Bottom (120).

            var input = new InputManager();
            Tap(tree, input, new Vector2(100, 150));

            Assert.Null(tree.Selected);
            Assert.False(tree.WasSelectionChanged);
            Assert.False(tree.WasExpansionChanged);
        }

        [Fact]
        public void Disabled_IgnoresTaps()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            tree.Enabled = false;

            var input = new InputManager();
            Tap(tree, input, new Vector2(100, 12));   // would select A if enabled
            Assert.Null(tree.Selected);
            Assert.False(tree.WasSelectionChanged);

            Tap(tree, input, new Vector2(8, 12));     // would toggle A if enabled
            Assert.False(a.Expanded);
            Assert.False(tree.WasExpansionChanged);
        }

        // ---- drag-and-drop row reorder ------------------------------------------------------------------

        // A frame carrying an optional held key set alongside the mouse (the reorder drag needs Escape mid-press).
        InputState KeyMouseFrame(Vector2 pos, bool down, HashSet<Key>? keys = null)
        {
            var mb = new HashSet<MouseButton>();
            if (down) mb.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(mb);
            return new InputState(keys ?? new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                mb, edgePressed, pos, Vector2.Zero, 0f, 960, 540, mouseReleased: edgeReleased);
        }

        void Step(TreeView tree, InputManager input, Vector2 pos, bool down, HashSet<Key>? keys = null)
        {
            input.Update(KeyMouseFrame(pos, down, keys));
            tree.Update(input);
        }

        // The centre of visible row `i`, well past the caret column so a press there grabs the label (not the caret).
        static Vector2 RowLabel(TreeView tree, int i)
        {
            Rect r = tree.RowBounds(i);
            return new Vector2(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);
        }

        [Fact]
        public void TreeView_DragRow_FiresOnReordered_WithinParent()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)
            TreeNode a1 = a.Children[0], a2 = a.Children[1];

            TreeNode? dragged = null;
            int from = -1, to = -1, fired = 0;
            tree.OnReordered = (n, f, t) => { dragged = n; from = f; to = t; fired++; };

            var input = new InputManager();
            Rect a2Row = tree.RowBounds(2);
            var release = new Vector2(a2Row.X + a2Row.Width * 0.5f, a2Row.Y + a2Row.Height * 0.75f);   // A2's lower half

            Step(tree, input, RowLabel(tree, 1), down: false);   // hover A1
            Step(tree, input, RowLabel(tree, 1), down: true);    // press A1's label
            Step(tree, input, release, down: true);              // drag down past A2's midline (arms + tracks)
            Step(tree, input, release, down: false);             // release

            Assert.Equal(1, fired);
            Assert.Same(a1, dragged);
            Assert.Equal(0, from);                   // A1's index within A's children
            Assert.Equal(1, to);                     // moved to A2's slot (RemoveAt(0) then Insert(1))
            Assert.True(tree.WasReordered);
        }

        // Wheel scrolling is no longer frozen while a drag is armed: the wheel updates ScrollOffset mid-gesture,
        // and the SAME frame's drop geometry (TrackDrag/ComputeDrop, run right after the wheel block) resolves
        // against the just-updated offset - so a release at an unmoved screen position lands on whichever row the
        // scroll brought under the pointer, not the row that was there before scrolling.
        [Fact]
        public void Wheel_DuringArmedDrag_ScrollsAndDropResolvesAgainstNewPositions()
        {
            var tree = new TreeView(Area);   // Area (0,0,200,120), RowHeight 24 -> 5 rows visible, 20 flat roots
            for (int i = 0; i < 20; i++) tree.Roots.Add(new TreeNode(LocalizedText.Raw("R" + i)));

            TreeNode? dragged = null;
            int from = -1, to = -1, fired = 0;
            tree.OnReordered = (n, f, t) => { dragged = n; from = f; to = t; fired++; };

            var input = new InputManager();
            Vector2 origin = RowLabel(tree, 1);            // row 1's label, well past the caret zone
            var dragPos = new Vector2(origin.X, 80f);      // clears DragThreshold (6), arms the drag on row 1

            input.Update(Frame(origin, false)); tree.Update(input);    // idle: establishes position
            input.Update(Frame(origin, true)); tree.Update(input);     // press: origin pinned on row 1
            input.Update(Frame(dragPos, true)); tree.Update(input);    // held past threshold: arms, drags row 1

            // Wheel down one unit while the drag is held, pointer left exactly where it is.
            input.Update(Frame(dragPos, true, -1f)); tree.Update(input);
            Assert.Equal(3f * tree.RowHeight, tree.ScrollOffset, 3);   // one wheel unit = WheelRowsPerNotch (3) rows

            // Release at the SAME screen position: before this fix the geometry would still be frozen at the
            // pre-scroll offset, landing back on row 1 itself (a same-row no-op that fires nothing). With scroll-
            // aware geometry it resolves against the row the scroll brought under the pointer instead.
            input.Update(Frame(dragPos, false)); tree.Update(input);

            Assert.Equal(1, fired);
            Assert.Same(tree.Roots[1], dragged);
            Assert.Equal(1, from);
            Assert.Equal(5, to);
            Assert.True(tree.WasReordered);
        }

        [Fact]
        public void TreeView_DragAcrossParents_IsRejected()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)

            int fired = 0;
            tree.OnReordered = (_, _, _) => fired++;

            var input = new InputManager();
            Step(tree, input, RowLabel(tree, 1), down: false);   // hover A1 (child of A)
            Step(tree, input, RowLabel(tree, 1), down: true);    // press A1
            Step(tree, input, RowLabel(tree, 3), down: true);    // drag onto B, a root (different parent)
            Step(tree, input, RowLabel(tree, 3), down: false);   // release over B

            Assert.Equal(0, fired);                  // cross-parent drop is a no-op
            Assert.False(tree.WasReordered);
            Assert.Null(tree.Selected);              // no stray selection from the aborted gesture
        }

        [Fact]
        public void TreeView_CaretClick_StillTogglesDuringDragIdle()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];

            int reordered = 0;
            tree.OnReordered = (_, _, _) => reordered++;

            var input = new InputManager();
            Tap(tree, input, new Vector2(8, 12));    // caret-zone tap on A (a plain press-origin tap, no drag)

            Assert.True(a.Expanded);                 // expansion toggle intact under the drag code path
            Assert.True(tree.WasExpansionChanged);
            Assert.False(tree.WasReordered);
            Assert.Equal(0, reordered);
        }

        [Fact]
        public void TreeView_DragWithoutHandler_DoesNotArm_AndTapStillSelects()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)
            TreeNode a2 = a.Children[1];

            // No OnReordered wired: the drag path must stay inert no matter how far the press travels.
            var input = new InputManager();
            Vector2 release = RowLabel(tree, 2);     // A2's body, well past DragThreshold from A1's row

            Step(tree, input, RowLabel(tree, 1), down: false);   // hover A1
            Step(tree, input, RowLabel(tree, 1), down: true);    // press A1's label
            Step(tree, input, release, down: true);              // move onto A2 (would arm a drag if a handler were wired)
            Step(tree, input, release, down: false);             // release on A2

            Assert.False(tree.WasReordered);          // no insertion state ever armed, so nothing can commit
            // With the drag path inert, the release falls through to the OLD pre-drag tap path: IsTapIn(Bounds)
            // only requires the press-origin AND the release to both land inside the tree's Bounds (not the same
            // row), so a press-here-release-there gesture selects the row under the RELEASE position (A2), not
            // the press-origin row (A1).
            Assert.Same(a2, tree.Selected);
            Assert.True(tree.WasSelectionChanged);
        }

        [Fact]
        public void TreeView_EscapeCancelsDrag()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)

            int fired = 0;
            tree.OnReordered = (_, _, _) => fired++;

            var input = new InputManager();
            var esc = new HashSet<Key> { Key.Escape };

            Step(tree, input, RowLabel(tree, 1), down: false);   // hover A1
            Step(tree, input, RowLabel(tree, 1), down: true);    // press A1
            Step(tree, input, RowLabel(tree, 2), down: true);    // drag down (arms)
            Step(tree, input, RowLabel(tree, 2), down: true, esc);   // Escape while held: abort
            Step(tree, input, RowLabel(tree, 2), down: false);   // release after the abort

            Assert.Equal(0, fired);                  // aborted drag fires nothing
            Assert.False(tree.WasReordered);
            Assert.Null(tree.Selected);              // and the post-abort release does not select A2
        }

        // ---- CanReorder: per-row drag gating --------------------------------------------------------------

        // A predicate that returns false for the dragged row blocks arming: the same gesture that reorders when the
        // predicate is absent (drag A1 onto A2's lower half) now arms nothing, so the drop fires no reorder and the
        // release falls through to the plain tap path (which selects the row under the release).
        [Fact]
        public void TreeView_CanReorderFalse_BlocksArming()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)
            TreeNode a1 = a.Children[0], a2 = a.Children[1];

            int fired = 0;
            tree.OnReordered = (_, _, _) => fired++;
            tree.CanReorder = _ => false;            // no row may be dragged

            var input = new InputManager();
            Rect a2Row = tree.RowBounds(2);
            var release = new Vector2(a2Row.X + a2Row.Width * 0.5f, a2Row.Y + a2Row.Height * 0.75f);

            Step(tree, input, RowLabel(tree, 1), down: false);   // hover A1
            Step(tree, input, RowLabel(tree, 1), down: true);    // press A1's label
            Step(tree, input, release, down: true);              // drag onto A2 (would arm if unblocked)
            Step(tree, input, release, down: false);             // release on A2

            Assert.Equal(0, fired);                  // predicate blocked arming, so nothing committed
            Assert.False(tree.WasReordered);
            Assert.Same(a2, tree.Selected);          // the release falls through to the tap path (selects A2)
            Assert.Same(a1, a.Children[0]);          // the host list is the widget's, and it never moved a row
        }

        // A null predicate (the default) preserves the pre-CanReorder behavior: the drag arms and commits exactly as
        // TreeView_DragRow_FiresOnReordered_WithinParent expects.
        [Fact]
        public void TreeView_CanReorderNull_PreservesDragBehavior()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)
            TreeNode a1 = a.Children[0];

            TreeNode? dragged = null;
            int from = -1, to = -1, fired = 0;
            tree.OnReordered = (n, f, t) => { dragged = n; from = f; to = t; fired++; };
            tree.CanReorder = null;                  // explicit: all rows reorderable

            var input = new InputManager();
            Rect a2Row = tree.RowBounds(2);
            var release = new Vector2(a2Row.X + a2Row.Width * 0.5f, a2Row.Y + a2Row.Height * 0.75f);

            Step(tree, input, RowLabel(tree, 1), down: false);
            Step(tree, input, RowLabel(tree, 1), down: true);
            Step(tree, input, release, down: true);
            Step(tree, input, release, down: false);

            Assert.Equal(1, fired);
            Assert.Same(a1, dragged);
            Assert.Equal(0, from);
            Assert.Equal(1, to);
            Assert.True(tree.WasReordered);
        }

        // A predicate that returns true for the dragged row arms and commits like the null default.
        [Fact]
        public void TreeView_CanReorderTrue_ArmsAndCommits()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0];
            a.Expanded = true;                       // rows: A(0), A1(1), A2(2), B(3)
            TreeNode a1 = a.Children[0];

            int fired = 0;
            TreeNode? probed = null;
            tree.OnReordered = (_, _, _) => fired++;
            tree.CanReorder = n => { probed = n; return true; };   // every row reorderable

            var input = new InputManager();
            Rect a2Row = tree.RowBounds(2);
            var release = new Vector2(a2Row.X + a2Row.Width * 0.5f, a2Row.Y + a2Row.Height * 0.75f);

            Step(tree, input, RowLabel(tree, 1), down: false);
            Step(tree, input, RowLabel(tree, 1), down: true);
            Step(tree, input, release, down: true);
            Step(tree, input, release, down: false);

            Assert.Equal(1, fired);
            Assert.True(tree.WasReordered);
            Assert.Same(a1, probed);                 // the predicate saw the press-origin row before arming
        }

        // ---- TreeView.ScrollTo(TreeNode) / FindByTag ------------------------------------------------------

        // The visible-row index of `node`, or -1 when it is not (yet) part of the visible walk. A small local
        // helper so each case below stays a one-liner instead of re-writing the loop.
        static int VisibleIndexOf(TreeView tree, TreeNode node)
        {
            IReadOnlyList<(TreeNode Node, int Depth)> rows = tree.VisibleRows();
            for (int i = 0; i < rows.Count; i++)
                if (ReferenceEquals(rows[i].Node, node)) return i;
            return -1;
        }

        [Fact]
        public void ScrollTo_ExpandsAncestors_BringsRowIntoView()
        {
            // Case 1: a node behind a COLLAPSED ancestor chain, far enough down the flattened list that bringing
            // it into view also requires an actual scroll (not just expansion). ScrollTo expands every ancestor
            // and scrolls so the row lands fully inside Bounds.
            var tree = new TreeView(Area);   // Area height 120 -> 5 rows visible at RowHeight 24
            for (int i = 0; i < 5; i++) tree.Roots.Add(new TreeNode(LocalizedText.Raw("Filler" + i)));   // rows 0..4
            var deep = new TreeNode(LocalizedText.Raw("Deep"));
            var mid = new TreeNode(LocalizedText.Raw("Mid"));
            mid.Children.Add(deep);
            var parent = new TreeNode(LocalizedText.Raw("Parent"));
            parent.Children.Add(mid);
            tree.Roots.Add(parent);   // root index 5, collapsed: only "Parent" is visible before ScrollTo

            Assert.False(parent.Expanded);
            Assert.False(mid.Expanded);

            tree.ScrollTo(deep);

            Assert.True(parent.Expanded);
            Assert.True(mid.Expanded);
            int deepIndex = VisibleIndexOf(tree, deep);
            Assert.True(deepIndex >= 0);
            Rect deepRow = tree.RowBounds(deepIndex);
            Assert.True(deepRow.Y >= tree.Bounds.Y - 0.01f && deepRow.Bottom <= tree.Bounds.Bottom + 0.01f);
            Assert.True(tree.ScrollOffset > 0f);   // a real scroll happened, not just the expansion

            // Case 2: a node that is ALREADY fully visible (no scroll needed). ScrollTo leaves the offset
            // untouched (a no-op) and the row stays in view.
            var tree2 = new TreeView(Area);
            for (int i = 0; i < 3; i++) tree2.Roots.Add(new TreeNode(LocalizedText.Raw("R" + i)));
            float before = tree2.ScrollOffset;
            tree2.ScrollTo(tree2.Roots[1]);
            Assert.Equal(before, tree2.ScrollOffset, 3);
            Rect r2 = tree2.RowBounds(1);
            Assert.True(r2.Y >= tree2.Bounds.Y && r2.Bottom <= tree2.Bounds.Bottom);

            // Case 3: a node at the very END of a long flat list. ScrollTo clamps to the max scroll rather than
            // over-scrolling past the content (mirrors ScrollablePanel.ScrollTo's clamp idiom).
            var tree3 = new TreeView(Area);
            var nodes = new List<TreeNode>();
            for (int i = 0; i < 20; i++)
            {
                var n = new TreeNode(LocalizedText.Raw("R" + i));
                nodes.Add(n);
                tree3.Roots.Add(n);
            }
            // content = 20 * 24 = 480 px in a 120 px view -> max scroll 360.
            tree3.ScrollTo(nodes[19]);
            Assert.Equal(360f, tree3.ScrollOffset, 3);
            Rect r3 = tree3.RowBounds(19);
            Assert.Equal(tree3.Bounds.Bottom, r3.Bottom, 3);   // last row's bottom lands exactly at the view edge
        }

        [Fact]
        public void ScrollTo_NodeNotInTree_IsNoOp()
        {
            var tree = NewTree();
            var stray = new TreeNode(LocalizedText.Raw("Stray"));   // never added to Roots

            float before = tree.ScrollOffset;
            tree.ScrollTo(stray);

            Assert.Equal(before, tree.ScrollOffset, 3);
        }

        [Fact]
        public void FindByTag_ResolvesNode()
        {
            var tree = NewTree();
            TreeNode a = tree.Roots[0], a2 = a.Children[1], a2a = a2.Children[0];
            a2a.Tag = "target-id";

            // Found even though its ancestors (A, A2) are collapsed: the search ignores Expanded entirely.
            Assert.False(a.Expanded);
            Assert.False(a2.Expanded);
            TreeNode? found = tree.FindByTag(tag => tag as string == "target-id");
            Assert.Same(a2a, found);

            Assert.Null(tree.FindByTag(tag => tag as string == "missing"));
        }
    }
}
