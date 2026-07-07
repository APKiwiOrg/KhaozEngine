using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Windowing.Actions
{
    /// <summary>
    /// Versioned, forward-compatible JSON serialization for an <see cref="ActionMap"/>'s bindings, as a plain
    /// string in / string out. It does NOT touch the settings storage: the engine avoids a Windowing -&gt;
    /// Persistence dependency edge, so a game takes the string this returns and hands it to its own
    /// <c>ISettingsStorage</c> (or any store). See the USING doc for the round-trip.
    ///
    /// <para><b>What round-trips.</b> Only per-action binding OVERRIDES are serialized, keyed by action id. Action
    /// declarations (ids + kinds) come from code, not the file, so a persisted file never invents actions. On
    /// <see cref="Apply"/>, each declared action that has an entry gets its bindings replaced; actions with no entry
    /// keep their code defaults. This is what makes the format resilient: renaming/removing an action in code simply
    /// ignores its stale persisted entry.</para>
    ///
    /// <para><b>Forward compatibility + degradation.</b> The envelope carries a <see cref="BindingsDocument.Version"/>
    /// int. Degradation is PER BINDING, not per file: a source whose <c>kind</c> is a string this build does not know
    /// (a future kind) reads as <see cref="InputSourceKind.None"/> and is DROPPED, while every OTHER binding in the same
    /// document survives. If dropping leaves an action with zero valid bindings, that action's persisted override is
    /// skipped so it keeps its code defaults rather than ending up unbound; sibling actions are unaffected. A malformed
    /// TOP-LEVEL document (bad JSON syntax) is the only whole-file failure: <see cref="Deserialize"/> returns an empty
    /// document so <see cref="Apply"/> is a no-op (all defaults stand).</para>
    ///
    /// <para><b>Version guard.</b> A document whose <see cref="BindingsDocument.Version"/> is GREATER than
    /// <see cref="CurrentVersion"/> (written by a newer build) is still applied, but only through the tolerant
    /// per-field / per-binding path above: unknown kinds and unparseable fields drop individually, known ones load.
    /// <see cref="Apply"/> reports this via <see cref="ApplyResult.FromFutureVersion"/> so a caller can warn the player
    /// that some newer bindings may have been dropped. An equal-or-older version applies identically (the tolerant path
    /// is always on); the flag simply records that the file came from ahead of this build.</para>
    /// </summary>
    public static class ActionMapSerializer
    {
        /// <summary>The current serialized schema version. Bump only on a breaking envelope change.</summary>
        public const int CurrentVersion = 1;

        static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Serialize an action map's current bindings to a versioned JSON string.</summary>
        public static string Serialize(ActionMap map)
        {
            if (map is null) throw new ArgumentNullException(nameof(map));
            var doc = new BindingsDocument { Version = CurrentVersion };
            foreach (var id in map.ActionIds)
            {
                var entry = new ActionEntry { Action = id };
                foreach (var b in map.BindingsOf(id))
                    entry.Bindings.Add(SourceDto.From(b.Source));
                doc.Actions.Add(entry);
            }
            return JsonSerializer.Serialize(doc, Options);
        }

        /// <summary>
        /// Parse a persisted JSON string into a document. Returns an empty document (version = current) if the input
        /// is null/empty or malformed, so callers never see an exception from bad on-disk data.
        /// </summary>
        public static BindingsDocument Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new BindingsDocument { Version = CurrentVersion };
            try
            {
                var doc = JsonSerializer.Deserialize<BindingsDocument>(json, Options);
                return doc ?? new BindingsDocument { Version = CurrentVersion };
            }
            catch (JsonException)
            {
                return new BindingsDocument { Version = CurrentVersion };
            }
        }

        /// <summary>
        /// Apply a persisted document's overrides onto a map that already has its actions declared with defaults. For
        /// each entry whose action is declared, valid bindings replace the action's bindings; a binding whose kind is
        /// unknown to this build drops individually, and an entry left with no valid bindings (or for an unknown action)
        /// is skipped so defaults stand. When the document's <see cref="BindingsDocument.Version"/> is newer than this
        /// build it is still applied through this tolerant path and <see cref="ApplyResult.FromFutureVersion"/> is set.
        /// The returned <see cref="ApplyResult"/> converts implicitly to the overridden-action count.
        /// </summary>
        public static ApplyResult Apply(ActionMap map, BindingsDocument document)
        {
            if (map is null) throw new ArgumentNullException(nameof(map));
            if (document?.Actions is null) return default;
            bool fromFuture = document.Version > CurrentVersion;
            int applied = 0;
            int droppedBindings = 0;
            foreach (var entry in document.Actions)
            {
                if (entry?.Action is null || !map.HasAction(entry.Action)) continue;
                var valid = new List<InputSource>();
                if (entry.Bindings is not null)
                    foreach (var dto in entry.Bindings)
                    {
                        var src = dto.ToSource();
                        if (src.Kind != InputSourceKind.None) valid.Add(src);
                        else droppedBindings++; // unknown/unparseable kind: drop this one, keep the rest
                    }
                if (valid.Count == 0) continue; // keep code defaults rather than unbinding
                map.ClearBindings(entry.Action);
                foreach (var s in valid) map.Bind(entry.Action, s);
                applied++;
            }
            return new ApplyResult(applied, droppedBindings, fromFuture);
        }

        /// <summary>Convenience: parse and apply in one call. The result converts implicitly to the overridden count.</summary>
        public static ApplyResult Load(ActionMap map, string? json) => Apply(map, Deserialize(json));

        /// <summary>
        /// Outcome of an <see cref="Apply"/> / <see cref="Load"/>. Converts implicitly to <see cref="AppliedCount"/> so
        /// existing <c>int applied = Load(...)</c> callers keep working, while exposing the forward-compat facts.
        /// </summary>
        public readonly struct ApplyResult
        {
            internal ApplyResult(int appliedCount, int droppedBindings, bool fromFutureVersion)
            {
                AppliedCount = appliedCount;
                DroppedBindings = droppedBindings;
                FromFutureVersion = fromFutureVersion;
            }

            /// <summary>How many declared actions had their bindings overridden by the document.</summary>
            public int AppliedCount { get; }
            /// <summary>How many individual bindings were dropped because their kind was unknown/unparseable to this build.</summary>
            public int DroppedBindings { get; }
            /// <summary>True if the document's <c>version</c> was newer than <see cref="CurrentVersion"/> (some newer bindings may have dropped).</summary>
            public bool FromFutureVersion { get; }

            /// <summary>Implicit conversion to the overridden-action count, for callers that only want the number.</summary>
            public static implicit operator int(ApplyResult r) => r.AppliedCount;
        }

        // ---- DTOs (the on-disk shape; separate from the runtime structs) ----

        /// <summary>The serialized envelope: a version plus per-action binding overrides.</summary>
        public sealed class BindingsDocument
        {
            /// <summary>Schema version (forward-compat gate).</summary>
            public int Version { get; set; } = CurrentVersion;
            /// <summary>Per-action binding overrides.</summary>
            public List<ActionEntry> Actions { get; set; } = new();
        }

        /// <summary>One action's persisted binding list.</summary>
        public sealed class ActionEntry
        {
            /// <summary>The action id (opaque identifier, never a display string).</summary>
            public string? Action { get; set; }
            /// <summary>The bindings, in slot order.</summary>
            public List<SourceDto> Bindings { get; set; } = new();
        }

        /// <summary>
        /// The serialized shape of one <see cref="InputSource"/>. Every field is optional so an older/newer file, or
        /// one from a build that knew fewer fields, still parses; unset fields fall back to the source defaults. The
        /// discriminator <see cref="Kind"/> is stored as a RAW STRING (not the enum) so an unrecognized future kind
        /// does NOT throw during deserialize (which would discard the whole file) - it fails to map in
        /// <see cref="ToSource"/> and only THAT binding is dropped. It still serializes as the enum name.
        /// </summary>
        public sealed class SourceDto
        {
            /// <summary>The source kind, as its enum NAME. An unknown name maps to <see cref="InputSourceKind.None"/> in <see cref="ToSource"/> and drops just this binding.</summary>
            public string? Kind { get; set; }
            public Key Key { get; set; }
            public Key Key2 { get; set; }
            public Key Key3 { get; set; }
            public Key Key4 { get; set; }
            public MouseButton MouseButton { get; set; }
            public GamepadButton GamepadButton { get; set; }
            public GamepadStick Stick { get; set; }
            public StickComponent Component { get; set; }
            public GamepadTriggerSide Trigger { get; set; }
            public float Scale { get; set; } = 1f;
            public bool Invert { get; set; }
            public float ButtonThreshold { get; set; } = InputSource.DefaultButtonThreshold;

            public static SourceDto From(InputSource s) => new()
            {
                Kind = s.Kind.ToString(), Key = s.Key, Key2 = s.Key2, Key3 = s.Key3, Key4 = s.Key4,
                MouseButton = s.MouseButton, GamepadButton = s.GamepadButton,
                Stick = s.Stick, Component = s.StickComponent, Trigger = s.TriggerSide,
                Scale = s.Scale, Invert = s.Invert, ButtonThreshold = s.ButtonThreshold,
            };

            /// <summary>
            /// Rebuild the runtime source. The raw <see cref="Kind"/> string is parsed tolerantly: an unrecognized name
            /// (a future kind) maps to <see cref="InputSource.None"/>, which callers drop - the rest of the document is
            /// unaffected.
            /// </summary>
            public InputSource ToSource()
            {
                // Tolerant parse: unknown name OR an out-of-range numeric string (a future kind) both drop to None.
                if (!Enum.TryParse<InputSourceKind>(Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                    return InputSource.None; // unknown/future kind: drop just this binding
                switch (kind)
                {
                    case InputSourceKind.Key: return InputSource.FromKey(Key);
                    case InputSourceKind.MouseButton: return InputSource.FromMouseButton(MouseButton);
                    case InputSourceKind.GamepadButton: return InputSource.FromGamepadButton(GamepadButton);
                    case InputSourceKind.GamepadStickAxis:
                        return InputSource.StickAxis(Stick, Component, Invert, Scale);
                    case InputSourceKind.GamepadTrigger:
                        return InputSource.Trigger(Trigger, Scale, ButtonThreshold);
                    case InputSourceKind.KeyAxis1D:
                        return InputSource.Axis1D(negative: Key2, positive: Key);
                    case InputSourceKind.KeyAxis2D:
                        return InputSource.Wasd(left: Key2, right: Key, down: Key4, up: Key3);
                    default:
                        return InputSource.None;
                }
            }
        }
    }
}
