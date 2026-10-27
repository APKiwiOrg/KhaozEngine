using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    public sealed partial class Scene3D
    {
        bool _disposalComplete;
        bool _pointShadowOwnersReleased;
        internal bool DisposalSafeForDependants { get; private set; }
        public void Dispose()
        {
            // Drain in-flight GPU work before destroying anything because uploads or draws may still be queued on
            // the device's async submission thread (Mesa lavapipe can segfault on destroyed resources).
            DisposeShadowReconfiguration();
            if (_disposalComplete) return;
            DisposalSafeForDependants = false;
            var disposal = new ResourceDisposal();
            if (!disposal.Release("drain", _gd.WaitForIdle)) disposal.ThrowIfFailed();
            // Optical fields retire into the queue before it flushes. Sharing the stage keeps later ordinals stable.
            // A retirement.<n> failure is therefore either the optical field invalidation or the queue flush.
            if (!disposal.Release("retirement", () => { _water.Optics.Dispose(); _retired.Dispose(); })) disposal.ThrowIfFailed();
            DisposalSafeForDependants = true;
            _disposalComplete = true;
            bool Release(string kind, Action? release) => disposal.Release(kind, release);
            DisposeFoliage(disposal);
            Release("post", _post.Dispose);
            Release("lines", _lines.Dispose);
            Release("depthLines", _depthLines.Dispose);
            Release("fills", _fills.Dispose);
            Release("billboards", _billboards.Dispose);
            Release("transitions", _transitions.Dispose);
            Release("texBillboards", _texBillboards.Dispose);
            Release("beams", _beams.Dispose);
            Release("trails", _trails.Dispose);
            Release("decalRenderer", _decalRenderer.Dispose);
            Release("particleRenderer", _particleRenderer.Dispose);
            Release("distortionRenderer", _distortionRenderer.Dispose);
            Release("sky", _sky.Dispose);
            Release("starfield", _starfield.Dispose);
            Release("water", () =>
            {
                try { _water.Dispose(); }
                finally { _waterColumnColor?.Dispose(); }
            });
            Release("overlayMeshes", _overlayMeshes.Dispose);
            Release("silhouettes", _silhouettes.Dispose);
            Release("targetOutlines", _targetOutlines.Dispose);
            Release("res", _res.Dispose);
            // Match the external shadow receivers defined by CollectLiveMaterialSets.
            bool shadowReceiversReleased = true;
            foreach (var m in _meshes)
                if (m is { } mesh)
                {
                    Release("meshVb", mesh.Vb.Dispose);
                    Release("meshOutlineNormalVb", mesh.OutlineNormalVb.Dispose);
                    Release("meshIb", mesh.Ib.Dispose);
                    shadowReceiversReleased &= Release("meshMaterialSet", () => mesh.MaterialSet?.Dispose());
                    Release("meshOutlineMaterialSet", () => mesh.OutlineMaterialSet?.Dispose());
                    Release("meshShadowCutoutSet", () => mesh.ShadowCutoutSet?.Dispose());
                }
            foreach (var m in _skinnedMeshes)
                if (m is { } e)
                {
                    Release("eVb", e.Vb.Dispose);
                    Release("eIb", e.Ib.Dispose);
                    shadowReceiversReleased &= Release("eMaterialSet", () => e.MaterialSet?.Dispose());
                    shadowReceiversReleased &= Release("eSkinnedMaterialSet", () => e.SkinnedMaterialSet?.Dispose());
                    Release("eOutlineMaterialSet", () => e.OutlineMaterialSet?.Dispose());
                }
            foreach (var set in _texBillboardSets) Release("texturedBillboardSet", () => set?.Dispose());
            _texBillboardSets.Clear();
            // Per-mesh sets name these scene-loaded maps. Ground bundles own their maps separately.
            if (shadowReceiversReleased)
            {
                foreach (var texture in _textures) Release("texture", () => texture?.Dispose());
                _textures.Clear();
            }
            shadowReceiversReleased &= DisposeSplatMaterials(disposal);
            shadowReceiversReleased &= DisposeTileGroundMaterials(disposal);
            DisposeModelWithInteriorCutBorrowers(disposal, shadowReceiversReleased);
            DisposeMotionVectorsView(disposal);
            DisposePointShadowResources(disposal, shadowReceiversReleased);
            DisposeTemporalResolve(disposal);
            if (_pointShadowOwnersReleased) Release("atmosphere", _atmosphere.Dispose);
            disposal.ThrowIfFailed();
        }
    }
}
