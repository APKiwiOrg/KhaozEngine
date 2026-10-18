using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// The <see cref="GameApp"/> overlay set: add order, the input hold and fault containment. Passes iterate a
    /// snapshot rebuilt at the start of <see cref="Update"/> only after the set changed, so an idle frame allocates
    /// nothing and an overlay may add or remove overlays, itself included, from inside either pass. A change takes
    /// effect from the next frame, except that a removed overlay that has not run yet in the current pass is
    /// skipped. Membership is by reference, so two overlays that compare equal are still two overlays. When a hold
    /// ends, <see cref="HeldInputRelease"/> hides whatever is still down until its release. Used from the frame
    /// thread only.
    /// </summary>
    internal sealed class GameOverlayHost
    {
        readonly List<IGameOverlay> _overlays = new();
        readonly Action<string, Exception> _error;
        readonly HeldInputRelease _release = new();
        IGameOverlay[] _snapshot = Array.Empty<IGameOverlay>();
        bool _changed;
        bool _held;

        /// <param name="error">Receives the one line logged for a faulting overlay. Defaults to <see cref="Log.Error"/>.</param>
        internal GameOverlayHost(Action<string, Exception>? error = null) => _error = error ?? Log.Error;

        internal bool IsEmpty => _overlays.Count == 0;

        /// <summary>Attach <paramref name="overlay"/> and append it. Throws when it is already present.</summary>
        internal void Add(IGameOverlay overlay, GameOverlayContext context)
        {
            ArgumentNullException.ThrowIfNull(overlay);
            ArgumentNullException.ThrowIfNull(context);
            if (IndexOf(overlay) >= 0)
                throw new InvalidOperationException($"Overlay {overlay.GetType().Name} is already added.");

            overlay.Attach(context);
            _overlays.Add(overlay);
            _changed = true;
        }

        /// <summary>Remove <paramref name="overlay"/>. False when it was not present.</summary>
        internal bool Remove(IGameOverlay overlay)
        {
            int index = IndexOf(overlay);
            if (index < 0) return false;
            _overlays.RemoveAt(index);
            _changed = true;
            return true;
        }

        /// <summary>
        /// Update every overlay with the raw <paramref name="input"/>. Returns <paramref name="input"/> itself when
        /// none holds and nothing is hidden after a hold, else <see cref="InputState.WithoutInteraction"/> or the
        /// frame <see cref="HeldInputRelease"/> filtered. A faulting overlay does not hold.
        /// </summary>
        internal InputState Update(InputState input, float realDt)
        {
            if (_changed)
            {
                _snapshot = _overlays.ToArray();
                _changed = false;
            }

            bool held = false;
            foreach (IGameOverlay overlay in _snapshot)
            {
                if (Skipped(overlay)) continue;
                try
                {
                    held |= overlay.Update(input, realDt);
                }
                catch (Exception ex)
                {
                    Fault(overlay, nameof(IGameOverlay.Update), ex);
                }
            }
            bool holdEnded = _held && !held;
            _held = held;
            return held ? input.WithoutInteraction() : _release.Apply(input, holdEnded);
        }

        /// <summary>Draw every overlay of this frame's snapshot into the begun UI <paramref name="batch"/>.</summary>
        internal void DrawUi(SpriteBatch batch)
        {
            foreach (IGameOverlay overlay in _snapshot)
            {
                if (Skipped(overlay)) continue;
                try
                {
                    overlay.DrawUi(batch);
                }
                catch (Exception ex)
                {
                    Fault(overlay, nameof(IGameOverlay.DrawUi), ex);
                }
            }
        }

        // The membership lookup runs only on a frame whose set changed after the snapshot was taken.
        bool Skipped(IGameOverlay overlay) => _changed && IndexOf(overlay) < 0;

        int IndexOf(IGameOverlay overlay)
        {
            for (int i = 0; i < _overlays.Count; i++)
                if (ReferenceEquals(_overlays[i], overlay)) return i;
            return -1;
        }

        void Fault(IGameOverlay overlay, string pass, Exception ex)
        {
            Remove(overlay);
            // The log line must not take the frame down either. The overlay is already removed.
            try
            {
                _error($"Overlay {overlay.GetType().Name} faulted in {pass} and was removed: {ex.Message}", ex);
            }
            catch (Exception)
            {
                // The line is lost. Removing the overlay still matters.
            }
        }
    }
}
