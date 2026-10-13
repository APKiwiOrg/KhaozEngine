using System;
using System.Numerics;

namespace KhaozEngine.Windowing;

internal readonly record struct NativeMacOSGestureEvent(int Type, double X, double Y,
    double DeltaX, double DeltaY, bool Precise, double Magnification,
    ulong Modifiers, ulong Phase, ulong MomentumPhase);

/// <summary>Conversion of event-time values only. No native calls or device inference.</summary>
internal static class NativeGestureNormalization
{
    internal static bool TryMacOS(NativeMacOSGestureEvent input, float windowHeight, Vector2 scale,
        out PointerGestureSample sample)
    {
        sample = default;
        if (input.Type != 22 && input.Type != 30) return false;
        var position = new Vector2((float)input.X, (float)(windowHeight - input.Y))
            * PointerMotionScale.Normalize(scale);
        bool command = (input.Modifiers & ((1UL << 18) | (1UL << 20))) != 0;
        bool momentum = input.Type == 22 && input.MomentumPhase != 0;
        var phase = Phase(momentum ? input.MomentumPhase : input.Phase);
        sample = input.Type == 30
            ? new PointerGestureSample(PointerGestureKind.Magnify, PointerGestureSource.MacOSNative,
                phase, position, scaleFactor: (float)(1 + input.Magnification), commandDown: command)
            : new PointerGestureSample(input.Precise ? PointerGestureKind.PreciseScroll : PointerGestureKind.Wheel,
                input.Precise || !command ? PointerGestureSource.MacOSNative : PointerGestureSource.ModifierWheelCompatibility,
                phase, position, new Vector2((float)input.DeltaX, (float)input.DeltaY),
                commandDown: command, isMomentum: momentum);
        return IsValid(sample);
    }

    internal static bool TryWindowsWheel(uint message, nuint wParam, Vector2 clientPosition, Vector2 scale,
        out PointerGestureSample sample)
    {
        sample = default;
        if (message != 0x020A && message != 0x020E) return false;
        float delta = unchecked((short)((ulong)wParam >> 16)) / 120f;
        var scroll = message == 0x020E ? new Vector2(-delta, 0) : new Vector2(0, delta);
        sample = Wheel(scroll, clientPosition * PointerMotionScale.Normalize(scale), (wParam & 8) != 0);
        return IsValid(sample);
    }

    internal static Vector2 WindowsScreenPosition(nint lParam) =>
        new(unchecked((short)lParam), unchecked((short)((long)lParam >> 16)));

    internal static PointerGestureSample Wheel(Vector2 delta, Vector2 positionPixels, bool commandDown) =>
        new(PointerGestureKind.Wheel,
            commandDown ? PointerGestureSource.ModifierWheelCompatibility : PointerGestureSource.UnknownWheel,
            PointerGestureInputPhase.None, positionPixels, delta, commandDown: commandDown);

    static PointerGestureInputPhase Phase(ulong flags)
    {
        if ((flags & 16) != 0) return PointerGestureInputPhase.Cancelled;
        if ((flags & 8) != 0) return PointerGestureInputPhase.Ended;
        if ((flags & (1 | 32)) != 0) return PointerGestureInputPhase.Began;
        if ((flags & (2 | 4)) != 0) return PointerGestureInputPhase.Changed;
        return PointerGestureInputPhase.None;
    }

    static bool IsValid(PointerGestureSample sample)
    {
        try { sample.Validate(); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
