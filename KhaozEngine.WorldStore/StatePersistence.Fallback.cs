using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

public sealed partial class StatePersistence<TState>
{
    private async Task<(byte[]? Data, bool FromFallback)> LoadRecordAsync(PersistenceLoadRequest request)
    {
        byte[]? data = await store.LoadAsync(request.StoreKey).ConfigureAwait(false);
        if (data is not null || config.LoadFallback is not { } fallback) return (data, false);
        return (await fallback(request).ConfigureAwait(false), true);
    }
}
