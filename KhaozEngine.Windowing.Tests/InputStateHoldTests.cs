using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class InputStateHoldTests
{
    [Fact]
    public void Held_input_carries_no_text_or_edges()
    {
        var pad = new GamepadState(0, new HashSet<GamepadButton>(), new HashSet<GamepadButton>(),
            new HashSet<GamepadButton>(), new Vector2(0.5f, 0f), Vector2.Zero, 0f, 0f);
        var source = new InputState(
            PointerGestureFrame.Empty, horizontalScrollDelta: 1.5f, mouseDeltaPoints: new Vector2(3f, 4f),
            framebufferScale: new Vector2(2f, 2f),
            down: new HashSet<Key> { Key.F10 }, pressed: new HashSet<Key> { Key.F10 },
            released: new HashSet<Key> { Key.A },
            mouseDown: new HashSet<MouseButton> { MouseButton.Left },
            mousePressed: new HashSet<MouseButton> { MouseButton.Left },
            mousePosition: new Vector2(640f, 360f), mouseDelta: new Vector2(6f, 8f), scrollDelta: 2f,
            width: 1280, height: 720, gamepads: new[] { pad },
            touches: new[] { new TouchPoint(1, new Vector2(10f, 10f), TouchPhase.Moved) },
            windowFocused: true, repeated: new HashSet<Key> { Key.Backspace },
            mouseReleased: new HashSet<MouseButton> { MouseButton.Right },
            textInput: "abc", textInputAvailable: true, pointerCaptured: true);

        InputState held = source.WithoutInteraction();

        Assert.Empty(held.KeysDown);
        Assert.Empty(held.KeysPressed);
        Assert.Empty(held.KeysReleased);
        Assert.Empty(held.KeysRepeated);
        Assert.Empty(held.MouseDown);
        Assert.Empty(held.MousePressed);
        Assert.Empty(held.MouseReleased);
        Assert.Equal(0f, held.ScrollDelta);
        Assert.Equal(0f, held.HorizontalScrollDelta);
        Assert.Equal(Vector2.Zero, held.MouseDelta);
        Assert.Equal(Vector2.Zero, held.MouseDeltaPoints);
        Assert.Empty(held.Gestures.Samples);
        Assert.True(held.Gestures.IsAvailable);
        Assert.Empty(held.Touches);
        Assert.Empty(held.Gamepads);
        Assert.Equal("", held.TextInput);

        Assert.Equal(1280, held.Width);
        Assert.Equal(720, held.Height);
        Assert.True(held.WindowFocused);
        Assert.Equal(new Vector2(640f, 360f), held.MousePosition);
        Assert.Equal(new Vector2(2f, 2f), held.FramebufferScale);
        Assert.True(held.PointerCaptured);
        Assert.True(held.TextInputAvailable);

        // The source snapshot is untouched.
        Assert.Contains(Key.F10, source.KeysPressed);
        Assert.Equal("abc", source.TextInput);
    }
}
