using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// Casting animated foliage in point-light shadows (ruling S5). Each submitted casting patch prefix, gathered before
/// camera rejection in <c>Scene3D.FoliageShadows.cs</c>, selects rest-pose proxies from its batch's
/// <see cref="FoliagePointShadowStream"/>. The static signature and the face draw ask one query, mix only mesh
/// identity and rest transforms, and draw through the rigid caster pipeline, so time, agitation and motion
/// coefficients never dirty a cached static map.
/// </summary>
public sealed partial class Scene3D
{
    // One lazily built proxy stream per casting batch, kept for the batch's life and retired after it.
    readonly Dictionary<FoliageBatch, FoliagePointShadowStream> _foliagePointStreams = new();

    // This point-shadow frame's batches, each with a slice of merged layout ranges.
    readonly List<FoliagePointSource> _foliagePointSources = new();
    readonly List<FoliagePointShadowRange> _foliagePointRanges = new();
    readonly List<FoliagePointPending> _foliagePointPending = new();
    readonly Dictionary<FoliageBatch, int> _foliagePointSourceOrder = new();
    int _foliagePointFrame = -1;

    // One light's runs, shared by its six faces.
    readonly List<FoliagePointShadowRun> _foliagePointRuns = new();
    readonly List<FoliagePointFaceRun> _foliagePointFaceRuns = new();
    int _foliagePointFaceLight = -1;

    /// <summary>Rest-pose foliage caster draws issued into point-shadow cells this frame. Also counted in
    /// <see cref="ShadowPassDiagnostics.PointFaceDrawCalls"/>.</summary>
    internal int FoliagePointShadowDrawCalls { get; private set; }

    /// <summary>Live proxy streams. For the lifetime tests.</summary>
    internal int FoliagePointShadowStreamCount => _foliagePointStreams.Count;

    /// <summary>Open a frame's foliage point-shadow work: reset the counter and retire every stream whose batch
    /// was released. Runs every frame, requested or not, so a released batch's stream never outlives it by more
    /// than one frame.</summary>
    void BeginFoliagePointShadowFrame()
    {
        FoliagePointShadowDrawCalls = 0;
        foreach (KeyValuePair<FoliageBatch, FoliagePointShadowStream> entry in _foliagePointStreams)
        {
            if (!entry.Key.IsDisposed) continue;
            entry.Value.Dispose();
            _foliagePointStreams.Remove(entry.Key);
        }
    }

    // Cached point runs must not keep a released batch's CPU product alive after its lease retires.
    void ForgetPreparedFoliagePointSources(FoliageBatch batch)
    {
        if (_foliagePointStreams.Remove(batch, out var stream)) stream.Dispose();
        _foliagePointSources.Clear();
        _foliagePointRanges.Clear();
        _foliagePointPending.Clear();
        _foliagePointSourceOrder.Clear();
        _foliagePointFaceRuns.Clear();
        _foliagePointRuns.Clear();
        _foliagePointFrame = -1;
        _foliagePointFaceLight = -1;
    }

    /// <summary>Translate the submitted casting prefixes into per-batch layout ranges, once per point-shadow frame.
    /// A batch submitted more than once keeps the union of its prefixes, so no proxy is drawn twice.</summary>
    void EnsureFoliagePointShadowFrame()
    {
        if (_foliagePointFrame == _pointShadowFrame) return;
        _foliagePointFrame = _pointShadowFrame;
        _foliagePointFaceLight = -1;
        _foliagePointSources.Clear();
        _foliagePointRanges.Clear();
        _foliagePointPending.Clear();
        _foliagePointSourceOrder.Clear();

        foreach (FoliageDraw draw in _foliageShadowDraws)
        {
            if (!FoliageDrawValid(draw)) continue;
            if (!_foliagePointSourceOrder.TryGetValue(draw.Batch, out int order))
            {
                order = _foliagePointSourceOrder.Count;
                _foliagePointSourceOrder.Add(draw.Batch, order);
            }
            _foliagePointPending.Add(new FoliagePointPending(order, draw.Batch, draw.Patch.Start, draw.Count));
        }
        if (_foliagePointPending.Count == 0) return;
        _foliagePointPending.Sort(static (a, b) =>
        {
            int comparison = a.Order.CompareTo(b.Order);
            return comparison != 0 ? comparison : a.Start.CompareTo(b.Start);
        });

        int i = 0;
        while (i < _foliagePointPending.Count)
        {
            FoliagePointPending first = _foliagePointPending[i];
            int rangeStart = _foliagePointRanges.Count;
            int start = first.Start, end = first.Start + first.Count;
            for (i++; i < _foliagePointPending.Count && _foliagePointPending[i].Order == first.Order; i++)
            {
                FoliagePointPending next = _foliagePointPending[i];
                if (next.Start <= end)
                {
                    end = Math.Max(end, next.Start + next.Count);
                    continue;
                }
                _foliagePointRanges.Add(new FoliagePointShadowRange(start, end - start));
                (start, end) = (next.Start, next.Start + next.Count);
            }
            _foliagePointRanges.Add(new FoliagePointShadowRange(start, end - start));
            _foliagePointSources.Add(new FoliagePointSource(FoliagePointStreamFor(first.Batch),
                rangeStart, _foliagePointRanges.Count - rangeStart, first.Batch));
        }
    }

