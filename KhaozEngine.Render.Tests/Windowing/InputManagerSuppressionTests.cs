using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class InputManagerSuppressionTests
{
    static readonly IReadOnlySet<Key> NoKeys = new HashSet<Key>();
    static readonly IReadOnlySet<MouseButton> NoButtons = new HashSet<MouseButton>();
    static readonly Rect Box = new(100f, 100f, 200f, 80f);
    static readonly Vector2 Inside = new(150f, 140f);

    [Fact]
    public void SuppressionQuarantinesHeldButtonUntilObservedRelease()
    {
        var input = new InputManager();
        input.Update(Frame(mouseDown: Buttons(MouseButton.Left), mousePressed: Buttons(MouseButton.Left)));
        Assert.True(input.IsPointerDown);

        input.SuppressPointerInput();
        Assert.False(input.IsPointerDown);
        Assert.False(input.IsPointerJustPressed);

        input.Update(Frame(mouseDown: Buttons(MouseButton.Left)));
        Assert.False(input.IsPointerDown);
        Assert.False(input.IsDragStartIn(Box));

        input.Update(Frame(mouseReleased: Buttons(MouseButton.Left)));
        Assert.False(input.IsPointerJustReleased);

        input.Update(Frame(mouseDown: Buttons(MouseButton.Left), mousePressed: Buttons(MouseButton.Left)));
        Assert.True(input.IsPointerDown);
        Assert.True(input.IsPointerJustPressed);
    }

    [Fact]
    public void SuppressionRejectsSameFrameTap()
    {
        var input = new InputManager();
        input.Update(Frame(mousePressed: Buttons(MouseButton.Left), mouseReleased: Buttons(MouseButton.Left)));
        Assert.True(input.IsTapIn(Box));

        input.SuppressPointerInput();

        Assert.False(input.IsTapIn(Box));
        Assert.False(input.IsPointerJustReleased);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.Right)]
    public void SuppressedCompletedTapDoesNotSwallowImmediateFreshPress(MouseButton button)
    {
        var input = new InputManager();
        input.Update(Frame(mousePressed: Buttons(button), mouseReleased: Buttons(button)));
        AssertReleased(input, button);

        input.SuppressPointerInput();
        input.Update(Frame(mouseDown: Buttons(button), mousePressed: Buttons(button)));

        AssertPressed(input, button);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.Right)]
    public void SuppressedOrdinaryReleaseDoesNotSwallowImmediateFreshPress(MouseButton button)
    {
        var input = new InputManager();
        input.Update(Frame(mouseDown: Buttons(button), mousePressed: Buttons(button)));
        input.Update(Frame(mouseReleased: Buttons(button)));
        AssertReleased(input, button);

        input.SuppressPointerInput();
        input.Update(Frame(mouseDown: Buttons(button), mousePressed: Buttons(button)));

        AssertPressed(input, button);
    }

    [Fact]
    public void SuppressionKeepsKeyboardGamepadAndHoverWhileClearingWheelForOneFrame()
    {
        var input = new InputManager();
        input.Update(Frame(
            keysDown: Keys(Key.Enter),
            keysPressed: Keys(Key.Enter),
            scroll: 2f,
            gamepads: new[] { Pad(GamepadButton.A) }));

        input.SuppressPointerInput();

        Assert.Equal(0f, input.ScrollDelta);
        Assert.False(input.IsMouseWheelScrolledUp);
        Assert.True(input.IsKeyJustPressed(Key.Enter));
        Assert.True(input.IsNewButtonPress(GamepadButton.A, PlayerIndex.One, out _));
        Assert.True(input.Pointer.IsHoveringIn(Box));

        input.Update(Frame(scroll: 2f));
        Assert.Equal(2f, input.ScrollDelta);
        Assert.True(input.IsMouseWheelScrolledUp);
    }

    [Fact]
    public void UpdateWithoutSuppressionLeavesPointerAndWheelActive()
    {
        var input = new InputManager();
        input.Update(Frame(mouseDown: Buttons(MouseButton.Left), mousePressed: Buttons(MouseButton.Left), scroll: 1f));

        Assert.True(input.IsPointerDown);
        Assert.True(input.IsPointerJustPressed);
        Assert.True(input.IsDragStartIn(Box));
        Assert.Equal(1f, input.ScrollDelta);
    }

    static InputState Frame(
        IReadOnlySet<Key>? keysDown = null,
        IReadOnlySet<Key>? keysPressed = null,
        IReadOnlySet<MouseButton>? mouseDown = null,
        IReadOnlySet<MouseButton>? mousePressed = null,
        IReadOnlySet<MouseButton>? mouseReleased = null,
        float scroll = 0f,
        IReadOnlyList<GamepadState>? gamepads = null) => new(
            keysDown ?? NoKeys,
            keysPressed ?? NoKeys,
            NoKeys,
            mouseDown ?? NoButtons,
            mousePressed ?? NoButtons,
            Inside,
            Vector2.Zero,
            scroll,
            960,
            540,
            gamepads,
            mouseReleased: mouseReleased ?? NoButtons);

    static IReadOnlySet<Key> Keys(params Key[] keys) => new HashSet<Key>(keys);

    static IReadOnlySet<MouseButton> Buttons(params MouseButton[] buttons) => new HashSet<MouseButton>(buttons);

    static void AssertReleased(InputManager input, MouseButton button)
    {
        bool released = button switch
        {
            MouseButton.Left => input.IsPointerJustReleased,
            MouseButton.Middle => input.IsMiddleJustReleased,
            MouseButton.Right => input.IsRightJustReleased,
            _ => false,
        };
        Assert.True(released);
    }

    static void AssertPressed(InputManager input, MouseButton button)
    {
        (bool down, bool pressed) = button switch
        {
            MouseButton.Left => (input.IsPointerDown, input.IsPointerJustPressed),
            MouseButton.Middle => (input.IsMiddleDown, input.IsMiddleJustPressed),
            MouseButton.Right => (input.IsRightDown, input.IsRightJustPressed),
            _ => (false, false),
        };
        Assert.True(down);
        Assert.True(pressed);
    }

    static GamepadState Pad(params GamepadButton[] buttons)
    {
        var pressed = new HashSet<GamepadButton>(buttons);
        return new GamepadState(0, pressed, pressed, new HashSet<GamepadButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 0f);
    }
}
