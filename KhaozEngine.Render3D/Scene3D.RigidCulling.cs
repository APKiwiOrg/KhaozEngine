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
        /// <see cref="_drawnInstances"/> / <see cref="_culledInstances"/>. Allocation-free on the hot path (the mask
        /// grows, never per-frame allocated). The shadow depth pass does not consult this mask.
        /// </summary>
        void ComputeMainPassVisibility(in FrustumPlanes frustum)
        {
            int total = _instanceData.Count;
            if (_instanceVisible.Length < total)
                _instanceVisible = new bool[Math.Max(total, _instanceVisible.Length * 2)];

            _drawnInstances = 0;
            _culledInstances = _earlyCulledInstances;
            if (total == 0) return;

            if (!FrustumCulling)
            {
                for (int i = 0; i < total; i++) _instanceVisible[i] = true;
                _drawnInstances = total;
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
                    _instanceVisible[slot] = visible;
                    if (visible) _drawnInstances++; else _culledInstances++;
                }
            }
        }

    }
}
