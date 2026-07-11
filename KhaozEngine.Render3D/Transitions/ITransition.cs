using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A screen- or world-space transition that masks a teleport. Phased: <b>cover</b> -> <b>swap</b> (the world
    /// repositions and the follow camera warps under cover) -> optional streaming <b>hold</b> -> <b>reveal</b>. The
    /// engine ships three built-ins - <see cref="HardBlink"/>, <see cref="CameraDissolve"/>, <see cref="CharDissolve"/>
    /// - and a consumer selects, tunes, and gates them per teleport site (login/reconnect, self-rescue, fast-travel),
    /// or authors its own. The timing is pure (no GPU) so it is headless-testable; the render reads <see cref="Cover"/>
    /// / <see cref="Phase"/> each frame (the built-ins' render is wired into the Render3D scene).
    /// </summary>
    public interface ITransition
    {
        /// <summary>The current lifecycle phase.</summary>
        TransitionPhase Phase { get; }

        /// <summary>True while running (<see cref="TransitionPhase.Cover"/>/<see cref="TransitionPhase.Hold"/>/
        /// <see cref="TransitionPhase.Reveal"/>); false when <see cref="TransitionPhase.Idle"/> or
        /// <see cref="TransitionPhase.Done"/>.</summary>
        bool IsActive { get; }

        /// <summary>How covered the view is: 0 = fully revealed (the live view), 1 = fully covered (the swap is
        /// masked). Ramps 0->1 during <see cref="TransitionPhase.Cover"/>, holds at 1 during
        /// <see cref="TransitionPhase.Hold"/>, ramps 1->0 during <see cref="TransitionPhase.Reveal"/>. What "covered"
        /// looks like is the effect's business (a solid overlay, a frozen-frame crossfade, an avatar dissolve).</summary>
        float Cover { get; }

        /// <summary>Starts the transition from the beginning (re-arms it if already running).</summary>
        void Begin();

        /// <summary>Advances the transition by <paramref name="dt"/> seconds. <paramref name="destinationReady"/>
        /// releases the streaming <see cref="TransitionPhase.Hold"/> as soon as it is true (effects that do not hold
        /// ignore it); the hold is otherwise released by its bounded timeout so it can never hang. Fires
        /// <see cref="Swapped"/> once when the cover completes and <see cref="Completed"/> once on entering
        /// <see cref="TransitionPhase.Done"/>.</summary>
        void Update(float dt, bool destinationReady);

        /// <summary>Fired once, at full cover before the reveal: the consumer warps the follow camera and repositions
        /// under cover here.</summary>
        event Action? Swapped;

        /// <summary>Fired once when the transition finishes (enters <see cref="TransitionPhase.Done"/>).</summary>
        event Action? Completed;
    }
}
