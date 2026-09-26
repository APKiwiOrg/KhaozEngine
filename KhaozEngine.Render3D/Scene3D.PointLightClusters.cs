using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Builds and reports the point-light cluster grid at the lit-pass boundary.</summary>
public sealed partial class Scene3D
{
    /// <summary>Diagnostics for the point-light cluster grid built for the latest rendered frame.</summary>
    public PointLightClusterDiagnostics PointLightClusters { get; private set; }

    void BuildAndUploadPointLightClusters(IGpuCommandList cl, Matrix4x4 viewProjection, Vector3 eyeRender)
    {
        _model.BuildAndUploadPointLightClusters(cl, CollectionsMarshal.AsSpan(_lights), viewProjection, eyeRender,
            ActiveCamera.Forward, CurrentFrameView.Projection, _frameOrigin);
        PointLightClusters = _model.PointLightClusterDiagnostics(_lights.Count);
    }
}
