using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The overlay-mesh half of <see cref="Scene3D"/>: the queued translucent unlit proxies (collision proxies
    /// first, nav / AoI / chunk-bounds layers later) that draw into the model MRT after the geometry passes.
    /// <para>
    /// Split out of <c>Scene3D.cs</c> when the pass grew its pack-then-upload phase for
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see>. The pass is one coherent thing
    /// (its own queue, its own sort, its own renderer) rather than an arbitrary slice of the frame loop.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>
        /// Overlay meshes (collision proxies etc.): after the model pass wrote depth (meshes + textured billboards
        /// + beams), draw the queued translucent unlit proxies into the SAME model FB with the depth test on (no
        /// write), so a proxy is occluded by nearer geometry yet blends over farther geometry, then flows through
        /// the post chain with the rest of the model pass. Fully skipped when nothing is queued, so a frame with no
        /// overlay draws renders byte-identical to before this pass existed.
        /// </summary>
        void DrawOverlayMeshes(IGpuCommandList cl, Matrix4x4 vp)
        {
            if (_overlayMeshDraws.Count == 0) return;

            cl.SetFramebuffer(_res.ModelFB);
            int on = _overlayMeshDraws.Count;
            _overlayMeshes.EnsureCapacity(on);
            _overlayMeshes.BeginFrame(GpuClip.Correct(vp, _gd.Capabilities));
            // Sort the overlay proxies back-to-front by their world-origin view depth: they alpha-blend with
            // depth-write off, so overlapping proxies must composite far-to-near (the pre-sort submission order
            // blended wrong when a near proxy was queued before a far one behind it). Uses each draw's own UBO
            // slot indexed by the sorted position k, so the slot assignment stays unique.
            _sortCenters.Clear();
            for (int i = 0; i < on; i++) _sortCenters.Add(_overlayMeshDraws[i].World.Translation);
            TransparencySort.ComputeOrder(CollectionsMarshal.AsSpan(_sortCenters), on,
                ActiveCamera.Eye, ActiveCamera.Forward, ref _sortKeys, ref _sortOrder);
            // Two phases on purpose. Every slot is packed into the renderer's CPU image first and uploaded in ONE
            // whole-buffer write, because a partial uniform write between the draws is a blocking Map on D3D11
            // (#408); the draws are recorded after it, against bytes already on the way.
            for (int k = 0; k < on; k++)
            {
                var (handle, world) = _overlayMeshDraws[_sortOrder[k]];
                if (!_slots.IsValid(handle.Index, handle.Generation)) continue;   // stale handle: skip
                var m = _meshes[handle.Index];
                if (m is not { } mesh) continue;
                _overlayMeshes.Enqueue(mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, k, ToRender(world));
                _frameStats.DrawCalls++;
            }
            _overlayMeshes.Flush(cl);
        }
    }
}
