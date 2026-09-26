using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The rigid motion variant's per-frame input (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3): one motion
/// slot per grouped instance, parallel to the instance stream, and the compact previous transforms the slots
/// index.</summary>
internal static class RigidMotionSlots
{
    /// <summary>
    /// Fill <paramref name="slots"/> and <paramref name="previous"/> for this frame's grouped instances. A slot is -1 (the
    /// instance's own transform, camera-only motion) for an unkeyed instance, a key with no last frame, or a frame with no
    /// <paramref name="history"/>. Otherwise it indexes <paramref name="previous"/>, which holds that key's last-frame
    /// world, ABSOLUTE in the history, reduced by this frame's <paramref name="renderOrigin"/> exactly as the current
    /// transform is. Returns the count written. Allocation-free once <paramref name="previous"/> has grown.
    /// </summary>
    public static int Build(ReadOnlySpan<MotionKey> keys, MotionHistory? history, Vector3 renderOrigin,
        Span<float> slots, List<Matrix4x4> previous)
    {
        previous.Clear();
        for (int i = 0; i < keys.Length; i++)
        {
            if (history is not null && !keys[i].IsNone && history.TryGetPreviousRigid(keys[i], out Matrix4x4 world))
            {
                world.M41 -= renderOrigin.X;
                world.M42 -= renderOrigin.Y;
                world.M43 -= renderOrigin.Z;
                slots[i] = previous.Count;
                previous.Add(world);
            }
            else slots[i] = -1f;
        }
        return previous.Count;
    }
}
