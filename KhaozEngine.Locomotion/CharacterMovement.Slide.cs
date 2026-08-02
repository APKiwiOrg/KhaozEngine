using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The SURFACE-CONTACT half of the movement step: what analytic terrain steeper than MoveTuning.MaxSlopeRadians does
// to a character that meets it. One concern (a steep surface is something you slide on, never a refusal), split out
// of the main CharacterMovement.cs so that file - already the engine's largest - does not grow, exactly as
// CharacterMovement.Fluid.cs, CharacterMovement.Momentum.cs, CharacterMovement.Landing.cs and
// CharacterMovement.Horizontal.cs did. Same partial type, same shared private core: every horizontal advance goes
// through AdvanceWallSlide, and StepCore hands a contact tick to ResolveSlide.
//
// This REPLACES the direction-aware ascent gate of 17.26.0 and its 17.26.1 min-reference fence (#369, #440), which
// two playtests voted down: a gate refuses movement, and refusal is not how terrain behaves. Refusal produced both
// reported bugs - one that a fence was too loose to stop (a repeated jump ratcheting up a sheer face, because the
// raised feet discounted the rise) and one that a tighter fence caused (sideways movement into a face while jumping
// reading as an invisible wall). The model here has neither failure mode available to it, because it never says no:
//
//   1. WALL SLIDE. A horizontal move whose destination ground stands more than MoveTuning.StepHeight above the feet
//      is a wall contact. The into-face component of the move dies and the along-face component survives, so
//      strafing along a cliff mid-jump keeps its lateral travel. Grounded and airborne, command path and momentum
//      path, one function.
//   2. NO TRACTION. Ground steeper than MoveTuning.MaxSlopeRadians never grants support, so gravity decomposes
//      against the surface and the character accelerates down the fall line until it reaches walkable ground, open
//      air, or water. Climbing self-defeats because there is no footing to climb from, which is what retires the
//      ascent gate rather than patching it a third time.
//
// Everything here is pure scalar arithmetic in a fixed order over the same pure delegates both heads hold, so a
// slide replays bit-identically through ClientPrediction.Reconcile. It adds NO carried state: the fall-line speed
// lives in MoveState.HorizontalVelocity and MoveState.VerticalVelocity, both of which already ride the wire.
public static partial class CharacterMovement
{
    // How close the feet must be to steep ground under the CURRENT column for the character to be sliding ON it
    // rather than falling PAST it, in metres. It is the slide's answer to MoveTuning.GroundedEpsilon and exists for
    // the same reason: without a skin, a face that curves away by a hair under a tick's travel drops the character
    // into a one-tick free fall and re-catches it, which reads as chatter.
    //
    // Sized between the two things it has to separate. A slide holds the capsule exactly on the surface by
    // construction (the fall-line integration commits precisely the drop the horizontal travel needs, and the input
    // steer has no fall-line authority), so on the low side it only has to cover float noise and a gently convex
    // face. On the high side it must stay far below one tick of a jump - a jump clears 0.33 m in its first tick at
    // the shipped 9.8 m/s and 30 Hz - so a character that jumps beside a face escapes contact immediately instead of
    // having its launch resolved against the surface.
    private const float SlideContactSkin = 0.05f;

    // Below this the XZ part of a normal carries no direction to read (the surface is level, or the delegate handed
    // back something degenerate), so the face direction falls through to the movement direction. Matches the 1e-6
    // scale the command dead-zones and the momentum epsilon already use, squared for a length-squared test.
    private const float FaceNormalEpsilonSq = 1e-12f;

    /// <summary>True when a ground normal is steeper than the tuning's walkable gate. The single reading of
    /// "too steep" for the whole step: the support decision, the wall contact, and the slide contact all ask this one
    /// question of the same value, in the same way the retired gate asked it - <see cref="MathF.Acos"/> of the
    /// clamped Y against <see cref="MoveTuning.MaxSlopeRadians"/> - so nothing shifted at the threshold itself and a
    /// walkable slope is bit-identical to every release before this one.</summary>
    private static bool IsSteepGround(in Vector3 normal, in MoveTuning tuning)
        => MathF.Acos(Math.Clamp(normal.Y, 0f, 1f)) > tuning.MaxSlopeRadians;

