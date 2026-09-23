using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns the fixed clustered-light index buffer shared by every lit receiver.</summary>
internal sealed partial class ModelRenderer
{
    PointLightClusterBuilder _pointLightClusters = null!;
    IGpuBuffer _pointLightClusterBuffer = null!;
    Vector4 _clusterDepth;
    Vector4 _clusterCamera;

    internal IGpuBuffer PointLightClusterBuffer => _pointLightClusterBuffer;
    internal int PointLightClusterOverflowedCount => _pointLightClusters.OverflowedClusters;
    internal int PointLightClusterReferenceCount => _pointLightClusters.LightReferenceCount;

    internal PointLightClusterDiagnostics PointLightClusterDiagnostics(int submittedLightCount)
    {
        if (submittedLightCount == 0)
            return new PointLightClusterDiagnostics(0, PointLightClusterBuilder.ClusterCount, 0, 0,
                PointLightClusterProjection.Invalid, 0f, 0f);
        PointLightClusterProjection projection = _pointLightClusters.Depth.W switch
        {
            > 0.5f => PointLightClusterProjection.Perspective,
            >= 0f => PointLightClusterProjection.Orthographic,
            _ => PointLightClusterProjection.Invalid,
        };
        return new PointLightClusterDiagnostics(submittedLightCount, PointLightClusterBuilder.ClusterCount,
            _pointLightClusters.LightReferenceCount, _pointLightClusters.OverflowedClusters, projection,
            _pointLightClusters.Depth.X, _pointLightClusters.Depth.Y);
    }

    void CreatePointLightClusters(IGpuResourceFactory factory)
    {
        _pointLightClusters = new PointLightClusterBuilder();
        _pointLightClusterBuffer = factory.CreateBuffer(new GpuBufferDescription(
            checked((uint)(PointLightClusterBuilder.ImageUIntCount * sizeof(uint))),
            GpuBufferUsage.StructuredBufferReadOnly, 16));
    }

    internal void BuildAndUploadPointLightClusters(IGpuCommandList cl, ReadOnlySpan<PointLightData> lights,
        Matrix4x4 viewProjection, Vector3 eyeRender, Vector3 forward, Matrix4x4 projection,
        Vector3 renderOrigin)
    {
        if (lights.IsEmpty)
        {
            _clusterDepth = new Vector4(0f, 0f, 0f, -1f);
            _clusterCamera = Vector4.Zero;
            _frameImageDirty = true;
            return;
        }
        Matrix4x4 correctedViewProjection = GpuClip.Correct(viewProjection, _gd.Capabilities);
        _pointLightClusters.Build(lights, correctedViewProjection, eyeRender, forward, projection, renderOrigin);
        _clusterDepth = _pointLightClusters.Depth;
        _clusterCamera = _pointLightClusters.CameraForward;
        // Only the used prefix, the headers plus this frame's indices, rather than the whole 940,032-byte image.
        cl.UpdateBuffer<uint>(_pointLightClusterBuffer, 0,
            new ReadOnlySpan<uint>(_pointLightClusters.Image, 0, _pointLightClusters.UsedUIntCount));
        _frameImageDirty = true;
    }
}
