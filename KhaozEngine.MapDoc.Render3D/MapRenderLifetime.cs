using System.Threading;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Allocation and release observations, including pending work and renderer-held retirement.</summary>
public sealed class MapRenderLifetime
{
    long _created, _disposed, _live, _cpu, _gpu, _queued, _upload, _workers;
    public long Created => Interlocked.Read(ref _created);
    public long Disposed => Interlocked.Read(ref _disposed);
    public long LiveHandles => Interlocked.Read(ref _live);
    public long CpuBytes => Interlocked.Read(ref _cpu);
    public long GpuBytes => Interlocked.Read(ref _gpu);
    public long QueuedBytes => Interlocked.Read(ref _queued);
    public long UploadBytes => Interlocked.Read(ref _upload);
    public long WorkerCount => Interlocked.Read(ref _workers);
    internal void Allocated() { Interlocked.Increment(ref _created); Interlocked.Increment(ref _live); }
    internal void Released() { Interlocked.Increment(ref _disposed); Interlocked.Decrement(ref _live); }
    internal void Cpu(long delta) => Interlocked.Add(ref _cpu, delta);
    internal void Gpu(long delta) => Interlocked.Add(ref _gpu, delta);
    internal void Queued(long delta) => Interlocked.Add(ref _queued, delta);
    internal void Uploaded(long delta) => Interlocked.Add(ref _upload, delta);
    internal void Worker(long delta) => Interlocked.Add(ref _workers, delta);
}
