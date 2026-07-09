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

        static InputState Frame(Vector2 pos, bool down, float scroll = 0f)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, scroll, 960, 540);
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
        static void Tap(TreeView tree, InputManager input, Vector2 at)
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
    }
}
