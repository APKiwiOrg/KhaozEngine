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
/// and one substep through the air. Every position is the body's feet. A null world has no statics, so the shell
/// moves freely.</summary>
internal static partial class ShellMotion
{
    /// <summary>The gap a sweep leaves when it hits something. Free motion is never shortened by it.</summary>
    internal const float ContactSkin = 0.001f;

    const int MaxRecoveryPasses = 4;
    const int MaxSlides = 4;

    /// <summary>Pushes the shell out of any static it overlaps along the true MTV plus the skin, at most
    /// <see cref="MaxRecoveryPasses"/> passes. <paramref name="cleared"/> is false when the shell still overlaps
    /// after the last pass. The returned feet are always finite.</summary>
    internal static Vector3 RecoverWithQueries(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning, out bool cleared)
    {
        cleared = true;
        if (!queries.HasShell)
            return feet;
        Vector3 input = feet;
        var route = new ContactMotionRoute(queries, feet);
        queries.ValidatePlacement(feet);
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        for (int pass = 0; ; pass++)
        {
            if (!queries.PenetrationShell(shape, ShellGeometry.Centre(feet, tuning), out Vector3 mtv))
                return feet;
            float depth = mtv.Length();
            if (pass == MaxRecoveryPasses || !(depth > 0f) || !float.IsFinite(depth))
            {
                if (queries.HasMediumProof) throw new ContactQueryFailure(MovementAvailability.Unresolved);
                cleared = false;
                return feet;
            }
            // The skin on top of the MTV keeps a recovered shell from resting exactly tangent.
            Vector3 pushed = feet + mtv * ((depth + ContactSkin) / depth);
            if (!route.Append(pushed))
            {
                queries.RefuseRecovery();
                cleared = false;
                return input;
            }
            feet = pushed;
        }
    }

    /// <summary>How far the shell can rise, up to <see cref="MoveTuning.StepHeight"/>. A hit gives the clear
    /// distance less the skin, never negative.</summary>
    internal static float LiftWithQueries(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning)
    {
        float step = tuning.StepHeight;
        if (!queries.HasShell || !queries.SweepShell(ShellGeometry.Shape(tuning),
                ShellGeometry.Centre(feet, tuning), Vector3.UnitY, step, out SweepHit hit,
                QueryFilter.StaticsOnly))
            return step;
        return MathF.Max(0f, hit.Distance - ContactSkin);
    }

    /// <summary>Sweeps the shell, raised by <paramref name="lift"/>, horizontally by <paramref name="move"/>. A
    /// hit advances to the contact less the skin. A wall, ceiling or rising-support contact then removes the
    /// rest of the move's component along the contact's horizontal normal, at most <see cref="MaxSlides"/>
    /// times. <paramref name="clampXz"/> is the play-area clamp, which cuts each slide along a contact to the bound
    /// as <see cref="BoundSlide"/> describes. The caller clamps the move itself and splits a long move into
    /// substeps.</summary>
    internal static ShellSweep SweepWithQueries(ContactQueryContext queries, Vector3 feet, float lift, Vector2 move,
        in MoveTuning tuning, float cosMaxSlope, Func<float, float, Vector2>? clampXz = null)
    {
        var route = new ContactMotionRoute(queries, feet);
        return SweepWithQueries(queries, feet, lift, move, tuning, cosMaxSlope, ref route, clampXz);
    }

