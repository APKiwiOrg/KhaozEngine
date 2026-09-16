using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    public sealed partial class Scene3D
    {
        bool[] _retainedInstances = Array.Empty<bool>();
        int _earlyCulledInstances;

        // Keep every caster, regardless of the shadow tier or camera. Explicit opt-outs have no depth-pass
        // consumer, so rejected geometry can avoid packing, upload and gaps in the visible model runs (#836).
        // Queue transforms and the frustum stay absolute. Only UploadInstancesRelative changes render space.
        // ONLY !CastsShadows instances are tested here, and that is load-bearing rather than incidental: the depth
        // pass needs offscreen casters, so anything that still casts has to survive this walk whatever the camera
        // can see. A SHADOW-ONLY instance (#974) is a caster, so it is never early-rejected here, and it must not
        // be: rejecting it would drop the shadow of geometry that is hidden precisely because it is between the
        // camera and what it shades.
        internal ReadOnlySpan<bool> CullOptedOutInstances(in FrustumPlanes frustum)
        {
            _earlyCulledInstances = 0;
            if (!FrustumCulling) return default;
            IReadOnlyList<SceneInstances.Instance> items = _instances.Items;
            if (_retainedInstances.Length < items.Count)
                _retainedInstances = new bool[Math.Max(items.Count, _retainedInstances.Length * 2)];

            MeshHandle previous = default;
            Mesh mesh = default;
            bool havePrevious = false, haveMesh = false, ground = false;
            for (int i = 0; i < items.Count; i++)
            {
                SceneInstances.Instance instance = items[i];
                bool keep = true;
                if (!instance.CastsShadows)
                {
                    // Dense layers commonly submit many consecutive instances of the same mesh. Bounds and
                    // generation validity stay fixed during this walk, so resolve that handle once per stretch.
                    if (!havePrevious || instance.Mesh.Index != previous.Index || instance.Mesh.Generation != previous.Generation)
                    {
                        previous = instance.Mesh;
                        havePrevious = true;
                        haveMesh = _slots.IsValid(previous.Index, previous.Generation)
                            && _meshes[previous.Index] is { };
                        if (haveMesh) mesh = _meshes[previous.Index]!.Value;
                        ground = haveMesh && (mesh.SplatMaterial >= 0 || mesh.TileGroundMaterial >= 0);
                    }
                    // A stale handle stays conservative, exactly like the later main-pass mask. The draw loop
                    // skips it by generation, so culling never borrows bounds from a replacement slot occupant.
                    if (haveMesh) keep = IntersectsMainPass(mesh.Bounds, instance.World, ground, frustum);
                }
                _retainedInstances[i] = keep;
                if (!keep) _earlyCulledInstances++;
            }
            return _retainedInstances.AsSpan(0, items.Count);
        }

        // Shared by early opt-out rejection and the grouped main-pass mask. Ground under a pure translation
        // uses its exact world AABB. Rotated or scaled ground and all models retain the existing sphere rule.
        internal static bool IntersectsMainPass(in MeshBounds bounds, in Matrix4x4 world,
            bool ground, in FrustumPlanes frustum)
        {
            if (ground && IsPureTranslation(world, out Vector3 translation))
                return frustum.IntersectsAabb(bounds.Min + translation, bounds.Max + translation);
            bounds.WorldSphere(world, out Vector3 center, out float radius);
            return frustum.IntersectsSphere(center, radius);
        }

        /// <summary>
        /// Fill <see cref="_instanceVisible"/> for this frame's grouped instance buffer: true where the instance's
        /// world-space bounding sphere is (conservatively) inside <paramref name="frustum"/>. When
        /// <see cref="FrustumCulling"/> is off every slot is visible (parity path). Also updates
        /// <see cref="_drawnInstances"/> / <see cref="_culledInstances"/> / <see cref="_shadowOnlyInstances"/>.
        /// Allocation-free on the hot path (the mask grows, never per-frame allocated). The shadow depth pass does
        /// not consult this mask, which is what lets a SHADOW-ONLY slot (issue #974) be false here and still record
        /// depth: it is withheld from the colour pass on BOTH paths below, and counted in neither the drawn nor the
        /// culled total, because it was never a candidate the camera could reject.
        /// </summary>
        void ComputeMainPassVisibility(in FrustumPlanes frustum)
        {
            int total = _instanceData.Count;
            if (_instanceVisible.Length < total)
                _instanceVisible = new bool[Math.Max(total, _instanceVisible.Length * 2)];

            _drawnInstances = 0;
            _culledInstances = _earlyCulledInstances;
            _shadowOnlyInstances = 0;
            if (total == 0) return;

            if (!FrustumCulling)
            {
                // Parity path: every slot the camera would have kept is visible, and a shadow-only slot is still
                // withheld, so turning culling off proves the cull is pixel-neutral rather than revealing roofs.
                for (int i = 0; i < total; i++) Record(i, ClassifyMainPassSlot(ShadowOnlyAt(i), insideFrustum: true));
                return;
            }

            // Walk runs so each slot's mesh bounds come from its run's mesh. The world matrix is the uploaded
            // instance model matrix. A stale-handle run (mesh unloaded this frame) is conservatively kept visible
            // (the draw loop skips it anyway by the same stale check), so culling never diverges from the draw.
            foreach (var run in _runs)
            {
                bool valid = _slots.IsValid(run.Mesh.Index, run.Mesh.Generation);
                Mesh mesh = default; bool haveMesh = false;
                if (valid && _meshes[run.Mesh.Index] is { } m) { mesh = m; haveMesh = true; }
                // Ground chunks (splat terrain, and a tile world's region planes) draw chunk-local under a PURE
                // TRANSLATION (their region origin), so their local AABB offset by that translation IS the world
                // AABB: cull them with the tighter AABB test (a flat chunk's bounding sphere is far too
                // conservative), and the offset is exact. Props/models use the world-sphere test (cheap under
                // arbitrary scale/rotation), and so does a ground instance under a rotation or a scale.
                bool materialPassPlaced = haveMesh && (mesh.SplatMaterial >= 0 || mesh.TileGroundMaterial >= 0);
                for (uint s = 0; s < run.Count; s++)
                {
                    int slot = (int)(run.Start + s);
                    bool visible = true;
                    // Explicit non-casters survived CullOptedOutInstances against this same absolute frustum
                    // before grouping. Reuse that result instead of testing every visible blade twice.
                    if (haveMesh && _instanceCastKinds[slot] != ShadowCastKind.None)
                    {
                        Matrix4x4 world = _instanceData[slot].Model;
                        visible = IntersectsMainPass(mesh.Bounds, world, materialPassPlaced, frustum);
                    }
                    Record(slot, ClassifyMainPassSlot(ShadowOnlyAt(slot), visible));
                }
            }
        }

        // The shadow-only flag for one uploaded slot. An absent or short list (a GroupInstances call that omitted
        // it) reads as "nothing is shadow-only", which is the pre-policy shape and the safe direction to fail in.
        bool ShadowOnlyAt(int slot) => slot < _instanceShadowOnly.Count && _instanceShadowOnly[slot];

        // Write one slot's mask bit and charge it to exactly one counter.
        void Record(int slot, MainPassSlot kind)
        {
            _instanceVisible[slot] = kind == MainPassSlot.Drawn;
            if (kind == MainPassSlot.Drawn) _drawnInstances++;
            else if (kind == MainPassSlot.Culled) _culledInstances++;
            else _shadowOnlyInstances++;
        }
    }
}
