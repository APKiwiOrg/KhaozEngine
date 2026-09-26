using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly HashSet<FoliageBatch> _foliageBatches = new();
    readonly List<FoliageDraw> _foliageDraws = new();
    readonly List<ModelRenderer.FoliageUniforms> _foliageUniforms = new();
    int _foliagePatchTests;
    bool _foliageDisposed;
    // _frameIndex (Scene3D.FrameView.cs) counts Begin, so a batch's state and the pixel scale can tell "last frame"
    // from "some earlier frame". The scale rolls like a batch's state, on the frame's first upload, so a second render
    // inside the frame reads the same last frame and does not replace the frame's own.
    float _foliageScale, _foliagePreviousScale;
    long _foliageScaleFrame = long.MinValue;
    bool _foliagePreviousScaleValid;

    /// <summary>This frame's foliage uniform slots. For tests.</summary>
    internal ReadOnlySpan<ModelRenderer.FoliageUniforms> FoliageUniformsForTests => CollectionsMarshal.AsSpan(_foliageUniforms);

    /// <summary>This submission with its batch's last frame folded in. The first submission of a batch in a frame rolls
    /// the batch's current into previous, so every submission in a frame measures against the batch's last submission
    /// of the frame before. A batch not submitted last frame has no previous and reads its own current.</summary>
    ModelRenderer.FoliageUniforms WithFoliageMotion(FoliageBatch batch, in ModelRenderer.FoliageUniforms current)
    {
        if (batch.MotionFrame != _frameIndex)
        {
            batch.MotionPreviousValid = batch.MotionFrame == _frameIndex - 1;
            batch.MotionPrevious = batch.MotionCurrent;
            batch.MotionFrame = _frameIndex;
        }
        batch.MotionCurrent = current;
        return current.WithPrevious(batch.MotionPreviousValid ? batch.MotionPrevious : current);
    }

    readonly record struct FoliageDraw(FoliageBatch Batch, FoliagePatch Patch, int Count, int UniformSlot, float CullRadius);

    /// <summary>Last completed foliage submission counters. These are workload counts, not GPU timings.</summary>
    public FoliageFrameStats LastFoliageStats { get; private set; }

    /// <summary>Copy authored placements into immutable spatial patches. The first visible frame uploads
    /// their instance stream once. Later frames update only draw constants. Meshes remain scene-owned.</summary>
    public FoliageBatch CreateFoliageBatch(ReadOnlySpan<FoliageInstance> instances, float patchSize = 8f)
    {
        ObjectDisposedException.ThrowIf(_foliageDisposed, this);
        FoliagePatchLayout layout = FoliagePatchLayout.Build(instances, FoliageBounds, patchSize);
        var data = new ModelRenderer.FoliageInstanceData[layout.Instances.Length];
        for (int i = 0; i < data.Length; i++)
        {
            FoliageInstance item = layout.Instances[i];
            Mesh mesh = _meshes[item.Mesh.Index]!.Value;
            float height = mesh.Bounds.Max.Y - mesh.Bounds.Min.Y;
            data[i] = new ModelRenderer.FoliageInstanceData
            {
                Model = item.Transform,
                Parameters = new Vector4(item.ThinningRank, mesh.Bounds.Min.Y,
                    height > .00001f ? 1f / height : 0f, mesh.AlphaCutoff),
            };
        }
        var batch = new FoliageBatch(this, layout, data);
        _foliageBatches.Add(batch);
        return batch;
    }

    MeshBounds? FoliageBounds(MeshHandle handle)
    {
        if (!_slots.IsValid(handle.Index, handle.Generation)) return null;
        if (_meshes[handle.Index] is not { } mesh) return null;
        if (mesh.SplatMaterial >= 0 || mesh.TileGroundMaterial >= 0)
            throw new ArgumentException("Foliage requires a model mesh, not a terrain material.", nameof(handle));
        return mesh.Bounds;
    }

    /// <summary>Queue conservative patch prefixes. Wind and exact distance fades run on the GPU.
    /// Returns candidate mesh instances, including blades the shader may reject. At most four cosmetic
    /// interactors are supported. Inputs are copied, so callers may reuse their scratch immediately.</summary>
    public int DrawFoliage(FoliageBatch batch, Vector3 focus, in FoliageRenderSettings settings,
        ReadOnlySpan<FoliageInteractor> interactors = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_foliageDisposed || batch.IsDisposed, batch);
        if (!ReferenceEquals(batch.Owner, this)) throw new ArgumentException("Foliage belongs to another scene.", nameof(batch));
        settings.Validate();
        if (!float.IsFinite(focus.X) || !float.IsFinite(focus.Y) || !float.IsFinite(focus.Z))
            throw new ArgumentException("Foliage focus must be finite.", nameof(focus));
        if (interactors.Length > 4) throw new ArgumentException("Foliage supports at most four interactors.", nameof(interactors));
        foreach (ref readonly FoliageInteractor interactor in interactors) interactor.Validate();
        if (settings.QualityDensity == 0f || batch.Count == 0) return 0;

        int slot = _foliageUniforms.Count;
        int candidates = 0;
        foreach (FoliagePatch patch in batch.Layout.Patches)
        {
            _foliagePatchTests++;
            int count = patch.CandidateCount(batch.Layout.Instances, focus, settings);
            if (count == 0) continue;
            float radius = patch.CullingRadius(settings.WindStrength > 0f || interactors.Length > 0);
            _foliageDraws.Add(new FoliageDraw(batch, patch, count, slot, radius));
            candidates = checked(candidates + count);
        }
        if (candidates > 0)
        {
            ModelRenderer.FoliageUniforms uniforms =
                ModelRenderer.FoliageUniforms.Build(focus, settings, interactors, EffectTimeSeconds);
            _foliageUniforms.Add(TemporalActive ? WithFoliageMotion(batch, uniforms) : uniforms);
        }
        return candidates;
    }

    internal void ReleaseFoliage(FoliageBatch batch)
    {
        if (batch.IsDisposed) return;
        batch.IsDisposed = true;
        _foliageBatches.Remove(batch);
        if (batch.Buffer is not null) _retired.Retire(batch.Buffer);
        batch.Buffer = null;
        batch.Pending = null;
    }

    void BeginFoliageFrame()
    {
        _foliageDraws.Clear();
        _foliageUniforms.Clear();
        _foliagePatchTests = 0;
    }

    void PrepareFoliageFrame(IGpuCommandList cl, in FrustumPlanes frustum)
    {
        // Render size and camera overrides are final now. Submission can precede either change.
        int kept = 0;
        for (int i = 0; i < _foliageDraws.Count; i++)
        {
            FoliageDraw draw = _foliageDraws[i];
            if (draw.Batch.IsDisposed || (FrustumCulling &&
                !frustum.IntersectsSphere(draw.Patch.Bounds.Center, draw.CullRadius))) continue;
            _foliageDraws[kept++] = draw;
        }
        _foliageDraws.RemoveRange(kept, _foliageDraws.Count - kept);
        long uploaded = 0;
        foreach (FoliageDraw draw in _foliageDraws)
        {
            FoliageBatch batch = draw.Batch;
            if (batch.IsDisposed || batch.Pending is not { } data) continue;
            uint bytes = checked((uint)data.Length * ModelRenderer.FoliageInstanceData.SizeInBytes);
            batch.Buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(bytes, GpuBufferUsage.VertexBuffer));
            cl.UpdateBuffer<ModelRenderer.FoliageInstanceData>(batch.Buffer, 0, data);
            batch.Pending = null;
            uploaded += bytes;
        }
        long uniforms = 0;
        if (kept > 0)
        {
            Span<ModelRenderer.FoliageUniforms> slots = CollectionsMarshal.AsSpan(_foliageUniforms);
            float metresPerPixel = ModelRenderer.FoliageUniforms.MetresPerPixel(_currentFrameView.Projection, _res.Height);
            if (_res.MotionAllocated) PrepareFoliageMotion(slots, metresPerPixel);
            else ModelRenderer.FoliageUniforms.ApplyPixelScale(slots, metresPerPixel);
            uniforms = _model.UploadFoliageUniforms(cl, slots);
        }
        _frameStats.AddInstanceUpload(uploaded);
        LastFoliageStats = new FoliageFrameStats(_foliagePatchTests, 0, 0, uploaded, uniforms);
    }

    /// <summary>FoliageMotionVert's last frame, once the frame's history is known. With valid history
    /// (<see cref="MotionHistoryValid"/>) each slot keeps the state its submission folded in and takes last frame's
    /// pixel scale, or this frame's when no foliage uploaded last frame. Without it every slot's last frame is its own,
    /// scale included, so the recomputed displacement cancels and the variant writes the motion the other paths write
    /// with no previous state.</summary>
    void PrepareFoliageMotion(Span<ModelRenderer.FoliageUniforms> slots, float metresPerPixel)
    {
        if (_foliageScaleFrame != _frameIndex)
        {
            _foliagePreviousScaleValid = _foliageScaleFrame == _frameIndex - 1;
            _foliagePreviousScale = _foliageScale;
            _foliageScale = metresPerPixel;
            _foliageScaleFrame = _frameIndex;
        }
        bool history = MotionHistoryValid;
        if (!history)
            foreach (ref ModelRenderer.FoliageUniforms slot in slots) slot = slot.WithPrevious(slot);
        ModelRenderer.FoliageUniforms.ApplyPixelScale(slots, metresPerPixel,
            history && _foliagePreviousScaleValid ? _foliagePreviousScale : metresPerPixel);
    }

    void DrawFoliagePass(IGpuCommandList cl)
    {
        int patches = 0, candidates = 0;
        foreach (FoliageDraw draw in _foliageDraws)
        {
            if (draw.Batch.IsDisposed || draw.Batch.Buffer is null) continue;
            MeshHandle handle = draw.Patch.Mesh;
            if (!_slots.IsValid(handle.Index, handle.Generation) || _meshes[handle.Index] is not { } mesh) continue;
            _model.DrawFoliageMesh(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                draw.Batch.Buffer, (uint)draw.Patch.Start, (uint)draw.Count, (uint)draw.UniformSlot, mesh.MaterialSet);
            CountMeshDraw(mesh.IndexCount, (uint)draw.Count);
            candidates += draw.Count;
            patches++;
        }
        LastFoliageStats = LastFoliageStats with { SubmittedPatches = patches, CandidateInstances = candidates };
        if (patches > 0) _model.BindPass(cl);
    }

    void DisposeFoliage()
    {
        _foliageDisposed = true;
        foreach (FoliageBatch batch in _foliageBatches)
        {
            batch.IsDisposed = true;
            batch.Buffer?.Dispose();
            batch.Buffer = null;
            batch.Pending = null;
        }
        _foliageBatches.Clear();
        BeginFoliageFrame();
    }
}
