namespace KhaozEngine.ItemInstances;

/// <summary>
/// The fourteen primitives of spec 10.2, CLOSED. The engine owns this code and a currency composes it in
/// data, so a game that needs something outside this list registers an <c>ICraftOperation</c> at 1,024 or
/// above rather than asking for a fifteenth number here.
/// <para>
/// <b>The numbers ARE the authored <c>currency_step.operation</c> values</b>, 1 to 14, which
/// <see cref="CurrencyStepContentType.MinPrimitiveOperation"/> and
/// <see cref="CurrencyStepContentType.MaxPrimitiveOperation"/> already bound on both sides of the row
/// codec. They are durable the moment one currency row names one, so none of them moves.
/// </para>
/// <para>
/// <b>Each member has exactly one static apply method on <c>CraftPrimitives</c></b>, split across three
/// files by SUBJECT: affixes, sockets and scalars. That split is chosen so a file grows only when its own
/// subject gains a primitive, which is the size ratchet's growth test rather than a line count.
/// </para>
/// </summary>
public enum CraftPrimitive
{
    /// <summary>One pick through spec 9.4 steps 6 to 8 against the item's OWN base and item level.</summary>
    AddRandomMod = 1,

    /// <summary>Removes the selected affixes from kind 131.</summary>
    RemoveMod = 2,

    /// <summary>A fresh roll position for every selected affix, same mods and same tiers.</summary>
    RerollValues = 3,

    /// <summary>Discards the affixes of the masked kinds and re-runs spec 9.4 steps 4 to 9 for them.</summary>
    RerollMods = 4,

    /// <summary>
    /// Writes kind 130, then trims or fills the affix list to the new rule's counts. The only primitive
    /// that can both add and remove affixes.
    /// </summary>
    SetRarity = 5,

    /// <summary>Appends one empty socket to kind 132.</summary>
    AddSocket = 6,

    /// <summary>Moves an item INTO a socket, KEEPING its instance id.</summary>
    Socket = 7,

    /// <summary>Moves it back out, keeping its instance id.</summary>
    Unsocket = 8,

    /// <summary>Writes one entry into kind 133.</summary>
    ApplyEnchant = 9,

    /// <summary>Removes the selected entries from kind 133.</summary>
    RemoveEnchant = 10,

    /// <summary>Raises kind 5's current toward its maximum.</summary>
    Repair = 11,

    /// <summary>Writes kind 3, as a delta or as an absolute.</summary>
    SetQuality = 12,

    /// <summary>
    /// Sets kind 128's state to 1 and its revealed mask to the registered gated bits. The only primitive
    /// with no content parameters at all, and it is in the list rather than being a game concern because
    /// the unidentified mechanic is engine machinery (spec 12.7).
    /// </summary>
    Identify = 13,

    /// <summary>Writes one bit of kind 1, and refuses a bit above 2 in v1.</summary>
    SetFlag = 14,
}
