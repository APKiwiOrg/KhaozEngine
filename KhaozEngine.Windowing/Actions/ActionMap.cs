using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing.Actions
{
    /// <summary>
    /// A named set of actions plus their bindings, for one player. This is BOTH the declaration (actions + default
    /// bindings, added via <c>AddAction</c> / <see cref="Bind"/>) and the per-frame runtime: call
    /// <see cref="Update"/> once per frame with the immutable <see cref="InputState"/> snapshot, then read action
    /// state by id via <see cref="IsDown"/> / <see cref="WasPressed"/> / <see cref="WasReleased"/> / <see cref="GetAxis"/> /
    /// <see cref="GetAxis2D"/>. Actions are declared via <c>AddAction</c>. Evaluation is pure: state in (the snapshot
    /// plus the map's previous snapshot), values out.
    /// Nothing here touches Silk.NET or the window; it only reads snapshots, so it is fully headless-testable.
    ///
    /// <para><b>Per-player.</b> Each map targets one <see cref="PlayerIndex"/>; gamepad sources read that player's pad
    /// (<c>input.Gamepad((int)PlayerIndex)</c>). Keyboard/mouse sources are global (one keyboard), so two maps on
    /// different players sharing a keyboard binding both see it: split by using distinct keys or gamepad-only bindings
    /// for player 2+.</para>
    ///
    /// <para><b>Combining multiple bindings (documented semantics):</b>
    /// <list type="bullet">
    /// <item><b>Button</b>: OR. The action is down if ANY binding is down; pressed if any binding's press edge fires
    /// this frame AND the action was not already down last frame; released is the symmetric was-down-now-up edge.</item>
    /// <item><b>Axis1D</b>: SUM then clamp to [-1, 1]. Bindings add (so a stick at 0.3 plus a key at 1 saturates), and
    /// the result is clamped.</item>
    /// <item><b>Axis2D</b>: per-component SUM, then the WASD/keyboard composites are normalized so a diagonal is length
    /// 1 (a raw WASD diagonal is (1,1) length ~1.414, which would move faster diagonally). A source that is already a
    /// stick (a real analog magnitude) is clamped to length 1 but NOT re-normalized up. After summing all bindings the
    /// combined vector is clamped to length 1. See <see cref="GetAxis2D"/>.</item>
    /// </list>
    /// Edge detection (pressed/released) for button actions is computed the same way <see cref="InputManager"/> does
    /// it: from the current snapshot's own press/release sets where the source exposes them, plus a was-down/now-down
    /// comparison against the previous frame for composite/axis-as-button sources.</para>
    /// </summary>
    public sealed class ActionMap
    {
        readonly Dictionary<string, InputActionKind> _kinds = new();
        readonly Dictionary<string, List<InputBinding>> _bindings = new();
        readonly List<string> _order = new();

        // Per-frame evaluated state, refreshed by Update.
        readonly Dictionary<string, bool> _downNow = new();
        readonly Dictionary<string, bool> _downPrev = new();
        InputState _input = InputState.Empty;
        bool _hasFrame;

        /// <summary>The player whose gamepad this map reads. Keyboard/mouse are global.</summary>
        public PlayerIndex PlayerIndex { get; }

        /// <summary>A map for the given player (default player one).</summary>
        public ActionMap(PlayerIndex player = PlayerIndex.One) { PlayerIndex = player; }

        /// <summary>The action ids declared on this map, in declaration order.</summary>
        public IReadOnlyList<string> ActionIds => _order;

        /// <summary>Declare an action. Redeclaring the same id updates its kind and keeps its bindings.</summary>
        public ActionMap AddAction(InputAction action)
        {
            if (!_kinds.ContainsKey(action.Id))
            {
                _order.Add(action.Id);
                _bindings[action.Id] = new List<InputBinding>();
                _downNow[action.Id] = false;
                _downPrev[action.Id] = false;
            }
            _kinds[action.Id] = action.Kind;
            return this;
        }

        /// <summary>Declare an action AND append one or more default bindings in one call.</summary>
        public ActionMap AddAction(InputAction action, params InputSource[] defaults)
        {
            AddAction(action);
            foreach (var s in defaults) Bind(action.Id, s);
            return this;
        }

        /// <summary>Append a binding (a new slot) to an already-declared action. Throws if the id is unknown.</summary>
        public ActionMap Bind(string actionId, InputSource source)
        {
            RequireAction(actionId);
            _bindings[actionId].Add(InputBinding.Of(source));
            return this;
        }

        /// <summary>The kind of a declared action. Throws if unknown.</summary>
        public InputActionKind KindOf(string actionId)
        {
            RequireAction(actionId);
            return _kinds[actionId];
        }

        /// <summary>True if the id is declared on this map.</summary>
        public bool HasAction(string actionId) => _kinds.ContainsKey(actionId);

        /// <summary>The bindings on an action, in slot order (slot = list index). Empty if none. Throws if unknown id.</summary>
        public IReadOnlyList<InputBinding> BindingsOf(string actionId)
        {
            RequireAction(actionId);
            return _bindings[actionId];
        }

        /// <summary>Number of binding slots on an action.</summary>
        public int BindingCount(string actionId)
        {
            RequireAction(actionId);
            return _bindings[actionId].Count;
        }

        /// <summary>
        /// Replace the source at <paramref name="slot"/> (used by rebinding). If <paramref name="slot"/> equals the
        /// current count the source is appended (a new slot). Throws for a slot beyond that.
        /// </summary>
        public void SetBinding(string actionId, int slot, InputSource source)
        {
            RequireAction(actionId);
            var list = _bindings[actionId];
            if (slot == list.Count) { list.Add(InputBinding.Of(source)); return; }
            if (slot < 0 || slot > list.Count)
                throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {slot} out of range for action '{actionId}' (has {list.Count}).");
            list[slot] = InputBinding.Of(source);
        }

        /// <summary>Remove all bindings from an action (used before re-applying persisted or default bindings).</summary>
        public void ClearBindings(string actionId)
        {
            RequireAction(actionId);
            _bindings[actionId].Clear();
        }

        // ---- per-frame runtime ---------------------------------------------

        /// <summary>
        /// Evaluate all actions from this frame's snapshot. Call once per frame before reading. The previous frame's
        /// "down" set is retained for composite/axis-as-button press/release edges.
        /// </summary>
        public void Update(InputState input)
        {
            _input = input;
            int player = (int)PlayerIndex;
            foreach (var id in _order)
            {
                _downPrev[id] = _hasFrame && _downNow[id];
                _downNow[id] = EvaluateDown(id, input, player);
            }
            _hasFrame = true;
        }

        bool EvaluateDown(string id, InputState input, int player)
        {
            foreach (var b in _bindings[id])
                if (b.Source.EvaluateDown(input, player)) return true;
            return false;
        }

        /// <summary>True while the button action is held (OR of its bindings). Non-button actions: down when magnitude &gt; 0.5.</summary>
        public bool IsDown(string actionId)
        {
            RequireAction(actionId);
            return _downNow.TryGetValue(actionId, out var d) && d;
        }

        /// <summary>
        /// True only on the frame the button action went down. Uses each binding's own press edge (key/gamepad-button
        /// press sets) OR'd, gated so it does not fire if the action was already down last frame; composite/axis
        /// sources contribute a was-up/now-down edge against the previous snapshot.
        /// </summary>
        public bool WasPressed(string actionId)
        {
            RequireAction(actionId);
            if (!_hasFrame) return false;
            // Press edge = down now, up last frame. This composes the OR-over-bindings "down" set (which already
            // uses each binding's snapshot state) with the map's previous-frame memory, so a single held-then-released
            // binding re-fires correctly and multiple bindings never double-fire.
            return _downNow[actionId] && !_downPrev[actionId];
        }

        /// <summary>True only on the frame the button action went up (was-down last frame, up now, or a snapshot release edge).</summary>
        public bool WasReleased(string actionId)
        {
            RequireAction(actionId);
            if (!_hasFrame) return false;
            // Release edge = down last frame, up now. Symmetric with WasPressed against the previous snapshot.
            return _downPrev[actionId] && !_downNow[actionId];
        }

        /// <summary>
        /// The 1D value of an axis action: SUM of all bindings' 1D readings, clamped to [-1, 1]. Also works on a
        /// Button action (returns 1 while down, else 0) and on an Axis2D action (returns its X).
        /// </summary>
        public float GetAxis(string actionId)
        {
            RequireAction(actionId);
            if (_kinds[actionId] == InputActionKind.Axis2D) return GetAxis2D(actionId).X;
            int player = (int)PlayerIndex;
            float sum = 0f;
            foreach (var b in _bindings[actionId]) sum += b.Source.EvaluateAxis1D(_input, player);
            return Math.Clamp(sum, -1f, 1f);
        }

        /// <summary>
        /// The 2D value of an Axis2D action. Each binding is read as a 2D vector; keyboard composites (WASD) are
        /// normalized so a diagonal is unit length (equal speed in all 8 directions), sticks keep their analog
        /// magnitude (clamped to 1). All bindings are summed, then the combined vector is clamped to length 1.
        /// </summary>
        public Vector2 GetAxis2D(string actionId)
        {
            RequireAction(actionId);
            int player = (int)PlayerIndex;
            Vector2 sum = Vector2.Zero;
            foreach (var b in _bindings[actionId])
            {
                Vector2 v = b.Source.EvaluateAxis2D(_input, player);
                if (b.Source.Kind == InputSourceKind.KeyAxis2D)
                {
                    // WASD diagonal normalization: a raw (1,1) becomes unit length so diagonal != faster.
                    if (v.LengthSquared() > 1f) v = Vector2.Normalize(v);
                }
                else if (v.LengthSquared() > 1f)
                {
                    v = Vector2.Normalize(v);
                }
                sum += v;
            }
            if (sum.LengthSquared() > 1f) sum = Vector2.Normalize(sum);
            return sum;
        }

        void RequireAction(string actionId)
        {
            if (actionId is null) throw new ArgumentNullException(nameof(actionId));
            if (!_kinds.ContainsKey(actionId))
                throw new KeyNotFoundException($"Action '{actionId}' is not declared on this map.");
        }
    }
}
