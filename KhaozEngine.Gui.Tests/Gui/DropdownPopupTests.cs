using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public class DropdownPopupTests
{
    static readonly Rect Canvas = new(0, 0, 400, 300);
    readonly MouseFrames _mouse = new();

    static Dropdown Create(Rect? trigger = null, int count = 10) => new(
        Enumerable.Range(0, count).Select(i => new DropdownOption(LocalizedText.Raw($"Option {i}"), 100 + i)).ToArray(),
        trigger ?? new Rect(100, 100, 160, 30))
    { PopupViewport = Canvas };

    InputState Frame(Vector2 at, bool down = false, float wheel = 0, Key? key = null, bool sameFrameTap = false,
        Vector2? framebufferScale = null)
    {
        var buttons = new HashSet<MouseButton>();
        if (down) buttons.Add(MouseButton.Left);
        var (pressed, released) = _mouse.Advance(buttons);
        if (sameFrameTap) pressed.Add(MouseButton.Left);
        var keys = key.HasValue ? new HashSet<Key> { key.Value } : new HashSet<Key>();
        return new InputState(Vector2.Zero, framebufferScale ?? Vector2.One, keys, keys, new HashSet<Key>(),
            buttons, pressed, at, Vector2.Zero, wheel, 400, 300, mouseReleased: released);
    }

    void Tick(Dropdown dropdown, Pointer pointer, Vector2 at, bool down = false, float wheel = 0)
    {
        pointer.Update(Frame(at, down, wheel));
        dropdown.Update(pointer, wheel);
    }

    void Tap(Dropdown dropdown, Pointer pointer, Vector2 at)
    {
        Tick(dropdown, pointer, at, true);
        Tick(dropdown, pointer, at);
    }

    [Fact]
    public void Popup_fits_below_without_moving_the_trigger()
    {
        Rect trigger = new(100, 50, 160, 30);
        var dropdown = Create(trigger, 3);
        dropdown.Open();
        Assert.Equal(trigger, dropdown.TriggerBounds);
        Assert.Equal(new Rect(100, 80, 160, 90), dropdown.PopupBounds);
        Assert.Equal(new Rect(100, 140, 160, 30), dropdown.OptionBounds(2));
    }

    [Fact]
    public void Popup_flips_above_at_the_canvas_bottom_and_selects_the_actual_row()
    {
        Rect trigger = new(100, 270, 160, 30);
        var dropdown = Create(trigger, 3);
        var pointer = new Pointer();
        Tap(dropdown, pointer, new Vector2(150, 285));
        Assert.Equal(trigger, dropdown.TriggerBounds);
        Assert.Equal(new Rect(100, 180, 160, 90), dropdown.PopupBounds);
        Assert.Equal(new Rect(100, 240, 160, 30), dropdown.OptionBounds(2));
        Tap(dropdown, pointer, new Vector2(150, 255));
        Assert.Equal(102, dropdown.SelectedValue);
        Assert.False(dropdown.IsOpen);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(350, 240)]
    public void Popup_clamps_horizontally_independently_of_the_trigger(float x, float expectedX)
    {
        var dropdown = Create(new Rect(x, 50, 160, 30), 3);
        dropdown.Open();
        Assert.Equal(x, dropdown.TriggerBounds.X);
        Assert.Equal(new Rect(expectedX, 80, 160, 90), dropdown.PopupBounds);
        Assert.Equal(expectedX, dropdown.OptionBounds(1).X);
    }

    [Fact]
    public void Tall_popup_reports_only_visible_hit_geometry_and_reserves_that_geometry()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 145));
        Assert.Equal(new Rect(100, 130, 160, 170), dropdown.PopupBounds);
        Assert.Equal(0, dropdown.FirstVisibleIndex);
        Assert.Equal(6, dropdown.VisibleOptionCount);
        Assert.Equal(new Rect(100, 280, 160, 20), dropdown.OptionBounds(5));
        Assert.Equal(default, dropdown.OptionBounds(6));
        Assert.True(pointer.IsBlocked(new Vector2(150, 299)));
        Assert.False(pointer.IsBlocked(new Vector2(150, 301)));
    }

    [Fact]
    public void Wheel_reaches_last_option_and_selects_its_value()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 145), wheel: -100);
        Assert.True(dropdown.WheelConsumed);
        Assert.Equal(4, dropdown.FirstVisibleIndex);
        Assert.Equal(new Rect(100, 270, 160, 30), dropdown.OptionBounds(9));
        Tick(dropdown, pointer, new Vector2(150, 145));
        Assert.False(dropdown.WheelConsumed);
        Assert.Equal(4, dropdown.FirstVisibleIndex);
        Tap(dropdown, pointer, new Vector2(150, 285));
        Assert.Equal(109, dropdown.SelectedValue);
    }

    [Fact]
    public void Positive_wheel_scrolls_up_without_resetting_to_the_highlight()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 145), wheel: -2);
        Assert.Equal(2, dropdown.FirstVisibleIndex);
        Tick(dropdown, pointer, new Vector2(150, 145), wheel: 1);
        Assert.Equal(1, dropdown.FirstVisibleIndex);
        Assert.Equal(0, dropdown.HighlightedIndex);
    }

    [Fact]
    public void Clipped_portion_of_a_row_cannot_be_selected()
    {
        var dropdown = Create(new Rect(100, 80, 160, 30));
        dropdown.PopupViewport = new Rect(0, 0, 400, 195);
        var pointer = new Pointer();
        dropdown.Open();
        Assert.Equal(new Rect(100, 170, 160, 25), dropdown.OptionBounds(2));
        Assert.Equal(default, dropdown.OptionBounds(3));
        Tap(dropdown, pointer, new Vector2(150, 198));
        Assert.Equal(100, dropdown.SelectedValue);
        Assert.False(dropdown.IsOpen);
        Assert.True(pointer.IsConsumed);
    }

    [Fact]
    public void Press_scroll_release_cannot_select_the_replacement_row()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 175), true);
        Tick(dropdown, pointer, new Vector2(150, 175), true, -1);
        Tick(dropdown, pointer, new Vector2(150, 175));
        Assert.Equal(100, dropdown.SelectedValue);
        Assert.False(dropdown.WasChanged);
        Assert.True(dropdown.IsOpen);
        Assert.True(pointer.IsConsumed);
        Tap(dropdown, pointer, new Vector2(150, 175));
        Assert.Equal(102, dropdown.SelectedValue);
    }

    [Fact]
    public void Reflow_during_a_press_cannot_select_a_replacement_row()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 175), true);
        dropdown.TriggerBounds = new Rect(100, 70, 160, 30);
        Tick(dropdown, pointer, new Vector2(150, 175));
        Assert.Equal(100, dropdown.SelectedValue);
        Assert.False(dropdown.WasChanged);
        Assert.True(pointer.IsConsumed);
    }

    [Fact]
    public void Coordinate_mapping_change_cancels_an_option_press()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 175), true);
        pointer.Update(Frame(new Vector2(150, 175), framebufferScale: new Vector2(2)));
        dropdown.Update(pointer);
        Assert.Equal(100, dropdown.SelectedValue);
        Assert.True(pointer.IsConsumed);
    }

    [Fact]
    public void Same_frame_tap_selects_a_visible_row()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        pointer.Update(Frame(new Vector2(150, 175), sameFrameTap: true));
        Assert.True(dropdown.Update(pointer));
        Assert.Equal(101, dropdown.SelectedValue);
        Assert.True(pointer.IsConsumed);
    }

    [Fact]
    public void Popup_owns_wheel_only_when_open_and_under_the_pointer_even_at_a_scroll_limit()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        Tick(dropdown, pointer, new Vector2(150, 145), wheel: -1);
        Assert.False(dropdown.WheelConsumed);
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(20, 20), wheel: -1);
        Assert.False(dropdown.WheelConsumed);
        Assert.Equal(0, dropdown.FirstVisibleIndex);
        Tick(dropdown, pointer, new Vector2(150, 115), wheel: -1);
        Assert.False(dropdown.WheelConsumed);
        Tick(dropdown, pointer, new Vector2(150, 145), wheel: 1);
        Assert.True(dropdown.WheelConsumed);
        Assert.Equal(0, dropdown.FirstVisibleIndex);
        pointer.Occlude(dropdown.PopupBounds);
        dropdown.Update(pointer, -1);
        Assert.False(dropdown.WheelConsumed);
    }

    [Fact]
    public void Wheel_consumption_allows_the_parent_to_receive_a_scroll_free_snapshot()
    {
        var dropdown = Create();
        var input = new InputManager();
        var parent = new ScrollablePanel(Canvas) { ItemCount = 100 };
        dropdown.Open();
        input.Update(Frame(new Vector2(150, 145), wheel: -1));
        dropdown.Update(input, focused: true);
        parent.Update(input.Pointer, dropdown.WheelConsumed ? input.State.WithoutScroll() : input.State);
        Assert.Equal(1, dropdown.FirstVisibleIndex);
        Assert.Equal(0, dropdown.HighlightedIndex);
        Assert.Equal(0, parent.ScrollOffset);
    }

    [Fact]
    public void Bounded_input_manager_does_not_apply_unowned_wheel_as_keyboard_navigation()
    {
        var dropdown = Create();
        var input = new InputManager();
        dropdown.Open();
        input.Update(Frame(new Vector2(20, 20), wheel: -1));
        dropdown.Update(input, focused: true);
        Assert.False(dropdown.WheelConsumed);
        Assert.Equal(0, dropdown.HighlightedIndex);
        Assert.Equal(0, dropdown.FirstVisibleIndex);
    }

    [Fact]
    public void Opening_and_keyboard_wrapping_keep_the_target_visible()
    {
        var dropdown = Create();
        dropdown.SelectByValue(109);
        dropdown.Open();
        Assert.Equal(new Rect(100, 270, 160, 30), dropdown.OptionBounds(9));
        dropdown.HighlightNext();
        Assert.Equal(0, dropdown.HighlightedIndex);
        Assert.Equal(new Rect(100, 130, 160, 30), dropdown.OptionBounds(0));
        dropdown.HighlightPrevious();
        Assert.Equal(9, dropdown.HighlightedIndex);
        Assert.Equal(new Rect(100, 270, 160, 30), dropdown.OptionBounds(9));
        Assert.False(dropdown.CommitHighlight());
        Assert.False(dropdown.IsOpen);
    }

    [Fact]
    public void Keyboard_navigation_scrolls_to_and_commits_the_last_item()
    {
        var dropdown = Create();
        var input = new InputManager();
        dropdown.Open();
        for (int i = 0; i < 9; i++)
        {
            input.Update(Frame(Vector2.Zero, key: Key.Down));
            dropdown.Update(input, focused: true);
        }
        Assert.Equal(9, dropdown.HighlightedIndex);
        Assert.Equal(new Rect(100, 270, 160, 30), dropdown.OptionBounds(9));
        input.Update(Frame(Vector2.Zero, key: Key.Enter));
        Assert.True(dropdown.Update(input, focused: true));
        Assert.Equal(109, dropdown.SelectedValue);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(400, 0)]
    [InlineData(-1, 300)]
    public void Empty_canvas_has_no_visible_or_selectable_rows(float width, float height)
    {
        var dropdown = Create();
        dropdown.PopupViewport = new Rect(0, 0, width, height);
        dropdown.Open();
        Assert.Equal(default, dropdown.OptionBounds(0));
        Assert.Equal(0, dropdown.FirstVisibleIndex);
        Assert.Equal(0, dropdown.VisibleOptionCount);
    }

    [Fact]
    public void Clamped_keyboard_navigation_reveals_the_highlight_after_wheel_scrolling()
    {
        var dropdown = Create();
        dropdown.Wrap = false;
        var input = new InputManager();
        dropdown.Open();
        input.Update(Frame(new Vector2(150, 145), wheel: -2));
        dropdown.Update(input, focused: true);
        Assert.Equal(2, dropdown.FirstVisibleIndex);
        input.Update(Frame(Vector2.Zero, key: Key.Up));
        dropdown.Update(input, focused: true);
        Assert.Equal(0, dropdown.HighlightedIndex);
        Assert.Equal(new Rect(100, 130, 160, 30), dropdown.OptionBounds(0));
    }

    [Fact]
    public void Simultaneous_keyboard_and_wheel_each_act_once()
    {
        var dropdown = Create();
        var input = new InputManager();
        dropdown.Open();
        input.Update(Frame(new Vector2(150, 145), wheel: -1, key: Key.Down));
        dropdown.Update(input, focused: true);
        Assert.True(dropdown.WheelConsumed);
        Assert.Equal(1, dropdown.FirstVisibleIndex);
        Assert.Equal(1, dropdown.HighlightedIndex);
    }

    [Fact]
    public void Returning_to_the_original_scroll_offset_does_not_revive_an_option_press()
    {
        var dropdown = Create();
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(150, 175), true);
        Tick(dropdown, pointer, new Vector2(150, 175), true, -1);
        Tick(dropdown, pointer, new Vector2(150, 175), true, 1);
        Tick(dropdown, pointer, new Vector2(150, 175));
        Assert.Equal(100, dropdown.SelectedValue);
        Assert.True(dropdown.IsOpen);
        Assert.True(pointer.IsConsumed);
    }

    [Fact]
    public void Shifted_popup_does_not_reserve_the_gap_to_its_trigger()
    {
        var dropdown = Create(new Rect(350, 270, 160, 30), 3);
        var pointer = new Pointer();
        dropdown.Open();
        Tick(dropdown, pointer, new Vector2(300, 285));
        Assert.True(pointer.IsBlocked(new Vector2(250, 200)));
        Assert.True(pointer.IsBlocked(new Vector2(375, 285)));
        Assert.False(pointer.IsBlocked(new Vector2(300, 285)));
        Tap(dropdown, pointer, new Vector2(300, 285));
        Assert.False(dropdown.IsOpen);
        Assert.True(pointer.IsConsumed);
    }

    [Fact]
    public void No_viewport_preserves_unconstrained_geometry_and_wheel_navigation()
    {
        var dropdown = Create(new Rect(350, 270, 160, 30));
        dropdown.PopupViewport = null;
        var input = new InputManager();
        dropdown.Open();
        Assert.Equal(new Rect(350, 300, 160, 300), dropdown.PopupBounds);
        Assert.Equal(new Rect(350, 570, 160, 30), dropdown.OptionBounds(9));
        Assert.Equal(10, dropdown.VisibleOptionCount);
        input.Update(Frame(new Vector2(20, 20), wheel: -1));
        dropdown.Update(input, focused: true);
        Assert.Equal(1, dropdown.HighlightedIndex);
        Assert.False(dropdown.WheelConsumed);
    }
}
