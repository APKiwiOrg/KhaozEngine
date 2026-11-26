using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Attachments;

/// <summary>A convex hull in body rest-model metres and the indices of its cheap capsules in canonical set collider order.</summary>
public sealed record ClothHullGroup(int FeatureGroup, ClothSupportHull Hull, ImmutableArray<int> CapsuleIndices);

internal static class AttachmentSetClothHulls
{
    internal static void Validate(AttachmentPartDefinition[] parts, AttachmentLayer? layer, IReadOnlyList<ColliderDefinition> colliders)
    {
        var usedCapsules = new HashSet<int>();
        for (int partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            if (parts[partIndex] is not RigidAttachmentPartDefinition rigid || rigid.ClothHulls.IsDefaultOrEmpty) continue;
            var groups = new HashSet<int>();
            var canonical = ImmutableArray.CreateBuilder<ClothHullGroup>(rigid.ClothHulls.Length);
            for (int index = 0; index < rigid.ClothHulls.Length; index++)
            {
                ClothHullGroup group = rigid.ClothHulls[index];
                if (layer is null || group is null || group.Hull is null || group.FeatureGroup <= 0 ||
                    !groups.Add(group.FeatureGroup) || group.CapsuleIndices.IsDefaultOrEmpty)
                    throw Refuse(rigid.Id, partIndex, index, "A hull needs a layered rigid part, a distinct positive group and fallback capsules.");
                foreach (int capsule in group.CapsuleIndices)
                {
                    if ((uint)capsule >= colliders.Count || colliders[capsule].Joint != rigid.Joint || !usedCapsules.Add(capsule)
                        || colliders.Count(c => c == colliders[capsule]) != 1)
                        throw Refuse(rigid.Id, partIndex, index, "A grouped capsule must be unambiguous and attached to the rigid part's joint.", capsule);
                }
                canonical.Add(group with { CapsuleIndices = group.CapsuleIndices.Sort() });
            }
            canonical.Sort(static (a, b) => a.FeatureGroup.CompareTo(b.FeatureGroup));
            parts[partIndex] = rigid with { ClothHulls = canonical.MoveToImmutable() };
        }
    }

    static AttachmentSetArgumentException Refuse(uint partId, int partIndex, int groupIndex, string message, int? capsule = null) =>
        new(AttachmentSetRefusal.ClothHullGroupInvalid, message, "parts")
        { PartId = partId, PartIndex = partIndex, JsonPath = $"$.parts[{partIndex}].clothHulls[{groupIndex}]", ColliderIndex = capsule };
}
