using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The outcome of one horizontal shell substep. <see cref="Achieved"/> is the horizontal move that
/// happened. <see cref="Blocked"/> says a shell contact removed part of the move.</summary>
internal readonly record struct ShellSweep(Vector2 Achieved, bool Blocked, ShellObstructions Obstructions = default);

/// <summary>The outcome of one shell substep through the air. <see cref="Achieved"/> is the move that happened.
/// <see cref="Highest"/> and <see cref="Lowest"/> bound the feet's vertical offset from the start along the way.
/// <see cref="Blocked"/> says a contact removed part of the move.</summary>
internal readonly record struct ShellFlight(Vector3 Achieved, bool Blocked, float Highest, float Lowest);

/// <summary>Moves the shell: recovery out of overlaps, the upward lift, one horizontal substep with wall slides
/// and one substep through the air. Every position is the body's feet. A null world has no statics, so the shell moves freely.</summary>
internal static class ShellMotion
{
    /// <summary>The gap a sweep leaves when it hits something. Free motion is never shortened by it.</summary>
    internal const float ContactSkin = 0.001f;

    const int MaxRecoveryPasses = 4;
    const int MaxSlides = 4;

    /// <summary>Pushes the shell out of any static it overlaps along the true MTV plus the skin, at most
    /// <see cref="MaxRecoveryPasses"/> passes. <paramref name="cleared"/> is false when the shell still overlaps
    /// after the last pass. The returned feet are always finite.</summary>
    internal static Vector3 Recover(IPhysicsWorld? world, Vector3 feet, in MoveTuning tuning, out bool cleared)
    {
        cleared = true;
        if (world is null)
            return feet;
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        for (int pass = 0; ; pass++)
        {
            if (!world.ComputePenetration(shape, Pose.At(ShellGeometry.Centre(feet, tuning)), out Vector3 mtv))
                return feet;
            float depth = mtv.Length();
            if (pass == MaxRecoveryPasses || !(depth > 0f) || !float.IsFinite(depth))
            {
                cleared = false;
                return feet;
            }
            // The skin on top of the MTV keeps a recovered shell from resting exactly tangent.
            feet += mtv * ((depth + ContactSkin) / depth);
        }
    }

    /// <summary>How far the shell can rise, up to <see cref="MoveTuning.StepHeight"/>. A hit gives the clear
    /// distance less the skin, never negative.</summary>
    internal static float Lift(IPhysicsWorld? world, Vector3 feet, in MoveTuning tuning)
    {
        float step = tuning.StepHeight;
        if (world is null || !world.SweepCapsule(ShellGeometry.Shape(tuning),
                Pose.At(ShellGeometry.Centre(feet, tuning)), Vector3.UnitY, step, out SweepHit hit,
                QueryFilter.StaticsOnly))
            return step;
        return MathF.Max(0f, hit.Distance - ContactSkin);
    }

    /// <summary>Sweeps the shell, raised by <paramref name="lift"/>, horizontally by <paramref name="move"/>. A
    /// hit advances to the contact less the skin. A wall, ceiling or rising-support contact then removes the
    /// rest of the move's component along the contact's horizontal normal, at most <see cref="MaxSlides"/>
    /// times. The caller splits a long move into substeps.</summary>
    internal static ShellSweep Sweep(IPhysicsWorld? world, Vector3 feet, float lift, Vector2 move,
        in MoveTuning tuning, float cosMaxSlope)
    {
        if (world is null)
            return new ShellSweep(move, false);
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        Vector3 centre = ShellGeometry.Centre(feet, tuning) + new Vector3(0f, lift, 0f);
        // A shell that starts touching a face sweeps from this offset off the face, so a sweep along the face
        // does not report the touch again. The shell itself does not move by it.
        Vector3 offset = Vector3.Zero;
        Vector2 achieved = Vector2.Zero;
        Vector2 remaining = move;
        bool blocked = false;
        ShellObstructions obstructions = default;
        for (int slide = 0; slide <= MaxSlides; slide++)
        {
            double length = Math.Sqrt((double)remaining.X * remaining.X + (double)remaining.Y * remaining.Y);
            if (!(length > 0))
                break;
            Vector2 direction = new((float)(remaining.X / length), (float)(remaining.Y / length));
            if (!world.SweepCapsule(shape, Pose.At(centre + offset), new Vector3(direction.X, 0f, direction.Y),
                    (float)length, out SweepHit hit, QueryFilter.StaticsOnly))
            {
                achieved += remaining;
                remaining = Vector2.Zero;
                break;
            }
            Vector3 normal = hit.Normal;
            if (normal == Vector3.Zero)
            {
                // A touching start reports distance 0 with no normal. The face is read from an inflated shell.
                if (!TouchNormal(world, shape, centre + offset, out normal))
                    break;
                offset += normal * ContactSkin;
            }
            else
            {
                Vector2 advance = direction * MathF.Max(0f, hit.Distance - ContactSkin);
                centre += new Vector3(advance.X, 0f, advance.Y);
                achieved += advance;
                remaining -= advance;
            }
            obstructions.Add(hit.Body, ContactClassifier.ClassifyShell(normal, cosMaxSlope));
            if (slide == MaxSlides)
                break;
            ContactClass contact = ContactClassifier.ClassifyShell(normal, cosMaxSlope);
            if (contact is not (ContactClass.Wall or ContactClass.Ceiling or ContactClass.RisingSupport))
                continue;
            Vector2 across = new(normal.X, normal.Z);
            if (!(across.LengthSquared() > 0f))
                continue;
            across = Vector2.Normalize(across);
            float into = Vector2.Dot(remaining, across);
            if (into < 0f)
            {
                remaining -= into * across;
                blocked = true;
            }
        }
        return new ShellSweep(achieved, blocked || remaining != Vector2.Zero, obstructions);
    }

