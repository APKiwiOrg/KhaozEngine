using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class AttachmentSetMotionJson
{
    internal static ColliderDefinition[] ReadColliders(JsonElement root)
    {
        if (!root.TryGetProperty("colliders", out JsonElement value)) return [];
        const string arrayPath = "$.colliders";
        JsonElement array = StrictAttachmentJson.ArrayValue(value, arrayPath, RootRefusal);
        int count = array.GetArrayLength();
        AttachmentSetMotion.CheckColliderCount(count);
        var colliders = new ColliderDefinition[count];
        for (int index = 0; index < count; index++)
            colliders[index] = ReadCollider(array[index], index);
        return colliders;
    }

    static ColliderDefinition ReadCollider(JsonElement value, int index)
    {
        string path = $"$.colliders[{index}]";
        return AttachmentColliderJson.Read(value, path, Refuse, error =>
            new AttachmentSetDataException(AttachmentSetRefusal.ColliderJointNameMalformedUtf16,
                $"{path}.joint: a joint name must contain well-formed UTF-16.", error)
            { JsonPath = path + ".joint", ColliderIndex = index });

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message) { JsonPath = jsonPath, PropertyName = property, ColliderIndex = index };
    }

    internal static PartMotionDefinition? ReadMotion(JsonElement part, uint partId, int partIndex, bool linkStretchFormat, bool clothFormat = false)
    {
        if (!part.TryGetProperty("motion", out JsonElement motion)) return null;
        string path = AttachmentSetMotion.MotionPath(partIndex);
        if (motion.ValueKind != JsonValueKind.Object)
            throw Refuse(StrictAttachmentJson.Fault.ValueShape, path, $"{path}: expected an object.", null);
        // A repeated discriminator is a duplicate before it is a kind, so the reader never dispatches on one of two
        // kinds. The other members are checked after the kind, because a future kind brings members of its own.
        int kinds = 0;
        foreach (JsonProperty member in motion.EnumerateObject())
            if (member.NameEquals("kind") && ++kinds > 1)
                throw Refuse(StrictAttachmentJson.Fault.DuplicateProperty, path, $"{path}: duplicate property 'kind'.", "kind");
        JsonElement kindValue = StrictAttachmentJson.Required(motion, "kind", path, Refuse);
        if (kindValue.ValueKind != JsonValueKind.String)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonStringRequired, $"{path}.kind: expected a string.")
            { JsonPath = path + ".kind", PropertyName = "kind", PartId = partId, PartIndex = partIndex };
        string kind = kindValue.GetString()!;
        if (kind == "cloth" && clothFormat)
        {
            StrictAttachmentJson.Members(motion, path, Refuse, "kind", "surface", "fallback");
            JsonElement surface = StrictAttachmentJson.Required(motion, "surface", path, Refuse);
            string surfacePath = path + ".surface";
            StrictAttachmentJson.Members(surface, surfacePath, Refuse, "key", "sha256");
            string key = StrictAttachmentJson.Text(StrictAttachmentJson.Required(surface, "key", surfacePath, Refuse), surfacePath + ".key", Refuse);
            string hash = StrictAttachmentJson.Text(StrictAttachmentJson.Required(surface, "sha256", surfacePath, Refuse), surfacePath + ".sha256", Refuse);
            JsonElement fallback = StrictAttachmentJson.Required(motion, "fallback", path, Refuse);
            return new ClothMotionDefinition(new ClothSurfaceRef(key, hash), ReadFallback(fallback, path + ".fallback", partId, partIndex));
        }
        if (kind != "chains")
            throw new AttachmentSetDataException(AttachmentSetRefusal.MotionKindUnsupported, $"{path}: unsupported motion kind '{kind}'.")
            { JsonPath = path + ".kind", PartId = partId, PartIndex = partIndex, Kind = kind };

        return ReadChains(motion, path, partId, partIndex, linkStretchFormat);

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { JsonPath = jsonPath, PropertyName = property, PartId = partId, PartIndex = partIndex };
    }

    static ChainMotionDefinition ReadFallback(JsonElement fallback, string path, uint partId, int partIndex)
    {
        StrictAttachmentJson.Members(fallback, path, Refuse, "kind", "chains", "links", "linkStretch");
        string kind = StrictAttachmentJson.Text(StrictAttachmentJson.Required(fallback, "kind", path, Refuse), path + ".kind", Refuse);
        if (kind != "chains")
            throw new AttachmentSetDataException(AttachmentSetRefusal.MotionKindUnsupported, "Cloth fallback requires chain motion.")
            { JsonPath = path + ".kind", PartId = partId, PartIndex = partIndex, Kind = kind };
        return ReadChains(fallback, path, partId, partIndex, linkStretchFormat: true);

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { JsonPath = jsonPath, PropertyName = property, PartId = partId, PartIndex = partIndex };
    }

    static ChainMotionDefinition ReadChains(JsonElement motion, string path, uint partId, int partIndex, bool linkStretchFormat)
    {
        StrictAttachmentJson.Members(motion, path, Refuse,
            linkStretchFormat ? ["kind", "chains", "links", "linkStretch"] : ["kind", "chains", "links"]);
        JsonElement chains = StrictAttachmentJson.ArrayValue(
            StrictAttachmentJson.Required(motion, "chains", path, Refuse), path + ".chains", Refuse);
        int count = chains.GetArrayLength();
        AttachmentSetMotion.CheckChainCount(count, partId, partIndex, path);
        var definitions = new ChainDefinition[count];
        for (int chainIndex = 0; chainIndex < count; chainIndex++)
            definitions[chainIndex] = ReadChain(chains[chainIndex], path, partId, partIndex, chainIndex);
        ChainLink[] links = motion.TryGetProperty("links", out JsonElement linksValue)
            ? ReadLinks(StrictAttachmentJson.ArrayValue(linksValue, path + ".links", Refuse), path, partId, partIndex) : [];
        float? linkStretch = motion.TryGetProperty("linkStretch", out JsonElement stretchValue)
            ? ReadLinkStretch(stretchValue) : null;
        return new ChainMotionDefinition(definitions, links) { LinkStretch = linkStretch };

        float ReadLinkStretch(JsonElement value)
        {
            // Validate before an authored value just above one can round to 1f.
            if (value.ValueKind == JsonValueKind.Number
                && (!value.TryGetDouble(out double authored) || authored is not (> 0d and <= 1d)))
                throw AttachmentSetMotion.LinkStretchRangeRefusal(partId, partIndex, path);
            return StrictAttachmentJson.Number(value, path + ".linkStretch", Refuse);
        }

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { JsonPath = jsonPath, PropertyName = property, PartId = partId, PartIndex = partIndex };
    }

    static ChainDefinition ReadChain(JsonElement chain, string motionPath, uint partId, int partIndex, int chainIndex)
    {
        string path = motionPath + $".chains[{chainIndex}]";
        StrictAttachmentJson.Members(chain, path, Refuse,
            "joints", "tip", "stiffness", "damping", "gravity", "trail", "angleLimit", "radius");
        JsonElement joints = StrictAttachmentJson.ArrayValue(
            StrictAttachmentJson.Required(chain, "joints", path, Refuse), path + ".joints", Refuse);
        int count = joints.GetArrayLength();
        AttachmentSetMotion.CheckJointCount(count, partId, partIndex, chainIndex, motionPath);
        var names = new string[count];
        for (int jointIndex = 0; jointIndex < count; jointIndex++)
            names[jointIndex] = ReadChainJoint(joints[jointIndex], path, partId, partIndex, chainIndex, jointIndex);

        JsonElement tipValue = StrictAttachmentJson.Required(chain, "tip", path, Refuse);
        Vector3 tip = AttachmentColliderJson.ReadPoint(tipValue, path + ".tip", (_, jsonPath, message, _) =>
            new AttachmentSetDataException(AttachmentSetRefusal.ChainTipInvalid, message)
            { JsonPath = jsonPath, PartId = partId, PartIndex = partIndex, ChainIndex = chainIndex });
        var settings = new ChainSettings(Setting("stiffness"), Setting("damping"), Setting("gravity"), Setting("trail"),
            Setting("angleLimit"), Setting("radius"));
        return new ChainDefinition(names, tip, settings);

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { JsonPath = jsonPath, PropertyName = property, PartId = partId, PartIndex = partIndex, ChainIndex = chainIndex };

        float Setting(string name) => StrictAttachmentJson.Number(
            StrictAttachmentJson.Required(chain, name, path, Refuse), path + "." + name, Refuse);
    }

    static string ReadChainJoint(JsonElement value, string chainPath, uint partId, int partIndex, int chainIndex, int jointIndex)
    {
        string path = chainPath + $".joints[{jointIndex}]";
        if (value.ValueKind != JsonValueKind.String)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonStringRequired, $"{path}: expected a joint name.")
            { JsonPath = path, PartId = partId, PartIndex = partIndex, ChainIndex = chainIndex, JointIndex = jointIndex };
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException error)
        {
            throw new AttachmentSetDataException(AttachmentSetRefusal.ChainJointNameMalformedUtf16,
                $"{path}: a joint name must contain well-formed UTF-16.", error)
            { JsonPath = path, PartId = partId, PartIndex = partIndex, ChainIndex = chainIndex, JointIndex = jointIndex };
        }
    }

    static ChainLink[] ReadLinks(JsonElement links, string motionPath, uint partId, int partIndex)
    {
        int count = links.GetArrayLength();
        AttachmentSetMotion.CheckLinkCount(count, partId, partIndex, motionPath);
        var result = new ChainLink[count];
        for (int linkIndex = 0; linkIndex < count; linkIndex++)
        {
            JsonElement pair = links[linkIndex];
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                || !ReadIndex(pair[0], out int first) || !ReadIndex(pair[1], out int second))
                throw new AttachmentSetDataException(AttachmentSetRefusal.ChainLinkInvalid, "A chain link must be a pair of integer chain indices.")
                {
                    JsonPath = motionPath + $".links[{linkIndex}]",
                    PartId = partId, PartIndex = partIndex, LinkIndex = linkIndex,
                };
            result[linkIndex] = new ChainLink(first, second);
        }
        return result;

        static bool ReadIndex(JsonElement value, out int index)
        {
            index = default;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out index);
        }
    }

    internal static void RequireLayerForAdditions(IReadOnlyList<AttachmentPartDefinition> parts, IReadOnlyList<ColliderDefinition> colliders)
    {
        for (int index = 0; index < parts.Count; index++)
            if (parts[index] is SkinnedAttachmentPartDefinition { Motion: not null } part)
                throw new AttachmentSetDataException(AttachmentSetRefusal.MotionWithoutLayer, "A set with motion requires a layer.")
                { JsonPath = "$", PropertyName = "layer", PartId = part.Id, PartIndex = index };
        if (colliders.Count > 0)
            throw new AttachmentSetDataException(AttachmentSetRefusal.MotionWithoutLayer, "A set with colliders requires a layer.")
            { JsonPath = "$", PropertyName = "layer" };
    }

    internal static bool HasAdditions(AttachmentSetDefinition set) => set.Colliders.Count > 0
        || set.Parts.Any(part => part is SkinnedAttachmentPartDefinition { Motion: not null });

    internal static bool HasLinkStretch(AttachmentSetDefinition set) =>
        set.Parts.Any(part => part is SkinnedAttachmentPartDefinition skin && PartMotionFallback.Chains(skin.Motion)?.LinkStretch is not null);

    internal static void WriteColliders(Utf8JsonWriter writer, IReadOnlyList<ColliderDefinition> colliders)
    {
        if (colliders.Count == 0) return;
        writer.WriteStartArray("colliders");
        foreach (ColliderDefinition collider in colliders) AttachmentColliderJson.Write(writer, collider);
        writer.WriteEndArray();
    }

    internal static void WriteMotion(Utf8JsonWriter writer, PartMotionDefinition? motion)
    {
        if (motion is ClothMotionDefinition cloth)
        {
            writer.WriteStartObject("motion");
            writer.WriteString("kind", "cloth");
            writer.WriteStartObject("surface");
            writer.WriteString("key", cloth.Surface.Key);
            writer.WriteString("sha256", cloth.Surface.Sha256);
            writer.WriteEndObject();
            WriteChains(writer, "fallback", cloth.Fallback);
            writer.WriteEndObject();
        }
        else if (PartMotionFallback.Chains(motion) is { } chains) WriteChains(writer, "motion", chains);
    }

    static void WriteChains(Utf8JsonWriter writer, string property, ChainMotionDefinition chains)
    {
        writer.WriteStartObject(property);
        writer.WriteString("kind", "chains");
        writer.WriteStartArray("chains");
        foreach (ChainDefinition chain in chains.Chains) WriteChain(writer, chain);
        writer.WriteEndArray();
        if (chains.Links.Count > 0)
        {
            writer.WriteStartArray("links");
            foreach (ChainLink link in chains.Links)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(link.First);
                writer.WriteNumberValue(link.Second);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (chains.LinkStretch is { } stretch) writer.WriteNumber("linkStretch", stretch);
        writer.WriteEndObject();
    }

    static void WriteChain(Utf8JsonWriter writer, ChainDefinition chain)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("joints");
        foreach (string joint in chain.Joints) writer.WriteStringValue(joint);
        writer.WriteEndArray();
        AttachmentColliderJson.WritePoint(writer, "tip", chain.Tip);
        ChainSettings settings = chain.Settings;
        WriteNumber("stiffness", settings.Stiffness);
        WriteNumber("damping", settings.Damping);
        WriteNumber("gravity", settings.Gravity);
        WriteNumber("trail", settings.Trail);
        WriteNumber("angleLimit", settings.AngleLimit);
        WriteNumber("radius", settings.Radius);
        writer.WriteEndObject();

        void WriteNumber(string member, float value) => writer.WriteNumber(member, value == 0 ? 0f : value);
    }

    static Exception RootRefusal(StrictAttachmentJson.Fault fault, string path, string message, string? property) =>
        new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message) { JsonPath = path, PropertyName = property };
}
