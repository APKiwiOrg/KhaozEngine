using System;
using System.Numerics;

namespace KhaozEngine.Windowing.Actions
{
    /// <summary>
    /// The kind of raw source a single <see cref="InputSource"/> reads from the immutable
    /// <see cref="InputState"/> snapshot. The set is deliberately open-for-extension: new kinds append at the
    /// END of the enum (never renumber) so serialized bindings stay forward/backward compatible. A persisted
    /// binding referring to a kind this build does not know is degraded to the default binding on load rather
    /// than throwing (see <see cref="ActionMapSerializer"/>).
    /// </summary>
    public enum InputSourceKind
    {
        /// <summary>Unset / unknown. A deserialized source that fails to parse lands here and is dropped.</summary>
        None = 0,

        /// <summary>A single keyboard <see cref="Key"/> (button-like: down / pressed / released edges).</summary>
        Key = 1,

        /// <summary>A single mouse button (button-like). Note: the snapshot carries no mouse "released" set, so
        /// the released edge is unavailable for mouse sources (see <see cref="InputSource.EvaluateReleased"/>).</summary>
        MouseButton = 2,

        /// <summary>A single gamepad <see cref="GamepadButton"/> on the map's player pad (button-like).</summary>
        GamepadButton = 3,

        /// <summary>One component (X or Y) of a gamepad analog stick on the player pad (axis-like, -1..1 after deadzone).</summary>
        GamepadStickAxis = 4,

        /// <summary>A gamepad trigger on the player pad (axis-like, 0..1). Also usable as a button via a threshold.</summary>
        GamepadTrigger = 5,

        /// <summary>Two keys forming a 1D axis: <c>Negative</c> key drives -1, <c>Positive</c> key drives +1 (composite).</summary>
        KeyAxis1D = 6,

        /// <summary>Four keys forming a 2D axis (WASD-style): a horizontal key pair and a vertical key pair (composite).</summary>
        KeyAxis2D = 7,
    }

    /// <summary>Which analog stick a <see cref="InputSourceKind.GamepadStickAxis"/> reads.</summary>
    public enum GamepadStick { Left = 0, Right = 1 }

    /// <summary>Which component of a stick a <see cref="InputSourceKind.GamepadStickAxis"/> reads.</summary>
    public enum StickComponent { X = 0, Y = 1 }

    /// <summary>Which gamepad trigger a <see cref="InputSourceKind.GamepadTrigger"/> reads.</summary>
    public enum GamepadTriggerSide { Left = 0, Right = 1 }

