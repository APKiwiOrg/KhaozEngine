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

    /// <summary>The fraction of <paramref name="move"/> from <paramref name="from"/> that stays inside the bound,
    /// given the clamp's answer <paramref name="clampedEnd"/> for the move's end. Each axis the clamp moved is a face
    /// of an axis-aligned bound, and the move meets it at the clamped value on that axis. An axis the move does not
    /// travel cannot be crossed by it, so a clamp that moved only such an axis gives zero.</summary>
    internal static float Reach(Vector2 from, Vector2 move, Vector2 clampedEnd)
    {
        Vector2 end = from + move;
        float reach = 1f;
        bool crossed = false;
        if (clampedEnd.X != end.X && move.X != 0f)
        {
            reach = MathF.Min(reach, (clampedEnd.X - from.X) / move.X);
            crossed = true;
        }
        if (clampedEnd.Y != end.Y && move.Y != 0f)
        {
            reach = MathF.Min(reach, (clampedEnd.Y - from.Y) / move.Y);
            crossed = true;
        }
        return crossed ? Math.Clamp(reach, 0f, 1f) : 0f;
    }
}
