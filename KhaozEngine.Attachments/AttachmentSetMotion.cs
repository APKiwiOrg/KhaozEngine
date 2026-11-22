using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class AttachmentSetMotion
{
    const int MaxChains = 32;
    const int MaxJoints = 16;
    const int MaxLinks = 64;

    internal static AttachmentPartDefinition Part(AttachmentPartDefinition part, bool layered, int partIndex)
    {
        if (part is not SkinnedAttachmentPartDefinition { Motion: { } motion } skinned) return part;
        uint partId = skinned.Id;
        if (!layered)
            throw PartRefusal(AttachmentSetRefusal.MotionWithoutLayer, partId, partIndex, "motion requires a set with a layer.", "");
        if (motion is not ChainMotionDefinition chains)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.MotionKindUnsupported, $"Attachment part {partId}: unsupported motion kind.", "parts")
            { PartId = partId, PartIndex = partIndex, ActualType = motion.GetType(), JsonPath = MotionPath(partIndex) + ".kind" };

        IReadOnlyList<ChainDefinition> definitions = chains.Chains ?? [];
        CheckChainCount(definitions.Count, partId, partIndex);
        var jointNames = new HashSet<string>(StringComparer.Ordinal);
        var owned = ImmutableArray.CreateBuilder<ChainDefinition>(definitions.Count);
        for (int chainIndex = 0; chainIndex < definitions.Count; chainIndex++)
            owned.Add(OwnChain(definitions[chainIndex], jointNames, partId, partIndex, chainIndex));
        ImmutableArray<ChainLink> links = OwnLinks(chains.Links ?? [], definitions.Count, partId, partIndex);
        return skinned with { Motion = new ChainMotionDefinition(owned.MoveToImmutable(), links) };
    }

    static ChainDefinition OwnChain(ChainDefinition? chain, HashSet<string> jointNames, uint partId, int partIndex, int chainIndex)
    {
        IReadOnlyList<string> joints = chain?.Joints ?? [];
        CheckJointCount(joints.Count, partId, partIndex, chainIndex);
        var owned = ImmutableArray.CreateBuilder<string>(joints.Count);
        for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
        {
            string name = joints[jointIndex];
            AttachmentSetRefusal? fault = string.IsNullOrWhiteSpace(name) ? AttachmentSetRefusal.ChainJointNameEmpty
                : !AttachmentText.IsWellFormedUtf16(name) ? AttachmentSetRefusal.ChainJointNameMalformedUtf16
                : !jointNames.Add(name) ? AttachmentSetRefusal.ChainJointDuplicate
                : null;
            if (fault is { } reason)
            {
                string message = reason switch
                {
                    AttachmentSetRefusal.ChainJointNameEmpty => "a chain joint name is required.",
                    AttachmentSetRefusal.ChainJointNameMalformedUtf16 => "the chain joint name must contain well-formed UTF-16.",
                    _ => $"the joint '{name}' is already in a chain of the part.",
                };
                throw PartRefusal(reason, partId, partIndex, message, $".chains[{chainIndex}].joints[{jointIndex}]",
                    chainIndex: chainIndex, jointIndex: jointIndex, jointName: name);
            }
            owned.Add(name);
        }
        Vector3 tip = chain!.Tip;
        if (!float.IsFinite(tip.X) || !float.IsFinite(tip.Y) || !float.IsFinite(tip.Z) || tip == Vector3.Zero)
            throw PartRefusal(AttachmentSetRefusal.ChainTipInvalid, partId, partIndex, "the chain tip must be finite and nonzero.",
                $".chains[{chainIndex}].tip", chainIndex: chainIndex);
        if (chain.Settings.FirstInvalidMember() is { } setting)
            throw PartRefusal(AttachmentSetRefusal.ChainSettingOutOfRange, partId, partIndex, $"the chain setting '{setting}' is out of range.",
                $".chains[{chainIndex}].{setting}", chainIndex: chainIndex, settingName: setting);
        return chain with { Joints = owned.MoveToImmutable() };
    }

    static ImmutableArray<ChainLink> OwnLinks(IReadOnlyList<ChainLink> links, int chainCount, uint partId, int partIndex)
    {
        CheckLinkCount(links.Count, partId, partIndex);
        var seen = new HashSet<ChainLink>();
        var owned = ImmutableArray.CreateBuilder<ChainLink>(links.Count);
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
        {
            ChainLink link = links[linkIndex];
            if ((uint)link.First >= (uint)chainCount || (uint)link.Second >= (uint)chainCount || link.First == link.Second)
                throw PartRefusal(AttachmentSetRefusal.ChainLinkInvalid, partId, partIndex, "a link must name two distinct chains of the part.",
                    $".links[{linkIndex}]", linkIndex: linkIndex);
            if (link.First > link.Second) link = new ChainLink(link.Second, link.First);
            if (!seen.Add(link))
                throw PartRefusal(AttachmentSetRefusal.ChainLinkDuplicate, partId, partIndex, "a link already names this pair of chains.",
                    $".links[{linkIndex}]", linkIndex: linkIndex);
            owned.Add(link);
        }
        owned.Sort(static (left, right) => left.First != right.First ? left.First.CompareTo(right.First) : left.Second.CompareTo(right.Second));
        return owned.MoveToImmutable();
    }

    internal static ImmutableArray<ColliderDefinition> Colliders(IEnumerable<ColliderDefinition> colliders)
    {
        ArgumentNullException.ThrowIfNull(colliders);
        if (colliders.TryGetNonEnumeratedCount(out int count)) CheckColliderCount(count);
        // An uncounted source can be arbitrarily long. Admit at most the first item beyond the cap.
        ColliderDefinition[] owned = colliders.Take(AttachmentSetDefinition.MaxColliders + 1).ToArray();
        CheckColliderCount(owned.Length);
        for (int index = 0; index < owned.Length; index++)
        {
            ColliderDefinition collider = owned[index];
            if (collider.FindFault() is not { } fault) continue;
            (AttachmentSetRefusal reason, string field, string message) = fault switch
            {
                ColliderDefinition.Fault.JointNameEmpty => (AttachmentSetRefusal.ColliderJointNameEmpty, "joint", "a joint name is required."),
                ColliderDefinition.Fault.JointNameMalformedUtf16 => (AttachmentSetRefusal.ColliderJointNameMalformedUtf16, "joint", "a joint name must contain well-formed UTF-16."),
                ColliderDefinition.Fault.StartInvalid => (AttachmentSetRefusal.ColliderPointInvalid, "start", "the start point must be finite."),
                ColliderDefinition.Fault.EndInvalid => (AttachmentSetRefusal.ColliderPointInvalid, "end", "the end point must be finite."),
                ColliderDefinition.Fault.RadiusInvalid => (AttachmentSetRefusal.ColliderRadiusInvalid, "radius", "the radius must be finite and positive."),
                _ => throw new ArgumentOutOfRangeException(nameof(fault)),
            };
            string path = $"$.colliders[{index}].{field}";
            throw new AttachmentSetArgumentException(reason, $"{path}: {message}", nameof(colliders))
            { JsonPath = path, ColliderIndex = index, JointName = collider.Joint };
        }
        Array.Sort(owned, ColliderDefinition.CompareCanonical);
        return ImmutableArray.CreateRange(owned);
    }

    internal static void CheckChainCount(int count, uint partId, int partIndex)
    {
        if (count == 0)
            throw PartRefusal(AttachmentSetRefusal.ChainsEmpty, partId, partIndex, "chain motion requires at least one chain.",
                ".chains", actualCount: count, expectedCount: 1);
        if (count > MaxChains)
            throw PartRefusal(AttachmentSetRefusal.TooManyChains, partId, partIndex, $"a part holds at most {MaxChains} chains.",
                ".chains", actualCount: count, expectedCount: MaxChains);
    }

    internal static void CheckJointCount(int count, uint partId, int partIndex, int chainIndex)
    {
        if (count == 0)
            throw PartRefusal(AttachmentSetRefusal.ChainJointsEmpty, partId, partIndex, "a chain requires at least one joint.",
                $".chains[{chainIndex}].joints", chainIndex: chainIndex, actualCount: count, expectedCount: 1);
        if (count > MaxJoints)
            throw PartRefusal(AttachmentSetRefusal.TooManyChainJoints, partId, partIndex, $"a chain holds at most {MaxJoints} joints.",
                $".chains[{chainIndex}].joints", chainIndex: chainIndex, actualCount: count, expectedCount: MaxJoints);
    }

    internal static void CheckLinkCount(int count, uint partId, int partIndex)
    {
        if (count > MaxLinks)
            throw PartRefusal(AttachmentSetRefusal.TooManyLinks, partId, partIndex, $"a part holds at most {MaxLinks} links.",
                ".links", actualCount: count, expectedCount: MaxLinks);
    }

    internal static void CheckColliderCount(int count)
    {
        if (count > AttachmentSetDefinition.MaxColliders)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.TooManyColliders,
                $"A worn set holds at most {AttachmentSetDefinition.MaxColliders} colliders.", "colliders")
            { JsonPath = "$.colliders", ActualCount = count, ExpectedCount = AttachmentSetDefinition.MaxColliders };
    }

    internal static string MotionPath(int partIndex) => $"$.parts[{partIndex}].motion";

    static AttachmentSetArgumentException PartRefusal(AttachmentSetRefusal reason, uint partId, int partIndex, string message,
        string suffix, int? chainIndex = null, int? jointIndex = null, int? linkIndex = null, string? jointName = null,
        string? settingName = null, int? actualCount = null, int? expectedCount = null) =>
        new(reason, $"Attachment part {partId}: {message}", "parts")
        {
            JsonPath = MotionPath(partIndex) + suffix,
            PartId = partId,
            PartIndex = partIndex,
            ChainIndex = chainIndex,
            JointIndex = jointIndex,
            LinkIndex = linkIndex,
            JointName = jointName,
            SettingName = settingName,
            ActualCount = actualCount,
            ExpectedCount = expectedCount,
        };
}
