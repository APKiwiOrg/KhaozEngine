using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Attachments;

/// <summary>
/// Admits a set's layer, its covered regions and its parts' region tags. A region name is only a name here. Whether a
/// body has that region is checked when an outfit is resolved against the body's region map.
/// </summary>
internal static class AttachmentSetRegions
{
    /// <summary>Check the layer and return the covered regions, unique and in ordinal order so listing order never changes the revision.</summary>
    internal static string[] Covers(AttachmentLayer layer, IEnumerable<string> covers)
    {
        if (!Enum.IsDefined(layer))
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.LayerUnknown, $"Layer {(byte)layer} is not a defined layer.", nameof(layer))
            { Layer = layer };
        ArgumentNullException.ThrowIfNull(covers);
        string[] names = covers.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index];
            string context = $"covers[{index}]";
            CheckName(name, context, nameof(covers), coverIndex: index);
            if (!seen.Add(name))
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.CoveredRegionDuplicate, $"{context}: region '{name}' is already covered.", nameof(covers))
                { CoverIndex = index, RegionName = name };
        }
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Check a part's region tag and return the part the set owns. A skinned part's runs are copied with adjacent runs
    /// of one region merged, so two parts that tag every triangle alike write the same bytes.
    /// </summary>
    internal static AttachmentPartDefinition Part(AttachmentPartDefinition part, bool layered, string parameter)
    {
        string context = $"Attachment part {part.Id}";
        if (part is RigidAttachmentPartDefinition rigid)
        {
            if (rigid.Region is null)
                return layered
                    ? throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartRegionMissing, $"{context}: a set with a layer requires a region on every part.", parameter)
                    { PartId = part.Id }
                    : rigid;
            if (!layered) throw LayerMissing(part, parameter);
            CheckName(rigid.Region, context, parameter, partId: part.Id);
            return rigid;
        }

        var skinned = (SkinnedAttachmentPartDefinition)part;
        IReadOnlyList<NamedRegionRun>? runs = skinned.Regions;
        if (!layered)
        {
            if (runs is { Count: > 0 }) throw LayerMissing(part, parameter);
            // Own the empty list too, so a caller's list that gains runs later cannot change the set.
            return ReferenceEquals(runs, Array.Empty<NamedRegionRun>()) ? skinned : skinned with { Regions = Array.Empty<NamedRegionRun>() };
        }
        if (runs is null || runs.Count == 0)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartRegionMissing, $"{context}: a set with a layer requires region runs on every skinned part.", parameter)
            { PartId = part.Id };

        var merged = new List<NamedRegionRun>(runs.Count);
        long triangles = 0;
        for (int runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            NamedRegionRun run = runs[runIndex];
            string runContext = $"{context} run {runIndex}";
            CheckName(run.Region, runContext, parameter, partId: part.Id, runIndex: runIndex);
            if (run.TriangleCount < 1)
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartRegionRunEmpty, $"{runContext}: a run covers at least one triangle.", parameter)
                { PartId = part.Id, RunIndex = runIndex, RegionName = run.Region };
            triangles += run.TriangleCount;
            if (triangles > int.MaxValue)
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.RegionTriangleCountOverflow, $"{runContext}: runs exceed the Int32 triangle range.", parameter)
                { PartId = part.Id, RunIndex = runIndex, RegionName = run.Region, ActualTriangleCount = triangles };
            if (merged.Count > 0 && string.Equals(merged[^1].Region, run.Region, StringComparison.Ordinal))
                merged[^1] = merged[^1] with { TriangleCount = merged[^1].TriangleCount + run.TriangleCount };
            else merged.Add(run);
        }
        return skinned with { Regions = Array.AsReadOnly(merged.ToArray()) };
    }

    /// <summary>Refuse a blank or malformed region name, naming the covered entry or the part and run that holds it.</summary>
    static void CheckName(string name, string context, string parameter, int? coverIndex = null, uint? partId = null, int? runIndex = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.RegionNameEmpty, $"{context}: a region name is required.", parameter)
            { CoverIndex = coverIndex, PartId = partId, RunIndex = runIndex, RegionName = name };
        if (!AttachmentText.IsWellFormedUtf16(name))
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.RegionNameMalformedUtf16, $"{context}: a region name must contain well-formed UTF-16.", parameter)
            { CoverIndex = coverIndex, PartId = partId, RunIndex = runIndex, RegionName = name };
    }

    static AttachmentSetArgumentException LayerMissing(AttachmentPartDefinition part, string parameter) =>
        new(AttachmentSetRefusal.LayerMissing, $"Attachment part {part.Id}: region tags need a set with a layer.", parameter) { PartId = part.Id };
}
