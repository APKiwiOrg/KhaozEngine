using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Primitives;
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

    /// <summary>Queue a local point light and retain its exact anchor through receiver and point-shadow binding.
    /// Exclusion box corners in shadow use the same anchor-local space as localPosition.</summary>
    public void AddLightAnchored(Vector3 localPosition, RenderAnchor anchor, Color colour, float radius, float intensity, LightShadow shadow)
    {
        if (!(radius > 0) || !float.IsFinite(radius) || !float.IsFinite(localPosition.X) ||
            !float.IsFinite(localPosition.Y) || !float.IsFinite(localPosition.Z)) return;
        Vector4 c = colour;
        var light = new ModelRenderer.PointLightData
        {
            PosRadius = new Vector4(0, 0, 0, radius),
            ColorIntensity = new(c.X, c.Y, c.Z, intensity),
            Anchor = anchor,
            LocalPosition = localPosition,
            LocalShadow = shadow,
        };
        light.PosRadius = light.RelativePosRadius(Vector3.Zero);
        light.Shadow = light.RelativeShadow(Vector3.Zero);
        _lights.Add(light);
    }

    internal static Vector3 AnchoredLightPosition(ModelRenderer.PointLightData light, Vector3 origin)
    {
        var position = light.RelativePosRadius(origin);
        return new(position.X, position.Y, position.Z);
    }

    long AnchoredLightSignature(long signature, int index)
    {
        var light = _lights[index];
        if (light.Anchor is not { } anchor) return signature;
        ulong hash = unchecked((ulong)signature);
        MixAnchoredPointSignature(ref hash, new(Matrix4x4.CreateTranslation(light.LocalPosition), anchor));
        if (light.LocalShadow.HasExclusionBox)
        {
            MixPointSignature(ref hash, Matrix4x4.CreateTranslation(light.LocalShadow.ExclusionMin));
            MixPointSignature(ref hash, Matrix4x4.CreateTranslation(light.LocalShadow.ExclusionMax));
        }
        return unchecked((long)hash);
    }

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
