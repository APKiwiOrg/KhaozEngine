using System;
using System.Collections.Generic;

namespace KhaozEngine.Attachments;

/// <summary>
/// What an outfit hides on one body: the body regions under any layered piece and, for each worn set, the regions a
/// strictly higher layer covers. Every region name the worn sets use is resolved against the body's region map here.
/// Resolve when the outfit changes, never per frame. The result holds no reference to the caller's list.
/// </summary>
public sealed class OutfitCoverage
{
    readonly Worn[] worn;

    OutfitCoverage(Worn[] worn, RegionSet bodyHidden)
    {
        this.worn = worn;
        BodyHidden = bodyHidden;
    }

    /// <summary>The number of worn sets the outfit was resolved from.</summary>
    public int WornCount => worn.Length;

    /// <summary>The body regions covered by any worn set that has a layer.</summary>
    public RegionSet BodyHidden { get; }

    /// <summary>
    /// Resolve <paramref name="worn"/> against <paramref name="body"/>, refusing with <see cref="OutfitDataException"/>
    /// when a set names a region the body does not declare.
    /// </summary>
    public static OutfitCoverage Resolve(BodyRegionMap body, IReadOnlyList<AttachmentSetDefinition> worn)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(worn);
        var resolved = new Worn[worn.Count];
        for (int index = 0; index < resolved.Length; index++)
        {
            AttachmentSetDefinition set = worn[index] ?? throw new ArgumentNullException(nameof(worn), $"worn[{index}] is null.");
            resolved[index] = Worn.Resolve(body, set, index);
        }

        RegionSet bodyHidden = RegionSet.Empty;
        foreach (Worn piece in resolved)
            bodyHidden = bodyHidden.Union(piece.Covers);
        for (int index = 0; index < resolved.Length; index++)
        {
            if (resolved[index].Layer is not { } layer)
                continue;
            RegionSet hidden = RegionSet.Empty;
            foreach (Worn other in resolved)
                if (other.Layer > layer)
                    hidden = hidden.Union(other.Covers);
            resolved[index].Hidden = hidden;
        }
        return new OutfitCoverage(resolved, bodyHidden);
    }

    /// <summary>
    /// The regions of worn set <paramref name="wornIndex"/> covered by a strictly higher layer. Empty for a set with no
    /// layer.
    /// </summary>
    public RegionSet HiddenFor(int wornIndex) => At(wornIndex).Hidden;

    /// <summary>True when a higher layer covers the region rigid part <paramref name="partId"/> sits on. A rigid part hides whole.</summary>
    /// <exception cref="ArgumentException">The set has no rigid part with that ID.</exception>
    public bool RigidPartHidden(int wornIndex, uint partId)
    {
        Worn piece = At(wornIndex);
        if (!piece.RigidRegions.TryGetValue(partId, out int region))
            throw new ArgumentException($"worn[{wornIndex}] has no rigid part {partId}.", nameof(partId));
        // A rigid part of a set with no layer has no region, and RegionSet.Contains refuses its marker as out of range.
        return region != Worn.NoRegion && piece.Hidden.Contains(region);
    }

    /// <summary>
    /// The runs of skinned part <paramref name="partId"/> in triangle order, each naming a body region index. A run is
    /// hidden when <see cref="HiddenFor"/> contains its region. Empty for a part of a set with no layer.
    /// </summary>
    /// <exception cref="ArgumentException">The set has no skinned part with that ID.</exception>
    public IReadOnlyList<RegionRun> SkinnedPartRegions(int wornIndex, uint partId)
    {
        Worn piece = At(wornIndex);
        return piece.SkinnedRegions.TryGetValue(partId, out IReadOnlyList<RegionRun>? runs)
            ? runs
            : throw new ArgumentException($"worn[{wornIndex}] has no skinned part {partId}.", nameof(partId));
    }

    Worn At(int wornIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wornIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wornIndex, worn.Length);
        return worn[wornIndex];
    }

    sealed class Worn
    {
        // A rigid part of a set with no layer maps to -1, which no region index uses.
        internal const int NoRegion = -1;

        Worn(AttachmentLayer? layer, RegionSet covers, Dictionary<uint, int> rigidRegions, Dictionary<uint, IReadOnlyList<RegionRun>> skinnedRegions)
        {
            Layer = layer;
            Covers = covers;
            RigidRegions = rigidRegions;
            SkinnedRegions = skinnedRegions;
        }

        internal AttachmentLayer? Layer { get; }
        internal RegionSet Covers { get; }
        internal RegionSet Hidden { get; set; }
        internal Dictionary<uint, int> RigidRegions { get; }
        internal Dictionary<uint, IReadOnlyList<RegionRun>> SkinnedRegions { get; }

        internal static Worn Resolve(BodyRegionMap body, AttachmentSetDefinition set, int wornIndex)
        {
            RegionSet covers = RegionSet.Empty;
            foreach (string name in set.Covers)
                covers = covers.With(Region(body, name, wornIndex, null, null));

            var rigidRegions = new Dictionary<uint, int>();
            var skinnedRegions = new Dictionary<uint, IReadOnlyList<RegionRun>>();
            foreach (AttachmentPartDefinition part in set.Parts)
            {
                if (part is RigidAttachmentPartDefinition rigid)
                {
                    rigidRegions.Add(part.Id, rigid.Region is null ? NoRegion : Region(body, rigid.Region, wornIndex, part.Id, null));
                    continue;
                }
                IReadOnlyList<NamedRegionRun> named = ((SkinnedAttachmentPartDefinition)part).Regions;
                var runs = new RegionRun[named.Count];
                for (int runIndex = 0; runIndex < runs.Length; runIndex++)
                    runs[runIndex] = new RegionRun(Region(body, named[runIndex].Region, wornIndex, part.Id, runIndex), named[runIndex].TriangleCount);
                skinnedRegions.Add(part.Id, Array.AsReadOnly(runs));
            }
            return new Worn(set.Layer, covers, rigidRegions, skinnedRegions);
        }

        static int Region(BodyRegionMap body, string name, int wornIndex, uint? partId, int? runIndex)
        {
            if (body.TryRegion(name, out int index))
                return index;
            string where = partId is null ? "covers" : runIndex is null ? $"part {partId}" : $"part {partId} run {runIndex}";
            throw new OutfitDataException(OutfitRefusal.RegionUnknown, $"worn[{wornIndex}] {where}: the body has no region '{name}'.")
            { WornIndex = wornIndex, PartId = partId, RunIndex = runIndex, RegionName = name };
        }
    }
}
