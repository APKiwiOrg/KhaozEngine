using KhaozEngine.Accounts;

namespace KhaozEngine.Tests.Accounts;

/// <summary>
/// The conformance suite over the reference store. It overrides nothing, which is the claim: every fact holds here
/// first, and a durable backend is conformant when its own subclass is as bare as this one (a gated leg adds only
/// the skipping attribute).
/// </summary>
public sealed class InMemoryAccountStoreConformanceTests : AccountStoreConformance
{
    /// <inheritdoc />
    protected override IAccountStore NewStore(bool whitelistOnCreate) => new InMemoryAccountStore(whitelistOnCreate);
}
