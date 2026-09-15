namespace KhaozEngine.Catalog;

/// <summary>
/// A derived table one content type builds ONCE at load, registered through
/// <see cref="ContentTypeRegistry.RegisterContentType"/> so a type whose gameplay reads want an index gets
/// one without the host wiring a second pass.
/// <para>
/// The rules that make it safe: it runs at boot AFTER the engine's own indexes and BEFORE the validator, in
/// TYPE ID ORDER so an index over engine rows is built before one over a later band's, it MAY read another
/// type's rows through the snapshot and MAY NOT read another index, and it THROWS to fail the boot closed
/// rather than returning a partial index. Every index is built EAGERLY, because each is walked inside
/// gameplay and a lazy build inside a tick is a latency spike.
/// </para>
/// </summary>
public interface IContentLoadIndex
{
    /// <summary>The content type this index is over, which is the type it was registered against.</summary>
    ContentTypeId Type { get; }

    /// <summary>Builds the index from the loaded snapshot. Throws to fail the boot closed.</summary>
    void Build(IContentSnapshot snapshot);
}
