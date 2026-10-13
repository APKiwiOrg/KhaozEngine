using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class BottomSheetChildInputTests
{
    static readonly Rect Available = new(100, 100, 800, 800);
    static readonly Rect Child = new(350, 750, 100, 100);
    readonly InputManager _input = new();
    readonly GuiSurface _surface = new(null!);
    readonly DraggableBottomSheet _sheet = new(new[]
    {
        BottomSheetDetent.Fixed("closed", 0, true), BottomSheetDetent.Fixed("peek", 200),
        BottomSheetDetent.Fixed("full", 600)
    }, "peek", new() { HandleToggleDetentIds = new[] { "peek", "full" } });
    readonly PannableCanvas _map = new() { Viewport = Available, ContentBounds = new(0, 0, 2000, 2000) };

    public BottomSheetChildInputTests()
    {
        _map.Camera.Position = new(1000, 1000);
        Step(Frame());
    }

    [Theory]
    [InlineData(false, .01f)]
    [InlineData(true, .01f)]
    [InlineData(false, .5f)]
    [InlineData(true, .5f)]
    public void Pending_body_contact_reaches_child_button_and_icon_without_lower_map_tap(bool touch, float hold)
    {
        Assert.False(Step(Frame(down: true, press: true, touch: touch, phase: TouchPhase.Began)));
        Assert.False(_input.Pointer.IsConsumed);
        Assert.False(Step(Frame(down: true, touch: touch), hold));
        Assert.False(_input.Pointer.IsConsumed);
        Assert.True(Step(Frame(touch: touch, phase: TouchPhase.Ended)));
        Assert.False(_input.Pointer.IsConsumed);
    }

    [Fact]
    public void Same_frame_body_tap_reaches_child_on_long_frame_without_lower_map_tap()
    {
        Assert.True(Step(Frame(press: true), .5f));
        Assert.False(_input.Pointer.IsConsumed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Body_drag_consumes_child_release_including_final_release_movement(bool touch, bool finalMovement)
    {
        Step(Frame(down: true, press: true, touch: touch, phase: TouchPhase.Began));
        if (!finalMovement)
        {
            Assert.False(Step(Frame(x: 420, down: true, touch: touch)));
            Assert.True(_input.Pointer.IsConsumed);
            Assert.False(Step(Frame(x: 420, down: true, touch: touch), .1f));
        }
        Assert.False(Step(Frame(x: 420, touch: touch, phase: TouchPhase.Ended)));
        Assert.True(_input.Pointer.IsConsumed);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Handle_and_cancelled_pending_body_contacts_cannot_activate_exposed_child(bool touch, int action)
    {
        float y = action == 0 ? 710 : 760;
        Step(Frame(y: y, down: true, press: true, touch: touch, phase: TouchPhase.Began), child: Available);
        if (action == 1) _sheet.CancelGestures();
        if (action == 2) _sheet.SetDetent("closed", false);
        Assert.True(_input.Pointer.IsConsumed);
        Assert.False(Step(Frame(y: y, down: true, touch: touch), child: Available));
        Assert.False(Step(Frame(y: y, touch: touch, phase: TouchPhase.Ended), child: Available));
        Assert.True(_input.Pointer.IsConsumed);
    }

    [Fact]
    public void Touch_drag_consumption_survives_delayed_emulated_mouse_release()
    {
        Step(Frame(down: true, press: true, touch: true, phase: TouchPhase.Began));
        Step(Frame(x: 420, down: true, touch: true));
        Step(Frame(x: 420, down: true, touch: true, phase: TouchPhase.Ended));
        Assert.False(Step(Frame(x: 420, down: true)));
        Assert.False(Step(Frame(x: 420)));
        Assert.True(_input.Pointer.IsConsumed);
    }

    [Fact]
    public void Cancel_after_touch_ends_consumes_its_pending_emulated_left_contact()
    {
        Step(Frame(down: true, press: true, touch: true, phase: TouchPhase.Began));
        Step(Frame(down: true, touch: true, phase: TouchPhase.Ended));
        Assert.False(_input.Pointer.IsConsumed);
        _sheet.SetDetent("closed", false);
        Assert.False(Step(Frame(), child: Available));
        Assert.True(_input.Pointer.IsConsumed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dismissal_after_admission_cannot_rescue_lower_map_start(bool touch)
    {
        Step(Frame(down: true, press: true, touch: touch, phase: TouchPhase.Began));
        _sheet.SetDetent("closed", false);
        Assert.False(Step(Frame(y: 850, down: true, touch: touch), child: Available));
        Assert.False(Step(Frame(y: 850, touch: touch, phase: TouchPhase.Ended), child: Available));
    }

    bool Step(InputState frame, float dt = .01f, Rect? child = null)
    {
        _input.Update(frame);
        _sheet.Update(_input, Available, default, 1000, dt);
        _surface.Begin(null, _input.Pointer);
        bool clicked = _surface.Button(null!, child ?? Child, LocalizedText.Raw("Child"));
        Assert.Equal(clicked, _surface.IconButton(child ?? Child, "child", GuiStyle.Default));
        _map.Update(_input, dt);
        Assert.False(_map.TryGetGestureTap(out _));
        Assert.False(_map.GestureController.DoubleTapped);
        Assert.Equal(new Vector2(1000, 1000), _map.Camera.Position);
        Assert.Equal(1, _map.Camera.Zoom);
        return clicked;
    }

    static InputState Frame(float x = 400, float y = 760, bool down = false, bool press = false,
        bool touch = false, TouchPhase phase = TouchPhase.Moved) =>
        new(new PointerGestureFrame(Array.Empty<PointerGestureSample>()), 0, Vector2.Zero, Vector2.One,
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            press ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            new(x, y), Vector2.Zero, 0, 2000, 2000,
            touches: touch ? new[] { new TouchPoint(1, new(x, y), phase) } : Array.Empty<TouchPoint>(),
            mouseReleased: down ? new HashSet<MouseButton>() : new HashSet<MouseButton> { MouseButton.Left });
}