    /// <summary>The face's OUTWARD horizontal direction: the normal's XZ projection, normalized. It points away from
    /// the face and down its fall line, so a positive dot with a velocity means travelling AWAY from the face and a
    /// negative one means INTO it.
    /// <para>When the projection is degenerate (a vertical-only normal, which a level surface has and which a
    /// mismatched normal/height delegate pair can also produce) there is no face direction to read, so the movement
    /// direction stands in as the face's own: the face is met head-on and the whole move dies. That is the
    /// conservative direction, and the only one that keeps a mismatched pair from admitting a move under
    /// terrain.</para></summary>
    private static (float x, float z) FaceDirection(in Vector3 normal, Vector2 velocity)
    {
        float lenSq = normal.X * normal.X + normal.Z * normal.Z;
        if (lenSq > FaceNormalEpsilonSq)
        {
            float inv = 1f / MathF.Sqrt(lenSq);
            return (normal.X * inv, normal.Z * inv);
        }
        float vSq = velocity.X * velocity.X + velocity.Y * velocity.Y;
        if (vSq <= FaceNormalEpsilonSq) return (0f, 0f);
        float vInv = 1f / MathF.Sqrt(vSq);
        return (-velocity.X * vInv, -velocity.Y * vInv);
    }

    /// <summary>Advance an XZ position by a horizontal velocity for one tick, WALL-SLIDING off analytic terrain that
    /// the step cannot reach: when the destination's ground normal is steeper than
    /// <see cref="MoveTuning.MaxSlopeRadians"/> AND its ground stands more than <see cref="MoveTuning.StepHeight"/>
    /// above the feet, the move keeps only its along-face component. Shared by the ordinary command path
    /// (<c>DesiredHorizontalCore</c>), the airborne momentum path, and the slide, so the rule cannot come to mean
    /// three different things depending on which one drove the tick.
    /// <para>BOTH CONDITIONS ARE LOAD-BEARING. The steepness test is what leaves walkable ground untouched: a fast
    /// run up a legal ramp can rise more than a StepHeight in one tick, and treating that as a wall would turn every
    /// steep-but-walkable hill into a fence at high speed. The height test is what makes this a CONTACT rather than
    /// the retired gate: ground within a step of the feet is something the character can be seated on, so it is
    /// admitted, and the no-traction rule (see <c>ResolveSlide</c>) is what makes doing so worthless rather than a
    /// climb.</para>
    /// <para>THE PROJECTION IS THE WHOLE FIX FOR THE REPORTED FEEL BUG. The retired gate refused the entire move the
    /// moment any of it pointed at a face, so holding a direction 45 degrees into a cliff while jumping lost the
    /// lateral half too - an invisible wall eating air control. Removing only the into-face component leaves the
    /// along-face travel exactly what it would have been with no face there at all.</para>
    /// <para>ANTI-TUNNEL. The projected move is re-tested against the same two conditions, and refused outright if it
    /// still lands in a wall. That only happens in a concave corner, where sliding along one face runs into another
    /// and there is genuinely nowhere to go, and it is what keeps the property the refusal gate used to carry alone:
    /// an XZ can never be committed under terrain and left for a later ground clamp to pop the capsule up a
    /// cliff.</para>
    /// <para><c>active</c> false is a tick with nothing to advance, and it skips the sampling entirely rather than
    /// evaluating the delegates at the unchanged position. <c>feetY</c> is the world Y of the character's FEET this
    /// tick: the capsule centre minus <see cref="MoveTuning.CapsuleHalfHeight"/>, since <see cref="MoveState.Position"/>
    /// is the capsule CENTRE.</para></summary>
    private static (float x, float z) AdvanceWallSlide(float x, float z, Vector2 velocity, bool active, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, Func<float, float, float> groundHeight,
        float feetY)
    {
        if (!active) return (x, z);
        float nx = x + velocity.X * dt;
        float nz = z + velocity.Y * dt;
        if (groundNormal is null) return (nx, nz);
        // Fixed order on both heads: the destination normal first, and its height only inside the steep branch, so a
        // walkable tick costs exactly the one delegate call the retired gate cost it.
        Vector3 destNormal = groundNormal(nx, nz);
        if (!IsSteepGround(destNormal, tuning)) return (nx, nz);
        if (groundHeight(nx, nz) - feetY <= tuning.StepHeight) return (nx, nz);

        (float fx, float fz) = FaceDirection(destNormal, velocity);
        float into = velocity.X * fx + velocity.Y * fz;
        if (into >= 0f) return (nx, nz);        // travelling away from the face: the wall is behind, nothing to remove
        float sx = velocity.X - into * fx;
        float sz = velocity.Y - into * fz;
        float tx = x + sx * dt;
        float tz = z + sz * dt;
        Vector3 tangentNormal = groundNormal(tx, tz);
        if (IsSteepGround(tangentNormal, tuning) && groundHeight(tx, tz) - feetY > tuning.StepHeight) return (x, z);
        return (tx, tz);
    }

