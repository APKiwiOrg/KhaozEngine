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
/// A focused <see cref="TextArea"/> runs every frame of a form. A frame with no typing, no key, no press and no
/// wheel must not slice the text through <see cref="TextEntry.Apply(string, InputState, int, Func{string, char, bool}?, bool)"/>
/// or rebuild its layout. The caret sits mid text, so a stray edit pass would allocate the slice.
/// </summary>
[Collection("AllocSensitive")]
public sealed class TextAreaAllocationTests
{
    [Fact]
    public void Idle_focused_update_does_not_allocate()
    {
        var area = new TextArea(new Rect(100, 100, 216, 112), new FixedMeasurer());
        area.SetText("first line of text\nsecond line of text\nthird");
        area.Focus();
        var pointer = new Pointer();
        var left = new HashSet<Key> { Key.Left };
        var leftFrame = new InputState(left, left, new HashSet<Key>(), new HashSet<MouseButton>(),
            new HashSet<MouseButton>(), new Vector2(150, 150), Vector2.Zero, 0f, 960, 540);
        pointer.Update(leftFrame);
        area.Update(pointer, leftFrame, 0.016f);
        Assert.True(area.Caret < area.Text.Length);

        var idle = new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), new Vector2(150, 150), Vector2.Zero, 0f, 960, 540);
        for (int frame = 0; frame < 3; frame++)
        {
            pointer.Update(idle);
            area.Update(pointer, idle, 0.016f);
        }
        string text = area.Text;

        AllocAssert.NoPerCallAllocation("TextArea focused idle update", () =>
        {
            for (int frame = 0; frame < 60; frame++)
            {
                pointer.Update(idle);
                area.Update(pointer, idle, 0.016f);
            }
        });
        Assert.Same(text, area.Text);
        Assert.True(area.IsFocused);
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 20f;

        public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

        public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
    }
}
