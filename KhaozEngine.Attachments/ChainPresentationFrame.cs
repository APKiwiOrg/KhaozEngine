using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationFrame(ChainRig rig)
{
    internal Matrix4x4[] Frames { get; } = new Matrix4x4[rig.JointCount];
    internal Quaternion[] Swings { get; } = new Quaternion[rig.JointCount];
    internal ChainPresentationAnchor[] Anchors { get; } = new ChainPresentationAnchor[rig.AnchorCount];

    internal void SetRest(ReadOnlySpan<ChainPresentationAnchor> anchors)
    {
        rig.RestFromAnchor.CopyTo(Frames);
        Array.Fill(Swings, Quaternion.Identity);
        anchors.CopyTo(Anchors);
    }

    internal void CopyFrom(ChainPresentationFrame source)
    {
        source.Frames.CopyTo(Frames, 0);
        source.Swings.CopyTo(Swings, 0);
        source.Anchors.CopyTo(Anchors, 0);
    }

    internal void CopyJoints(ChainPresentationFrame source, Range joints)
    {
        source.Frames.AsSpan()[joints].CopyTo(Frames.AsSpan()[joints]);
        source.Swings.AsSpan()[joints].CopyTo(Swings.AsSpan()[joints]);
    }

    internal void CaptureJoints(ChainParticles particles, ChainExclusion excluded = default)
    {
        var worlds = particles.JointWorlds;
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            if (!excluded.Includes(chain))
            {
                rig.RestFromAnchor.Slice(first, end - first).CopyTo(Frames.AsSpan(first));
                Swings.AsSpan(first, end - first).Fill(Quaternion.Identity);
                continue;
            }
            var inverse = Anchors[definition.Anchor].Inverse;
            var root = worlds[first] * inverse;
            // Inverse composition can round the offset, but the root stays pinned to its anchor.
            root.Translation = rig.RestFromAnchor[first].Translation;
            Frames[first] = root;
            for (int joint = first + 1; joint < end; joint++)
                Frames[joint] = worlds[joint] * inverse;
            particles.JointSwings.AsSpan(first, end - first).CopyTo(Swings.AsSpan(first));
        }
    }
}
