using KhaozEngine.ItemInstances;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The durable half of <see cref="InstanceIdAllocator"/>, kept in memory, which is all a measurement run
/// needs: the process never crashes and never restores, so the reserved block and the epoch are whatever
/// the allocator last wrote. A server holds this in its world store instead.
/// </summary>
internal sealed class BenchmarkInstanceIdStore : IInstanceIdStore
{
    InstanceIdState _state;

    /// <inheritdoc />
    public InstanceIdState Read() => _state;

    /// <inheritdoc />
    public void Persist(in InstanceIdState state) => _state = state;
}
