using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void PrepareGeometryShadowVisibility(out bool shadowMapActive, out int shadowCascadeCount,
        out bool hasPointShadowRequests)
    {
        using var phase = ProfileCpu("cpu.geometry.shadow-visibility");
        shadowMapActive = Post.Quality.Shadows.ResolveFor(_gd.Capabilities).Effective == ShadowMode.ShadowMap;
        shadowCascadeCount = 0;
        if (shadowMapActive)
        {
            shadowCascadeCount = ComputeShadowCascades();
            if (shadowCascadeCount == 0) shadowMapActive = false;   // degenerate camera: no shadows this frame
            for (int i = 0; i < shadowCascadeCount; i++)
                _shadowFrustums[i] = FrustumPlanes.Extract(_cascadeCpuVps[i]);
        }

        hasPointShadowRequests = PreparePointShadowRequests(ActiveCamera.Eye);
    }

    // Profiling-only visibility scans distinguish a skipped route from a zero-duration executed interval.
    bool HasOpaqueCommands(int route, bool visibleOnly = false)
    {
        foreach (var run in _runs)
        {
            if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation) || _meshes[run.Mesh.Index] is not { } mesh) continue;
            int kind = mesh.SplatMaterial >= 0 ? 1 : mesh.TileGroundMaterial >= 0 ? 2 : 0;
            if (kind != route) continue;
            if (kind == 1 && _splatMaterials[mesh.SplatMaterial] is null) continue;
            if (kind == 2 && _tileGroundMaterials[mesh.TileGroundMaterial] is null) continue;
            if (kind > 0 && !visibleOnly) return true;
            for (uint i = run.Start; i < run.Start + run.Count; i++) if (_instanceVisible[i]) return true;
        }
        return false;
    }
}
