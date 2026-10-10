using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The play-area clamp applied to a move before it runs, so a move ends inside the bound.</summary>
internal static class PlayArea
{
    /// <summary>Cuts <paramref name="move"/> from <paramref name="from"/> so its end is the clamp's answer for it, and
    /// says whether the clamp shortened it. A null clamp, or one that answers a non-finite end, leaves the move whole,
    /// and the controller's clamp after the move then holds the tick.</summary>
    internal static bool Clamp(Vector2 from, ref Vector2 move, Func<float, float, Vector2>? clampXz)
    {
        if (clampXz is null) return false;
        Vector2 target = from + move;
        Vector2 clamped = clampXz(target.X, target.Y);
        if (!float.IsFinite(clamped.X) || !float.IsFinite(clamped.Y) || clamped == target) return false;
        move = clamped - from;
        return true;
    }
}
