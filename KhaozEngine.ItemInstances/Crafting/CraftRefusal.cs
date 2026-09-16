namespace KhaozEngine.ItemInstances;

/// <summary>
/// Why a craft step refused, as a CLOSED vocabulary. Spec 10.3 fixes the shape of a refusal for the guard
/// half, "a refusal names a guard rather than a message", and the same argument holds for everything else a
/// primitive can say no to: a message is a string a caller reads and a translator cannot, while a kind is a
/// value a counter can bucket, a client can localize and a test can assert on.
/// <para>
/// It is CLOSED on purpose and it is not the payload decoder's vocabulary. Contracts 9.7 fixes eight reason
/// tokens for BYTES that arrived from a peer or a page, and nothing here adds one: a craft refusal is about
/// an operation a caller asked for, so the two sets stay apart and
/// <see cref="CraftRefusalKind.PayloadMalformed"/> is the one place they meet, as the working copy's answer
/// to a target whose stored bytes do not decode at all.
/// </para>
/// </summary>
public enum CraftRefusalKind : byte
{
    /// <summary>No refusal, which is the value a <see cref="CraftRefusal"/> nobody filled in carries.</summary>
    None = 0,

    /// <summary>The target's stored bytes do not decode, so there is nothing to craft on.</summary>
    PayloadMalformed = 1,

    /// <summary>
    /// The write named a property kind this process has no registration for, spec 10.5's first power. The
    /// subject is the kind. An unregistered kind the DECODE preserved is untouched and kept verbatim, which
    /// is a different thing entirely and is contracts 9.4.
    /// </summary>
    PropertyKindUnregistered = 2,

    /// <summary>The write would take the payload past <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/>.</summary>
    PayloadTooLong = 3,

    /// <summary>The item carries no field of the kind the step needs. The subject is the kind.</summary>
    FieldAbsent = 4,

    /// <summary>The selected affix is not on the item. The subject is the index or the mod id asked for.</summary>
    AffixAbsent = 5,

    /// <summary>
    /// The affix list is at its ceiling, so the entry has nowhere to go. The subject is the kind. The
    /// ceiling is the item's own rarity rule's <c>max_affixes</c> when it names a rarity this version
    /// carries, and kind 131's byte count otherwise, because that count is a byte and a 256th entry has
    /// nowhere to go either way.
    /// </summary>
    AffixListFull = 6,

    /// <summary>The step named a content row this version has no live copy of. The subject is the row id.</summary>
    ContentRowMissing = 7,

    /// <summary>
    /// A <c>SetRarity</c> of 0 walks <c>upgrade_from</c> and found no answer or more than one. The subject
    /// is how many rules named the item's current rarity, which is the number that has to be exactly one.
    /// </summary>
    RarityUpgradeAmbiguous = 8,

    /// <summary>The socket index is past the end of kind 132. The subject is the index.</summary>
    SocketIndexOutOfRange = 9,

    /// <summary>The socket already holds an item. The subject is the index.</summary>
    SocketOccupied = 10,

    /// <summary>The socket holds nothing to take out. The subject is the index.</summary>
    SocketEmpty = 11,

    /// <summary>
    /// The socket type's <c>socket_tag_rule</c> rows refuse the contained item. The subject is the socket
    /// type id. Reject is checked FIRST and wins, and a socket type with no accept row accepts nothing.
    /// </summary>
    SocketTagRejected = 12,

    /// <summary>
    /// The contained item's payload is longer than the socket type's <c>max_nested_bytes</c>. The subject is
    /// that budget, resolved, so a 0 in content reads here as the whole payload cap.
    /// </summary>
    NestedPayloadTooLong = 13,

    /// <summary>
    /// The contained item's payload carries a field that itself nests, which contracts 9.5 makes a
    /// structural limit rather than a convention. The subject is the offending kind.
    /// </summary>
    NestedPayloadNests = 14,

    /// <summary>
    /// The flag bit is reserved in v1, which is every bit above 2. The subject is the bit.
    /// </summary>
    FlagBitReserved = 15,

    /// <summary>The value is outside the range its kind stores. The subject is the kind.</summary>
    ValueOutOfRange = 16,

    /// <summary>
    /// The draw found no live candidate, so the pick placed nothing. The subject is the mod kind asked for.
    /// The draws were still CONSUMED, because spec 9.3's draw count is a function of the pick count and not
    /// of whether a pool happened to be empty.
    /// </summary>
    DrawEmpty = 17,
}

/// <summary>
/// One refusal: a kind out of the closed vocabulary and the one number that names what it is about. There
/// is no message and there is no exception, because a refusal is an ordinary outcome of asking for
/// something the item cannot have rather than a bug in the asking.
/// </summary>
/// <param name="Kind">Why the step refused.</param>
/// <param name="Subject">What it was about, whose meaning is the <paramref name="Kind"/>'s: a property
/// kind, a socket index, a content row id, a flag bit, or 0 when the kind is the whole of it.</param>
public readonly record struct CraftRefusal(CraftRefusalKind Kind, int Subject)
{
    /// <summary>Whether this value names a refusal at all, which <see cref="CraftRefusalKind.None"/> does not.</summary>
    public bool IsRefusal => Kind != CraftRefusalKind.None;
}
