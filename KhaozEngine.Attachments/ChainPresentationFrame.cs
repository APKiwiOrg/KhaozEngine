using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationFrame(ChainRig rig)
{
    internal Matrix4x4[] Frames { get; } = new Matrix4x4[rig.JointCount];
    internal Quaternion[] Swings { get; } = new Quaternion[rig.JointCount];
    internal ChainPresentationAnchor[] Anchors { get; } = new ChainPresentationAnchor[rig.AnchorCount];

    internal void SetRest(ReadOnlySpan<ChainPose> anchors, ReadOnlySpan<Matrix4x4> inverses)
    {
        rig.RestFromAnchor.CopyTo(Frames);
        Array.Fill(Swings, Quaternion.Identity);
        SetAnchors(anchors, inverses);
    }

    internal void SetAnchors(ReadOnlySpan<ChainPose> anchors, ReadOnlySpan<Matrix4x4> inverses)
    {
        for (int index = 0; index < anchors.Length; index++)
            Anchors[index] = new ChainPresentationAnchor(inverses[index], anchors[index].Rotation);
    }

    internal void CopyFrom(ChainPresentationFrame source)
    {
        source.Frames.CopyTo(Frames, 0);
        source.Swings.CopyTo(Swings, 0);
        source.Anchors.CopyTo(Anchors, 0);
    }
}
