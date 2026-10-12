using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.WorldMap.Gui;

internal sealed class TestInput
{
    HashSet<MouseButton> _previous = [];
    internal Pointer Pointer { get; } = new();

    internal Pointer At(Vector2 position, bool down = false)
    {
        Pointer.Update(Frame(position, down));
        return Pointer;
    }

    InputState Frame(Vector2 position, bool down)
    {
        HashSet<MouseButton> held = [];
        if (down) held.Add(MouseButton.Left);
        var pressed = new HashSet<MouseButton>(held);
        pressed.ExceptWith(_previous);
        var released = new HashSet<MouseButton>(_previous);
        released.ExceptWith(held);
        _previous = held;
        return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            held, pressed, position, Vector2.Zero, 0, 960, 540, mouseReleased: released);
    }
}
