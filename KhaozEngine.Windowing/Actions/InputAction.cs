using System;

namespace KhaozEngine.Windowing.Actions
{
    /// <summary>How an action is read: as a button (down/pressed/released) or as an analog axis.</summary>
    public enum InputActionKind
    {
        /// <summary>A digital action read via IsDown / WasPressed / WasReleased (e.g. "Jump", "Fire").</summary>
        Button = 0,
        /// <summary>A 1D analog action read via GetAxis (e.g. "Throttle", "Turn"). Range roughly -1..1.</summary>
        Axis1D = 1,
        /// <summary>A 2D analog action read via GetAxis2D (e.g. "Move", "Look"). Each component roughly -1..1.</summary>
        Axis2D = 2,
    }

    /// <summary>
    /// The identity of a named action: a stable string <see cref="Id"/> plus its <see cref="Kind"/>. The id is an
    /// opaque engine IDENTIFIER (greppable, stable across releases, used as the persistence key), NOT a player-facing
    /// display string. Games map an id to a localized label via their own StringId catalog; the engine never localizes
    /// here. Prefer namespaced ids like <c>"gameplay.jump"</c> or <c>"move"</c>.
    /// </summary>
    public readonly struct InputAction : IEquatable<InputAction>
    {
        /// <summary>The opaque, stable identifier (persistence key). Never a display string.</summary>
        public string Id { get; }
        /// <summary>How this action is evaluated and read.</summary>
        public InputActionKind Kind { get; }

        public InputAction(string id, InputActionKind kind)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Action id must be non-empty.", nameof(id));
            Id = id; Kind = kind;
        }

        /// <summary>A Button action.</summary>
        public static InputAction Button(string id) => new(id, InputActionKind.Button);
        /// <summary>A 1D-axis action.</summary>
        public static InputAction Axis1D(string id) => new(id, InputActionKind.Axis1D);
        /// <summary>A 2D-axis action.</summary>
        public static InputAction Axis2D(string id) => new(id, InputActionKind.Axis2D);

        public bool Equals(InputAction other) => Id == other.Id && Kind == other.Kind;
        public override bool Equals(object? obj) => obj is InputAction a && Equals(a);
        public override int GetHashCode() => HashCode.Combine(Id, Kind);
        public override string ToString() => $"{Id} ({Kind})";
    }

    /// <summary>
    /// A binding = one <see cref="InputSource"/> occupying a numbered slot on an action. An action holds a LIST of
    /// bindings (slot 0, 1, ...); multiple bindings mean "any of these", combined per the action kind (see
    /// <see cref="ActionMap"/> for OR / sum-clamp semantics). A slot is the stable address rebinding targets.
    /// </summary>
    public readonly struct InputBinding : IEquatable<InputBinding>
    {
        /// <summary>The raw source this binding reads.</summary>
        public InputSource Source { get; }

        public InputBinding(InputSource source) { Source = source; }

        /// <summary>Wrap a source in a binding.</summary>
        public static InputBinding Of(InputSource source) => new(source);

        public bool Equals(InputBinding other) => Source.Equals(other.Source);
        public override bool Equals(object? obj) => obj is InputBinding b && Equals(b);
        public override int GetHashCode() => Source.GetHashCode();
    }
}
