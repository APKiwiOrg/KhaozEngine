using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly WaterCausticPlan _waterCausticPlan = new();
    readonly WaterSettings _waterCausticScratch = new();
    WaterCausticRenderer? _waterCaustics;

    /// <summary>Caustic opt-ins and map layers of the latest rendered frame. Begin and preparation preserve it.</summary>
    public WaterCausticDiagnostics WaterCausticDiagnostics { get; private set; }

    /// <summary>Caustic lifecycle tests observe the map atlas, null until a body participates.
    /// Borrowed for observation. Never dispose or mutate it.</summary>
    internal IGpuTexture? WaterCausticMapForTests => _waterCaustics?.MapTexture;

    /// <summary>Caustic lifecycle tests observe the surface field atlas, null until a body participates.
    /// Borrowed for observation. Never dispose or mutate it.</summary>
    internal IGpuTexture? WaterSurfaceFieldForTests => _waterCaustics?.FieldTexture;

    /// <summary>The window of the latest built plan, default when Begin cleared it or nothing participated.</summary>
    internal WaterSurfaceWindow WaterSurfaceWindowForTests => _waterCausticPlan.Window;

    /// <summary>
    /// Size the atlases for every queued participant, ignoring the window, which can only over-allocate. Preparation
    /// and render both run it, and the second is a no-op unless settings changed after preparation made another body
    /// participate, so a planned layer always has an atlas column. The map comparison rather than the growth result is
    /// what retries a lit set replacement that failed on an earlier frame, and the old map stays alive until then.
    /// </summary>
    void EnsureWaterCausticCapacity()
    {
        int required = WaterCausticPlan.RequiredLayers(CollectionsMarshal.AsSpan(_waterPlanes), Post.Water, _waterCausticScratch);
        if (required > 0) (_waterCaustics ??= new WaterCausticRenderer(_gd, _retired)).EnsureLayers(required);
        if (_waterCaustics?.MapTexture is not { } map || ReferenceEquals(map, _model.CausticMap)) return;
        var liveSets = new List<IGpuResourceSet>();
        CollectLiveMaterialSets(liveSets);
        _model.SetCausticMap(map, liveSets, CommitMaterialSets);
        _waterCaustics.RetireReplacedMap();
    }

    /// <summary>The caustic pass records ahead of the cluster upload, whose records and window block read this frame's
    /// plan, and both precede the model pass that samples the map.</summary>
    void RecordWaterCausticsAndLightClusters(IGpuCommandList cl, Matrix4x4 rasterViewProjection, Vector3 eye)
    {
        // Planned on the unjittered view, so the window does not shake with the temporal sequence.
        _waterCausticPlan.Build(RelativeWaterPlanes(), Post.Water, Post.LightDirection, eye, ActiveCamera.Forward,
            _currentFrameView.ViewProjection, ClusterFrameGrid.IsOrthographic(_currentFrameView.Projection), _frameOrigin);
        WaterCausticDiagnostics = _waterCausticPlan.Diagnostics;
        using (ProfilePass(cl, "water.caustics", executed: _waterCausticPlan.Active))
            if (_waterCausticPlan.Active)
            {
                _waterCaustics!.Record(cl, _waterCausticPlan, RelativeWaterPlanes(), Post.Water, Post.LightDirection,
                    EffectTimeSeconds, _frameOrigin);
                _frameStats.DrawCalls += _waterCausticPlan.Bodies.Length * WaterCausticRenderer.DrawCallsPerBody;
            }
        using (ProfilePass(cl, "opaque.light-clusters")) BuildAndUploadLightingClusters(cl, rasterViewProjection, eye);
    }

    // The receivers address the atlas, so W is its capacity rather than the frame's used layers. An inactive frame
    // passes the default, which is what leaves the block out of the upload.
    Vector4 WaterCausticWindowVector()
    {
        if (!_waterCausticPlan.Active) return default;
        WaterSurfaceWindow window = _waterCausticPlan.Window;
        return new Vector4(window.MinX, window.MinZ, 1f / WaterSurfaceWindow.ExtentMetres, _waterCaustics!.AtlasLayers);
    }
}
