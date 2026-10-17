using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// A focused <see cref="TextArea"/> runs every frame of a form. A frame with no typing, no edit or navigation key,
/// no press and no wheel must not slice the text through
/// <see cref="TextEntry.Apply(string, InputState, int, Func{string, char, bool}?, bool)"/> or rebuild its layout.
/// The caret sits mid text, so a stray edit pass would allocate the slice.
/// </summary>
[Collection("AllocSensitive")]
public sealed class TextAreaAllocationTests
{
    static readonly Vector2 Inside = new(150, 150);

    [Fact]
    public void Idle_focused_update_does_not_allocate() =>
        AssertFrameAllocatesNothing("TextArea focused idle update", Frame());

    [Fact]
    public void Held_shift_repeat_does_not_allocate() =>
        AssertFrameAllocatesNothing("TextArea focused Shift repeat",
            Frame(down: new[] { Key.LeftShift }, repeated: new[] { Key.LeftShift }));

    [Fact]
    public void Shift_press_does_not_allocate() =>
        AssertFrameAllocatesNothing("TextArea focused Shift press",
            Frame(down: new[] { Key.LeftShift }, pressed: new[] { Key.LeftShift }));

    [Fact]
    public void Function_key_does_not_allocate() =>
        AssertFrameAllocatesNothing("TextArea focused F5 press",
            Frame(down: new[] { Key.F5 }, pressed: new[] { Key.F5 }));

    [Fact]
    public void Command_enter_does_not_allocate() =>
        AssertFrameAllocatesNothing("TextArea focused Ctrl+Enter",
            Frame(down: new[] { Key.LeftControl, Key.Enter }, pressed: new[] { Key.LeftControl, Key.Enter }));

    static void AssertFrameAllocatesNothing(string description, InputState frame)
    {
        var area = new TextArea(new Rect(100, 100, 216, 112), new FixedMeasurer());
        area.SetText("first line of text\nsecond line of text\nthird");
        area.Focus();
        var pointer = new Pointer();
        InputState left = Frame(down: new[] { Key.Left }, pressed: new[] { Key.Left });
        pointer.Update(left);
        area.Update(pointer, left, 0.016f);
        Assert.True(area.Caret < area.Text.Length);

        for (int i = 0; i < 3; i++)
        {
            pointer.Update(frame);
            area.Update(pointer, frame, 0.016f);
        }
        string text = area.Text;
        int caret = area.Caret;

        AllocAssert.NoPerCallAllocation(description, () =>
        {
            for (int i = 0; i < 60; i++)
            {
                pointer.Update(frame);
                area.Update(pointer, frame, 0.016f);
            }
        });
        Assert.Same(text, area.Text);
        Assert.Equal(caret, area.Caret);
        Assert.True(area.IsFocused);
    }

    static InputState Frame(Key[]? down = null, Key[]? pressed = null, Key[]? repeated = null) =>
        new(new HashSet<Key>(down ?? Array.Empty<Key>()), new HashSet<Key>(pressed ?? Array.Empty<Key>()),
            new HashSet<Key>(), new HashSet<MouseButton>(), new HashSet<MouseButton>(), Inside, Vector2.Zero, 0f,
            960, 540, repeated: new HashSet<Key>(repeated ?? Array.Empty<Key>()));

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 20f;

        public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

        public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
    }
}
