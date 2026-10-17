using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class OcclusionGateTests
{
    readonly MouseFrames _mouse = new();

    InputState Frame(Vector2 position, bool down = false, float scroll = 0f)
    {
        var held = new HashSet<MouseButton>();
        if (down) held.Add(MouseButton.Left);
        var (pressed, released) = _mouse.Advance(held);
        return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            held, pressed, position, Vector2.Zero, scroll, 1000, 1000, mouseReleased: released);
    }

    (GuiDragContext Drag, Pointer Pointer) Dragging(Vector2 position)
    {
        var pointer = new Pointer();
        pointer.Update(Frame(new Vector2(120, 120)));
        pointer.Update(Frame(new Vector2(120, 120), down: true));
        pointer.Update(Frame(position, down: true));
        var drag = new GuiDragContext();
        var source = new Rect(100, 100, 40, 40);
        Assert.True(drag.ShouldBeginDrag(pointer, source));
        Assert.True(drag.Begin(pointer, new DragPayload("item", "bag", 0), source));
        drag.BeginFrame(pointer, 0.016f);
        return (drag, pointer);
    }

    [Fact]
    public void SlotGrid_DropOntoOccludedSlot_Refused()
    {
        Assert.Equal(1, Drop(covered: false));
        Assert.Equal(0, Drop(covered: true));

        int Drop(bool covered)
        {
            var grid = new SlotGrid(new Rect(100, 100, 0, 0), count: 30, columns: 5)
            {
                SlotSize = 40f,
                Spacing = 4f,
                BeginDragPayload = i => new DragPayload("item", "bag", i)
            };
            var drag = new GuiDragContext();
            var pointer = new Pointer();
            pointer.Update(Frame(new Vector2(164, 120)));
            pointer.Update(Frame(new Vector2(164, 120), down: true));
            pointer.Update(Frame(new Vector2(120, 120), down: true));
            drag.BeginFrame(pointer, 0.016f);
            grid.Update(pointer, drag);
            drag.EndFrame();
            Assert.True(drag.IsDragging);

            int dropped = 0;
            grid.OnSlotDropped = (slot, payload) =>
            {
                Assert.Equal(0, slot);
                Assert.Equal("item", payload.Token);
                dropped++;
            };
            pointer.Update(Frame(new Vector2(120, 120)));
            drag.BeginFrame(pointer, 0.016f);
            if (covered) pointer.Occlude(new Rect(100, 100, 40, 40));
            grid.Update(pointer, drag);
            drag.EndFrame();
            return dropped;
        }
    }

    [Fact]
    public void OfferTargetIn_RefusesOccludedPosition()
    {
        (GuiDragContext drag, Pointer pointer) = Dragging(new Vector2(300, 300));
        var target = new Rect(280, 280, 60, 60);
        pointer.Occlude(target);
        Assert.False(drag.OfferTargetIn(target, "target", accepted: true));
        Assert.False(drag.IsOverTarget);

        pointer.ClearOcclusion();
        Assert.False(drag.OfferTargetIn(target, "target", accepted: true));
        Assert.True(drag.IsOverAcceptingTarget);

        pointer.Update(Frame(new Vector2(300, 300)));
        drag.BeginFrame(pointer, 0.016f);
        pointer.Occlude(target);
        Assert.False(drag.OfferTargetIn(target, "target", accepted: true));
        Assert.False(drag.IsOverTarget);
        Assert.False(drag.WasDropped);

        pointer.ClearOcclusion();
        Assert.True(drag.OfferTargetIn(target, "target", accepted: true));
        Assert.True(drag.WasDropped);
        Assert.Equal("target", drag.LastDrop.TargetId);
    }

    [Fact]
    public void ScrollablePanel_WheelUnderCover_Ignored()
    {
        var panel = Panel();
        var pointer = new Pointer();
        InputState state = Frame(new Vector2(150, 200), scroll: -5f);
        pointer.Update(state);
        pointer.Occlude(panel.ContentBounds);
        panel.Update(pointer, state);
        Assert.Equal(0f, panel.ScrollOffset);

        pointer.ClearOcclusion();
        panel.Update(pointer, state);
        Assert.Equal(150f, panel.ScrollOffset);
    }

    [Fact]
    public void ScrollablePanel_EdgeScrollUnderCover_Ignored()
    {
        var panel = Panel();
        panel.DragEdgeScrollingEnabled = true;
        panel.DragEdgeScrollBand = 40f;
        panel.DragEdgeScrollSpeed = 200f;
        panel.ScrollTo(100f);
        var bottom = new Vector2(150, 390);
        (GuiDragContext drag, Pointer pointer) = Dragging(bottom);
        InputState state = Frame(bottom, down: true);
        pointer.Update(state);
        drag.BeginFrame(pointer, 0.5f);
        pointer.Occlude(new Rect(140, 380, 20, 20));
        panel.Update(pointer, state, 0.5f, drag);
        Assert.Equal(100f, panel.ScrollOffset);

        pointer.ClearOcclusion();
        panel.Update(pointer, state, 0.5f, drag);
        Assert.Equal(175f, panel.ScrollOffset);
    }

    static ScrollablePanel Panel() => new(new Rect(100, 100, 200, 300))
    {
        ItemCount = 20,
        ItemHeight = 40,
        ItemSpacing = 4
    };

    [Fact]
    public void TreeView_NoDropPositionWhileOccluded()
    {
        using var rig = new TreeViewDrawRig();
        var tree = new TreeView(new Rect(0, 0, 200, 120));
        tree.Roots.Add(new TreeNode(LocalizedText.Raw("A")));
        tree.Roots.Add(new TreeNode(LocalizedText.Raw("B")));
        tree.DrawRow = (_, _, _, _) => { };
        int reordered = 0;
        tree.OnReordered = (_, from, to) =>
        {
            Assert.Equal(0, from);
            Assert.Equal(1, to);
            reordered++;
        };
        var input = new InputManager();
        var origin = new Vector2(100, 12);
        var target = new Vector2(100, 42);
        var cover = new Rect(0, 24, 200, 24);
        input.Update(Frame(origin));
        tree.Update(input);
        input.Update(Frame(origin, down: true));
        tree.Update(input);
        input.Update(Frame(target, down: true));
        tree.Update(input);
        rig.Draw(tree);
        Assert.Equal(1, rig.Batch.FrameStats.Quads);

        input.Update(Frame(target, down: true));
        input.Pointer.Occlude(cover);
        tree.Update(input);
        rig.Draw(tree);
        Assert.Equal(0, rig.Batch.FrameStats.Quads);

        input.Pointer.ClearOcclusion();
        tree.Update(input);
        rig.Draw(tree);
        Assert.Equal(1, rig.Batch.FrameStats.Quads);

        input.Pointer.OccludeAll();
        tree.Update(input);
        rig.Draw(tree);
        Assert.Equal(0, rig.Batch.FrameStats.Quads);

        input.Pointer.ClearOcclusion();
        tree.Update(input);
        rig.Draw(tree);
        Assert.Equal(1, rig.Batch.FrameStats.Quads);

        input.Update(Frame(target));
        input.Pointer.Occlude(cover);
        tree.Update(input);
        Assert.False(tree.WasReordered);
        Assert.Equal(0, reordered);

        input.Update(Frame(origin, down: true));
        tree.Update(input);
        input.Update(Frame(target, down: true));
        tree.Update(input);
        input.Update(Frame(target));
        tree.Update(input);
        Assert.True(tree.WasReordered);
        Assert.Equal(1, reordered);
    }

    [Fact]
    public void PropertyGrid_HoverUnderCover_Ignored()
    {
        var grid = new PropertyGrid(new Rect(0, 0, 300, 150));
        var row = new FloatRow(LocalizedText.Raw("A"), () => 0f, _ => { });
        grid.Rows.Add(row);
        var input = new InputManager();
        input.Update(Frame(new Vector2(10, 14)));
        grid.Update(input, 0f);
        Assert.Same(row, grid.HoveredRow);

        input.Pointer.Occlude(new Rect(0, 0, 100, 28));
        grid.Update(input, 0f);
        Assert.Null(grid.HoveredRow);

        input.Pointer.ClearOcclusion();
        grid.Update(input, 0f);
        Assert.Same(row, grid.HoveredRow);
    }

    [Fact]
    public void GuiSurface_HoverCaptured_TrueWhenOccluded()
    {
        var surface = new GuiSurface(null!);
        var pointer = new Pointer();
        pointer.Update(Frame(new Vector2(150, 120)));
        surface.Begin(null, pointer);
        Assert.False(surface.HoverCaptured);

        pointer.Occlude(new Rect(100, 100, 120, 40));
        Assert.True(surface.HoverCaptured);

        pointer.ClearOcclusion();
        Assert.False(surface.HoverCaptured);
    }

    [Fact]
    public void BottomSheet_RefusesOccludedPressOrigin()
    {
        var available = new Rect(100, 100, 800, 800);
        var sheet = new DraggableBottomSheet(new[]
        {
            BottomSheetDetent.Fixed("closed", 0, true),
            BottomSheetDetent.Fixed("peek", 200),
            BottomSheetDetent.Fixed("full", 600)
        }, "peek", new BottomSheetOptions { HandleToggleDetentIds = new[] { "peek", "full" } });
        var input = new InputManager();
        input.Update(Frame(new Vector2(400, 710)));
        sheet.Update(input, available, default, 1000, 0f);
        input.Update(Frame(new Vector2(400, 710), down: true));
        input.Pointer.Occlude(sheet.HandleBounds);
        sheet.Update(input, available, default, 1000, 0.016f);
        input.Update(Frame(new Vector2(400, 300), down: true));
        sheet.Update(input, available, default, 1000, 0.016f);
        Assert.Equal(200f, sheet.Bounds.Height);

        input.Update(Frame(new Vector2(400, 300)));
        sheet.Update(input, available, default, 1000, 0.016f);
        input.Update(Frame(new Vector2(400, 710), down: true));
        sheet.Update(input, available, default, 1000, 0.016f);
        input.Update(Frame(new Vector2(400, 300), down: true));
        sheet.Update(input, available, default, 1000, 0.016f);
        Assert.Equal(600f, sheet.Bounds.Height);
    }
}