    /// <summary>Sweeps the shell along <paramref name="move"/>. A hit advances to the contact less the skin. Every
    /// contact then removes the component into its normal from the rest of the move and from
    /// <paramref name="velocity"/>, vertical included, at most <see cref="MaxSlides"/> times. So a flat ceiling stops a
    /// rise and a vertical wall leaves the vertical part alone. A contact never lifts: when that removal would raise a
    /// vector above its own rise, only its horizontal component into the contact goes. The caller splits a long move
    /// into substeps.</summary>
    internal static ShellFlight Fly(IPhysicsWorld? world, Vector3 feet, Vector3 move, ref Vector3 velocity,
        in MoveTuning tuning)
    {
        if (world is null)
            return new ShellFlight(move, false, MathF.Max(0f, move.Y), MathF.Min(0f, move.Y));
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        Vector3 centre = ShellGeometry.Centre(feet, tuning);
        // As in Sweep, a touching start sweeps from an offset off the face that the shell itself does not take.
        Vector3 offset = Vector3.Zero;
        Vector3 achieved = Vector3.Zero;
        Vector3 remaining = move;
        bool blocked = false;
        float highest = 0f, lowest = 0f;
        for (int slide = 0; slide <= MaxSlides; slide++)
        {
            double length = Math.Sqrt((double)remaining.X * remaining.X + (double)remaining.Y * remaining.Y +
                (double)remaining.Z * remaining.Z);
            if (!(length > 0))
                break;
            var direction = new Vector3((float)(remaining.X / length), (float)(remaining.Y / length),
                (float)(remaining.Z / length));
            if (!world.SweepCapsule(shape, Pose.At(centre + offset), direction, (float)length, out SweepHit hit,
                    QueryFilter.StaticsOnly))
            {
                achieved += remaining;
                remaining = Vector3.Zero;
                highest = MathF.Max(highest, achieved.Y);
                lowest = MathF.Min(lowest, achieved.Y);
                break;
            }
            Vector3 normal = hit.Normal;
            if (normal == Vector3.Zero)
            {
                if (!TouchNormal(world, shape, centre + offset, out normal))
                    break;
                offset += normal * ContactSkin;
            }
            else
            {
                normal = Vector3.Normalize(normal);
                Vector3 advance = direction * MathF.Max(0f, hit.Distance - ContactSkin);
                centre += advance;
                achieved += advance;
                remaining -= advance;
                highest = MathF.Max(highest, achieved.Y);
                lowest = MathF.Min(lowest, achieved.Y);
            }
            velocity = Slide(velocity, normal);
            if (slide == MaxSlides)
                break;
            Vector3 rest = Slide(remaining, normal);
            blocked |= rest != remaining;
            remaining = rest;
        }
        return new ShellFlight(achieved, blocked || remaining != Vector3.Zero, highest, lowest);
    }

    // The vector less its component into the contact, unless that would raise it above its own rise.
    static Vector3 Slide(Vector3 vector, Vector3 normal)
    {
        float into = Vector3.Dot(vector, normal);
        if (!(into < 0f))
            return vector;
        Vector3 slid = vector - into * normal;
        return slid.Y > MathF.Max(vector.Y, 0f) ? Across(vector, normal) : slid;
    }

    // The move less its horizontal component into the contact's horizontal normal. The vertical part is kept.
    static Vector3 Across(Vector3 move, Vector3 normal)
    {
        var across = new Vector2(normal.X, normal.Z);
        if (!(across.LengthSquared() > 0f))
            return move;
        across = Vector2.Normalize(across);
        float into = move.X * across.X + move.Z * across.Y;
        return into < 0f ? new Vector3(move.X - into * across.X, move.Y, move.Z - into * across.Y) : move;
    }

    /// <summary>The outward normal of the face a touching shell rests on, from the MTV of the shell grown by the
    /// skin. False when even the grown shell overlaps nothing.</summary>
    static bool TouchNormal(IPhysicsWorld world, CapsuleShape shape, Vector3 centre, out Vector3 normal)
    {
        var grown = new CapsuleShape(shape.Radius + ContactSkin, shape.Length);
        if (world.ComputePenetration(grown, Pose.At(centre), out Vector3 mtv) && mtv.LengthSquared() > 0f)
        {
            normal = Vector3.Normalize(mtv);
            return true;
        }
        normal = Vector3.Zero;
        return false;
    }
}
