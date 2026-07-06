using KhaozEngine.Commerce;

namespace KhaozEngine.Tests.Commerce;

public sealed class InMemoryWalletStoreTests : WalletStoreContract
{
    protected override IWalletStore NewStore() => new InMemoryWalletStore();
}
