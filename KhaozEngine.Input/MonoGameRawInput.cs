using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Input;

/// <summary>
/// Production <see cref="IRawInput"/>. The ONLY class in the stack that touches the MonoGame input
/// statics (<see cref="Mouse"/>, <see cref="Keyboard"/>, <see cref="GamePad"/>, <see cref="TouchPanel"/>).
/// Games must read input through this seam, never the statics directly.
/// </summary>
public sealed class MonoGameRawInput : IRawInput
{
    private readonly GameWindow _window;

    /// <summary>Creates the adapter. <paramref name="window"/> supplies the client bounds.</summary>
    public MonoGameRawInput(GameWindow window) => _window = window;

    /// <inheritdoc/>
    public RawInputState Read()
    {
        MouseState mouse = Mouse.GetState();

        var pads = new GamePadState[4];
        for (int i = 0; i < 4; i++)
            pads[i] = GamePad.GetState((PlayerIndex)i);

        TouchCollection touches = TouchPanel.GetState();
        var list = new List<TouchPoint>(touches.Count);
        foreach (TouchLocation t in touches)
            list.Add(new TouchPoint(t.Position, t.State));

        return new RawInputState(
            new Point(mouse.X, mouse.Y),
            mouse.LeftButton == ButtonState.Pressed,
            mouse.MiddleButton == ButtonState.Pressed,
            mouse.RightButton == ButtonState.Pressed,
            mouse.ScrollWheelValue,
            Keyboard.GetState(),
            pads,
            list,
            _window.ClientBounds);
    }
}
