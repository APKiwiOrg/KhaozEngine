using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>WaterColumnResourceLifecycleTests exercises water disposal without releasing its borrowed model resources.</summary>
    internal void DisposeWaterForTests() => _water.Dispose();

    /// <summary>WaterColumnShadowBindingTests exercises the existing prepared-binding validation.</summary>
    internal void VerifyWaterOpticsForTests() => _water.VerifyOpticsFrame(_res, RelativeWaterPlanes(), Post.Water, EffectTimeSeconds);

    /// <summary>WaterColumnShadowBindingTests prepares the real optics path after replacing one shadow binding.</summary>
    internal void PrepareWaterOpticsForTests() =>
        _water.PrepareOpticsFrame(_res, new FramePrepare(Post.Water, RelativeWaterPlanes(), EffectTimeSeconds));

    /// <summary>WaterColumnShadowBindingTests replaces only the shadow inputs to exercise binding invalidation.</summary>
    internal void SetWaterShadowBindingsForTests(IGpuTexture atlas, IGpuSampler sampler, IGpuBuffer frame) =>
        _water.ColumnShadows.SetShadows(atlas, sampler, frame);

    /// <summary>WaterColumnShadowBindingTests records water alone to verify it never reuploads the shared model frame.</summary>
    internal void DrawPreparedWaterForTests(IGpuCommandList commands, ReadOnlySpan<WaterPlane> planes,
        Matrix4x4 view, Matrix4x4 cullView, Vector3 lightDirection, Color lightColor, Vector3 eye) =>
        _water.Draw(commands, _res, planes, view, cullView, lightDirection, lightColor, eye, Post.Water, Post.Sky, EffectTimeSeconds);

    /// <summary>WaterColumnShadowBindingTests supplies a shadow tail through the normal uniform packing path.</summary>
    internal void SetModelShadowUniformsForTests(ReadOnlySpan<Matrix4x4> matrices, int cascades, float texelStep,
        float constantBias, float slopeBias, float strength, float maxDistance, float border, float blend,
        ReadOnlySpan<float> normalOffsets) =>
        _model.SetShadowUniforms(matrices, cascades, texelStep, constantBias, slopeBias, strength, maxDistance,
            border, blend, normalOffsets);

    /// <summary>Water upload tests record the existing shared frame upload after controlling its lighting inputs.</summary>
    internal void UploadModelFrameForTests(IGpuCommandList commands, Matrix4x4 view, Vector3 eye,
        ReadOnlySpan<ModelRenderer.PointLightData> lights) => _model.SetFrameUniforms(commands, view, eye, Post, lights);

    /// <summary>WaterVolumeUploadTests exercises water and point-light assignment through their normal shared path.</summary>
    internal void BuildModelLightingClustersForTests(IGpuCommandList commands, ReadOnlySpan<ModelRenderer.PointLightData> lights,
        ReadOnlySpan<WaterVolumeRecord> water, Matrix4x4 view, Vector3 eye, Vector3 forward, Matrix4x4 projection, Vector3 origin) =>
        _model.BuildAndUploadLightingClusters(commands, lights, water, view, eye, forward, projection, origin);

    /// <summary>WaterVolumeUploadTests exercises the original light-only path's legacy frame bytes.</summary>
    internal void BuildModelPointLightClustersForTests(IGpuCommandList commands, ReadOnlySpan<ModelRenderer.PointLightData> lights,
        Matrix4x4 view, Vector3 eye, Vector3 forward, Matrix4x4 projection, Vector3 origin) =>
        _model.BuildAndUploadPointLightClusters(commands, lights, view, eye, forward, projection, origin);

    /// <summary>WaterVolumeUploadTests observes the real model's water assignment after a failed light assignment.</summary>
    internal WaterVolumeClusterDiagnostics ModelWaterVolumeDiagnosticsForTests => _model.WaterVolumeDiagnostics;

    /// <summary>WaterVolumeUploadTests observes fallback after the controlled light assignment failure.</summary>
    internal bool ModelUsesFullLightFallbackForTests(int submitted) => _model.PointLightClusterDiagnostics(submitted).UsesFullFallback;

    /// <summary>WaterVolumeUploadTests identifies the cluster upload's destination without borrowing the buffer.</summary>
    internal bool IsModelPointLightClusterBufferForTests(IGpuBuffer candidate) =>
        ReferenceEquals(_model.PointLightClusterBuffer, candidate);
}
