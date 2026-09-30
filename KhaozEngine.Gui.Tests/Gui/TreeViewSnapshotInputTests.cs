using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class TreeViewSnapshotInputTests
{
    static readonly Rect Bounds = new(0, 0, 200, 72);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_overloads_select_toggle_and_scroll_from_the_same_frame(bool manager)
    {
        TreeView tree = NewTree();
        var input = new Driver(manager);
        TreeNode? selected = null;
        tree.OnSelected = node => selected = node;

        input.Step(tree, 100, 12, true);
        Assert.True(input.Step(tree, 100, 12, false));
        Assert.Same(tree.Roots[0], tree.Selected);
        Assert.Same(tree.Roots[0], selected);
        Assert.True(tree.WasSelectionChanged);

        input.Step(tree, 8, 12, true);
        Assert.True(input.Step(tree, 8, 12, false));
        Assert.True(tree.Roots[0].Expanded);
        Assert.True(tree.WasExpansionChanged);
        Assert.False(tree.WasSelectionChanged);

        Assert.False(input.Step(tree, 100, 12, false, scroll: -0.25f));
        Assert.Equal(18, tree.ScrollOffset);
        Assert.True(input.Pointer.IsBlocked(new Vector2(100, 12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_overloads_reorder_after_mid_drag_wheel_and_preserve_escape_cancellation(bool manager)
    {
        TreeView tree = NewTree();
        var input = new Driver(manager);
        var moves = new List<(TreeNode Node, int From, int To)>();
        tree.OnReordered = (node, from, to) => moves.Add((node, from, to));

        input.Step(tree, 100, 12, true);
        input.Step(tree, 100, 30, true);
        input.Step(tree, 100, 55, true, scroll: -1f / 3f);
        Assert.True(input.Step(tree, 100, 55, false));
        Assert.Equal((tree.Roots[0], 0, 2), Assert.Single(moves));
        Assert.True(tree.WasReordered);
        Assert.Null(tree.Selected);
        Assert.Equal(24, tree.ScrollOffset);

        tree.ScrollOffset = 0;
        input.Step(tree, 100, 12, true);
        input.Step(tree, 100, 60, true);
        input.Step(tree, 100, 60, true, escape: true);
        Assert.False(input.Step(tree, 100, 60, false));
        Assert.Single(moves);
        Assert.Null(tree.Selected);
        Assert.False(tree.WasReordered);

        input.Step(tree, 100, 36, true);
        Assert.True(input.Step(tree, 100, 36, false));
        Assert.Same(tree.Roots[1], tree.Selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consumed_taps_and_outside_press_origins_never_select(bool manager)
    {
        TreeView tree = NewTree();
        var input = new Driver(manager);
        input.Step(tree, 100, 12, true);
        input.Pointer.ConsumeGesture();
        Assert.False(input.Step(tree, 100, 12, false));
        Assert.Null(tree.Selected);

        input.Step(tree, 220, 12, true);
        Assert.False(input.Step(tree, 100, 12, false));
        Assert.Null(tree.Selected);
        input.Step(tree, 100, 12, true);
        Assert.True(input.Step(tree, 100, 12, false));
        Assert.Same(tree.Roots[0], tree.Selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disabling_an_armed_drag_reserves_the_region_and_cancels_the_drop(bool manager)
    {
        TreeView tree = NewTree();
        var input = new Driver(manager);
        int moves = 0;
        tree.OnReordered = (_, _, _) => moves++;
        input.Step(tree, 100, 12, true);
        input.Step(tree, 100, 60, true);
        tree.Enabled = false;

        Assert.False(input.Step(tree, 100, 60, true, scroll: -1));
        Assert.True(input.Pointer.IsBlocked(new Vector2(100, 60)));
        Assert.Equal(0, tree.ScrollOffset);
        tree.Enabled = true;
        Assert.False(input.Step(tree, 100, 60, false));
        Assert.Equal(0, moves);
        Assert.Null(tree.Selected);
    }

    [Fact]
    public void Manager_forwarding_preserves_suppressed_wheel_input()
    {
        TreeView tree = NewTree();
        var input = new InputManager();
        input.Update(Frame(100, 12, false, scroll: -1));
        input.SuppressPointerInput();

        Assert.False(tree.Update(input));

        Assert.Equal(0, tree.ScrollOffset);
    }

    [Fact]
    public void Manager_rejects_null_input_before_changing_frame_flags()
    {
        TreeView tree = NewTree();
        var input = new Driver(manager: true);
        input.Step(tree, 100, 12, true);
        input.Step(tree, 100, 12, false);

        Assert.Throws<ArgumentNullException>(() => tree.Update(null!));

        Assert.True(tree.WasSelectionChanged);
        Assert.Same(tree.Roots[0], tree.Selected);
    }

    [Fact]
    public void Snapshot_overload_rejects_null_arguments_before_changing_frame_flags()
    {
        TreeView tree = NewTree();
        var input = new Driver(manager: false);
        input.Step(tree, 100, 12, true);
        input.Step(tree, 100, 12, false);

        Assert.Throws<ArgumentNullException>(() => tree.Update(null!, InputState.Empty));
        Assert.Throws<ArgumentNullException>(() => tree.Update(input.Pointer, null!));

        Assert.True(tree.WasSelectionChanged);
        Assert.Same(tree.Roots[0], tree.Selected);
    }

    static TreeView NewTree()
    {
        var tree = new TreeView(Bounds);
        var parent = new TreeNode(LocalizedText.Raw("parent"));
        parent.Children.Add(new TreeNode(LocalizedText.Raw("child")));
        tree.Roots.Add(parent);
        for (int i = 1; i < 6; i++) tree.Roots.Add(new TreeNode(LocalizedText.Raw("row")));
        return tree;
    }

    static InputState Frame(float x, float y, bool down, float scroll = 0, bool escape = false) => new(
        escape ? new HashSet<Key> { Key.Escape } : new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(), new HashSet<MouseButton>(),
        new Vector2(x, y), Vector2.Zero, scroll, 400, 300);

    sealed class Driver(bool manager)
    {
        readonly InputManager _manager = new();
        readonly Pointer _pointer = new();
        internal Pointer Pointer => manager ? _manager.Pointer : _pointer;

        internal bool Step(TreeView tree, float x, float y, bool down, float scroll = 0, bool escape = false)
        {
            InputState state = Frame(x, y, down, scroll, escape);
            if (manager)
            {
                _manager.Update(state);
                return tree.Update(_manager);
            }
            _pointer.Update(state);
            return tree.Update(_pointer, in state);
        }
    }
}
