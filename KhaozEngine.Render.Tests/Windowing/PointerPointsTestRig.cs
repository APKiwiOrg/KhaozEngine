using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Windowing;

internal sealed class PointerPointsTestRig
{
    public InputAccumulator Accumulator { get; } = new();
    public Vector2 Scale { get; set; }
    public Vector2 Cursor { get; private set; } = new(100, 150);
    public MouseButton Button { get; }

    public PointerPointsTestRig(Vector2 scale, MouseButton button = MouseButton.Right)
    {
        Scale = scale;
        Button = button;
        Sample(false);
    }

    public InputState Sample(bool down, Vector2 move = default, bool captured = false,
        bool hasMouse = true, bool focused = true)
    {
        Accumulator.OnFocusChanged(focused);
        if (down) Accumulator.OnMouseDown(Button);
        else Accumulator.OnMouseUp(Button);
        Cursor += move;
        return Accumulator.Snapshot(Cursor * Scale, hasMouse, 1920, 1080,
            pointerCaptured: captured, framebufferScale: Scale);
    }
}
