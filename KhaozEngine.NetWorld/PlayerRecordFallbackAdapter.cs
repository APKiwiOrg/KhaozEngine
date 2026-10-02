using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

internal static class PlayerRecordFallbackAdapter
{
    internal static PersistenceFallbackLoad? Bind(PlayerRecordFallbackLoad? fallback)
    {
        if (fallback is null) return null;
        return async request =>
        {
            PlayerRecord? record = await fallback(request).ConfigureAwait(false);
            return record?.Encode();
        };
    }
}
