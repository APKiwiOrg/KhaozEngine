using System.Collections.Generic;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The THREE placeholder presentations of spec 12.3, as localization keys. All three are player facing, so
/// none of them is a literal anywhere, which is the engine's founding localization rule.
/// <para>
/// They are prefixed <c>khaoz.</c> deliberately: they are ENGINE strings rather than content rows, so
/// contracts 12.1's derived <c>&lt;type key&gt;.&lt;content key&gt;.&lt;field&gt;</c> grammar does not name
/// them and the prefix keeps them out of its space. The engine ships the KEYS and no translation, exactly
/// as it ships no mod row.
/// </para>
/// <para>
/// <b>They resolve through the catalog's layered string catalog, which is a later milestone.</b> That
/// catalog reads the pack's text chunks first, then the game's own resx, then the key itself, and formats
/// through the <c>SafeFormat</c> path contracts 12.3 adopts so a translator's malformed template falls back
/// to the unformatted template rather than throwing inside the frame loop. Nothing here resolves anything:
/// what ships is the three keys, and the resolution arrives with the catalog that owns it.
/// </para>
/// <para>
/// A reason code and a stamped version are NOT player text and are never formatted into these strings. They
/// belong in the one log line of <see cref="InstanceValidationTelemetry"/>.
/// </para>
/// </summary>
public static class InstanceValidationStrings
{
    /// <summary>
    /// Shown for an entry that failed a check and carries a <see cref="QuarantineWrapper"/>. The item is
    /// unusable, untradeable and undroppable, and it can still be moved between slots.
    /// </summary>
    public const string Quarantined = "khaoz.item.quarantined";

    /// <summary>
    /// Shown for check 13's Retired finding, the contracts 8.2 kind 2 policy <c>0x01</c> placeholder. It is
    /// deliberately the same presentation quarantine gets, so a player sees one consistent thing.
    /// </summary>
    public const string Retired = "khaoz.item.retired";

    /// <summary>
    /// Shown in place of a gated kind the viewer may not see yet, spec 12.7's mechanic. Never a blank line,
    /// because an unidentified item that simply omitted its affixes would read as an item with none.
    /// </summary>
    public const string Unidentified = "khaoz.item.unidentified";

    /// <summary>All three keys, which is the whole of what this package contributes to a text catalog.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Quarantined, Retired, Unidentified };
}
