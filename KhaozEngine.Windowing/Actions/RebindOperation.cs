using System;
using System.Collections.Generic;

namespace KhaozEngine.Windowing.Actions
{
    /// <summary>Outcome of feeding one snapshot to a <see cref="RebindOperation"/>.</summary>
    public enum RebindStatus
    {
        /// <summary>Still listening; no eligible source went down this frame.</summary>
        Listening = 0,
        /// <summary>An eligible source was captured and applied to the map. <see cref="RebindOperation.Captured"/> holds it.</summary>
        Captured = 1,
        /// <summary>A cancel/exclusion source fired; the operation ended without changing the binding.</summary>
        Cancelled = 2,
    }

    /// <summary>
    /// A pure, headless rebind capture flow. Create one for an (actionId, slot), then feed it successive
    /// <see cref="InputState"/> snapshots via <see cref="Feed"/>. It watches the snapshot's PRESS edges (so a key
    /// held from before the rebind started is ignored until re-pressed) and, on the first eligible press, replaces the
    /// binding on the target map and reports the captured <see cref="InputSource"/>. Excluded sources (e.g. the cancel
    /// key, Escape) end the operation without changing anything.
    ///
    /// <para>Eligible capture kinds: keyboard keys, mouse buttons, gamepad buttons (on the map's player pad), and a
    /// full-tilt gamepad stick/trigger (captured as an axis source). Sticks/triggers capture when pushed past
    /// <see cref="AxisCaptureThreshold"/> so a resting stick does not immediately steal the binding.</para>
    ///
    /// <para>Nothing here touches Silk.NET; it is fed snapshots, so it is fully headless-testable. Games drive it from
    /// their rebind UI: start it, feed each frame's snapshot, react to <see cref="RebindStatus"/>.</para>
    /// </summary>
    public sealed class RebindOperation
    {
        /// <summary>Stick/trigger magnitude past which a rebind captures it as an axis source.</summary>
        public const float AxisCaptureThreshold = 0.7f;

        readonly ActionMap _map;
        readonly int _player;
        readonly HashSet<Key> _excludedKeys;
        readonly HashSet<GamepadButton> _excludedButtons;

        /// <summary>The action being rebound.</summary>
        public string ActionId { get; }
        /// <summary>The binding slot being replaced (or appended when it equals the current binding count).</summary>
        public int Slot { get; }
        /// <summary>The current status.</summary>
        public RebindStatus Status { get; private set; } = RebindStatus.Listening;
        /// <summary>The captured source once <see cref="Status"/> is <see cref="RebindStatus.Captured"/>, else <see cref="InputSource.None"/>.</summary>
        public InputSource Captured { get; private set; } = InputSource.None;

        /// <summary>True while still listening for input.</summary>
        public bool IsListening => Status == RebindStatus.Listening;

        /// <summary>
        /// Begin a rebind. <paramref name="excludeKeys"/> defaults to <see cref="Key.Escape"/> (the cancel key) when
        /// null; pass an empty set to allow rebinding Escape itself. <paramref name="excludeButtons"/> lets a game
        /// reserve gamepad buttons (e.g. Start/Guide) from capture.
        /// </summary>
        public RebindOperation(
            ActionMap map, string actionId, int slot,
            IEnumerable<Key>? excludeKeys = null, IEnumerable<GamepadButton>? excludeButtons = null)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            if (!map.HasAction(actionId))
                throw new KeyNotFoundException($"Action '{actionId}' is not declared on this map.");
            ActionId = actionId;
            Slot = slot;
            _player = (int)map.PlayerIndex;
            _excludedKeys = excludeKeys is null ? new HashSet<Key> { Key.Escape } : new HashSet<Key>(excludeKeys);
            _excludedButtons = excludeButtons is null ? new HashSet<GamepadButton>() : new HashSet<GamepadButton>(excludeButtons);
        }

        /// <summary>
        /// Feed one frame's snapshot. Returns the (possibly updated) status. Once terminal (Captured / Cancelled),
        /// further feeds are no-ops that return the terminal status.
        /// </summary>
        public RebindStatus Feed(InputState input)
        {
            if (Status != RebindStatus.Listening) return Status;

            // Cancel first: an excluded key/button PRESS this frame ends the operation.
            foreach (var k in input.KeysPressed)
                if (_excludedKeys.Contains(k)) { Status = RebindStatus.Cancelled; return Status; }
            var pad = input.Gamepad(_player);
            foreach (var btn in pad.ButtonsPressed)
                if (_excludedButtons.Contains(btn)) { Status = RebindStatus.Cancelled; return Status; }

            // Keyboard capture (press edge, first in enum order for determinism).
            foreach (var k in input.KeysPressed)
            {
                if (_excludedKeys.Contains(k)) continue;
                return Apply(InputSource.FromKey(k));
            }

            // Mouse-button capture (press edge).
            foreach (var mb in input.MousePressed)
                return Apply(InputSource.FromMouseButton(mb));

            // Gamepad-button capture (press edge on the player's pad).
            foreach (var btn in pad.ButtonsPressed)
            {
                if (_excludedButtons.Contains(btn)) continue;
                return Apply(InputSource.FromGamepadButton(btn));
            }

            // Gamepad stick / trigger capture (full-tilt, so a resting stick doesn't steal focus).
            var l = pad.LeftStickDeadzoned();
            var r = pad.RightStickDeadzoned();
            if (MathF.Abs(l.X) >= AxisCaptureThreshold) return Apply(InputSource.StickAxis(GamepadStick.Left, StickComponent.X, invert: l.X < 0));
            if (MathF.Abs(l.Y) >= AxisCaptureThreshold) return Apply(InputSource.StickAxis(GamepadStick.Left, StickComponent.Y, invert: l.Y < 0));
            if (MathF.Abs(r.X) >= AxisCaptureThreshold) return Apply(InputSource.StickAxis(GamepadStick.Right, StickComponent.X, invert: r.X < 0));
            if (MathF.Abs(r.Y) >= AxisCaptureThreshold) return Apply(InputSource.StickAxis(GamepadStick.Right, StickComponent.Y, invert: r.Y < 0));
            if (pad.LeftTrigger >= AxisCaptureThreshold) return Apply(InputSource.Trigger(GamepadTriggerSide.Left));
            if (pad.RightTrigger >= AxisCaptureThreshold) return Apply(InputSource.Trigger(GamepadTriggerSide.Right));

            return Status;
        }

        RebindStatus Apply(InputSource source)
        {
            _map.SetBinding(ActionId, Slot, source);
            Captured = source;
            Status = RebindStatus.Captured;
            return Status;
        }
    }
}
