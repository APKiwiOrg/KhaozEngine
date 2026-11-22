using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class AttachmentSetLiftJson
{
    internal static RegionStandoff[] ReadStandoff(JsonElement root)
    {
        if (!root.TryGetProperty("standoff", out JsonElement value)) return [];
        JsonElement array = StrictAttachmentJson.ArrayValue(value, AttachmentSetLift.StandoffPath, RefuseRoot);
        int count = array.GetArrayLength();
        AttachmentSetLift.CheckStandoffCount(count);
        var entries = new RegionStandoff[count];
        for (int index = 0; index < count; index++)
            entries[index] = ReadStandoffEntry(array[index], index);
        return entries;
    }

    static RegionStandoff ReadStandoffEntry(JsonElement value, int index)
    {
        string path = AttachmentSetLift.EntryPath(index);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonValueShapeInvalid, $"{path}: expected a [region, metres] pair.")
            { JsonPath = path, StandoffIndex = index };

        string region;
        try
        {
            region = StrictAttachmentJson.Text(value[0], path + "[0]", Refuse);
        }
        catch (InvalidOperationException error)
        {
            // An unpaired escaped surrogate has no decoded region name.
            throw new AttachmentSetDataException(AttachmentSetRefusal.StandoffRegionNameMalformedUtf16,
                $"{path}[0]: a standoff region name must contain well-formed UTF-16.", error)
            { JsonPath = path + "[0]", StandoffIndex = index };
        }
        return new RegionStandoff(region, StrictAttachmentJson.Number(value[1], path + "[1]", Refuse));

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { JsonPath = jsonPath, PropertyName = property, StandoffIndex = index };
    }

    internal static PartLiftDefinition? ReadLift(JsonElement part, uint partId, int partIndex)
    {
        if (!part.TryGetProperty("lift", out JsonElement value)) return null;
        string path = AttachmentSetLift.LiftPath(partIndex);
        StrictAttachmentJson.Members(value, path, Refuse, "clearance");
        JsonElement clearance = StrictAttachmentJson.Required(value, "clearance", path, Refuse);
        return new PartLiftDefinition(StrictAttachmentJson.Number(clearance, path + ".clearance", Refuse));

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { JsonPath = jsonPath, PropertyName = property, PartId = partId, PartIndex = partIndex };
    }

    internal static void RequireLayerForAdditions(IReadOnlyList<AttachmentPartDefinition> parts, IReadOnlyList<RegionStandoff> standoff)
    {
        if (standoff.Count > 0)
            throw new AttachmentSetDataException(AttachmentSetRefusal.StandoffWithoutLayer, "A set with standoff requires a layer.")
            { JsonPath = "$", PropertyName = "layer" };
        for (int index = 0; index < parts.Count; index++)
            if (parts[index] is SkinnedAttachmentPartDefinition { Lift: not null } part)
                throw new AttachmentSetDataException(AttachmentSetRefusal.LiftWithoutLayer, "A set with lift requires a layer.")
                { JsonPath = "$", PropertyName = "layer", PartId = part.Id, PartIndex = index };
    }

    internal static bool HasAdditions(AttachmentSetDefinition set) => set.Standoff.Count > 0
        || set.Parts.Any(part => part is SkinnedAttachmentPartDefinition { Lift: not null });

    internal static void WriteStandoff(Utf8JsonWriter writer, IReadOnlyList<RegionStandoff> standoff)
    {
        if (standoff.Count == 0) return;
        writer.WriteStartArray("standoff");
        foreach (RegionStandoff entry in standoff)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(entry.Region);
            writer.WriteNumberValue(entry.Metres == 0 ? 0f : entry.Metres);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    internal static void WriteLift(Utf8JsonWriter writer, PartLiftDefinition? lift)
    {
        if (lift is null) return;
        writer.WriteStartObject("lift");
        writer.WriteNumber("clearance", lift.ClearanceMetres == 0 ? 0f : lift.ClearanceMetres);
        writer.WriteEndObject();
    }

    static Exception RefuseRoot(StrictAttachmentJson.Fault fault, string path, string message, string? property) =>
        new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message) { JsonPath = path, PropertyName = property };
}
