using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class TreeViewRowPaintTests
{
    [Fact]
    public void Custom_painter_replaces_labels_on_visible_rows_with_clipped_indented_content()
    {
        using var rig = new TreeViewDrawRig();
        TreeView tree = NewTree();
        var painted = new List<(Rect Bounds, TreeNode Node, bool Selected)>();
        SetPainter(tree, (batch, bounds, node, selected) =>
        {
            Assert.Same(rig.Batch, batch);
            painted.Add((bounds, node, selected));
        });

        rig.Draw(tree);

        Assert.Equal(new[]
        {
            (new Rect(30, 20, 180, 12), tree.Roots[0], true),
            (new Rect(46, 32, 164, 24), tree.Roots[0].Children[0], false),
            (new Rect(30, 56, 180, 14), tree.Roots[1], false),
        }, painted);
        // One selected fill and two two-line carets remain. None of the three default glyphs are drawn.
        Assert.Equal(5, rig.Batch.FrameStats.Quads);
        Assert.Equal(0, rig.Batch.ScissorDepth);
    }

    [Fact]
    public void Custom_content_draws_in_the_row_clip_and_keeps_the_outer_clip()
    {
        using var rig = new TreeViewDrawRig();
        TreeView tree = NewTree();
        var clips = new List<Rect>();
        SetPainter(tree, (batch, bounds, node, selected) =>
        {
            clips.Add(rig.CommandList.Scissors[^1]);
            GuiDraw.Fill(batch, rig.White, bounds, Vector4.One);
            GuiDraw.Fill(batch, rig.White, new Rect(bounds.Right - 4, bounds.Y, 4, bounds.Height), Vector4.One);
        });

        rig.Draw(tree, new Rect(40, 25, 150, 40));

        Assert.Equal(new[]
        {
            new Rect(40, 25, 150, 7),
            new Rect(46, 32, 144, 24),
            new Rect(40, 56, 150, 9),
        }, clips);
        Assert.Equal(11, rig.Batch.FrameStats.Quads);
        Assert.Equal(new Rect(40, 25, 150, 40), rig.CommandList.Scissors[^2]);
        Assert.Equal(0, rig.Batch.ScissorDepth);
    }

    [Fact]
    public void Clearing_the_custom_painter_restores_default_labels()
    {
        using var rig = new TreeViewDrawRig();
        TreeView tree = NewTree();
        SetPainter(tree, (_, _, _, _) => { });
        rig.Draw(tree);
        Assert.Equal(5, rig.Batch.FrameStats.Quads);

        SetPainter(tree, null);
        rig.Draw(tree);

        Assert.Equal(8, rig.Batch.FrameStats.Quads);
    }

    [Fact]
    public void Painter_exception_restores_the_callers_clip()
    {
        using var rig = new TreeViewDrawRig();
        TreeView tree = NewTree();
        SetPainter(tree, (_, _, _, _) => throw new InvalidOperationException("paint failed"));
        rig.Batch.NewFrame(rig.CommandList, 400, 300);
        rig.Batch.Begin();
        rig.Batch.SetScissor(new Rect(40, 25, 150, 40));

        Assert.Throws<InvalidOperationException>(() => tree.Draw(rig.Batch, rig.White, rig.Font));

        Assert.Equal(1, rig.Batch.ScissorDepth);
        Assert.Equal(new Rect(40, 25, 150, 40), rig.CommandList.Scissors[^1]);
        rig.Batch.ClearScissor();
        rig.Batch.End();
    }

    [Fact]
    public void A_row_without_visible_content_does_not_call_the_custom_painter()
    {
        using var rig = new TreeViewDrawRig();
        TreeView tree = NewTree();
        tree.Bounds = new Rect(10, 20, 20, 50);
        int painted = 0;
        tree.DrawRow = (_, _, _, _) => painted++;

        rig.Draw(tree);

        Assert.Equal(0, painted);
        Assert.Equal(5, rig.Batch.FrameStats.Quads);
    }

    static TreeView NewTree()
    {
        var tree = new TreeView(new Rect(10, 20, 200, 50))
        {
            ScrollOffset = 12,
            Style = new GuiStyle { Text = Vector4.One, SelectedFill = Vector4.One },
        };
        var parent = new TreeNode(LocalizedText.Raw("A")) { Expanded = true };
        parent.Children.Add(new TreeNode(LocalizedText.Raw("B")));
        tree.Roots.Add(parent);
        var collapsed = new TreeNode(LocalizedText.Raw("C"));
        collapsed.Children.Add(new TreeNode(LocalizedText.Raw("hidden")));
        tree.Roots.Add(collapsed);
        tree.Roots.Add(new TreeNode(LocalizedText.Raw("offscreen")));
        tree.Selected = parent;
        return tree;
    }

    static void SetPainter(TreeView tree, Action<SpriteBatch, Rect, TreeNode, bool>? painter) =>
        tree.DrawRow = painter;
}
