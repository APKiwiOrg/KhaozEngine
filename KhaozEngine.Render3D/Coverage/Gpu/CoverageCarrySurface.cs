using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>One final indexed surface and its submitted previous surface. Bases count uint words.
/// Previous can read a surface written by the preceding submission. The cross-submission Vulkan dependency,
/// also used by ocean ping-pong, requires the separately gated synchronization-validation proof.</summary>
internal readonly record struct CoverageSurfaceRange(IGpuBuffer Current, IGpuBuffer Previous, IGpuBuffer Indices,
    uint CurrentBase, uint PreviousBase, int VertexCount, int IndexCount);

/// <summary>One cold storage generation. History leases keep submitted ranges alive through replacement.</summary>
internal sealed class CoverageCarrySurface : IDisposable
{
    internal readonly IGpuBuffer Surface, Work, Results;
    internal bool IsDisposed => disposed;
    int historyRetains;
    bool disposeRequested, disposed;
    internal CoverageCarrySurface(IGpuDevice device, int slots, int actors, int source, int vertices, int triangles)
    {
        IGpuBuffer? surface = null, work = null, results = null;
        try
        {
            surface = Storage(checked((long)slots * vertices * 16));
            work = Storage(CoverageCarryLayout.WorkWords(actors, source, vertices, triangles));
            results = Storage(checked(actors * 8L));
            Surface = surface; Work = work; Results = results;
        }
        catch { results?.Dispose(); work?.Dispose(); surface?.Dispose(); throw; }
        IGpuBuffer Storage(long words) => device.Factory.CreateBuffer(new(CoverageCarryLayout.Bytes(Math.Max(1, words)),
            GpuBufferUsage.StructuredBufferReadWrite, 4));
    }
    internal void RetainHistory()
    { ObjectDisposedException.ThrowIf(disposed, this); historyRetains++; }
    internal void ReleaseHistory()
    { historyRetains--; TryDispose(); }
    public void Dispose() { disposeRequested = true; TryDispose(); }
    void TryDispose()
    {
        if (disposed || !disposeRequested || historyRetains != 0) return;
        disposed = true;
        List<Exception>? failures = null;
        Release(Results); Release(Work); Release(Surface);
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException(failures);

        void Release(IGpuBuffer buffer)
        {
            try { buffer.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
    }
}

/// <summary>A cold-created retain retired after the last migration copy or submitted history draw.</summary>
internal sealed class CoverageCarryHistoryLease(CoverageCarrySurface storage, GpuRetireQueue retired) : IDisposable
{
    bool active, queued;
    internal void Activate()
    {
        if (queued) throw new InvalidOperationException("A retired history lease cannot be reactivated.");
        if (active) return;
        storage.RetainHistory(); active = true;
    }
    internal void Retire()
    { if (active && !queued) { queued = true; retired.Retire(this); } }
    public void Dispose()
    { if (active) { active = false; storage.ReleaseHistory(); } }
}
