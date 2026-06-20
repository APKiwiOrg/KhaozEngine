using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gui;

/// <summary>InputState builders for overlay tests.</summary>
public static class OverlayTestInput
{
    public static InputState KeyFrame(Key k) => new(
        new HashSet<Key> { k }, new HashSet<Key> { k }, new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(),
        Vector2.Zero, Vector2.Zero, 0, 960, 540);

    public static InputState PadFrame(GamepadButton b) => new(
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(),
        Vector2.Zero, Vector2.Zero, 0, 960, 540,
        new[]
        {
            new GamepadState(0,
                new HashSet<GamepadButton> { b }, new HashSet<GamepadButton> { b }, new HashSet<GamepadButton>(),
                Vector2.Zero, Vector2.Zero, 0, 0),
        });
}
