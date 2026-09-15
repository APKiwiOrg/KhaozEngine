using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The band a registering caller CLAIMS, and the mechanism behind spec 3.3's MAY NOT. Prose was not a
/// mechanism: without this, a game registering kind 300 would have succeeded and collided with the Scope B
/// range at the next engine release, silently, with two codecs and stored payloads under both.
/// <para>
/// A band is NOT a capability. It does not let an <see cref="Engine"/> caller do anything a
/// <see cref="Game"/> caller cannot, it only says which kind ids the caller is entitled to.
/// <c>ReplicationRegistry.FirstExtensionTypeId</c> is the engine's precedent one level up.
/// </para>
/// </summary>
public enum InstanceKindBand : byte
{
    /// <summary>Kinds <c>1</c> to <c>127</c>, the engine's generic per-instance facts.</summary>
    Engine = 1,

    /// <summary>Kinds <c>128</c> to <c>1023</c>, the item-instances fields.</summary>
    ScopeB = 2,

    /// <summary>Kinds <c>1024</c> to <c>65535</c>, the game's own.</summary>
    Game = 3,
}

/// <summary>
/// How far a property field travels, least visible first.
/// <para>
/// The ORDERING is load bearing rather than cosmetic: contracts 11.2's replication comparison is a
/// <c>&lt;=</c> on this enum, so a viewer cleared to <see cref="OwnerOnly"/> sees everything at or below
/// it. Renumbering these members changes who sees what.
/// </para>
/// <para>
/// The levels are deliberately NOT monotonic in the kind id (kinds 4, 5 and 6 are
/// <see cref="OwnerOnly"/> while 7 and 8 are <see cref="Everyone"/>), which is why spec 7.4 declines the
/// optional optimisation of making an owner projection a prefix truncation.
/// </para>
/// </summary>
public enum PropertyVisibility : byte
{
    /// <summary>The server alone ever holds these bytes. No client projection carries them.</summary>
    ServerOnly = 0,

    /// <summary>The owning player sees it, and nobody else does.</summary>
    OwnerOnly = 1,

    /// <summary>Every viewer sees it, which is the public view of an item on the ground or on another player.</summary>
    Everyone = 2,
}

/// <summary>One slot of a field's bytes, as the shape walker reads it.</summary>
public enum InstanceSlotKind : byte
{
    /// <summary>An unsigned minimal LEB128 varint, which is every content id, count and length here.</summary>
    Varint = 1,

    /// <summary>One raw byte.</summary>
    Byte = 2,

    /// <summary>Two raw bytes, little endian, which is an affix roll position.</summary>
    Fixed2 = 3,

    /// <summary>A length-prefixed nested payload, which only a socket entry carries and only one level deep.</summary>
    NestedPayload = 4,
}

/// <summary>How a field's repeat count is written, or that it does not repeat at all.</summary>
public enum InstanceCountWidth : byte
{
    /// <summary>The field does not repeat, so its <see cref="InstanceFieldShape.Entry"/> is empty.</summary>
    None = 0,

    /// <summary>One raw byte, which caps the repeat at 255 and costs one byte fewer than a varint.</summary>
    Byte = 1,

    /// <summary>An unsigned minimal LEB128 varint.</summary>
    Varint = 2,
}

/// <summary>Whether a reference sits in a field's header or once per repeat.</summary>
public enum InstanceReferenceSite : byte
{
    /// <summary>In the slots before the repeat count, so it occurs at most once per field.</summary>
    Header = 1,

    /// <summary>In one repeat's slots, so it occurs once per entry.</summary>
    Entry = 2,
}

/// <summary>
/// WHERE a property kind's values sit in its field bytes: the slots before the repeat count, how that count
/// is written, and one repeat's slots.
/// </summary>
/// <param name="Header">The slots before the repeat count.</param>
/// <param name="Count">How the repeat count is written, <see cref="InstanceCountWidth.None"/> for a field
/// that does not repeat.</param>
/// <param name="Entry">One repeat's slots, empty when <paramref name="Count"/> is
/// <see cref="InstanceCountWidth.None"/>.</param>
/// <remarks>
/// A shape carries no semantics at all, which is the point: a walker holding a shape and its targets finds,
/// reads and rewrites every content id in a payload without knowing what any kind MEANS. That is what gives
/// a game kind at or above 1024 remap, drift detection and quarantine for free, and what stops a kind being
/// remapped but not validated.
/// </remarks>
public readonly record struct InstanceFieldShape(
    ReadOnlyMemory<InstanceSlotKind> Header,
    InstanceCountWidth Count,
    ReadOnlyMemory<InstanceSlotKind> Entry);

/// <summary>
/// WHICH content type one slot of a shape holds an id for. <c>ContentFieldSchema</c>'s reference target is
/// the same idea one level up, in contracts 4.7.
/// </summary>
/// <param name="ContentTypeKey">The content type key the id belongs to, ordinally, as the catalog registers
/// it.</param>
/// <param name="Site">Whether the slot is in the header or in one repeat.</param>
/// <param name="SlotIndex">Which slot of that shape holds the id.</param>
/// <remarks>
/// The catalog's remap rules carry a type id and a from-id and <c>RemapRuleSet.Apply</c> cannot reference
/// this package, so it cannot know that kind 131's entries begin with a mod id. This is what says so.
/// </remarks>
public readonly record struct InstanceReferenceTarget(
    string ContentTypeKey,
    InstanceReferenceSite Site,
    int SlotIndex);