    internal static ShellSweep SweepWithQueries(ContactQueryContext queries, Vector3 feet, float lift, Vector2 move,
        in MoveTuning tuning, float cosMaxSlope, ref ContactMotionRoute route,
        Func<float, float, Vector2>? clampXz = null)
    {
        if (!route.Append(feet + new Vector3(0, lift, 0))) return new(Vector2.Zero, true);
        if (!queries.HasShell)
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
            if (!queries.SweepShell(shape, centre + offset, new Vector3(direction.X, 0f, direction.Y),
                    (float)length, out SweepHit hit, QueryFilter.StaticsOnly))
            {
                Vector2 target = achieved + remaining;
                if (!route.Move(feet + new Vector3(target.X, lift, target.Y), out bool constrained))
                    return new(achieved, true, obstructions);
                if (constrained)
                    return new(new(route.Current.X - feet.X, route.Current.Z - feet.Z), true, obstructions);
                achieved += remaining;
                remaining = Vector2.Zero;
                break;
            }
            Vector3 normal = hit.Normal;
            if (normal == Vector3.Zero)
            {
                // A touching start reports distance 0 with no normal. The face is read from an inflated shell.
                if (!TouchNormal(queries, shape, centre + offset, out normal))
                    break;
                offset += normal * ContactSkin;
            }
            else
            {
                Vector2 advance = direction * MathF.Max(0f, hit.Distance - ContactSkin);
                centre += new Vector3(advance.X, 0f, advance.Y);
                achieved += advance;
                remaining -= advance;
                if (!route.Move(feet + new Vector3(achieved.X, lift, achieved.Y), out bool constrained))
                    return new(achieved - advance, true, obstructions);
                if (constrained)
                    return new(new(route.Current.X - feet.X, route.Current.Z - feet.Z), true, obstructions);
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
                BoundSlide(new Vector2(feet.X, feet.Z) + achieved, ref remaining, across, clampXz);
            }
        }
        return new ShellSweep(achieved, blocked || remaining != Vector2.Zero, obstructions);
    }

    /// <summary>Sweeps the shell along <paramref name="move"/>. A hit advances to the contact less the skin. Every
    /// contact then removes the component into its normal from the rest of the move and from
    /// <paramref name="velocity"/>, at most <see cref="MaxSlides"/> times. A ceiling or a walkable face removes it
    /// whole, vertical included, so a flat ceiling stops a rise. A wall or a face steeper than the gate
    /// (<paramref name="cosMaxSlope"/>) removes it with horizontal motion alone, so it never lifts the body or slows
    /// its fall. <paramref name="clampXz"/> is the play-area clamp, which cuts the horizontal part of each slide
    /// along a contact to the bound as <see cref="BoundSlide"/> describes and leaves the velocity alone, because the
    /// bound is not a contact. The caller clamps the move itself and splits a long move into substeps.</summary>
    internal static ShellFlight FlyWithQueries(ContactQueryContext queries, Vector3 feet, Vector3 move, ref Vector3 velocity,
        in MoveTuning tuning, float cosMaxSlope, Func<float, float, Vector2>? clampXz = null)
    {
        if (!queries.HasShell)
            return new ShellFlight(move, false, MathF.Max(0f, move.Y), MathF.Min(0f, move.Y));
        var route = new ContactMotionRoute(queries, feet);
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        Vector3 centre = ShellGeometry.Centre(feet, tuning);
        // As in Sweep, a touching start sweeps from an offset off the face that the shell itself does not take.
        Vector3 offset = Vector3.Zero;
        Vector3 achieved = Vector3.Zero;
        Vector3 remaining = move;
        Span<Vector3> carried = stackalloc Vector3[1];
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
            if (!queries.SweepShell(shape, centre + offset, direction, (float)length, out SweepHit hit,
                    QueryFilter.StaticsOnly))
            {
                carried[0] = velocity;
                if (!route.Move(feet + achieved + remaining, out bool constrained, carried))
                    return new(achieved, true, highest, lowest);
                if (constrained)
                {
                    achieved = route.Current - feet;
                    velocity = carried[0];
                    return new(achieved, true, MathF.Max(highest, route.Highest), MathF.Min(lowest, route.Lowest));
                }
                achieved += remaining;
                remaining = Vector3.Zero;
                highest = MathF.Max(highest, achieved.Y);
                lowest = MathF.Min(lowest, achieved.Y);
                break;
            }
            Vector3 normal = hit.Normal;
            if (normal == Vector3.Zero)
            {
                if (!TouchNormal(queries, shape, centre + offset, out normal))
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
                carried[0] = velocity;
                if (!route.Move(feet + achieved, out bool constrained, carried))
                    return new(achieved - advance, true, highest, lowest);
                if (constrained)
                {
                    achieved = route.Current - feet;
                    velocity = carried[0];
                    return new(achieved, true, MathF.Max(highest, route.Highest), MathF.Min(lowest, route.Lowest));
                }
            }
            velocity = Slide(velocity, normal, cosMaxSlope);
            if (slide == MaxSlides)
                break;
            Vector3 rest = Slide(remaining, normal, cosMaxSlope);
            if (rest != remaining)
            {
                blocked = true;
                var along = new Vector2(rest.X, rest.Z);
                if (BoundSlide(new Vector2(feet.X + achieved.X, feet.Z + achieved.Z), ref along,
                        new Vector2(normal.X, normal.Z), clampXz))
                    rest = new Vector3(along.X, rest.Y, along.Y);
            }
            remaining = rest;
        }
        return new ShellFlight(achieved, blocked || remaining != Vector3.Zero, highest, lowest);
    }

    /// <summary>Cuts a slide along a contact, <paramref name="remaining"/> from <paramref name="axis"/>, to the play
    /// area, because the contact can turn a move that ended inside the bound toward it. True when the clamp cut it. A
    /// clamped end that would press into the contact, whose horizontal normal is <paramref name="across"/>, means the
    /// slide runs into the corner of the contact and the bound, so the slide keeps its own direction up to where it
    /// meets the bound and stops in that corner as it would in the corner of two walls.</summary>
    static bool BoundSlide(Vector2 axis, ref Vector2 remaining, Vector2 across, Func<float, float, Vector2>? clampXz)
    {
        Vector2 slide = remaining;
        if (!PlayArea.Clamp(axis, ref remaining, clampXz)) return false;
        if (Vector2.Dot(remaining, across) < 0f)
        {
            remaining = slide * PlayArea.Reach(axis, slide, axis + remaining);
            PlayArea.Clamp(axis, ref remaining, clampXz);
        }
        return true;
    }

    // The vector less its component into the contact. A wall or steep face keeps the vertical part and leaves the
    // contact with horizontal motion alone, so its plane cannot turn a run into a climb.
    static Vector3 Slide(Vector3 vector, Vector3 normal, float cosMaxSlope)
    {
        float into = Vector3.Dot(vector, normal);
        if (!(into < 0f))
            return vector;
        if (normal.Y < 0f || normal.Y >= cosMaxSlope)
            return vector - into * normal;
        float across = normal.X * normal.X + normal.Z * normal.Z;
        if (!(across > 0f))
            return vector;
        float scale = into / across;
        return new Vector3(vector.X - scale * normal.X, vector.Y, vector.Z - scale * normal.Z);
    }

    /// <summary>The outward normal of the face a touching shell rests on, from the MTV of the shell grown by the
    /// skin. False when even the grown shell overlaps nothing.</summary>
    static bool TouchNormal(ContactQueryContext queries, CapsuleShape shape, Vector3 centre, out Vector3 normal)
    {
        var grown = new CapsuleShape(shape.Radius + ContactSkin, shape.Length);
        if (queries.PenetrationShell(grown, centre, out Vector3 mtv) && mtv.LengthSquared() > 0f)
        {
            normal = Vector3.Normalize(mtv);
            return true;
        }
        normal = Vector3.Zero;
        return false;
    }
}
