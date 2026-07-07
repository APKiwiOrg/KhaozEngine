using System;

namespace KhaozEngine.Windowing.Actions
{
    /// <summary>
    /// Turn-key wrapper around an <see cref="ActionMap"/>: declare-once, load-persisted, evaluate-per-frame,
    /// save-on-change, with the minimum a game must write. It keeps the persistence boundary on the GAME side (the
    /// engine deliberately has no Windowing -&gt; Persistence dependency): you hand it a <c>save</c> callback that
    /// takes the serialized JSON string and writes it wherever the game keeps settings, and you feed it the loaded
    /// JSON string at construction.
    ///
    /// <para>Usage: build the map with default bindings, construct a controller with the persisted string (or null on
    /// first run) and a save sink, then call <see cref="Update"/> each frame and read via the <see cref="Map"/> or the
    /// pass-through readers. Start a rebind with <see cref="BeginRebind"/> and drive it via <see cref="Update"/>; when
    /// it captures, the controller auto-saves through your sink.</para>
    /// </summary>
    public sealed class ActionMapController
    {
        readonly Action<string>? _save;
        RebindOperation? _rebind;

        /// <summary>The underlying map (read action state here, or via the pass-throughs below).</summary>
        public ActionMap Map { get; }

        /// <summary>The rebind in flight, or null. Non-null while capturing.</summary>
        public RebindOperation? ActiveRebind => _rebind;

        /// <summary>True while a rebind is listening (games swallow gameplay input then).</summary>
        public bool IsRebinding => _rebind is { IsListening: true };

        /// <summary>
        /// Build a controller over an already-declared map. <paramref name="persistedJson"/> is the string previously
        /// produced by <see cref="Save"/> (or null/empty on first run); its overrides are applied immediately onto the
        /// map's defaults. <paramref name="save"/> receives the serialized JSON whenever the bindings change (a rebind
        /// captures or you call <see cref="Save"/>); pass null to disable auto-save.
        /// </summary>
        public ActionMapController(ActionMap map, string? persistedJson = null, Action<string>? save = null)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            _save = save;
            if (!string.IsNullOrWhiteSpace(persistedJson))
                ActionMapSerializer.Load(map, persistedJson);
        }

        /// <summary>
        /// Evaluate this frame. If a rebind is active it is fed the snapshot instead of normal evaluation being the
        /// only thing that happens (the map is still evaluated so held actions stay readable). On a capture the
        /// controller auto-saves and the rebind clears.
        /// </summary>
        public void Update(InputState input)
        {
            if (_rebind is { IsListening: true })
            {
                var status = _rebind.Feed(input);
                if (status == RebindStatus.Captured) { Save(); _rebind = null; }
                else if (status == RebindStatus.Cancelled) { _rebind = null; }
            }
            Map.Update(input);
        }

        /// <summary>
        /// Begin a rebind of <paramref name="actionId"/> slot <paramref name="slot"/>. The next <see cref="Update"/>
        /// calls feed it. Returns the operation so the UI can read its status/captured source.
        /// </summary>
        public RebindOperation BeginRebind(
            string actionId, int slot,
            System.Collections.Generic.IEnumerable<Key>? excludeKeys = null,
            System.Collections.Generic.IEnumerable<GamepadButton>? excludeButtons = null)
        {
            _rebind = new RebindOperation(Map, actionId, slot, excludeKeys, excludeButtons);
            return _rebind;
        }

        /// <summary>Cancel an in-flight rebind without changing the binding.</summary>
        public void CancelRebind() => _rebind = null;

        /// <summary>Serialize the current bindings and push them through the save sink (if any). Returns the JSON.</summary>
        public string Save()
        {
            string json = ActionMapSerializer.Serialize(Map);
            _save?.Invoke(json);
            return json;
        }

        // ---- pass-through readers (so games can hold just the controller) ---

        /// <summary>See <see cref="ActionMap.IsDown"/>.</summary>
        public bool IsDown(string actionId) => Map.IsDown(actionId);
        /// <summary>See <see cref="ActionMap.WasPressed"/>.</summary>
        public bool WasPressed(string actionId) => Map.WasPressed(actionId);
        /// <summary>See <see cref="ActionMap.WasReleased"/>.</summary>
        public bool WasReleased(string actionId) => Map.WasReleased(actionId);
        /// <summary>See <see cref="ActionMap.GetAxis"/>.</summary>
        public float GetAxis(string actionId) => Map.GetAxis(actionId);
        /// <summary>See <see cref="ActionMap.GetAxis2D"/>.</summary>
        public System.Numerics.Vector2 GetAxis2D(string actionId) => Map.GetAxis2D(actionId);
    }
}
