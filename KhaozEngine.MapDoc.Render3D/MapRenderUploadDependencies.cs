using System;
using System.Linq;
using System.Threading.Tasks;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Optional upload ordering. Private publication satisfies dependencies without exposing pending draws.</summary>
public interface IMapRenderUploadDependencies
{
    bool CanBeginUpload(MapPreparedResource resource);
    bool IsUploadDependency(MapPreparedResource resource);
    void CompleteUploads();
}
public sealed partial class MapRenderResidency
{
    internal MapRenderReadiness InspectReadiness()
    {
        lock (_gate)
        {
            RequireLive();
            return SnapshotReadiness();
        }
    }
    MapRenderReadiness SnapshotReadiness()
    {
        int pending = _pending is null ? 0 : Math.Max(1, _pending.Resources.Count - _pending.Owners.Count);
        long cpu = Lifetime.CpuBytes, gpu = Lifetime.GpuBytes, queued = Lifetime.QueuedBytes;
        if (_lastReadiness is { } old && ReferenceEquals(old.Transition, _transition) && old.Pending == pending &&
            old.Ready == _owners.Count && old.CpuBytes == cpu && old.GpuBytes == gpu && old.QueuedBytes == queued) return old;
        return _lastReadiness = new(_transition, pending, _owners.Count, cpu, gpu, queued);
    }
    internal void CompletePreparation()
    {
        Task? completion;
        lock (_gate) completion = _pending?.Job?.Completion;
        if (completion is not null)
        {
            try { completion.GetAwaiter().GetResult(); }
            catch (Exception error) when (Refusal(error) is not null || error is OperationCanceledException) { }
        }
    }
    void PublishDependencies(Pending pending)
    {
        if (Device is not IMapRenderUploadDependencies dependencies) return;
        foreach (var pair in pending.Uploads.ToArray())
        {
            if (!pair.Value.Ready || !dependencies.IsUploadDependency(pending.Resources[pair.Key])) continue;
            Fence(pending);
            var owner = new Owner(pair.Key, pair.Value.Publish());
            pending.Owners.Add(pair.Key, owner); pending.Created.Add(owner);
            pending.Uploads.Remove(pair.Key); pair.Value.Dispose();
        }
    }
}
