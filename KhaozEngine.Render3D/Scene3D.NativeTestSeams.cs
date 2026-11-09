using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    internal object BoundedWaterBufferIdentityForTests(MeshHandle mesh) => _water.DomainBufferIdentityForTests(mesh);
    internal static bool BufferMatchesIdentityForTests(IGpuBuffer buffer, object identity) => ReferenceEquals(TestObservationIdentity.For(buffer), identity);
    internal IReadOnlyList<(MeshHandle Mesh, RenderAnchor Anchor, Matrix4x4 LocalTransform, float? Opacity,
        float? SwellAmplitude, float? SwellWavelength, WaterWaveSource? WaveSource, Color? DeepColor)> BoundedWaterForTests =>
        Array.AsReadOnly(_boundedWater.Select(w => (w.Mesh, w.Anchor, w.LocalTransform, w.Look.Opacity,
            w.Look.SwellAmplitude, w.Look.SwellWavelength, w.Look.WaveSource, w.Look.DeepColor)).ToArray());
    internal IReadOnlyList<ModelRenderer.PointLightData> LightsForTests => Array.AsReadOnly(_lights.ToArray());
    internal IReadOnlyList<ModelRenderer.InstanceData> RelativeInstancesForTests => Array.AsReadOnly(_instanceDataRelative.ToArray());
    internal void ResetCaptureFrameIndexForTests() => _frameIndex = 0;
    internal IReadOnlyList<RenderAnchor?> FoliageAnchorsForTests => Array.AsReadOnly(_foliageBatches.Select(b => b.PreparedAnchor).ToArray());
    internal int FoliageDrawCountForTests => _foliageDraws.Count;
    internal void OmitFirstFoliageDrawForTests() => _foliageDraws.RemoveAt(0);
    internal IReadOnlyList<int> FoliageCacheCountsForTests => Array.AsReadOnly(new[]
    {
        _foliagePointSources.Count, _foliagePointPending.Count, _foliagePointSourceOrder.Count,
        _foliagePointFaceRuns.Count, _foliageDraws.Count, _foliageShadowDraws.Count,
    });
    internal int TileGroundMaterialSlotsForTests => _tileGroundMaterials.Count;
    internal object? TileGroundMaterialIdentityForTests(int index) => _tileGroundMaterials[index] is { } value ? TestObservationIdentity.For(value) : null;
}