    FoliagePointShadowStream FoliagePointStreamFor(FoliageBatch batch)
    {
        if (_foliagePointStreams.TryGetValue(batch, out FoliagePointShadowStream? stream)) return stream;
        stream = batch.PreparedPointStream ?? FoliagePointShadowStream.Build(batch.Layout.Instances, FoliageBounds);
        _foliagePointStreams.Add(batch, stream);
        return stream;
    }

    ReadOnlySpan<FoliagePointShadowRange> FoliagePointRanges(in FoliagePointSource source) =>
        CollectionsMarshal.AsSpan(_foliagePointRanges).Slice(source.RangeStart, source.RangeCount);

    /// <summary>Upload this frame's proxy streams in render space, outside any pass and before the point depth
    /// pass reads them. A stream already uploaded at this origin costs nothing.</summary>
    void UploadFoliagePointShadowStreams(IGpuCommandList cl)
    {
        EnsureFoliagePointShadowFrame();
        long bytes = 0;
        foreach (FoliagePointSource source in _foliagePointSources)
            bytes += source.Stream.EnsureUploaded(_gd, cl, _frameOrigin, _retired);
        _frameStats.AddInstanceUpload(bytes);
    }

    /// <summary>Fold every rest-pose proxy this light's map would draw into a static signature: mesh identity and
    /// its exact authored rest transform, nothing else. Native poses retain their local matrix and anchor.</summary>
    void MixFoliagePointSignature(ref ulong hash, Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        EnsureFoliagePointShadowFrame();
        foreach (FoliagePointSource source in _foliagePointSources)
        {
            source.Stream.Query(FoliagePointRanges(source), lightPosition, radius, nearRadius,
                exclusionMin, exclusionMax, _foliagePointRuns);
            foreach (FoliagePointShadowRun run in _foliagePointRuns)
                foreach (ref readonly FoliagePointShadowCaster caster in source.Stream.CastersIn(run))
                {
                    MixPointSignature(ref hash, SignatureWord((uint)caster.Mesh.Index, (uint)caster.Mesh.Generation));
                    if (source.Stream.AnchoredPose(caster) is { } anchored) MixAnchoredPointSignature(ref hash, anchored);
                    else MixPointSignature(ref hash, caster.Transform);
                }
        }
    }

    /// <summary>Draw the rest-pose proxies touching one light into one of its face cells through the rigid caster
    /// pipeline. The query runs on the light's first face and is reused by the other five. Returns the draws.</summary>
    int DrawFoliagePointShadowFace(IGpuCommandList cl, PointShadowRenderer renderer, PointShadowAtlas atlas,
        int packedIndex, int face, int row, Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        if (face == 0 || packedIndex != _foliagePointFaceLight)
        {
            EnsureFoliagePointShadowFrame();
            _foliagePointFaceLight = packedIndex;
            _foliagePointFaceRuns.Clear();
            foreach (FoliagePointSource source in _foliagePointSources)
            {
                if (source.Stream.Buffer is not { } buffer) continue;
                source.Stream.Query(FoliagePointRanges(source), lightPosition, radius, nearRadius,
                    exclusionMin, exclusionMax, _foliagePointRuns);
                foreach (FoliagePointShadowRun run in _foliagePointRuns)
                    _foliagePointFaceRuns.Add(new FoliagePointFaceRun(buffer, run, source.Batch));
            }
        }

        int draws = 0;
        foreach (FoliagePointFaceRun faceRun in _foliagePointFaceRuns)
        {
            MeshHandle handle = faceRun.Run.Mesh;
            if (!_slots.IsValid(handle.Index, handle.Generation) || _meshes[handle.Index] is not { } mesh) continue;
            renderer.BeginFace(cl, atlas, packedIndex * PointShadowMath.FaceCount + face, face, row,
                    ShadowCastKind.Opaque);
            if (faceRun.Batch.AnchorSet is { } anchor)
                renderer.DrawPreparedFoliage(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, faceRun.Buffer,
                    faceRun.Run.Start, faceRun.Run.Count, anchor, _model.PreparedAnchorLayout);
            else
                renderer.DrawCasterRun(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, faceRun.Buffer,
                    faceRun.Run.Start, faceRun.Run.Count);
            draws++;
        }
        FoliagePointShadowDrawCalls += draws;
        return draws;
    }

    /// <summary>Scene teardown. The scene's retire queue has already flushed, so the streams retired here are
    /// flushed again behind one device drain.</summary>
    void DisposeFoliagePointShadows()
    {
        foreach (FoliagePointShadowStream stream in _foliagePointStreams.Values) stream.Dispose();
        _foliagePointStreams.Clear();
        _foliagePointSources.Clear();
        _foliagePointFaceRuns.Clear();
        _retired.FlushAll();
    }

    readonly record struct FoliagePointSource(FoliagePointShadowStream Stream, int RangeStart, int RangeCount, FoliageBatch Batch);
    readonly record struct FoliagePointPending(int Order, FoliageBatch Batch, int Start, int Count);
    readonly record struct FoliagePointFaceRun(IGpuBuffer Buffer, FoliagePointShadowRun Run, FoliageBatch Batch);
}