    /// <summary>Whether the character is IN CONTACT with ground too steep to stand on, which is the one condition
    /// that turns a tick into a slide. Read from the START of the tick (the carried position and the ground under
    /// its own column), so it is a pure function of carried state and a reconcile replay reaches the same answer.
    /// <para>Three conjuncts, cheapest and most selective first so an ordinary tick pays almost nothing.
    /// <c>!Grounded</c> is not merely an optimisation: a character STANDING ON A PROP that bridges a steep gully is
    /// grounded on the prop, and the terrain normal beneath it must not slide it off. It is also self-consistent,
    /// because the support decision at the end of the previous tick already refused to ground the character on steep
    /// terrain - so a grounded character is, by construction, on walkable ground or on a prop. Then the contact test
    /// (one <c>groundHeight</c> call, which a character falling through open air fails immediately), and only then
    /// the normal.</para></summary>
    private static bool SlideContact(in MoveState state, in MoveTuning tuning, float halfHeight,
        Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal, out Vector3 normal)
    {
        normal = default;
        if (state.Grounded || groundNormal is null) return false;
        if (state.Position.Y - halfHeight > groundHeight(state.Position.X, state.Position.Z) + SlideContactSkin)
            return false;
        normal = groundNormal(state.Position.X, state.Position.Z);
        return IsSteepGround(normal, tuning);
    }

    /// <summary>What one SLIDING tick resolves to: the advanced XZ, the velocity the step is asking for (which
    /// <see cref="MoveState.CommandedVelocity"/> exports and the server anomaly check measures denial against), the
    /// FALL-LINE part of that velocity alone (which is what carries to the next tick), and the vertical velocity that
    /// replaces the ordinary gravity integrate for this tick.</summary>
    private readonly record struct SlideStep(float X, float Z, Vector2 Commanded, Vector2 Carry, float VerticalVelocity);

