using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Async, keyed durable store for authoritative server state (accounts, characters, zones), shaped for a
/// database backend rather than the per-user local JSON files of <c>KhaozEngine.Persistence</c>. The game
/// serializes a record to bytes by its own means and persists it under a stable key. Implementations:
/// <see cref="InMemoryWorldStore"/> (this dependency-free core, for tests/dev); durable backends are the opt-in
/// sibling packages KhaozEngine.WorldStore.Sqlite (dev/test + single-node) and KhaozEngine.WorldStore.SqlServer
/// (production / Azure SQL).
/// </summary>
public interface IWorldStore
{
    /// <summary>Loads the record for <paramref name="key"/>, or null if absent.</summary>
    Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserting or overwriting) the record for <paramref name="key"/>.</summary>
    Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>Deletes the record for <paramref name="key"/>; returns true if one was present.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>True if a record exists for <paramref name="key"/>.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
