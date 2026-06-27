using System;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// XZ-plane push-out (minimum-translation) resolution for a moving circle (the player capsule's footprint)
/// against the static collider shapes <see cref="WorldColliders"/> stores: an axis-aligned box, an oriented
/// box, and another circle. Each <c>Resolve*</c> returns the smallest translation that moves the circle out
/// of overlap; applied to a desired position it produces slide (the move's tangential component survives, the
/// penetrating component is removed). Companion to <see cref="CircleCollision"/> (overlap tests) and
/// <see cref="Segment2D"/>. Plain float (authoritative server + visual client run the same code).
/// </summary>
public static class BoxCollision
{
    const float Epsilon = 1e-6f;

    /// <summary>Push-out of circle (<paramref name="c"/>, <paramref name="r"/>) from an axis-aligned box
    /// centred at <paramref name="boxCenter"/> with half-extents <paramref name="half"/>. Returns true + the
    /// MTV in <paramref name="push"/> when overlapping; false + zero when clear or exactly touching.</summary>
    public static bool ResolveCircleAabb(Vector2 c, float r, Vector2 boxCenter, Vector2 half, out Vector2 push)
        => ResolveCircleBoxLocal(c - boxCenter, r, half, out push);

    /// <summary>Push-out of circle (<paramref name="c"/>, <paramref name="r"/>) from a box centred at
    /// <paramref name="boxCenter"/>, half-extents <paramref name="half"/>, rotated <paramref name="yaw"/>
    /// radians about its centre. Transforms the circle into the box's local frame, resolves as an AABB, then
    /// rotates the push back to world.</summary>
    public static bool ResolveCircleOrientedBox(Vector2 c, float r, Vector2 boxCenter, Vector2 half, float yaw, out Vector2 push)
    {
        float cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
        Vector2 d = c - boxCenter;
        // Rotate the offset by -yaw into the box's local axes.
        Vector2 local = new(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos);
        if (!ResolveCircleBoxLocal(local, r, half, out Vector2 localPush))
        {
            push = default;
            return false;
        }
        // Rotate the push back by +yaw into world.
        push = new Vector2(localPush.X * cos - localPush.Y * sin, localPush.X * sin + localPush.Y * cos);
        return true;
    }

    /// <summary>Push-out of circle (<paramref name="c"/>, <paramref name="r"/>) from a circle
    /// (<paramref name="other"/>, <paramref name="otherR"/>). MTV is along the centre line.</summary>
    public static bool ResolveCircleCircle(Vector2 c, float r, Vector2 other, float otherR, out Vector2 push)
    {
        float dx = c.X - other.X, dy = c.Y - other.Y;
        float combined = r + otherR;
        float dist2 = dx * dx + dy * dy;
        if (dist2 >= combined * combined)
        {
            push = default;
            return false;
        }
        float dist = MathF.Sqrt(dist2);
        if (dist < Epsilon)
        {
            // Concentric: no defined direction, pick +X so the resolve is still deterministic.
            push = new Vector2(combined, 0f);
            return true;
        }
        float depth = combined - dist;
        push = new Vector2(dx / dist * depth, dy / dist * depth);
        return true;
    }

    // Circle (centre = local, already box-relative) vs an AABB centred at the origin with half-extents 'half'.
    static bool ResolveCircleBoxLocal(Vector2 local, float r, Vector2 half, out Vector2 push)
    {
        bool insideX = MathF.Abs(local.X) <= half.X;
        bool insideY = MathF.Abs(local.Y) <= half.Y;
        if (insideX && insideY)
        {
            // Centre is inside the box: exit through the nearest face (minimum translation), pushing the whole
            // circle clear (face distance + r).
            float penX = half.X - MathF.Abs(local.X) + r;
            float penY = half.Y - MathF.Abs(local.Y) + r;
            if (penX <= penY)
                push = new Vector2(local.X >= 0f ? penX : -penX, 0f);
            else
                push = new Vector2(0f, local.Y >= 0f ? penY : -penY);
            return true;
        }

        // Nearest point on the box to the circle centre.
        float closestX = local.X < -half.X ? -half.X : local.X > half.X ? half.X : local.X;
        float closestY = local.Y < -half.Y ? -half.Y : local.Y > half.Y ? half.Y : local.Y;
        float dx = local.X - closestX, dy = local.Y - closestY;
        float dist2 = dx * dx + dy * dy;
        if (dist2 >= r * r)
        {
            push = default;
            return false;
        }
        float dist = MathF.Sqrt(dist2);
        if (dist < Epsilon)
        {
            push = default;
            return false;
        }
        float depth = r - dist;
        push = new Vector2(dx / dist * depth, dy / dist * depth);
        return true;
    }
}
