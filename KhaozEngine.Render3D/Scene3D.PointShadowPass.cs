using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The point-light shadow PASS half of <see cref="Scene3D"/>: which queued instances a given light's six faces
    /// draw, and the recording that fills one light's atlas row. Everything about WHEN a row is rendered (the slot
    /// cache, the static and dynamic budgets, the per-light dirty signature) belongs to the integration half and
    /// is deliberately not here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caster walk is <see cref="BuildShadowCasterSpans"/>'s walk with one predicate added: an instance takes
    /// part only when its world bounding sphere intersects the light sphere (design decision 8). It reuses
    /// <see cref="AppendCasterSpans"/> for the grouping rather than repeating it, by writing
    /// <see cref="ShadowCastKind.None"/> into a scratch classification for everything the sphere test rejected, so
    /// the one definition of "what a caster span is" still has exactly one implementation.
    /// </para>
    /// <para>
    /// SPACE. The cull runs in ABSOLUTE space against <c>_instanceData</c>, which stays absolute for its whole CPU
    /// life, exactly as the cascade cull does. The matrices handed to the GPU are RENDER-space, because the
    /// uploaded instance buffer is, so the light is reduced with <c>ToRender</c> before any face matrix is built
    /// and the frame origin rides in the uniform slot for the dissolve variants' world-anchored noise.
    /// </para>
    /// <para>
    /// SKINNED CASTERS ARE OUT OF THIS ROUND, by design decision 5. A cached static map that had baked a character
    /// into it would be wrong the moment that character moved, and the dynamic path is a follow-up with its own
    /// issue. Rigid casters (shadow-only instances included) are the whole caster set here.
    /// </para>
    /// </remarks>
    public sealed partial class Scene3D
    {
        PointShadowAtlas? _pointShadowAtlas;
        PointShadowRenderer? _pointShadows;

        // Per-render scratch, reused rather than reallocated: the per-instance classification the sphere cull
        // rewrites, and the caster spans it groups into.
        readonly List<ShadowCastKind> _pointCasterKinds = new();
        readonly List<ShadowCasterSpan> _pointCasterSpans = new();

        // The lights packed into this frame's slot ring, in the order they were packed. Cleared by
        // BeginPointShadowFrame and walked by RenderPointShadowSlots.
        readonly List<PackedPointShadowSlot> _packedPointSlots = new();

        /// <summary>The point-shadow atlas texture, or null when no atlas has been allocated. The receivers bind
        /// this (or their 1x1 default in its place).</summary>
        internal IGpuTexture? PointShadowTexture => _pointShadowAtlas?.Texture;

        /// <summary>One cell's size per axis in the live atlas, or 0 when there is none.</summary>
        internal int PointShadowFaceResolution => _pointShadowAtlas?.FaceResolution ?? 0;

        /// <summary>How many light rows the live atlas carries, or 0 when there is none.</summary>
        internal int PointShadowRows => _pointShadowAtlas?.Rows ?? 0;

        /// <summary>
        /// Make sure an atlas of exactly this layout exists, allocating one (or replacing a differently shaped one)
        /// on the spot. Returns false when the device refused the allocation, in which case the PREVIOUS atlas is
        /// left intact and drawable, mirroring how a refused cascade layout leaves the live one alone.
        /// <para>
        /// Lazy by construction (design decision 6): a scene that never asks for a point shadow never calls this
        /// and pays no memory at all. The integration half decides when to call it in a real frame.
        /// </para>
        /// </summary>
        internal bool EnsurePointShadowAtlas(int faceResolution, int rows)
        {
            if (_pointShadowAtlas is { } live && live.MatchesLayout(faceResolution, rows)) return true;
            PointShadowAtlas? replacement = PointShadowAtlas.TryCreate(_gd, faceResolution, rows);
            if (replacement is null) return false;
            PointShadowRenderer renderer;
            try
            {
                renderer = new PointShadowRenderer(_gd, replacement);
            }
            catch
            {
                // The atlas allocation is not the only thing a device can refuse: the pass behind it is four
                // shader sets and four pipelines. This method answers false rather than throwing, so a refusal
                // here frees the atlas that was built for a pass that does not exist and leaves the previous
                // atlas and renderer live, the way a refused cascade layout does.
                replacement.Dispose();
                return false;
            }
            // An outgoing atlas may still be under a queued frame's reads, so drain before freeing it. A first
            // allocation has nothing to free and pays no stall.
            if (_pointShadowAtlas is not null) _gd.WaitForIdle();
            DisposePointShadows();
            _pointShadowAtlas = replacement;
            _pointShadows = renderer;
            return true;
        }

        /// <summary>
        /// Open a frame's point-shadow work: forget the last frame's packed lights and make sure the slot ring
        /// holds <paramref name="slotCount"/> lights' worth of faces. Call ONCE per frame, before the first
        /// <see cref="PackPointShadowSlot"/>, because growing the ring mid-pack would strand what was already
        /// written into it.
        /// </summary>
        internal void BeginPointShadowFrame(int slotCount)
        {
            _packedPointSlots.Clear();
            _pointShadows?.EnsureFaceCapacity(slotCount);
        }

        /// <summary>
        /// Pack one light's six faces into slots <c>packedSlotIndex * 6 + face</c> of the ring, and remember the
        /// atlas <paramref name="slot"/> (the ROW it renders into), its position and its radius for the draw.
        /// <paramref name="lightPosAbsolute"/> is in the same absolute space the consumer queued its geometry in,
        /// and <paramref name="radius"/> is the light's reach, which is also the face far plane.
        /// <para>
        /// The packed index and the atlas row are two different numbers on purpose: the ring is packed densely for
        /// THIS frame's lights, while the row is the light's place in the atlas, which the slot cache owns.
        /// </para>
        /// </summary>
        internal void PackPointShadowSlot(int packedSlotIndex, int slot, Vector3 lightPosAbsolute, float radius)
        {
            if (_pointShadows is not { } renderer || _pointShadowAtlas is not { } atlas) return;

            Vector3 lightRender = ToRender(lightPosAbsolute);
            // The dissolve noise cell is floored at a few atlas texels for the cascade pass's reason: below that a
            // dither stops resolving. A face texel is widest at the far plane, where it spans 2 * radius / res, so
            // the cascade helper answers this pass correctly with the light radius in the cascade radius's place.
            float noiseScale = ShadowDissolveNoise.ScaleForCascade(radius, atlas.FaceResolution);
            for (int face = 0; face < PointShadowMath.FaceCount; face++)
            {
                Matrix4x4 vp = GpuClip.Correct(
                    PointShadowMath.FaceViewProjection(face, slot, atlas.Rows, lightRender, radius),
                    _gd.Capabilities);
                renderer.PackFace(packedSlotIndex * PointShadowMath.FaceCount + face, vp, lightRender, radius,
                    noiseScale, _frameOrigin);
            }
            _packedPointSlots.Add(new PackedPointShadowSlot(packedSlotIndex, slot, lightPosAbsolute, radius));
        }

        /// <summary>Upload every light packed this frame in ONE whole-buffer write. Must run OUTSIDE the pass, so
        /// between the last <see cref="PackPointShadowSlot"/> and <see cref="RenderPointShadowSlots"/>, which is
        /// where the cascade pass's own upload sits.</summary>
        internal void UploadPointShadowFaces(IGpuCommandList cl)
        {
            ArgumentNullException.ThrowIfNull(cl);
            _pointShadows?.UploadFaces(cl);
        }

        /// <summary>
        /// Render every packed light in ONE pass: for each, clear its atlas row and draw every rigid caster whose
        /// world sphere touches that light's sphere into each of its six face cells. Returns how many caster DRAW
        /// CALLS were issued in total, which is what the integration half reports as its face draw count.
        /// <para>
        /// Records into <paramref name="cl"/> and does not submit. Requires this frame's instances to be grouped
        /// and uploaded already (the pass reuses that buffer), which is exactly where the key light's depth pass
        /// sits too, and requires <see cref="UploadPointShadowFaces"/> to have run.
        /// </para>
        /// </summary>
        internal int RenderPointShadowSlots(IGpuCommandList cl)
        {
            ArgumentNullException.ThrowIfNull(cl);
            if (_pointShadows is not { } renderer || _packedPointSlots.Count == 0) return 0;

            int draws = 0;
            IGpuBuffer? instances = _model.InstanceBuffer;
            renderer.BeginPass(cl);
            foreach (PackedPointShadowSlot packed in _packedPointSlots)
            {
                renderer.ClearRow(cl, packed.Slot);
                if (instances is null) continue;

                // The caster set is per LIGHT (it is a sphere cull against that light), so it is rebuilt here
                // rather than once for the pass. This is CPU work with nothing recorded, so it costs the pass
                // nothing to do it between draws.
                BuildPointCasterSpans(packed.LightPosAbsolute, packed.Radius);
                for (int face = 0; face < PointShadowMath.FaceCount; face++)
                {
                    ShadowCastKind bound = ShadowCastKind.None;
                    foreach (ShadowCasterSpan span in _pointCasterSpans)
                    {
                        var m = _meshes[span.Index];
                        if (m is not { } mesh) continue;   // unloaded between the span build and here: skip its slice
                        if (span.Kind != bound)
                        {
                            renderer.BeginFace(cl, packed.PackedIndex * PointShadowMath.FaceCount + face, face,
                                packed.Slot, span.Kind);
                            bound = span.Kind;
                        }
                        renderer.DrawCasterRun(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                            instances, span.Start, span.Count);
                        draws++;
                    }
                }
            }
            renderer.EndPass(cl);
            return draws;
        }

        /// <summary>One light packed into this frame's ring: where its six faces sit in the ring, which atlas row
        /// it renders into, and the light itself, which the draw re-culls its casters against.</summary>
        readonly record struct PackedPointShadowSlot(
            int PackedIndex, int Slot, Vector3 LightPosAbsolute, float Radius);

        /// <summary>
        /// Build <see cref="_pointCasterSpans"/>: this light's caster draw list, in the exact order
        /// <see cref="RenderPointShadowSlots"/> draws it. Same rules as the cascade walk (a stale handle, a
        /// receive-only splat mesh and anything the consumer opted out of casting all drop out), plus the light
        /// sphere test.
        /// </summary>
        void BuildPointCasterSpans(Vector3 lightPosAbsolute, float radius)
        {
            _pointCasterSpans.Clear();
            _pointCasterKinds.Clear();
            if (_instanceData.Count == 0) return;

            // A scratch classification the whole instance array long, so AppendCasterSpans can group it exactly as
            // it groups the cascade pass's: everything the sphere rejected reads as None, which it already skips.
            for (int i = 0; i < _instanceData.Count; i++)
                _pointCasterKinds.Add(i < _instanceCastKinds.Count ? _instanceCastKinds[i] : ShadowCastKind.Opaque);

            foreach (MeshRun run in _runs)
            {
                if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                var m = _meshes[run.Mesh.Index];
                if (m is not { } mesh) continue;
                if (!MeshCastsShadows(mesh.SplatMaterial, TerrainCastsShadows)) continue;
                for (uint s = 0; s < run.Count; s++)
                {
                    int slot = (int)(run.Start + s);
                    if (slot >= _pointCasterKinds.Count) break;
                    if (_pointCasterKinds[slot] == ShadowCastKind.None) continue;
                    if (!InstanceTouchesLight(mesh.Bounds, _instanceData[slot].Model, lightPosAbsolute, radius))
                        _pointCasterKinds[slot] = ShadowCastKind.None;
                }
                AppendCasterSpans(run.Mesh.Index, run.Mesh.Generation, run.Start, run.Count,
                    _pointCasterKinds, _pointCasterSpans);
            }
        }

        /// <summary>
        /// Diagnostic: render ONE light's row on a command list of this method's own, then fence. For a test or a
        /// tool that wants the pass without the frame around it. The queued instances must already be grouped and
        /// uploaded, which one ordinary rendered frame leaves behind. Returns the caster draw count, as
        /// <see cref="RenderPointShadowSlots"/> does.
        /// <para>
        /// A WRAPPER over the four-step frame surface rather than a path of its own, so what a test drives is what
        /// the integration half will drive: one begin, one pack, one upload outside the pass, one pass.
        /// </para>
        /// </summary>
        internal int DebugRenderPointShadowSlot(int slot, Vector3 lightPosAbsolute, float radius)
        {
            if (_pointShadows is null) return 0;
            int draws;
            using (IGpuCommandList cl = _gd.Factory.CreateCommandList())
            {
                using (GpuRecording.Open(_gd, cl, "Scene3D.DebugRenderPointShadowSlot"))
                {
                    BeginPointShadowFrame(1);
                    PackPointShadowSlot(0, slot, lightPosAbsolute, radius);
                    UploadPointShadowFaces(cl);
                    draws = RenderPointShadowSlots(cl);
                }
                _gd.Submit(cl);
                _gd.WaitForIdle();
            }
            return draws;
        }

        /// <summary>Diagnostic: read the point-light shadow atlas (R32F linear distance over radius) back to the
        /// CPU as a float array, row-major, top-left. Lets a test verify the pass on a real device, which is what
        /// keeps the face convention here and the one the receiver samples with from drifting apart. Requires a
        /// mappable device and is not on the per-frame path. Mirrors <see cref="DebugReadShadowMap"/>. Empty when no
        /// atlas has been allocated.</summary>
        internal float[] DebugReadPointShadowAtlas(out int width, out int height)
        {
            width = 0;
            height = 0;
            if (_pointShadowAtlas is not { } atlas) return Array.Empty<float>();
            IGpuTexture tex = atlas.Texture;
            width = (int)tex.Width;
            height = (int)tex.Height;
            IGpuResourceFactory f = _gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                tex.Width, tex.Height, GpuPixelFormat.R32Float, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                using (GpuRecording.Open(_gd, cl, "Scene3D.DebugReadPointShadowAtlas")) cl.CopyTexture(tex, staging);
                _gd.Submit(cl);
                _gd.WaitForIdle();
            }
            var outF = new float[width * height];
            var map = _gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        outF[y * width + x] = *(float*)(data + y * (int)map.RowPitch + x * 4);
            }
            _gd.Unmap(staging);
            return outF;
        }

        /// <summary>
        /// The tail of <see cref="Dispose"/>: the tile-ground materials, then this pass's atlas. There is no
        /// ordering between the two, they are unrelated resources freed at the same moment.
        /// <para>
        /// They share one call because <c>Scene3D.cs</c> is at its file-size baseline and cannot grow a line, and
        /// two statements on one line there is the worse answer. It sits in this file because this is the partial
        /// that added the second release.
        /// </para>
        /// </summary>
        void DisposeTileGroundAndPointShadowResources()
        {
            DisposeTileGroundMaterials();
            DisposePointShadows();
        }

        /// <summary>Release the atlas and its pass. Called from
        /// <see cref="DisposeTileGroundAndPointShadowResources"/> and by
        /// <see cref="EnsurePointShadowAtlas"/> when a layout is replaced.</summary>
        void DisposePointShadows()
        {
            _pointShadows?.Dispose();
            _pointShadowAtlas?.Dispose();
            _pointShadows = null;
            _pointShadowAtlas = null;
        }
    }
}
