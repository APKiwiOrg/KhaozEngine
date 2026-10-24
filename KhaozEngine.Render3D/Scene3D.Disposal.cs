using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    public sealed partial class Scene3D
    {
        internal bool DisposalSafeForDependants { get; private set; }
        public void Dispose()
        {
            // Drain in-flight GPU work before destroying anything because uploads or draws may still be queued on
            // the device's async submission thread (Mesa lavapipe can segfault on destroyed resources).
            DisposeShadowReconfiguration();
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            int ordinal = 0;
            void Release(string kind, Action dispose)
            {
                try { dispose(); }
                catch (Exception error)
                {
                    if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                    else failures.Add(new("renderer.session.dispose.scene." + kind + "." + ordinal, error));
                }
                ordinal++;
            }
            Release("drain", _gd.WaitForIdle);
            if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
            Release("retirement", _retired.Dispose);
            if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
            DisposalSafeForDependants = true;
            Release("DisposeFoliage", DisposeFoliage);
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
            Release("water", _water.Dispose);
            Release("overlayMeshes", _overlayMeshes.Dispose);
            Release("silhouettes", _silhouettes.Dispose);
            Release("targetOutlines", _targetOutlines.Dispose);
            Release("res", _res.Dispose);
            foreach (var m in _meshes)
                if (m is { } mesh)
                {
                    Release("meshVb", mesh.Vb.Dispose);
                    Release("meshOutlineNormalVb", mesh.OutlineNormalVb.Dispose);
                    Release("meshIb", mesh.Ib.Dispose);
                    Release("meshMaterialSet", () => mesh.MaterialSet?.Dispose());
                    Release("meshOutlineMaterialSet", () => mesh.OutlineMaterialSet?.Dispose());
                    Release("meshShadowCutoutSet", () => mesh.ShadowCutoutSet?.Dispose());
                }
            foreach (var m in _skinnedMeshes)
                if (m is { } e)
                {
                    Release("eVb", e.Vb.Dispose);
                    Release("eIb", e.Ib.Dispose);
                    Release("eMaterialSet", () => e.MaterialSet?.Dispose());
                    Release("eSkinnedMaterialSet", () => e.SkinnedMaterialSet?.Dispose());
                    Release("eOutlineMaterialSet", () => e.OutlineMaterialSet?.Dispose());
                }
            foreach (var set in _texBillboardSets) Release("texturedBillboardSet", () => set?.Dispose());
            _texBillboardSets.Clear();
            foreach (var texture in _textures) Release("texture", () => texture?.Dispose());
            _textures.Clear();
            Release("DisposeSplatMaterials", DisposeSplatMaterials);
            Release("DisposeModelWithInteriorCutBorrowers", DisposeModelWithInteriorCutBorrowers);
            Release("DisposeMotionVectorsView", DisposeMotionVectorsView);
            Release("DisposeTileGroundAndPointShadowResources", DisposeTileGroundAndPointShadowResources);
            Release("DisposeTemporalResolve", DisposeTemporalResolve);
            Release("atmosphere", _atmosphere.Dispose);
            if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
        }
    }
}
