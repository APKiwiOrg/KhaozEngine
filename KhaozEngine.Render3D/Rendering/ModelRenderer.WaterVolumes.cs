using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>One camera grid for lit receivers, with independent point-light and water-volume counts.</summary>
internal sealed partial class ModelRenderer
{
    WaterVolumeClusterBuilder _waterVolumeClusters = null!;
    IGpuBuffer _waterVolumeBuffer = null!;
    internal WaterVolumeClusterDiagnostics WaterVolumeDiagnostics { get; private set; }

    static AtmosphereBindings CreateLitAtmosphere(IGpuDevice device)
        => new(LitReceiverRequirements.RequireSupportedDevice(device));

    void CreateWaterVolumes(IGpuResourceFactory factory)
    {
        _waterVolumeClusters = new WaterVolumeClusterBuilder();
        _waterVolumeBuffer = factory.CreateBuffer(new GpuBufferDescription(
            checked((uint)(WaterVolumeClusterBuilder.ImageUIntCount * sizeof(uint))),
            GpuBufferUsage.StructuredBufferReadOnly, 16));
    }

    static GpuResourceLayoutElement[] AppendWaterVolumeLayout(params GpuResourceLayoutElement[] existing) =>
        [.. existing, new("WaterVolumes", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Fragment)];

    IGpuBindableResource[] AppendWaterVolumeResources(params IGpuBindableResource[] existing) =>
        [.. existing, _waterVolumeBuffer];

    internal void BuildAndUploadLightingClusters(IGpuCommandList cl, ReadOnlySpan<PointLightData> lights,
        ReadOnlySpan<WaterVolumeRecord> waterRecords, Matrix4x4 viewProjection, Vector3 eyeRender,
        Vector3 forward, Matrix4x4 projection, Vector3 renderOrigin)
    {
        // Reset before either early return. Stale buffer contents are unreachable after an opt-out transition.
        WaterVolumeDiagnostics = default;
        _clusterDepth = new Vector4(0f, 0f, 0f, -1f);
        _clusterCamera = Vector4.Zero;
        _frameImageDirty = true;
        if (lights.IsEmpty && waterRecords.IsEmpty) return;

        Matrix4x4 correctedViewProjection = GpuClip.Correct(viewProjection, _gd.Capabilities);
        _clusterFrameGrid.TryPrepare(correctedViewProjection, eyeRender, forward, projection, renderOrigin);
        _clusterDepth = _clusterFrameGrid.Depth;
        _clusterCamera = _clusterFrameGrid.CameraForward;
        if (!lights.IsEmpty) BuildAndUploadPointLightClusters(cl, lights, renderOrigin);
        if (waterRecords.IsEmpty)
        {
            // Preserve the legacy light-only bytes, including an invalid assignment's full-list sentinel.
            // Mixed frames retain the prepared camera grid. Failed light assignments already flag every header.
            if (!lights.IsEmpty)
            {
                _clusterDepth = _pointLightClusters.Depth;
                _clusterCamera = _pointLightClusters.CameraForward;
            }
            return;
        }

        _waterVolumeClusters.Build(waterRecords, _clusterFrameGrid);
        WaterVolumeDiagnostics = new(_waterVolumeClusters.RecordCount, _waterVolumeClusters.DroppedRecords,
            _waterVolumeClusters.ReferenceCount, _waterVolumeClusters.OverflowedClusters);
        _clusterCamera.W = _waterVolumeClusters.RecordCount;
        if (_waterVolumeClusters.UsedUIntCount == 0) return;
        cl.UpdateBuffer<uint>(_waterVolumeBuffer, 0,
            _waterVolumeClusters.Image.AsSpan(0, _waterVolumeClusters.UsedUIntCount));
    }
}