    /// <summary>One tick on ground too steep to stand on: gravity decomposed against the surface, integrated along
    /// the fall line, and advanced through the same wall slide every other path uses.
    ///
    /// <para>THE MATH, in one sentence: the surface has a unit down-slope tangent
    /// <c>T = (ny*hx, -h, ny*hz)</c> - where <c>ny</c> is the normal's Y, <c>h = sqrt(1 - ny*ny)</c> is its
    /// horizontal magnitude, and <c>(hx, hz)</c> is its XZ direction - gravity along that tangent is exactly
    /// <c>g . T = Gravity * h</c>, and the whole slide is the scalar speed along <c>T</c>, integrated by that one
    /// acceleration and read back out as <c>speed * T</c>. So the horizontal acceleration is
    /// <c>Gravity * ny * h</c> down the fall line and the vertical is <c>-Gravity * h * h</c>, which are the two
    /// components of <c>g - (g.n)n</c> written from the same two numbers. A vertical wall (<c>ny</c> 0, <c>h</c> 1)
    /// gives free fall with no horizontal, and a 45 degree face gives an equal split - both correct by
    /// inspection.</para>
    ///
    /// <para>WHY A SCALAR AND NOT TWO INTEGRATORS. Resolving the carried velocity onto <c>T</c> and rebuilding it
    /// from the result keeps the horizontal and the vertical EXACTLY consistent with the surface, every tick, for
    /// free. Two consequences fall out. The tick a character first meets the face, its into-surface velocity is
    /// discarded and its along-surface velocity is kept, which is the inelastic contact a real fall onto a cliff has
    /// (grazing a near-vertical face keeps essentially all of the fall, hitting a 46 degree one keeps about
    /// seven-tenths). And on a planar face the committed drop is precisely the drop the committed horizontal travel
    /// needs, so the ground clamp never has to correct the slide and the character stays glued to the surface
    /// instead of bouncing off it.</para>
    ///
    /// <para>THE SPEED NEVER GOES NEGATIVE, and that is the whole no-ascent property. A negative fall-line speed is
    /// motion UP the face, and a surface you have no purchase on cannot carry you up it: clamping at zero is what
    /// makes the retired jump-ratchet exploit (#440) structurally unavailable rather than fenced off. Combined with
    /// the input rule below, the total horizontal velocity of a sliding character never has an up-slope component at
    /// all, so its destination column is never above its current one and the ground clamp has nothing to lift.</para>
    ///
    /// <para>INPUT STEERS ACROSS THE FALL LINE ONLY, at the same <see cref="MoveTuning.AirControl"/>-scaled speed the
    /// ordinary airborne path already commands - no new knob. The fall-line component of the command is removed in
    /// BOTH directions, not only up-slope: a character with no footing can neither push itself up the face nor push
    /// itself down it, and the across-slope direction is also the only one that keeps the body on the surface (it
    /// follows the contour, so it needs no drop to pay for). Allowing a down-slope push instead buys a hop off the
    /// surface every tick it is held, which is a visible bounce on any moderate slope.</para>
    ///
    /// <para>TERMINAL VELOCITY is <see cref="MoveTuning.MaxFallSpeed"/>, read through the surface: the clamp is
    /// applied to the fall-line speed as <c>MaxFallSpeed / h</c> so that the VERTICAL component lands exactly on the
    /// terminal the ordinary fall obeys, and the horizontal stays consistent with it rather than growing without
    /// bound and floating the character off the face. <c>h</c> is at least <c>sin(MaxSlopeRadians)</c> here (the
    /// caller has already established a steep normal), so the divide is safe.</para></summary>
    private static SlideStep ResolveSlide(in MoveState state, Vector2 moveDir, float speedFraction, bool run,
        float dt, in MoveTuning tuning, in Vector3 normal, Func<float, float, Vector3>? groundNormal,
        Func<float, float, float> groundHeight, float speedScale, float halfHeight)
    {
        // The surface frame. ny is clamped and h is DERIVED from it rather than measured, so the tangent is a unit
        // vector by construction even if a consumer's delegate hands back a normal that is not quite normalized.
        // Only the DIRECTION comes from the raw XZ.
        float ny = Math.Clamp(normal.Y, 0f, 1f);
        float h = MathF.Sqrt(MathF.Max(0f, 1f - ny * ny));
        (float hx, float hz) = FaceDirection(normal, moveDir);
        float tx = ny * hx, ty = -h, tz = ny * hz;

        // The carried velocity read as a fall-line speed, never upward, then accelerated by gravity along the
        // tangent and capped at the terminal the vertical axis obeys.
        float speed = state.HorizontalVelocity.X * tx + state.VerticalVelocity * ty + state.HorizontalVelocity.Y * tz;
        if (speed < 0f) speed = 0f;
        speed += tuning.Gravity * h * dt;
        float terminal = h > FaceNormalEpsilonSq ? tuning.MaxFallSpeed / h : tuning.MaxFallSpeed;
        if (speed > terminal) speed = terminal;
        var carry = new Vector2(speed * tx, speed * tz);
        float vVel = speed * ty;

        // The steer: the commanded velocity with its whole fall-line component removed.
        Vector2 commanded = carry;
        if (speedFraction > 0f)
        {
            float inputSpeed = (run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale * speedFraction;
            float ux = moveDir.X * inputSpeed, uz = moveDir.Y * inputSpeed;
            float fall = ux * hx + uz * hz;
            commanded = new Vector2(carry.X + (ux - fall * hx), carry.Y + (uz - fall * hz));
        }

        (float x, float z) = AdvanceWallSlide(state.Position.X, state.Position.Z, commanded,
            commanded != Vector2.Zero, dt, tuning, groundNormal, groundHeight, state.Position.Y - halfHeight);
        return new SlideStep(x, z, commanded, carry, vVel);
    }
}
