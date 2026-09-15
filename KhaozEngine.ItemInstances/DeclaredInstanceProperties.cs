using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// What a DEFINITION declares about the items made from it, which is one half of the which-items-get-an-id
/// rule (contracts 6.2, spec 3.6). The other half is the encoded payload length, and
/// <see cref="InstanceIdAllocator.NeedsInstanceId"/> is where the two meet.
/// </summary>
/// <remarks>
/// A flag here is a statement about the DEFINITION, never about a stored item. The rule is deliberately
/// asked of the item a caller is holding, so a definition gaining durability later does not retroactively
/// give every copy already in a bank an id it does not have.
/// </remarks>
[Flags]
public enum DeclaredInstanceProperties
{
    /// <summary>The definition declares nothing per instance. An item made from it is a plain stack.</summary>
    None = 0,

    /// <summary>The definition declares durability, property kind 5.</summary>
    Durability = 1,

    /// <summary>The definition declares sockets, property kind 132.</summary>
    Sockets = 2,

    /// <summary>The definition declares any other per-instance field: quality, charges, a bound-to
    /// subject, an affix budget, anything a copy can differ on.</summary>
    PerInstanceField = 4,
}
