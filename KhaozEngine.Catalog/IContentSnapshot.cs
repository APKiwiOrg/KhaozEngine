namespace KhaozEngine.Catalog;

/// <summary>
/// The narrow READ side every consumer outside this package is written against, so a game and the item
/// instances layer compile against an INTERFACE rather than against whichever concrete holder is live. Both
/// <c>ContentSnapshot</c> (a candidate, or a version loaded but not yet active) and <c>ContentRuntime</c>
/// (the active one) implement it, which is what lets the validator, a test and the running server all be
/// handed the same shape.
/// <para>
/// It is DECLARED here, with the registry and the seams that name it, and gains its seven members with
/// <c>ContentSnapshot</c>. Seven and no more: it carries no authoring concept, no chunk, no hash beyond the
/// version identity pair and no mutation, so a client holds one with the pure read graph of spec 2.1.
/// </para>
/// </summary>
public interface IContentSnapshot
{
}
