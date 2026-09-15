namespace KhaozEngine.Catalog;

/// <summary>
/// A derived table one content type builds ONCE at load, registered through
/// <see cref="ContentTypeRegistry.RegisterContentType"/> so a type whose gameplay reads want an index gets
/// one without the host wiring a second pass.
/// <para>
/// The rules that make it safe, and where each one is enforced.
/// </para>
/// <list type="bullet">
/// <item><description>It runs at boot step 7b, AFTER the engine's own four and BEFORE the validator.
/// <see cref="ContentRuntime.FromSnapshot"/> builds the four, so an index cannot run before them, and
/// <see cref="ContentRuntime.BuildLoadIndexes"/> is the separate step the boot sequences.</description></item>
/// <item><description>In TYPE ID ORDER, so an index over engine rows is built before one over a later
/// band's and the order never depends on what a host registered first.</description></item>
/// <item><description>It MAY read another type's ROWS through the snapshot it is handed, and it MAY NOT
/// read another index: <see cref="ContentRuntime.TryGetLoadIndex{T}"/> throws for the whole of step 7b, so
/// the rule is a refusal rather than a comment.</description></item>
/// <item><description>It THROWS to fail the boot closed rather than returning a partial index, which comes
/// back as a <see cref="ContentLoadIndexException"/> naming the type, with the original failure
/// kept.</description></item>
/// </list>
/// <para>
/// Every index is built EAGERLY, because each is walked inside gameplay and a lazy build inside a tick is a
/// latency spike. The built index is handed back through
/// <see cref="ContentRuntime.TryGetLoadIndex{T}"/> as the type the registering code owns.
/// </para>
/// </summary>
public interface IContentLoadIndex
{
    /// <summary>The content type this index is over, which is the type it was registered against.</summary>
    ContentTypeId Type { get; }

    /// <summary>Builds the index from the loaded snapshot. Throws to fail the boot closed.</summary>
    void Build(IContentSnapshot snapshot);
}
