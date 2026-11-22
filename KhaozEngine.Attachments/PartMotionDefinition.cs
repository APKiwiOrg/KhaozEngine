using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The authored motion of a skinned attachment part.</summary>
public abstract record PartMotionDefinition;

/// <summary>The ordered chains of a skinned part and the links between them.</summary>
/// <param name="Chains">The chains in the order their link indices refer to.</param>
/// <param name="Links">Pairs of zero-based chain indices. The part declares any bound through <see cref="LinkStretch"/>.</param>
public sealed record ChainMotionDefinition(IReadOnlyList<ChainDefinition> Chains, IReadOnlyList<ChainLink> Links)
    : PartMotionDefinition
{
    /// <summary>The optional maximum fractional departure from rest spacing for every link in the part.</summary>
    public float? LinkStretch { get; init; }

    /// <summary>Compare the part's link bound and the chains and links element by element.</summary>
    public bool Equals(ChainMotionDefinition? other) => other is not null && base.Equals(other)
        && EqualityComparer<float?>.Default.Equals(LinkStretch, other.LinkStretch)
        && (Chains ?? []).SequenceEqual(other.Chains ?? []) && (Links ?? []).SequenceEqual(other.Links ?? []);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (ChainDefinition chain in Chains ?? []) hash.Add(chain);
        foreach (ChainLink link in Links ?? []) hash.Add(link);
        hash.Add(LinkStretch);
        return hash.ToHashCode();
    }
}

/// <summary>A chain of joints in a skinned attachment part.</summary>
/// <param name="Joints">The part's ordinal joint names, in order from the anchor toward the tip.</param>
/// <param name="Tip">The virtual tip point in the last joint's rest frame.</param>
/// <param name="Settings">The chain's authored motion settings.</param>
public sealed record ChainDefinition(IReadOnlyList<string> Joints, Vector3 Tip, ChainSettings Settings)
{
    /// <summary>Compare the tip, settings and ordered joint names by value.</summary>
    public bool Equals(ChainDefinition? other) => other is not null && Tip.Equals(other.Tip) && Settings.Equals(other.Settings)
        && (Joints ?? []).SequenceEqual(other.Joints ?? [], StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tip);
        hash.Add(Settings);
        foreach (string joint in Joints ?? []) hash.Add(joint, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