    /// <summary>
    /// One raw input source a binding reads from an <see cref="InputState"/> snapshot. This is a discriminated
    /// value: <see cref="Kind"/> selects which of the payload fields are meaningful. Construct via the static
    /// factories (<see cref="FromKey"/>, <see cref="FromGamepadButton"/>, <see cref="StickAxis"/>,
    /// <see cref="Trigger"/>, <see cref="Axis1D"/>, <see cref="Wasd"/>, ...) rather than the ctor so intent is clear.
    ///
    /// <para><b>Button vs axis.</b> A source evaluates BOTH as a button (down/pressed/released, via a threshold on
    /// its analog value) AND as an axis (a float, or a 2D vector for the WASD composite). Which one an action uses
    /// depends on the <see cref="InputActionKind"/> of the action it is bound to.</para>
    ///
    /// <para><b>Modifiers.</b> <see cref="Scale"/> multiplies the analog value (default 1) and <see cref="Invert"/>
    /// negates it, so a stick component can be flipped or a trigger softened without a new source kind. They apply to
    /// the analog reading only; button evaluation uses the post-modifier magnitude against <see cref="ButtonThreshold"/>.</para>
    ///
    /// <para><b>Localization.</b> Every field here is an engine IDENTIFIER (an enum or a key name), never a
    /// player-facing display string. Games turn a source into a localized label on their side.</para>
    /// </summary>
    public readonly struct InputSource : IEquatable<InputSource>
    {
        /// <summary>Analog magnitude at or above which a source counts as "down" when read as a button.</summary>
        public const float DefaultButtonThreshold = 0.5f;

        /// <summary>Which raw source this reads.</summary>
        public InputSourceKind Kind { get; }
        /// <summary>Primary key (Key / KeyAxis1D positive / KeyAxis2D right).</summary>
        public Key Key { get; }
        /// <summary>Secondary key (KeyAxis1D negative / KeyAxis2D left).</summary>
        public Key Key2 { get; }
        /// <summary>KeyAxis2D up key.</summary>
        public Key Key3 { get; }
        /// <summary>KeyAxis2D down key.</summary>
        public Key Key4 { get; }
        /// <summary>Mouse button (MouseButton kind).</summary>
        public MouseButton MouseButton { get; }
        /// <summary>Gamepad button (GamepadButton kind).</summary>
        public GamepadButton GamepadButton { get; }
        /// <summary>Which stick (GamepadStickAxis kind).</summary>
        public GamepadStick Stick { get; }
        /// <summary>Which stick component (GamepadStickAxis kind).</summary>
        public StickComponent StickComponent { get; }
        /// <summary>Which trigger (GamepadTrigger kind).</summary>
        public GamepadTriggerSide TriggerSide { get; }
        /// <summary>Analog scale applied to the reading (default 1).</summary>
        public float Scale { get; }
        /// <summary>If true, negate the analog reading.</summary>
        public bool Invert { get; }
        /// <summary>Analog magnitude threshold for button evaluation (default <see cref="DefaultButtonThreshold"/>).</summary>
        public float ButtonThreshold { get; }

        InputSource(
            InputSourceKind kind, Key key = Key.None, Key key2 = Key.None, Key key3 = Key.None, Key key4 = Key.None,
            MouseButton mouseButton = MouseButton.Left, GamepadButton gamepadButton = GamepadButton.A,
            GamepadStick stick = GamepadStick.Left, StickComponent component = StickComponent.X,
            GamepadTriggerSide trigger = GamepadTriggerSide.Left,
            float scale = 1f, bool invert = false, float buttonThreshold = DefaultButtonThreshold)
        {
            Kind = kind; Key = key; Key2 = key2; Key3 = key3; Key4 = key4;
            MouseButton = mouseButton; GamepadButton = gamepadButton;
            Stick = stick; StickComponent = component; TriggerSide = trigger;
            Scale = scale; Invert = invert; ButtonThreshold = buttonThreshold;
        }

        /// <summary>An empty / unset source (dropped when evaluated).</summary>
        public static InputSource None => new(InputSourceKind.None);

        /// <summary>A single keyboard key.</summary>
        public static InputSource FromKey(Key key) => new(InputSourceKind.Key, key: key);

        /// <summary>A single mouse button.</summary>
        public static InputSource FromMouseButton(MouseButton button) => new(InputSourceKind.MouseButton, mouseButton: button);

        /// <summary>A single gamepad button (on the map's player pad).</summary>
        public static InputSource FromGamepadButton(GamepadButton button) => new(InputSourceKind.GamepadButton, gamepadButton: button);

        /// <summary>One component of a gamepad stick, with optional invert/scale (e.g. invert Y for camera).</summary>
        public static InputSource StickAxis(GamepadStick stick, StickComponent component, bool invert = false, float scale = 1f) =>
            new(InputSourceKind.GamepadStickAxis, stick: stick, component: component, invert: invert, scale: scale);

        /// <summary>A gamepad trigger (0..1), usable as an axis or, past <paramref name="buttonThreshold"/>, a button.</summary>
        public static InputSource Trigger(GamepadTriggerSide trigger, float scale = 1f, float buttonThreshold = DefaultButtonThreshold) =>
            new(InputSourceKind.GamepadTrigger, trigger: trigger, scale: scale, buttonThreshold: buttonThreshold);

        /// <summary>A two-key 1D axis: <paramref name="negative"/> drives -1, <paramref name="positive"/> drives +1.</summary>
        public static InputSource Axis1D(Key negative, Key positive) =>
            new(InputSourceKind.KeyAxis1D, key: positive, key2: negative);

        /// <summary>A four-key 2D axis (WASD-style). +X = right, +Y = up (matches the stick convention).</summary>
        public static InputSource Wasd(Key left, Key right, Key down, Key up) =>
            new(InputSourceKind.KeyAxis2D, key: right, key2: left, key3: up, key4: down);

        /// <summary>The canonical WASD 2D source (A/D horizontal, W/S vertical).</summary>
        public static InputSource WasdDefault => Wasd(Key.A, Key.D, Key.S, Key.W);

        /// <summary>The canonical arrow-keys 2D source.</summary>
        public static InputSource ArrowsDefault => Wasd(Key.Left, Key.Right, Key.Down, Key.Up);

        static float Mod(float v, float scale, bool invert) => invert ? -v * scale : v * scale;

        /// <summary>
        /// Read this source as a 2D axis from a snapshot. Only <see cref="InputSourceKind.KeyAxis2D"/> and a stick
        /// (both components) produce a real 2D value; every other kind yields (value, 0) from its 1D reading.
        /// A KeyAxis2D returns a raw per-axis vector in [-1,1] on each axis; diagonal normalization is applied by the
        /// action layer, not here (so summing multiple bindings composes before clamping).
        /// </summary>
        public Vector2 EvaluateAxis2D(InputState input, int playerIndex)
        {
            switch (Kind)
            {
                case InputSourceKind.KeyAxis2D:
                {
                    float x = (input.IsDown(Key) ? 1f : 0f) - (input.IsDown(Key2) ? 1f : 0f);
                    float y = (input.IsDown(Key3) ? 1f : 0f) - (input.IsDown(Key4) ? 1f : 0f);
                    return new Vector2(Mod(x, Scale, Invert), Mod(y, Scale, Invert));
                }
                case InputSourceKind.GamepadStickAxis:
                {
                    Vector2 s = Stick == GamepadStick.Left
                        ? input.Gamepad(playerIndex).LeftStickDeadzoned()
                        : input.Gamepad(playerIndex).RightStickDeadzoned();
                    // A single stick-axis source is one component; both-component 2D comes from binding X and Y
                    // sources, or the action layer reading the whole stick. Here we return the whole deadzoned stick
                    // so an Axis2D action bound to one StickAxis source (component ignored) gets a real vector.
                    return new Vector2(Mod(s.X, Scale, Invert), Mod(s.Y, Scale, Invert));
                }
                default:
                    return new Vector2(EvaluateAxis1D(input, playerIndex), 0f);
            }
        }

        /// <summary>Read this source as a 1D axis (float) from a snapshot.</summary>
        public float EvaluateAxis1D(InputState input, int playerIndex)
        {
            switch (Kind)
            {
                case InputSourceKind.Key:
                    return Mod(input.IsDown(Key) ? 1f : 0f, Scale, Invert);
                case InputSourceKind.MouseButton:
                    return Mod(input.IsDown(MouseButton) ? 1f : 0f, Scale, Invert);
                case InputSourceKind.GamepadButton:
                    return Mod(input.Gamepad(playerIndex).IsDown(GamepadButton) ? 1f : 0f, Scale, Invert);
                case InputSourceKind.KeyAxis1D:
                {
                    float v = (input.IsDown(Key) ? 1f : 0f) - (input.IsDown(Key2) ? 1f : 0f);
                    return Mod(v, Scale, Invert);
                }
                case InputSourceKind.GamepadStickAxis:
                {
                    Vector2 s = Stick == GamepadStick.Left
                        ? input.Gamepad(playerIndex).LeftStickDeadzoned()
                        : input.Gamepad(playerIndex).RightStickDeadzoned();
                    float v = StickComponent == StickComponent.X ? s.X : s.Y;
                    return Mod(v, Scale, Invert);
                }
                case InputSourceKind.GamepadTrigger:
                {
                    float t = TriggerSide == GamepadTriggerSide.Left
                        ? input.Gamepad(playerIndex).LeftTrigger
                        : input.Gamepad(playerIndex).RightTrigger;
                    return Mod(t, Scale, Invert);
                }
                case InputSourceKind.KeyAxis2D:
                    return EvaluateAxis2D(input, playerIndex).X;
                default:
                    return 0f;
            }
        }

        /// <summary>True while this source is held (button reading). Composite/axis sources are "down" past the threshold.</summary>
        public bool EvaluateDown(InputState input, int playerIndex)
        {
            switch (Kind)
            {
                case InputSourceKind.Key: return input.IsDown(Key);
                case InputSourceKind.MouseButton: return input.IsDown(MouseButton);
                case InputSourceKind.GamepadButton: return input.Gamepad(playerIndex).IsDown(GamepadButton);
                case InputSourceKind.None: return false;
                default: return MathF.Abs(EvaluateAxis1D(input, playerIndex)) >= ButtonThreshold;
            }
        }

        /// <summary>True only on the frame this source went down (press edge), using the snapshot's own edge sets.</summary>
        public bool EvaluatePressed(InputState input, int playerIndex)
        {
            switch (Kind)
            {
                case InputSourceKind.Key: return input.WasPressed(Key);
                case InputSourceKind.MouseButton: return input.WasPressed(MouseButton);
                case InputSourceKind.GamepadButton: return input.Gamepad(playerIndex).WasPressed(GamepadButton);
                default: return false; // axis/composite press edges are derived by the action layer against the previous frame
            }
        }

        /// <summary>
        /// True only on the frame this source went up (release edge). Available for keys and gamepad buttons via the
        /// snapshot's released sets. Mouse sources have NO released edge (the snapshot carries no mouse-released set),
        /// so this always returns false for a mouse source; the action layer derives mouse/axis release edges itself.
        /// </summary>
        public bool EvaluateReleased(InputState input, int playerIndex)
        {
            switch (Kind)
            {
                case InputSourceKind.Key: return input.WasReleased(Key);
                case InputSourceKind.GamepadButton: return input.Gamepad(playerIndex).WasReleased(GamepadButton);
                default: return false;
            }
        }

        /// <summary>True if this is a composite / analog source (not a plain single button).</summary>
        public bool IsComposite =>
            Kind is InputSourceKind.KeyAxis1D or InputSourceKind.KeyAxis2D
                 or InputSourceKind.GamepadStickAxis or InputSourceKind.GamepadTrigger;

        public bool Equals(InputSource other) =>
            Kind == other.Kind && Key == other.Key && Key2 == other.Key2 && Key3 == other.Key3 && Key4 == other.Key4 &&
            MouseButton == other.MouseButton && GamepadButton == other.GamepadButton &&
            Stick == other.Stick && StickComponent == other.StickComponent && TriggerSide == other.TriggerSide &&
            Scale.Equals(other.Scale) && Invert == other.Invert && ButtonThreshold.Equals(other.ButtonThreshold);

        public override bool Equals(object? obj) => obj is InputSource s && Equals(s);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Kind); h.Add(Key); h.Add(Key2); h.Add(Key3); h.Add(Key4);
            h.Add(MouseButton); h.Add(GamepadButton); h.Add(Stick); h.Add(StickComponent); h.Add(TriggerSide);
            h.Add(Scale); h.Add(Invert); h.Add(ButtonThreshold);
            return h.ToHashCode();
        }

        public static bool operator ==(InputSource a, InputSource b) => a.Equals(b);
        public static bool operator !=(InputSource a, InputSource b) => !a.Equals(b);
    }
}
