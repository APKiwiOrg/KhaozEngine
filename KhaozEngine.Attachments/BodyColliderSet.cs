using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.Attachments;

/// <summary>An immutable set of rest-space capsule colliders for one body rig.</summary>
public sealed class BodyColliderSet
{
    /// <summary>The maximum number of colliders in one body file.</summary>
    public const int MaxColliders = 32;

    /// <summary>Admit a set, refusing with <see cref="BodyCollidersDataException"/> when a content rule is broken.</summary>
    public BodyColliderSet(string rigIdentity, IEnumerable<ColliderDefinition> colliders)
    {
        ArgumentNullException.ThrowIfNull(rigIdentity);
        ArgumentNullException.ThrowIfNull(colliders);
        if (!Attachments.RigIdentity.IsValid(rigIdentity))
            throw new BodyCollidersDataException(BodyCollidersRefusal.RigIdentityInvalid, "rig must be a complete lowercase rig-v1 identity.")
            { JsonPath = "$.rig", RigIdentity = rigIdentity };

        ColliderDefinition[] owned = Own(colliders);
        for (int index = 0; index < owned.Length; index++)
            ValidateCollider(owned[index], index);

        Array.Sort(owned, ColliderDefinition.CompareCanonical);
        RigIdentity = rigIdentity;
        Colliders = ImmutableArray.CreateRange(owned);
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(BodyCollidersJson.Write(this)));
    }

    /// <summary>The complete <c>rig-v1:</c> identity of the body.</summary>
    public string RigIdentity { get; }

    /// <summary>Colliders in canonical order by ordinal joint name, start, end and radius.</summary>
    public IReadOnlyList<ColliderDefinition> Colliders { get; }

    /// <summary>The lowercase SHA-256 of the canonical bytes <see cref="BodyCollidersJson.Write"/> produces.</summary>
    public string Sha256 { get; }

    // A counted source is checked before it is copied. An uncounted one is read only until it passes the cap, so a
    // huge or endless sequence refuses after MaxColliders + 1 items instead of being copied whole.
    static ColliderDefinition[] Own(IEnumerable<ColliderDefinition> colliders)
    {
        if (colliders.TryGetNonEnumeratedCount(out int count))
        {
            ValidateCount(count);
            return colliders.ToArray();
        }
        var owned = new List<ColliderDefinition>();
        foreach (ColliderDefinition collider in colliders)
        {
            owned.Add(collider);
            ValidateCount(owned.Count);
        }
        return owned.ToArray();
    }

    internal static void ValidateCount(int count)
    {
        if (count > MaxColliders)
            throw new BodyCollidersDataException(BodyCollidersRefusal.TooManyColliders, $"A body carries at most {MaxColliders} colliders.")
            { JsonPath = "$.colliders", ColliderCount = count };
    }

    static void ValidateCollider(ColliderDefinition collider, int index)
    {
        if (collider.FindFault() is not { } fault) return;
        (BodyCollidersRefusal reason, string field, string message) = fault switch
        {
            ColliderDefinition.Fault.JointNameEmpty => (BodyCollidersRefusal.JointNameEmpty, "joint", "a joint name is required."),
            ColliderDefinition.Fault.JointNameMalformedUtf16 => (BodyCollidersRefusal.JointNameMalformedUtf16, "joint", "a joint name must contain well-formed UTF-16."),
            ColliderDefinition.Fault.StartInvalid => (BodyCollidersRefusal.PointInvalid, "start", "the start point must be finite."),
            ColliderDefinition.Fault.EndInvalid => (BodyCollidersRefusal.PointInvalid, "end", "the end point must be finite."),
            ColliderDefinition.Fault.RadiusInvalid => (BodyCollidersRefusal.RadiusInvalid, "radius", "the radius must be finite and positive."),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        string path = $"$.colliders[{index}].{field}";
        throw new BodyCollidersDataException(reason, $"{path}: {message}")
        { JsonPath = path, ColliderIndex = index, JointName = collider.Joint };
    }
}
