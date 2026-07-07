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
    /// int. A source whose <c>kind</c> is unknown to this build, or whose payload fails to parse (bad enum name, etc.),
    /// is DROPPED (not thrown): if that leaves an action with zero valid bindings its persisted override is skipped
    /// entirely so the action keeps its code defaults, rather than ending up unbound. A malformed top-level document
    /// returns an empty result from <see cref="Deserialize"/> so <see cref="Apply"/> is a no-op (defaults stand).</para>
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
        /// each entry whose action is declared, valid bindings replace the action's bindings; an entry with no valid
        /// bindings, or for an unknown action, is skipped (defaults stand). Returns the count of actions overridden.
        /// </summary>
        public static int Apply(ActionMap map, BindingsDocument document)
        {
            if (map is null) throw new ArgumentNullException(nameof(map));
            if (document?.Actions is null) return 0;
            int applied = 0;
            foreach (var entry in document.Actions)
            {
                if (entry?.Action is null || !map.HasAction(entry.Action)) continue;
                var valid = new List<InputSource>();
                if (entry.Bindings is not null)
                    foreach (var dto in entry.Bindings)
                    {
                        var src = dto.ToSource();
                        if (src.Kind != InputSourceKind.None) valid.Add(src);
                    }
                if (valid.Count == 0) continue; // keep code defaults rather than unbinding
                map.ClearBindings(entry.Action);
                foreach (var s in valid) map.Bind(entry.Action, s);
                applied++;
            }
            return applied;
        }

        /// <summary>Convenience: parse and apply in one call. Returns the count of actions overridden.</summary>
        public static int Load(ActionMap map, string? json) => Apply(map, Deserialize(json));

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
        /// one from a build that knew fewer fields, still parses; unset fields fall back to the source defaults. An
        /// unknown <see cref="Kind"/> string deserializes to <see cref="InputSourceKind.None"/> and is dropped.
        /// </summary>
        public sealed class SourceDto
        {
            public InputSourceKind Kind { get; set; }
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
                Kind = s.Kind, Key = s.Key, Key2 = s.Key2, Key3 = s.Key3, Key4 = s.Key4,
                MouseButton = s.MouseButton, GamepadButton = s.GamepadButton,
                Stick = s.Stick, Component = s.StickComponent, Trigger = s.TriggerSide,
                Scale = s.Scale, Invert = s.Invert, ButtonThreshold = s.ButtonThreshold,
            };

            /// <summary>Rebuild the runtime source. Unknown kinds map to <see cref="InputSource.None"/> (dropped by callers).</summary>
            public InputSource ToSource()
            {
                switch (Kind)
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
