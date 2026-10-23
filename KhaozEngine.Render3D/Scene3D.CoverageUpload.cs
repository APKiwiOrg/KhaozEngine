using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly Dictionary<(int, int), CoverageUploadEvidence> _coverageUploads = new();
    readonly Dictionary<(int, int), SkinnedGltfMesh> _coverageHostUploads = new();
    readonly Dictionary<(int, int), CoverageActorBounds> _coveragePaletteBounds = new();
    readonly List<SkinnedHostConformer> _coverageConformers = new();
    readonly CoverageFrameArena _coverageArena = new();
    bool _coverageDisposed;
    readonly int _coverageThread = Environment.CurrentManagedThreadId;
    void RequireCoverageThread()
    {
        if (Environment.CurrentManagedThreadId != _coverageThread)
            throw new InvalidOperationException("Coverage must run on the scene render thread.");
    }

    static void ValidateCoverageSource(SkinnedGltfMesh mesh)
    {
        if (mesh.Coverage is not { } part) return;
        if (mesh.Profile.Method != SkinningMethod.LinearBlend)
            throw new NotSupportedException("DQ coverage requires the separate precise coverage milestone.");
        if (!part.Matches(mesh)) throw new InvalidDataException($"Coverage asset '{part.Source.Identity}': uploaded host slice changed.");
    }

    /// <summary>Creates an actor-owned conformer from every material slice of one exact host source and map.</summary>
    public SkinnedHostConformer CreateHostConformer(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> meshes)
    {
        RequireCoverageThread();
        ObjectDisposedException.ThrowIf(_coverageDisposed, this);
        ArgumentNullException.ThrowIfNull(skeleton); ArgumentNullException.ThrowIfNull(meshes);
        if (KhaozEngine.Gpu.GpuRecording.OpenOwner(_gd) is not null)
            throw new InvalidOperationException("Create coverage conformers before opening a command list.");
        if (meshes.Count == 0) throw new ArgumentException("A host requires every material slice.", nameof(meshes));
        var uploaded = new SkinnedGltfMesh[meshes.Count];
        for (int i = 0; i < meshes.Count; i++)
        {
            var h = meshes[i];
            if (!_skinnedSlots.IsValid(h.Index, h.Generation) || !_coverageHostUploads.TryGetValue((h.Index, h.Generation), out var mesh)
                || mesh.Coverage is null || !SameSkeleton(mesh.Skeleton, skeleton))
                throw new ArgumentException("Host meshes must be live covered slices of the supplied skin.", nameof(meshes));
            uploaded[i] = mesh;
        }
        HostCoveragePart first = uploaded[0].Coverage!;
        if (meshes.Count != first.Source.Materials.Count || uploaded.Any(m =>
            !ReferenceEquals(m.Coverage!.Source, first.Source) || !ReferenceEquals(m.Coverage.Map, first.Map)
            || !m.InverseBind.AsSpan().SequenceEqual(uploaded[0].InverseBind))
            || uploaded.Select(m => m.Coverage!.MaterialSlice).Distinct().Count() != meshes.Count)
            throw new ArgumentException("A host requires one complete compatible source and map.", nameof(meshes));
        _coverageGpu ??= new(_gd, _retired);
        var gpu = _coverageGpu.Acquire(first.Source, first.Map, uploaded[0].InverseBind.Length);
        var topology = gpu.Host.Topology;
        var handles = topology.MaterialRanges.Select(range => meshes[Array.FindIndex(uploaded,
            m => m.Coverage!.MaterialSlice == range.MaterialSlice)]).ToArray();
        foreach (var handle in handles) _coveragePaletteBounds.TryAdd((handle.Index, handle.Generation), gpu.Host.Bounds);
        try
        {
            var conformer = new SkinnedHostConformer(this, handles, topology,
                (Matrix4x4[])uploaded[0].InverseBind.Clone(), gpu);
            _coverageArena.Preparations.EnsureCapacity(_coverageConformers.Count + 1);
            _coverageConformers.Add(conformer);
            return conformer;
        }
        catch { _coverageGpu.Release(gpu); throw; }
    }

    static bool SameSkeleton(Skeleton? a, Skeleton b) => a is not null
        && a.ParentIndices.AsSpan().SequenceEqual(b.ParentIndices) && a.RestLocal.AsSpan().SequenceEqual(b.RestLocal)
        && a.NodeLogicalIndex.AsSpan().SequenceEqual(b.NodeLogicalIndex) && a.JointToNode.AsSpan().SequenceEqual(b.JointToNode);

    internal void RetireCoverage(SkinnedHostConformer conformer)
    {
        if (_coverageDisposed) return;
        _coverageGpu?.Release(conformer.Gpu);
        _coverageConformers.Remove(conformer);
    }

    void DisposeCoverage()
    {
        DisposeCoverageOutcomes();
        _coverageGpu?.Dispose();
        _coverageTransition?.Dispose();
        _coverageMotion.Reset();
        _coverageDisposed = true;
        foreach (var conformer in _coverageConformers) conformer.Disposed = true;
        _coverageConformers.Clear();
        _coverageUploads.Clear(); _coverageHostUploads.Clear(); _coveragePaletteBounds.Clear();
    }
}
