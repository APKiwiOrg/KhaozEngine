using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>One casting foliage instance as a rigid point-shadow caster at its authored rest pose.
/// <paramref name="Sphere"/> is the absolute world bounding sphere (xyz centre, w radius), <paramref name="Slot"/>
/// its instance index in the proxy stream and <paramref name="SourceIndex"/> its index in the batch layout.</summary>
internal readonly record struct FoliagePointShadowCaster(
    MeshHandle Mesh, Matrix4x4 Transform, Vector4 Sphere, uint Slot, int SourceIndex);

/// <summary>Consecutive proxy slots of one mesh, drawn by one rigid caster call.</summary>
internal readonly record struct FoliagePointShadowRun(MeshHandle Mesh, uint Start, uint Count);

/// <summary>A submitted casting patch prefix in batch layout indices.</summary>
internal readonly record struct FoliagePointShadowRange(int Start, int Count);

/// <summary>
/// The rest-pose point-shadow proxies of one immutable foliage batch (ruling S5). Only casting instances are
/// stored, in layout order, so the proxies of one patch occupy consecutive slots. The 128-byte rigid instance
/// stream behind them is uploaded lazily, rebased by the frame's render origin, and reused until that origin moves.
/// Wind, agitation and motion profiles never reach it, so a static point-shadow map over animated foliage stays
/// cached.
/// </summary>
internal sealed class FoliagePointShadowStream : IDisposable
{
    readonly FoliagePointShadowCaster[] _casters;
    IGpuBuffer? _buffer;
    GpuRetireQueue? _retired;
    Vector3 _uploadedOrigin;
    bool _disposed;

    FoliagePointShadowStream(FoliagePointShadowCaster[] casters) => _casters = casters;

    /// <summary>The uploaded proxy stream, or null before the first upload and after disposal.</summary>
    internal IGpuBuffer? Buffer => _buffer;

    /// <summary>Every proxy, ascending by slot and by source index.</summary>
    internal ReadOnlySpan<FoliagePointShadowCaster> Casters => _casters;

    /// <summary>Collect the casting instances of a batch layout. An instance whose mesh has no bounds (a stale
    /// handle) is left out, as the patch layout leaves it out.</summary>
    internal static FoliagePointShadowStream Build(ReadOnlySpan<FoliageInstance> instances,
        Func<MeshHandle, MeshBounds?> boundsForMesh)
    {
        ArgumentNullException.ThrowIfNull(boundsForMesh);
        var casters = new List<FoliagePointShadowCaster>();
        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly FoliageInstance instance = ref instances[i];
            if (!instance.CastsShadows || boundsForMesh(instance.Mesh) is not MeshBounds bounds) continue;
            PointShadowCasterSphere sphere = PointShadowCasterSphere.FromRestBounds(bounds, instance.Transform, 1f);
            casters.Add(new FoliagePointShadowCaster(instance.Mesh, instance.Transform,
                new Vector4(sphere.Center, sphere.Radius), (uint)casters.Count, i));
        }
        return new FoliagePointShadowStream(casters.ToArray());
    }

    /// <summary>Upload the proxies in render space when nothing is uploaded yet or the render origin moved.
    /// Returns the bytes written. Must run outside a render pass. The retire queue is kept so
    /// <see cref="Dispose"/> retires the buffer behind the frames that may still read it.</summary>
    internal long EnsureUploaded(IGpuDevice device, IGpuCommandList cl, Vector3 renderOrigin, GpuRetireQueue retired)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(cl);
        ArgumentNullException.ThrowIfNull(retired);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retired = retired;
        if (_casters.Length == 0 || (_buffer is not null && _uploadedOrigin == renderOrigin)) return 0;

        uint bytes = checked((uint)_casters.Length * ModelRenderer.InstanceData.SizeInBytes);
        ModelRenderer.InstanceData[] staging = ArrayPool<ModelRenderer.InstanceData>.Shared.Rent(_casters.Length);
        try
        {
            for (int i = 0; i < _casters.Length; i++)
            {
                // The rigid path's rebase: only the translation column moves.
                Matrix4x4 model = _casters[i].Transform;
                model.M41 -= renderOrigin.X;
                model.M42 -= renderOrigin.Y;
                model.M43 -= renderOrigin.Z;
                staging[i] = new ModelRenderer.InstanceData { Model = model, Tint = Vector4.One };
            }
            _buffer ??= device.Factory.CreateBuffer(new GpuBufferDescription(bytes, GpuBufferUsage.VertexBuffer));
            cl.UpdateBuffer(_buffer, 0, (ReadOnlySpan<ModelRenderer.InstanceData>)staging.AsSpan(0, _casters.Length));
        }
        finally
        {
            ArrayPool<ModelRenderer.InstanceData>.Shared.Return(staging);
        }
        _uploadedOrigin = renderOrigin;
        return bytes;
    }

    /// <summary>
    /// Fill <paramref name="runs"/> with the proxies inside <paramref name="candidates"/> that reach the light's
    /// shadowing shell, under the shared <see cref="PointShadowCasterSphere"/> predicate, coalescing consecutive
    /// slots of one mesh. The list is cleared first. Candidates are disjoint layout ranges.
    /// </summary>
    internal void Query(ReadOnlySpan<FoliagePointShadowRange> candidates, Vector3 lightPosition, float radius,
        float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax, List<FoliagePointShadowRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        runs.Clear();
        foreach (FoliagePointShadowRange range in candidates)
        {
            foreach (ref readonly FoliagePointShadowCaster caster in SelectRange(range))
            {
                var sphere = new PointShadowCasterSphere(
                    new Vector3(caster.Sphere.X, caster.Sphere.Y, caster.Sphere.Z), caster.Sphere.W);
                if (!sphere.TouchesShadowingShell(lightPosition, radius, nearRadius, exclusionMin, exclusionMax))
                    continue;
                Append(runs, caster);
            }
        }
    }

    /// <summary>Select proxies by original layout index, preserving the full walk's long range endpoint.</summary>
    internal ReadOnlySpan<FoliagePointShadowCaster> SelectRange(FoliagePointShadowRange range)
    {
        if (range.Count <= 0) return ReadOnlySpan<FoliagePointShadowCaster>.Empty;
        int first = FirstAtOrAfter(range.Start);
        int end = FirstAtOrAfter((long)range.Start + range.Count);
        return _casters.AsSpan(first, end - first);
    }

    static void Append(List<FoliagePointShadowRun> runs, in FoliagePointShadowCaster caster)
    {
        int last = runs.Count - 1;
        if (last >= 0)
        {
            FoliagePointShadowRun run = runs[last];
            if (run.Mesh.Index == caster.Mesh.Index && run.Mesh.Generation == caster.Mesh.Generation
                && run.Start + run.Count == caster.Slot)
            {
                runs[last] = run with { Count = run.Count + 1 };
                return;
            }
        }
        runs.Add(new FoliagePointShadowRun(caster.Mesh, caster.Slot, 1));
    }

    int FirstAtOrAfter(long sourceIndex)
    {
        int low = 0, high = _casters.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_casters[middle].SourceIndex < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    /// <summary>Retire the uploaded stream through the queue its upload kept. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer is not null) _retired!.Retire(_buffer);
        _buffer = null;
    }
}
