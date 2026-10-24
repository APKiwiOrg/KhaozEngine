using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void DrawOpaqueRigidGeometry(IGpuCommandList cl)
    {
        using (ProfilePass(cl, "opaque.rigid", executed: _profiling is null || HasOpaqueCommands(0), fusedMotion: _res.MotionAllocated))
        {
            if (_instanceData.Count > 0)
            {
                foreach (var run in _runs)
                {
                    // Skip a run whose mesh was unloaded (stale handle): a destroyed entity may linger a frame.
                    // The instance data was uploaded contiguously, so skipping a run just leaves its slice undrawn.
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial >= 0 || mesh.TileGroundMaterial >= 0) continue;   // drawn in a ground pass below
                    // Draw only the visible contiguous sub-spans of this run (against the already-uploaded buffer).
                    uint spanStart = run.Start; uint spanLen = 0;
                    for (uint s = 0; s < run.Count; s++)
                    {
                        if (_instanceVisible[run.Start + s]) { if (spanLen == 0) spanStart = run.Start + s; spanLen++; }
                        else if (spanLen > 0) { _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, mesh.MaterialSet); CountMeshDraw(mesh.IndexCount, spanLen); spanLen = 0; }
                    }
                    if (spanLen > 0) { _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, mesh.MaterialSet); CountMeshDraw(mesh.IndexCount, spanLen); }
                }
            }
        }
        if (_instanceData.Count > 0)
        {
            using (ProfilePass(cl, "opaque.splat", executed: _profiling is null || HasOpaqueCommands(1), fusedMotion: _res.MotionAllocated && (_profiling is null || HasOpaqueCommands(1, visibleOnly: true)))) DrawSplatRuns(cl);
            using (ProfilePass(cl, "opaque.tile-ground", executed: _profiling is null || HasOpaqueCommands(2), fusedMotion: _res.MotionAllocated && (_profiling is null || HasOpaqueCommands(2, visibleOnly: true)))) DrawTileGroundRuns(cl);
        }
        else
        {
            SkipProfilePass(cl, "opaque.splat");
            SkipProfilePass(cl, "opaque.tile-ground");
        }
    }

    void DrawCpuSkinnedMain(IGpuCommandList cl)
    {
        SkipProfilePass(cl, "opaque.gpu-skinned");
        using var pass = ProfilePass(cl, "opaque.cpu-skinned", executed: _cpuSkinnedDraws.Count > 0,
            fusedMotion: _res.MotionAllocated && _drawnSkinnedInstances > 0);
        if (_cpuSkinnedDraws.Count == 0) return;
        // CPU path: the deformed geometry uploaded above, drawn without bones through the pipelines bound below.
        _model.BindCpuSkinnedPass(cl);   // the CPU-skinned pipeline (the rigid one, or its temporal variant)
        bool dissolveBound = false;
        for (int d = 0; d < _cpuSkinnedDraws.Count; d++)
        {
            var dr = _cpuSkinnedDraws[d];
            if (!dr.VisibleMain) continue;
            if (dr.Dissolve != dissolveBound)   // switch pipelines only when the dissolve state changes
            {
                if (dr.Dissolve) _model.BindDissolvePass(cl); else _model.BindCpuSkinnedPass(cl);
                dissolveBound = dr.Dissolve;
            }
            _model.DrawCpuSkinned(cl, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.BaseVertex, (uint)d, dr.MaterialSet);
            CountSkinnedDraw(dr.IndexCount);
        }
    }
}
