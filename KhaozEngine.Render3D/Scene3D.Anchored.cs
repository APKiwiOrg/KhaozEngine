using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>The precise submission retained beside an approximate absolute CPU culling transform.</summary>
internal readonly record struct AnchoredRigidTransform(Matrix4x4 Local, RenderAnchor Anchor)
{
    internal Matrix4x4 Absolute => RelativeTo(Vector3.Zero);
    internal Matrix4x4 RelativeTo(Vector3 origin)
    {
        var result = Local;
        result.M41 = (float)(((double)Anchor.X - origin.X) + Local.M41);
        result.M42 = (float)(((double)Anchor.Y - origin.Y) + Local.M42);
        result.M43 = (float)(((double)Anchor.Z - origin.Z) + Local.M43);
        return result;
    }
}

public sealed partial class Scene3D
{
    readonly List<AnchoredRigidTransform?> _instanceAnchors = new();
    AnchoredRigidTransform? InstanceAnchor(int slot) => slot < _instanceAnchors.Count ? _instanceAnchors[slot] : null;

    /// <summary>Queue a rigid draw whose World matrix is local to an integer world anchor. The anchor and local
    /// matrix survive grouping, temporal history and shadow submission without an absolute binary32 round trip.</summary>
    public void DrawAnchored(in RigidInstanceDraw draw, RenderAnchor anchor) => QueueRigid(in draw, anchor);

    void QueueRigid(in RigidInstanceDraw draw, RenderAnchor? anchor)
    {
        AnchoredRigidTransform? anchored = anchor is { } a ? new(draw.World, a) : null;
        var absolute = anchored is { } pose ? draw with { World = pose.Absolute } : draw;
        bool cut = absolute.CutInsideInteriors && RelevantInteriorCut(absolute.Mesh, absolute.World);
        _instances.Add(in absolute, cut, anchored);
        if (anchored is { } local && !draw.Motion.IsNone && !draw.ShadowOnly && TemporalActive)
            (_motionHistory ??= new MotionHistory()).RecordRigid(draw.Motion, local.Local, local.Anchor);
        else RecordRigidMotion(in absolute);
    }

    // Colour and both rigid shadow passes consume this same uploaded stream. Legacy zero-origin uploads still
    // bypass the copy entirely. The parallel anchor list follows the identical grouping and retention scatter.
    internal static void ReduceRigidInstances(IReadOnlyList<ModelRenderer.InstanceData> source,
        IReadOnlyList<AnchoredRigidTransform?> anchors, Vector3 origin, List<ModelRenderer.InstanceData> destination)
    {
        if (source.Count != anchors.Count) throw new ArgumentException("Anchors must cover every grouped instance.", nameof(anchors));
        destination.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            var data = source[i];
            AssertAffine(data.Model);
            if (anchors[i] is { } anchored) data.Model = anchored.RelativeTo(origin);
            else
            {
                data.Model.M41 -= origin.X;
                data.Model.M42 -= origin.Y;
                data.Model.M43 -= origin.Z;
            }
            destination.Add(data);
        }
    }

    internal static void MixAnchoredPointSignature(ref ulong hash, AnchoredRigidTransform anchored)
    {
        MixPointSignature(ref hash, unchecked((ulong)anchored.Anchor.X));
        MixPointSignature(ref hash, unchecked((ulong)anchored.Anchor.Y));
        MixPointSignature(ref hash, unchecked((ulong)anchored.Anchor.Z));
        MixPointSignature(ref hash, anchored.Local);
    }
}
