using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The direction of the swept move's pre-sweep push-out off a WALKABLE contact. The slide probe is the capsule inflated
// by SkinWidth, so its MTV is the real overlap plus up to one SkinWidth of clearance that exists only so the sweep starts
// outside with a real contact normal. A walkable contact is support, and support sets the body's height (step 4), so
// that clearance must not move the body along the surface it stands on.
public static partial class CharacterMovement
{
    /// <summary>The pre-sweep push-out for one depenetration pass. A steep (wall/riser) contact keeps the full MTV, so
    /// walking into a wall still pushes out horizontally.
    /// <para>A grounded body with no horizontal command takes only the vertical component of a walkable push, so a
    /// resting body cannot creep down-slope (mirrors the settle pass in StepCore).</para>
    /// <para>A moving body keeps its real overlap recovery along the MTV. A fast run-climb is grounded yet embeds in the
    /// risers it is mounting, and that horizontal recovery is what extracts it (the StairRunTangentPacing regression
    /// when it was removed). Only the clearance part is vertical. Pushed along a tilted walkable normal, it shoves a
    /// moving body back by up to SkinWidth times the normal's horizontal part every pass, down a slope or back off a
    /// low lip corner it is mounting. A slowing approach then stalls where that shove equals its commanded advance,
    /// short of its goal (KhaozEngine #1265 and #1270).</para></summary>
    /// <param name="push">The inflated probe's MTV, with a length above 1e-6.</param>
    /// <param name="restHold">Whether the body is grounded with no horizontal command this tick.</param>
    /// <param name="cosMaxSlope">The walkable slope gate, cos(MaxSlopeRadians).</param>
    private static Vector3 WalkableContactPush(Vector3 push, bool restHold, float cosMaxSlope)
    {
        float length = push.Length();
        // Divide-free normal.Y >= cosMaxSlope test.
        if (push.Y < cosMaxSlope * length) return push;
        if (restHold) return new Vector3(0f, push.Y, 0f);
        float overlap = MathF.Max(0f, length - SkinWidth);
        Vector3 recovery = push * (overlap / length);
        return recovery + new Vector3(0f, push.Y - recovery.Y, 0f);
    }
}
