using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
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

    readonly FoliagePointCasterIndex _foliagePointIndex = new();
    readonly List<FoliagePointCasterRef> _foliagePointHits = new();

    // One light's runs, shared by its six faces.
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

    /// <summary>Translate the submitted casting prefixes into per-batch layout ranges, once per point-shadow frame.
    /// A batch submitted more than once keeps the union of its prefixes, so no proxy is drawn twice.</summary>
    void EnsureFoliagePointShadowFrame(ShadowStageFoliageBuildOrigin origin = ShadowStageFoliageBuildOrigin.RowPreparation)
    {
        if (_foliagePointFrame == _pointShadowFrame) return;
        long probeStart = ShadowProbeTimestamp();
        _foliagePointFrame = _pointShadowFrame;
        _foliagePointFaceLight = -1;
        _foliagePointSources.Clear();
        _foliagePointRanges.Clear();
        _foliagePointPending.Clear();
        _foliagePointSourceOrder.Clear();
        _foliagePointIndex.Reset();

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
        if (_foliagePointPending.Count == 0)
        {
            _foliagePointIndex.Seal();
            RecordShadowProbeFoliageBuild(origin, probeStart);
            return;
        }
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
                rangeStart, _foliagePointRanges.Count - rangeStart));
        }
        for (int sourceOrdinal = 0; sourceOrdinal < _foliagePointSources.Count; sourceOrdinal++)
        {
            FoliagePointSource source = _foliagePointSources[sourceOrdinal];
            _foliagePointIndex.Add(sourceOrdinal, source.Stream, FoliagePointRanges(source));
            if (_shadowProbeActive) _shadowProbeCounts.FoliageStreamProxies += source.Stream.Casters.Length;
        }
        _foliagePointIndex.Seal();
        RecordShadowProbeFoliageBuild(origin, probeStart);
    }

    FoliagePointShadowStream FoliagePointStreamFor(FoliageBatch batch)
    {
        if (_foliagePointStreams.TryGetValue(batch, out FoliagePointShadowStream? stream)) return stream;
        stream = FoliagePointShadowStream.Build(batch.Layout.Instances, FoliageBounds);
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
    /// the absolute rest transform, nothing else.</summary>
    void MixFoliagePointSignature(ref ulong hash, Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        EnsureFoliagePointShadowFrame(ShadowStageFoliageBuildOrigin.Signature);
        int beforeTests = _foliagePointIndex.TouchTests, beforeSort = _foliagePointIndex.SortInputs;
        long queryStart = ShadowProbeTimestamp();
        _foliagePointIndex.Query(lightPosition, radius, nearRadius, exclusionMin, exclusionMax, _foliagePointHits);
        RecordShadowProbeFoliageQuery(ShadowStageQueryPurpose.Signature, beforeTests, beforeSort, queryStart);
        long foldStart = ShadowProbeTimestamp();
        foreach (FoliagePointCasterRef reference in _foliagePointHits)
        {
            FoliagePointSource source = _foliagePointSources[reference.SourceOrdinal];
            ref readonly FoliagePointShadowCaster caster = ref source.Stream.Casters[(int)reference.ProxySlot];
            MixPointSignature(ref hash, SignatureWord((uint)caster.Mesh.Index, (uint)caster.Mesh.Generation));
            MixPointSignature(ref hash, caster.Transform);
        }
        _shadowProbeTicks.FoliageSignatureFold = ShadowProbeElapsed(foldStart, _shadowProbeTicks.FoliageSignatureFold);
    }

    /// <summary>Draw the rest-pose proxies touching one light into one of its face cells through the rigid caster
    /// pipeline. The query runs on the light's first face and is reused by the other five. Returns the draws.</summary>
    int DrawFoliagePointShadowFace(IGpuCommandList cl, PointShadowRenderer renderer, PointShadowAtlas atlas,
        int packedIndex, int face, int row, Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax, in PackedPointShadowSlot packed)
    {
        if (face == 0 || packedIndex != _foliagePointFaceLight)
        {
            EnsureFoliagePointShadowFrame(ShadowStageFoliageBuildOrigin.RowRecording);
            _foliagePointFaceLight = packedIndex;
            _foliagePointFaceRuns.Clear();
            int beforeTests = _foliagePointIndex.TouchTests, beforeSort = _foliagePointIndex.SortInputs;
            long queryStart = ShadowProbeTimestamp();
            _foliagePointIndex.Query(lightPosition, radius, nearRadius, exclusionMin, exclusionMax, _foliagePointHits);
            RecordShadowProbeFoliageQuery(ShadowStageQueryPurpose.RowRecording, beforeTests, beforeSort, queryStart);
            int lastSource = -1;
            foreach (FoliagePointCasterRef reference in _foliagePointHits)
            {
                FoliagePointSource source = _foliagePointSources[reference.SourceOrdinal];
                if (source.Stream.Buffer is not { } buffer) continue;
                ref readonly FoliagePointShadowCaster caster = ref source.Stream.Casters[(int)reference.ProxySlot];
                int last = _foliagePointFaceRuns.Count - 1;
                if (last >= 0 && lastSource == reference.SourceOrdinal)
                {
                    FoliagePointFaceRun previous = _foliagePointFaceRuns[last];
                    FoliagePointShadowRun run = previous.Run;
                    if (run.Mesh.Index == caster.Mesh.Index && run.Mesh.Generation == caster.Mesh.Generation
                        && run.Start + run.Count == reference.ProxySlot)
                    {
                        _foliagePointFaceRuns[last] = previous with { Run = run with { Count = run.Count + 1 } };
                        continue;
                    }
                }
                _foliagePointFaceRuns.Add(new FoliagePointFaceRun(buffer,
                    new FoliagePointShadowRun(caster.Mesh, reference.ProxySlot, 1)));
                lastSource = reference.SourceOrdinal;
            }
        }

        using var pass = ProfilePointPass(cl, packed, PointProfilePass.Foliage, face,
            executed: _foliagePointFaceRuns.Count > 0);
        int draws = 0;
        foreach (FoliagePointFaceRun faceRun in _foliagePointFaceRuns)
        {
            MeshHandle handle = faceRun.Run.Mesh;
            if (!_slots.IsValid(handle.Index, handle.Generation) || _meshes[handle.Index] is not { } mesh) continue;
            if (draws == 0)
                renderer.BeginFace(cl, atlas, packedIndex * PointShadowMath.FaceCount + face, face, row,
                    ShadowCastKind.Opaque);
            renderer.DrawCasterRun(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, faceRun.Buffer,
                faceRun.Run.Start, faceRun.Run.Count);
            draws++;
            if (_shadowProbeActive) _shadowProbeCounts.PointFoliageIssuedInstances += faceRun.Run.Count;
        }
        FoliagePointShadowDrawCalls += draws;
        return draws;
    }

    /// <summary>Scene teardown. The scene's retire queue has already flushed, so the streams retired here are
    /// flushed again behind one device drain.</summary>
    void DisposeFoliagePointShadows(Internal.ResourceDisposal disposal)
    {
        foreach (FoliagePointShadowStream stream in _foliagePointStreams.Values) disposal.Release("foliagePoint.stream", stream.Dispose);
        _foliagePointStreams.Clear();
        _foliagePointSources.Clear();
        _foliagePointFaceRuns.Clear();
        disposal.Release("foliagePoint.retirement", _retired.FlushAll);
    }

    readonly record struct FoliagePointSource(FoliagePointShadowStream Stream, int RangeStart, int RangeCount);
    readonly record struct FoliagePointPending(int Order, FoliageBatch Batch, int Start, int Count);
    readonly record struct FoliagePointFaceRun(IGpuBuffer Buffer, FoliagePointShadowRun Run);
}
