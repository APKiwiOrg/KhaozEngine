using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// An internal opt-in write for bytes the caller has just verified with
/// <see cref="ContentPackReader.TryVerify"/> under the same hash. The bytes must remain unchanged
/// until the write completes. Public writes still use <see cref="IPackStore.PutAsync"/>.
/// </summary>
internal interface IVerifiedPackStoreWrite
{
    /// <summary>Writes verified bytes with the store's usual path and placement checks.</summary>
    Task PutVerifiedAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}
