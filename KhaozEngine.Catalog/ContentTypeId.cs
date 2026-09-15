namespace KhaozEngine.Catalog;

/// <summary>
/// A content type's stable numeric id, a <c>ushort</c> with the three reserved ranges of contracts 4.3.
/// <c>0</c> is reserved and is never a valid type id.
/// <para>
/// The three-way split exists so an engine release adding a type can never collide with a game's, which is
/// the property <c>ReplicationRegistry.FirstExtensionTypeId</c> already gives components. The predicates
/// here READ the split and <see cref="ContentRegistrationBand"/> is what a caller CLAIMS, so registration
/// compares the two rather than trusting either alone.
/// </para>
/// </summary>
/// <param name="Value">The raw type id, <c>1</c> to <c>65535</c>.</param>
public readonly record struct ContentTypeId(ushort Value)
{
    /// <summary>The engine's own range, <c>1</c> to <c>255</c>.</summary>
    public bool IsEngine => Value >= 1 && Value <= 255;

    /// <summary>The item-instances range, <c>256</c> to <c>1023</c>.</summary>
    public bool IsInstances => Value >= 256 && Value <= 1023;

    /// <summary>The game range, <c>1024</c> and above.</summary>
    public bool IsGame => Value >= 1024;
}
