using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// An <see cref="IWorldStore"/> whose WRITES park until the test releases them while its reads answer straight
/// through: the mirror image of <see cref="GatedWorldStore"/>, and the shape of a real remote backend
/// (WorldStore.SqlServer), where a write costs more than a read. That asymmetry is what makes the handover a kick
/// opens go wrong: the displaced session's save-on-leave and the newcomer's load-on-join are issued from ONE event
/// drain, the store orders neither against the other, and the faster read wins - so the newcomer is restored onto
/// the record from before the leave. Parking the writes explicitly drives that window with no timing assumption.
/// </summary>
internal sealed class SlowSaveWorldStore : IWorldStore
{
    private readonly IWorldStore inner;
    private readonly List<TaskCompletionSource> saveGates = new();
    private int completedSaves;

    public SlowSaveWorldStore(IWorldStore inner) => this.inner = inner;

    /// <summary>How many writes are currently parked, waiting for <see cref="ReleaseSaves"/>.</summary>
    public int PendingSaves { get { lock (saveGates) return saveGates.Count; } }

    /// <summary>How many writes have reached the inner store since the rig was built.</summary>
    public int CompletedSaves => Volatile.Read(ref completedSaves);

    /// <summary>Lets every currently-parked write through to the inner store.</summary>
    public void ReleaseSaves()
    {
        TaskCompletionSource[] gates;
        lock (saveGates) { gates = saveGates.ToArray(); saveGates.Clear(); }
        foreach (TaskCompletionSource g in gates) g.SetResult();
    }

    public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(key, cancellationToken);

    public async Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        await ParkAsync().ConfigureAwait(false);
        await inner.SaveAsync(key, data, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref completedSaves);
    }

    public async Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken cancellationToken = default)
    {
        await ParkAsync().ConfigureAwait(false);
        await inner.SaveManyAsync(items, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref completedSaves);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);

    private Task ParkAsync()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (saveGates) saveGates.Add(gate);
        return gate.Task;
    }
}
