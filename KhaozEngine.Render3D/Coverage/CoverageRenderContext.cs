using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Capture identity. The owning scene validates it against the current staged request and render invocation.</summary>
public readonly record struct CoverageRenderStamp(ulong SceneIdentity, ulong FrameGeneration, ulong ViewGeneration,
    MotionKey ActorKey, ulong PreparationId);

/// <summary>Actual model-then-pass position inputs. Bounds contain the uploaded local positions. The nonnegative
/// perturbation bounds every additional shader world-position term and its arithmetic in each coordinate.
/// Intentional clip-depth remapping and raster precision are outside this position-roundoff contract.</summary>
public readonly record struct CoverageRenderPass(uint CommandId, ulong EquipmentInstanceId, Matrix4x4 Model,
    Matrix4x4 ViewProjection, Vector3 LocalMin, Vector3 LocalMax, double PositionPerturbationBound);

/// <summary>Scene-owned captured evidence over frozen, pre-reserved storage. Internal construction prevents arbitrary
/// public callers from manufacturing drawable proof. A stamp alone does not authenticate scene ownership.</summary>
public readonly ref struct CoverageRenderContext
{
    internal CoverageRenderContext(CoverageRefinement topology, CoverageRenderStamp stamp, Vector3 renderOrigin,
        ReadOnlySpan<CoverageRenderPass> passes)
    {
        Topology = topology; Stamp = stamp; RenderOrigin = renderOrigin; Passes = passes;
    }

    public CoverageRefinement Topology { get; }
    public CoverageRenderStamp Stamp { get; }
    public Vector3 RenderOrigin { get; }
    public ReadOnlySpan<CoverageRenderPass> Passes { get; }
}
