using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Builds and reports the point-light cluster grid at the lit-pass boundary.</summary>
public sealed partial class Scene3D
{
    /// <summary>Diagnostics for the point-light cluster grid built for the latest rendered frame.</summary>
    public PointLightClusterDiagnostics PointLightClusters { get; private set; }

    /// <summary>
    /// Build the cluster grid against the RASTER view-projection, jitter included. A lit fragment finds its cluster by
    /// projecting its world position through the frame block's ViewProj (<c>pointLightClusterForFragment</c> in
    /// ShaderSources.Lighting.cs), which is the jittered matrix, so a grid built on the unjittered one would hand a
    /// fragment within half a pixel of a tile edge the neighbouring tile's lights. The projection only tells the
    /// builder perspective from orthographic, which jitter never changes, so it stays the unjittered one.
    /// </summary>
    void BuildAndUploadPointLightClusters(IGpuCommandList cl, Matrix4x4 rasterViewProjection, Vector3 eyeRender)
    {
        _model.BuildAndUploadPointLightClusters(cl, CollectionsMarshal.AsSpan(_lights), rasterViewProjection, eyeRender,
            ActiveCamera.Forward, _currentFrameView.Projection, _frameOrigin);
        PointLightClusters = _model.PointLightClusterDiagnostics(_lights.Count);
    }
}
