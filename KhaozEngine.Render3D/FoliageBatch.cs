using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Immutable foliage retained by one scene. Dispose when its authored placement changes or unloads.</summary>
public sealed class FoliageBatch : IDisposable
{
    internal readonly Scene3D Owner;
    internal readonly FoliagePatchLayout Layout;
    internal ModelRenderer.FoliageInstanceData[]? Pending;
    internal IGpuBuffer? Buffer;

    internal FoliageBatch(Scene3D owner, FoliagePatchLayout layout, ModelRenderer.FoliageInstanceData[] data)
    {
        Owner = owner;
        Layout = layout;
        Pending = data;
    }

    /// <summary>Number of retained mesh instances. Multipart models contribute one per part.</summary>
    public int Count => Layout.Instances.Length;

    /// <summary>Whether this batch has been released, including by its owning scene.</summary>
    public bool IsDisposed { get; internal set; }

    /// <summary>Retire the GPU storage behind the scene's fence. Safe to call twice.</summary>
    public void Dispose() => Owner.ReleaseFoliage(this);
}

/// <summary>Last rendered foliage workload. Candidates include blades rejected by the vertex shader.</summary>
public readonly record struct FoliageFrameStats(
    int TestedPatches, int SubmittedPatches, int CandidateInstances,
    long InstanceUploadBytes, long UniformUploadBytes);
