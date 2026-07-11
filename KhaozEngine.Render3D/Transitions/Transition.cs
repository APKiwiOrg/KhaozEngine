using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Shared phased timing for the built-in <see cref="ITransition"/> effects. Drives cover -> swap (under cover) ->
    /// optional streaming hold -> reveal from an explicit <c>dt</c> and a ready predicate, so it is fully
    /// headless-testable with no GPU. Subclasses add the per-frame render config (a colour, an edge). What "covered"
    /// LOOKS like is the subclass's business; this base owns only the timing and the normalized <see cref="Cover"/>.
    /// </summary>
    public abstract class Transition : ITransition
    {
        readonly float coverSeconds;
        readonly float holdTimeoutSeconds;
        readonly float revealSeconds;
        float phaseElapsed;
        float holdElapsed;
        bool swapFired;

        /// <summary>Configures the phase durations, in seconds (negatives clamp to 0).
        /// <paramref name="coverSeconds"/> 0 covers instantly (a frozen-frame effect);
        /// <paramref name="holdTimeoutSeconds"/> 0 skips the streaming hold (a world-space effect that assumes an
        /// already-streamed destination); <paramref name="revealSeconds"/> 0 reveals instantly.</summary>
        protected Transition(float coverSeconds, float holdTimeoutSeconds, float revealSeconds)
        {
            this.coverSeconds = MathF.Max(0f, coverSeconds);
            this.holdTimeoutSeconds = MathF.Max(0f, holdTimeoutSeconds);
            this.revealSeconds = MathF.Max(0f, revealSeconds);
        }

        /// <inheritdoc/>
        public TransitionPhase Phase { get; private set; } = TransitionPhase.Idle;

        /// <inheritdoc/>
        public bool IsActive => Phase is TransitionPhase.Cover or TransitionPhase.Hold or TransitionPhase.Reveal;

        /// <inheritdoc/>
        public event Action? Swapped;

        /// <inheritdoc/>
        public event Action? Completed;

        /// <inheritdoc/>
        public virtual float Cover => Phase switch
        {
            TransitionPhase.Cover => coverSeconds <= 0f ? 1f : Math.Clamp(phaseElapsed / coverSeconds, 0f, 1f),
            TransitionPhase.Hold => 1f,
            TransitionPhase.Reveal => revealSeconds <= 0f ? 0f : 1f - Math.Clamp(phaseElapsed / revealSeconds, 0f, 1f),
            _ => 0f,   // Idle or Done: fully revealed
        };

        /// <inheritdoc/>
        public void Begin()
        {
            Phase = TransitionPhase.Cover;
            phaseElapsed = 0f;
            holdElapsed = 0f;
            swapFired = false;
        }

        /// <summary>Cancels the transition back to <see cref="TransitionPhase.Idle"/> (fully revealed) WITHOUT firing
        /// <see cref="Swapped"/> or <see cref="Completed"/>. For a consumer teardown that tears down mid-transition (a
        /// disconnect, a scene swap): a stuck transition would otherwise hold the overlay covered forever. Idempotent.</summary>
        public void Reset()
        {
            Phase = TransitionPhase.Idle;
            phaseElapsed = 0f;
            holdElapsed = 0f;
            swapFired = false;
        }

        /// <inheritdoc/>
        public void Update(float dt, bool destinationReady)
        {
            if (!IsActive) return;
            dt = MathF.Max(0f, dt);
            phaseElapsed += dt;
            switch (Phase)
            {
                case TransitionPhase.Cover:
                    if (phaseElapsed >= coverSeconds) Swap();
                    break;
                case TransitionPhase.Hold:
                    holdElapsed += dt;
                    if (destinationReady || holdElapsed >= holdTimeoutSeconds) EnterReveal();
                    break;
                case TransitionPhase.Reveal:
                    if (phaseElapsed >= revealSeconds) { Phase = TransitionPhase.Done; Completed?.Invoke(); }
                    break;
            }
        }

        void Swap()
        {
            if (!swapFired) { swapFired = true; Swapped?.Invoke(); }   // camera warp + reposition happen under cover
            if (holdTimeoutSeconds <= 0f) EnterReveal();               // no streaming hold: straight to reveal
            else { Phase = TransitionPhase.Hold; phaseElapsed = 0f; holdElapsed = 0f; }
        }

        void EnterReveal()
        {
            Phase = TransitionPhase.Reveal;
            phaseElapsed = 0f;
        }
    }
}
