using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Every mid-life unload the scene offers, in one place, because they all answer the same question and used to
    /// answer it four different ways.
    /// <para>
    /// The question is: this resource may still be referenced by GPU work that has been submitted but not finished,
    /// so when is it safe to destroy? The original answer everywhere was a full <see cref="IGpuDevice.WaitForIdle"/>
    /// inside the unload call. That is correct and it is a pipeline stall on the frame thread, which a streaming
    /// world pays over and over: <c>UnloadMesh</c> moved off it first (#99), and the three siblings here followed
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/383">#383</see>). All of them now hand their
    /// resources to the scene's <see cref="GpuRetireQueue"/> and return, and the queue destroys them at a later
    /// <see cref="Scene3D.Begin"/> once a fence proves the GPU is done (or, on a backend with no completion fence,
    /// behind one drain three frame boundaries later). Nothing is ever destroyed in the frame it was retired in.
    /// <c>UnloadTileGroundMaterial</c> is the youngest of them and never shipped a drain at all: it was written
    /// against this file rather than converted into it.
    /// </para>
    /// <para>
    /// The skinned path is the one that mattered in the field. An MMO client despawns avatars and corpses
    /// continuously as they leave interest range, and every despawn used to drain the whole device.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>Free every sub-mesh of <paramref name="prop"/> (each via <see cref="UnloadMesh"/>) and its
        /// textures' owning scope. A <c>default</c>/invalid handle is a no-op.</summary>
        public void UnloadProp(PropHandle prop)
        {
            if (prop.Parts == null) return;
            foreach (MeshHandle part in prop.Parts) UnloadMesh(part);
        }

        /// <summary>
        /// Free the GPU buffers backing <paramref name="h"/> and release its slot for reuse. A <c>default</c>
        /// handle is a no-op. A stale or bogus handle (its generation no longer matches the slot, e.g. a
        /// double-free) throws <see cref="System.ArgumentException"/>.
        /// </summary>
        public void UnloadMesh(MeshHandle h)
        {
            if (h.Generation == 0) return;          // default handle: no-op
            _slots.Free(h.Index, h.Generation);     // throws on stale/invalid
            // Retire rather than destroy: queued GPU work may still reference these buffers, and draining the whole
            // device per unload stalled the frame thread on the terrain streaming path (every chunk leaving the ring
            // and every LOD flip lands here). The pool frees them behind one drain a few frames later. The per-mesh
            // material set goes too, but NOT the texture: that is owned in _textures and shared between meshes.
            if (_meshes[h.Index] is { } mesh) _retired.Retire(mesh.Vb, mesh.Ib, mesh.MaterialSet);
            _meshes[h.Index] = null;
        }

        /// <summary>Free a splat-terrain material's GPU resources (its texture arrays, params UBO, resource set) and
        /// release its slot. A <c>default</c>/Invalid handle is a no-op. Meshes still referencing it must be unloaded
        /// first (they hold no reference after this). Also a no-op once <see cref="Dispose"/> has run: Dispose
        /// already freed every splat material and cleared the backing list, so a caller that still holds a handle
        /// (e.g. a world disposed after its owning scene) would otherwise index past the end of the now-empty list
        /// and get an <see cref="System.ArgumentOutOfRangeException"/> instead of a silent no-op.
        /// <para>Does not drain: the whole material goes to the retire queue as one resource, so its arrays, UBO,
        /// set and any owned sampler are destroyed together once the GPU is done with them.</para></summary>
        public void UnloadSplatMaterial(SplatMaterialHandle h)
        {
            if (!h.IsValid || h.ListIndex >= _splatMaterials.Count) return;
            // A null slot (already unloaded) is retired as nothing: the queue ignores it, so there is no guard here.
            _retired.Retire(_splatMaterials[h.ListIndex]);
            _splatMaterials[h.ListIndex] = null;
        }

        /// <summary>Free a tile-ground material's GPU resources (its albedo texture array, params UBO, resource set)
        /// and release its slot. A <c>default</c>/Invalid handle is a no-op, and so is a call once
        /// <see cref="Dispose"/> has run, for the same reason <see cref="UnloadSplatMaterial"/> guards it. Meshes
        /// still referencing the material must be unloaded first (they hold no reference after this).
        /// <para>Does not drain, exactly as the splat sibling does not: the whole material goes to the retire queue
        /// as one resource, so its array, UBO, set and any owned sampler are destroyed together once the GPU is done
        /// with them.</para></summary>
        public void UnloadTileGroundMaterial(TileGroundMaterialHandle h)
        {
            if (!h.IsValid || h.ListIndex >= _tileGroundMaterials.Count) return;
            // A null slot (already unloaded) is retired as nothing: the queue ignores it, so there is no guard here.
            _retired.Retire(_tileGroundMaterials[h.ListIndex]);
            _tileGroundMaterials[h.ListIndex] = null;
        }

        /// <summary>Free the GPU texture backing <paramref name="h"/> (and its lazily-created textured-billboard
        /// resource set) and null its slot. A <c>default</c>/Invalid handle is a no-op, and unloading an
        /// already-unloaded slot is one too. The slot is NOT recycled, so handles stay stable. Because a
        /// texture can be shared by several meshes/materials, the scene can't know who else references it - any mesh
        /// still bound to this texture must be unloaded first or simply not drawn afterwards (mirrors
        /// <see cref="UnloadSplatMaterial"/>). Without this, textures only free at <see cref="Dispose"/>, so a
        /// long-lived scene that streams or reloads textured assets leaks one native texture per load. Also a no-op
        /// once <see cref="Dispose"/> has run: Dispose already freed every texture and cleared the backing list, so
        /// a caller that still holds a handle (e.g. a world disposed after its owning scene) would otherwise index
        /// past the end of the now-empty list and get an <see cref="System.ArgumentOutOfRangeException"/> instead of a
        /// silent no-op (mirrors <see cref="UnloadSplatMaterial"/>'s guard).</summary>
        public void UnloadTexture(TextureHandle h)
        {
            if (!h.IsValid || h.ListIndex >= _textures.Count) return;
            int i = h.ListIndex;
            // The 1-mip LoadTexture path returns with its UpdateTexture staging copy still queued on the device
            // (the mips>1 path already flushes via Submit+WaitForIdle). Destroying the texture while that copy is
            // in flight is a use-after-free the driver may survive silently (hardware) or crash on (Mesa lavapipe's
            // async queue thread segfaults executing the stale copy). That copy is SUBMITTED by the time this
            // returns, so the retire queue's fence, submitted at a later frame boundary, sits behind it in the
            // submission stream and proves it finished - the same proof the drain here gave, without the stall.
            _retired.Retire(_textures[i], i < _texBillboardSets.Count ? _texBillboardSets[i] : null, null);
            _textures[i] = null;
            if (i < _texBillboardSets.Count) _texBillboardSets[i] = null;
            // The particle renderer caches per-atlas resource sets keyed by this list index, so drop them too. A
            // later load reusing this freed slot would otherwise bind the stale (disposed) texture. It is handed
            // the queue so those sets are retired as well: it would otherwise drain the device itself, which would
            // have left a stall on this path for any scene that draws particles at all.
            _particleRenderer.InvalidateTextureSets(_retired);
        }

        /// <summary>Free a skinned mesh's GPU buffers and release its slot. A <c>default</c> handle is a no-op. A
        /// stale handle throws.
        /// <para>Does not drain. This is the unload an MMO client runs constantly, one per avatar or corpse leaving
        /// interest range, so the buffers and both material sets are retired and destroyed at a later frame
        /// boundary instead of stalling the frame thread on every despawn.</para></summary>
        public void UnloadSkinnedMesh(SkinnedMeshHandle h)
        {
            if (h.Generation == 0) return;
            _skinnedSlots.Free(h.Index, h.Generation);
            if (_skinnedMeshes[h.Index] is { } e)
            {
                // Four resources, and the three-argument overload takes three: the second call is the GPU-skinning
                // material set, which LoadSkinnedInternal builds alongside the CPU-path one so UseGpuSkinning can
                // flip live. Both land in the same batch, since a batch is one frame's retirements.
                _retired.Retire(e.Vb, e.Ib, e.MaterialSet);
                _retired.Retire(e.SkinnedMaterialSet);
            }
            _skinnedMeshes[h.Index] = null;
            _skinnedCpuVerts[h.Index] = null;
        }

        /// <summary>Sealed retirement batches the scene is holding, each one frame's worth of unloads waiting on its
        /// fence. Bounded by the queue's own safety valve
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/425">#425</see>), which is what this exists to
        /// let a test assert. <see cref="RetiredResourceCount"/> is the resource-level view of the same holding.</summary>
        internal int RetiredBatchCount => _retired.SealedBatchCount;

        /// <summary>How many times the retire queue's safety valve has fallen back to a device drain because the GPU
        /// had not signalled <see cref="GpuRetireQueue.MaxSealedBatches"/> batches' worth of fences. Zero on a device
        /// that keeps up with the frame loop.</summary>
        internal int RetireValveDrains => _retired.ValveDrains;
    }
}
