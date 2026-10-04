using System;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// An opt-in tap rule for <see cref="PointerGesture"/> that judges the straight-line distance from the press point
    /// rather than the summed path length, so a click that wobbles a few points and comes back is still a tap.
    /// </summary>
    /// <remarks>
    /// The press frame starts a clock at zero and each later held frame adds its elapsed time before it is judged.
    /// Distance is measured from <see cref="PointerGesture.TapPosition"/>, the cursor at the end of the press frame, so
    /// the press frame's own mouse delta never counts. While the clock is below <see cref="GraceSeconds"/> the press
    /// stays undecided up to <see cref="GraceDistancePoints"/>, and from then on up to <see cref="DistancePoints"/>.
    /// The press becomes a drag on the first frame its distance exceeds the limit in force.
    /// </remarks>
    /// <param name="DistancePoints">The distance from the press point, in window points, beyond which a press is a drag
    /// once the grace has ended. Finite and positive. It is also the gesture's <see cref="PointerGesture.ThresholdPixels"/>.
    /// </param>
    /// <param name="GraceSeconds">How long after the press the wider <see cref="GraceDistancePoints"/> applies.
    /// Finite and not negative.</param>
    /// <param name="GraceDistancePoints">The distance limit during the grace. Finite and at least
    /// <see cref="DistancePoints"/>.</param>
    public sealed record PointerTapTolerance(float DistancePoints, float GraceSeconds, float GraceDistancePoints)
    {
        /// <summary>The longest catch-up step, in window points, on a crossing the grace decided: the first held frame at
        /// or past <see cref="GraceSeconds"/>, with the distance still within <see cref="GraceDistancePoints"/>. A
        /// press that crept through the grace would otherwise jump by everything it gathered. A crossing the distance
        /// alone would have made keeps its full catch-up. Zero drops the capped catch-up. Not negative and not NaN.
        /// Unlimited by default.</summary>
        public float CatchUpLimitPoints { get; init; } = float.PositiveInfinity;

        internal void Validate(string paramName)
        {
            if (!float.IsFinite(DistancePoints) || DistancePoints <= 0f)
            {
                throw new ArgumentException($"DistancePoints {DistancePoints} must be finite and positive.", paramName);
            }

            if (!float.IsFinite(GraceSeconds) || GraceSeconds < 0f)
            {
                throw new ArgumentException($"GraceSeconds {GraceSeconds} must be finite and not negative.", paramName);
            }

            if (!float.IsFinite(GraceDistancePoints) || !(GraceDistancePoints >= DistancePoints))
            {
                throw new ArgumentException(
                    $"GraceDistancePoints {GraceDistancePoints} must be finite and at least DistancePoints {DistancePoints}.", paramName);
            }

            if (!(CatchUpLimitPoints >= 0f))
            {
                throw new ArgumentException($"CatchUpLimitPoints {CatchUpLimitPoints} must not be negative or NaN.", paramName);
            }
        }

        internal float LimitAt(float heldSeconds) => heldSeconds < GraceSeconds ? GraceDistancePoints : DistancePoints;

        internal Vector2 CapCatchUp(Vector2 catchUp)
        {
            float length = catchUp.Length();
            return length > CatchUpLimitPoints ? catchUp * (CatchUpLimitPoints / length) : catchUp;
        }
    }
}
