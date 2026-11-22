using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Attachments;

internal static class AttachmentSetLift
{
    internal const string StandoffPath = "$.standoff";
    internal const float MaxStandoffMetres = 0.25f;
    internal const float MaxClearanceMetres = 0.05f;

    internal static ImmutableArray<RegionStandoff> OwnStandoff(IEnumerable<RegionStandoff> standoff, bool layered)
    {
        ArgumentNullException.ThrowIfNull(standoff);
        if (standoff.TryGetNonEnumeratedCount(out int count)) CheckStandoffCount(count);
        // An uncounted source need only yield the first entry beyond the cap to be refused.
        RegionStandoff[] owned = standoff.Take(AttachmentSetDefinition.MaxStandoffEntries + 1).ToArray();
        CheckStandoffCount(owned.Length);
        if (owned.Length == 0) return [];
        if (!layered)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.StandoffWithoutLayer,
                "A set with standoff requires a layer.", nameof(standoff)) { JsonPath = "$" };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < owned.Length; index++)
        {
            RegionStandoff entry = owned[index];
            if (string.IsNullOrWhiteSpace(entry.Region))
                throw EntryRefusal(AttachmentSetRefusal.StandoffRegionNameEmpty, "A standoff region name is required.", index, entry.Region, component: 0);
            if (!AttachmentText.IsWellFormedUtf16(entry.Region))
                throw EntryRefusal(AttachmentSetRefusal.StandoffRegionNameMalformedUtf16, "A standoff region name must contain well-formed UTF-16.", index, entry.Region, component: 0);
            if (!seen.Add(entry.Region))
                throw EntryRefusal(AttachmentSetRefusal.StandoffRegionDuplicate, $"Standoff for region '{entry.Region}' is already declared.", index, entry.Region, component: 0);
            if (entry.Metres is not (>= 0 and <= MaxStandoffMetres))
                throw EntryRefusal(AttachmentSetRefusal.StandoffValueOutOfRange, "Standoff must be finite and between zero and 0.25 metres.", index, entry.Region, component: 1);
        }
        Array.Sort(owned, (left, right) => StringComparer.Ordinal.Compare(left.Region, right.Region));
        return ImmutableArray.CreateRange(owned);
    }

    internal static void CheckStandoffCount(int count)
    {
        if (count > AttachmentSetDefinition.MaxStandoffEntries)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.TooManyStandoffEntries,
                $"A worn set holds at most {AttachmentSetDefinition.MaxStandoffEntries} standoff entries.", "standoff")
            { JsonPath = StandoffPath, ActualCount = count, ExpectedCount = AttachmentSetDefinition.MaxStandoffEntries };
    }

    internal static void CheckPart(AttachmentPartDefinition part, bool layered, int partIndex)
    {
        if (part is not SkinnedAttachmentPartDefinition { Lift: { } lift }) return;
        if (!layered)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.LiftWithoutLayer,
                $"Attachment part {part.Id}: lift requires a set with a layer.", "parts")
            { JsonPath = "$", PartId = part.Id, PartIndex = partIndex };
        if (lift.ClearanceMetres is not (>= 0 and <= MaxClearanceMetres))
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.LiftClearanceOutOfRange,
                $"Attachment part {part.Id}: lift clearance must be finite and between zero and 0.05 metres.", "parts")
            { JsonPath = LiftPath(partIndex) + ".clearance", PartId = part.Id, PartIndex = partIndex };
    }

    internal static string EntryPath(int index) => $"{StandoffPath}[{index}]";

    internal static string LiftPath(int partIndex) => $"$.parts[{partIndex}].lift";

    static AttachmentSetArgumentException EntryRefusal(AttachmentSetRefusal reason, string message, int index, string? region, int component)
    {
        string path = $"{EntryPath(index)}[{component}]";
        return new AttachmentSetArgumentException(reason, $"{path}: {message}", "standoff")
        { JsonPath = path, StandoffIndex = index, RegionName = region };
    }
}
