using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The silhouette half of <see cref="Scene3D"/>: the queued per-entity inverted-hull highlights (a clicked
    /// monster, a selected prop) that draw into the model MRT after the overlay meshes. The overlay pass
    /// partial's shape, one coherent pass with its own queue and renderer.
    /// </summary>
    public sealed partial class Scene3D
    {
        // Per-entity silhouette draws (inverted hulls): queued in submission order, flushed into the model FB
        // right after the overlay meshes. Cleared each Begin().
        readonly List<(MeshHandle Mesh, Matrix4x4 World, Color Color, float Width)> _silhouetteDraws = new();

        /// <summary>Queue one per-entity SILHOUETTE: the mesh re-drawn as an inverted hull, vertices pushed
        /// along their world normals by <paramref name="widthMetres"/>, front faces culled, in a flat
        /// <paramref name="color"/> (alpha blends), depth tested without writing. The highlight for a clicked
        /// monster or a selected prop. The whole-scene edge post keeps the Outline name on
        /// <see cref="PixelPostProcessSettings"/>.</summary>
        /// <remarks>The normal push assumes a UNIFORM scale in <paramref name="world"/> (the shader rotates
        /// the normal by the world's upper 3x3 and normalizes, not the inverse transpose), which every prop
        /// and body transform in the engine satisfies. A nonuniform scale bends the hull off the surface:
        /// bake the scale into the mesh instead.</remarks>
        public void DrawMeshSilhouette(MeshHandle mesh, Matrix4x4 world, Color color, float widthMetres) =>
            _silhouetteDraws.Add((mesh, world, color, widthMetres));

        /// <summary>Count of silhouette draws queued this frame. Internal: lets tests assert <see cref="Begin"/>
        /// clears the queue and <see cref="DrawMeshSilhouette"/> enqueues.</summary>
        internal int SilhouetteDrawCount => _silhouetteDraws.Count;

        /// <summary>
        /// Silhouettes: after the overlay meshes, re-draw each queued mesh as an inverted hull (front faces
        /// culled, vertices pushed along world normals) in its flat colour. Depth is tested and never written,
        /// so the rim is occluded by nearer geometry and never occludes the passes that follow. No sort: the
        /// rims are thin, flat-coloured and rarely overlap, and submission order is stable. Fully skipped when
        /// nothing is queued, so a silhouette-free frame renders byte-identical to before this pass existed.
        /// </summary>
        void DrawSilhouettes(IGpuCommandList cl, Matrix4x4 vp)
        {
            if (_silhouetteDraws.Count == 0) return;

            cl.SetFramebuffer(_res.ModelFB);
            int n = _silhouetteDraws.Count;
            _silhouettes.EnsureCapacity(n);
            _silhouettes.BeginFrame(GpuClip.Correct(vp, _gd.Capabilities));
            for (int i = 0; i < n; i++)
            {
                var (handle, world, color, width) = _silhouetteDraws[i];
                if (!_slots.IsValid(handle.Index, handle.Generation)) continue;   // stale handle: skip
                var m = _meshes[handle.Index];
                if (m is not { } mesh) continue;
                _silhouettes.Enqueue(mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, i, ToRender(world),
                    color, width);
                _frameStats.DrawCalls++;
            }
            _silhouettes.Flush(cl);
        }
    }
}
