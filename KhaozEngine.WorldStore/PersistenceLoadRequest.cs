using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>Immutable identities captured on the host thread for one authenticated session's load-on-join.</summary>
/// <param name="Slot">The original connection slot, which may have been recycled before the load completes.</param>
/// <param name="AuthenticatedAccountId">The verified subject used for authentication and live-session policy.</param>
/// <param name="PersistenceKey">The bound durable key, without its store prefix.</param>
/// <param name="StoreKey">The complete primary store key, including the configured prefix.</param>
public readonly record struct PersistenceLoadRequest(
    int Slot,
    string AuthenticatedAccountId,
    string PersistenceKey,
    string StoreKey);

/// <summary>Loads or converts legacy bytes only when the primary record is missing. Null means no fallback record.
/// May continue off the host thread. Use the captured identities rather than reading the live slot. Returned bytes
/// use the primary record format and pass through normal guarded validation and application. An accepted fallback
/// remains dirty until saved to the primary key. Exceptions retain the load guard and surface through OnStoreError.
/// This hook must not apply live state or retire legacy data.</summary>
public delegate Task<byte[]?> PersistenceFallbackLoad(PersistenceLoadRequest request);
