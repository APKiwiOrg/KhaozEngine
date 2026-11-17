using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Attachments;
using static KhaozEngine.Tools.Coverage.CoverageBuildInputJson;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Bounded metadata and exact reference ownership, before any asset binding.</summary>
internal static class CoveragePackedInputJson
{
    internal sealed record Slot(CoverageSampleInventory Inventory, string Path, JsonElement Value, CoveragePoseReference? Reference);
    internal sealed record Metadata(CoverageDomainInventory[] Domains, Slot[] Samples, Slot[][] DenseRows,
        string[] Candidates, CoveragePackedChunkReference[] Chunks);

    internal static Metadata Read(JsonElement root)
    {
        var versionValue = Get(root, "formatVersion", "$");
        if (versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out int version) || version is not (1 or 2))
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnsupportedVersion, "Unsupported manifest version.")
            { Path = "$.formatVersion", FormatVersion = versionValue.ValueKind == JsonValueKind.Number && versionValue.TryGetInt64(out long v) ? v : null };
        string[] members = ["formatVersion", "host", "map", "configuration", "inverseBind", "stateSchemaSha256", "domains", "candidateSampleIds", "samples"];
        Members(root, "$", version == 2 ? [.. members, "poseChunks"] : members);
        foreach (string name in new[] { "host", "map", "configuration" })
        {
            var asset = Get(root, name, "$");
            Members(asset, "$." + name, "path", "sha256");
            _ = Text(asset, "path", "$." + name);
            _ = Digest(Get(asset, "sha256", "$." + name), "$." + name + ".sha256");
        }
        _ = Matrices(Get(root, "inverseBind", "$"), "$.inverseBind", 1024);
        _ = Digest(Get(root, "stateSchemaSha256", "$"), "$.stateSchemaSha256");
        var samples = Array(root, "samples", "$", CoverageBuildInput.MaxSamples);
        string[] candidates = Strings(root, "candidateSampleIds", "$", CoverageBuildInput.MaxCandidates);
        var domains = Array(root, "domains", "$", CoverageBuildInput.MaxSamples);
        long pairs = (long)samples.GetArrayLength() * candidates.Length;
        if (pairs > CoverageBuildInput.MaxPairs) throw Capacity("$.samples", pairs, CoverageBuildInput.MaxPairs);
        long denseCount = 0;
        foreach (var domain in domains.EnumerateArray())
        {
            Members(domain, "$.domains", "id", "kind", "authoredFrameIds", "blendPathIds", "denseRows");
            if (domain.TryGetProperty("denseRows", out _)) denseCount += Array(domain, "denseRows", "$.domains", CoverageBuildInput.MaxSamples).GetArrayLength();
        }
        if (denseCount > CoverageBuildInput.MaxSamples) throw Capacity("$.domains", denseCount, CoverageBuildInput.MaxSamples);
        var chunks = version == 2 ? Array(root, "poseChunks", "$", CoverageBuildInput.MaxPackedChunks).EnumerateArray()
            .Select((c, i) => Chunk(c, i)).ToArray() : [];
        long records = chunks.Sum(c => (long)c.RecordCount);
        if (records > 2L * CoverageBuildInput.MaxSamples) throw Capacity("$.poseChunks", records, 2L * CoverageBuildInput.MaxSamples);
        var used = chunks.Select(c => new bool[c.RecordCount]).ToArray();
        var ordinary = samples.EnumerateArray().Select((s, i) => Sample(s, $"$.samples[{i}]", version, chunks, used)).ToArray();
        var inventories = new List<CoverageDomainInventory>();
        var denseSlots = new List<Slot[]>();
        int domainIndex = 0;
        foreach (var domain in domains.EnumerateArray())
        {
            string path = $"$.domains[{domainIndex++}]";
            var kind = Text(domain, "kind", path) switch
            {
                "authored-clip" => CoverageDomainKind.AuthoredClip,
                "blend-path" => CoverageDomainKind.BlendPath,
                _ => throw Error(CoverageBuildInputRefusal.InvalidValue, path + ".kind"),
            };
            var slots = new List<Slot>();
            var rows = new List<CoverageDenseRowInventory>();
            if (domain.TryGetProperty("denseRows", out var dense))
                foreach (var row in dense.EnumerateArray())
                {
                    string rp = $"{path}.denseRows[{rows.Count}]";
                    Members(row, rp, "denseIndex", "precedingPhaseTick", "resolvedStateKey", "sample", "looped", "clamped");
                    ulong key = Unsigned(Get(row, "resolvedStateKey", rp), rp + ".resolvedStateKey");
                    var sample = Sample(Get(row, "sample", rp), rp + ".sample", version, chunks, used, key);
                    slots.Add(sample);
                    rows.Add(new(Integer(row, "denseIndex", rp), Integer(row, "precedingPhaseTick", rp), key, sample.Inventory,
                        Flag(row, "looped", rp), Flag(row, "clamped", rp)));
                }
            inventories.Add(new(Text(domain, "id", path), kind, System.Array.AsReadOnly(Strings(domain, "authoredFrameIds", path)),
                System.Array.AsReadOnly(Strings(domain, "blendPathIds", path)), rows.AsReadOnly()));
            denseSlots.Add(slots.ToArray());
        }
        for (int i = 0; i < used.Length; i++)
        {
            int missing = System.Array.IndexOf(used[i], false);
            if (missing >= 0) throw ReferenceError($"$.poseChunks[{i}]", i, missing);
        }
        return new(inventories.ToArray(), ordinary, denseSlots.ToArray(), candidates, chunks);
    }

    static CoveragePackedChunkReference Chunk(JsonElement value, int index)
    {
        string path = $"$.poseChunks[{index}]";
        Members(value, path, "path", "byteLength", "sha256", "recordCount");
        var length = Get(value, "byteLength", path);
        if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out long bytes) || bytes <= 0)
            throw Error(CoverageBuildInputRefusal.InvalidValue, path + ".byteLength");
        if (bytes > CoverageBuildInput.MaxPackedChunkBytes)
            throw Capacity(path + ".byteLength", bytes, CoverageBuildInput.MaxPackedChunkBytes, index);
        int count = Integer(value, "recordCount", path);
        if (count == 0) throw ReferenceError(path + ".recordCount", index);
        if (count > 2 * CoverageBuildInput.MaxSamples) throw Capacity(path + ".recordCount", count, 2 * CoverageBuildInput.MaxSamples, index);
        return new(Text(value, "path", path), bytes, Digest(Get(value, "sha256", path), path + ".sha256"), count);
    }

    static Slot Sample(JsonElement value, string path, int version, CoveragePackedChunkReference[] chunks, bool[][] used, ulong? denseKey = null)
    {
        string[] members = ["id", "domainId", "authoredFrameId", "blendPathId", "actorModel", "palette", "commands"];
        if (!denseKey.HasValue) members = [.. members, "stateKey"];
        Members(value, path, version == 2 ? [.. members, "poseRef"] : members);
        ulong key = denseKey ?? Unsigned(Get(value, "stateKey", path), path + ".stateKey");
        string? Optional(string name) => value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null ? String(item, path + "." + name) : null;
        var inventory = new CoverageSampleInventory(Text(value, "id", path), Text(value, "domainId", path), Optional("authoredFrameId"), Optional("blendPathId"), key);
        CoveragePoseReference? reference = null;
        if (version == 2 && value.TryGetProperty("poseRef", out var poseRef))
        {
            if (new[] { "actorModel", "palette", "commands" }.Any(n => value.TryGetProperty(n, out _))) throw ReferenceError(path + ".poseRef");
            Members(poseRef, path + ".poseRef", "chunk", "record");
            int Index(string name)
            {
                var item = Get(poseRef, name, path + ".poseRef");
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int index) || index < 0) throw ReferenceError(path + ".poseRef." + name);
                return index;
            }
            int chunk = Index("chunk"), record = Index("record");
            if (chunk >= chunks.Length || record >= chunks[chunk].RecordCount || used[chunk][record]) throw ReferenceError(path + ".poseRef", chunk, record);
            used[chunk][record] = true;
            reference = new(chunk, record);
        }
        else _ = ReadInline(value, path);
        return new(inventory, path, value, reference);
    }

    internal static CoverageNumericPose ReadInline(JsonElement value, string path)
    {
        var actor = Matrix(Get(value, "actorModel", path), path + ".actorModel");
        var palette = Matrices(Get(value, "palette", path), path + ".palette", 1024);
        var commands = new List<CoveragePackedCommand>();
        var ids = new HashSet<uint>();
        foreach (var command in Array(value, "commands", path, EquipmentCoverageDefinition.MaxCommands).EnumerateArray())
        {
            string cp = path + ".commands";
            Members(command, cp, "id", "equipmentInstanceId", "equipmentWorld", "coverageWorld", "transitionWeight");
            uint id = UInt(command, "id", cp);
            if (!ids.Add(id)) throw Error(CoverageBuildInputRefusal.AssetBinding, cp + ".id");
            ulong instance = Unsigned(Get(command, "equipmentInstanceId", cp), cp + ".equipmentInstanceId");
            if (instance == 0) throw Error(CoverageBuildInputRefusal.InvalidValue, cp + ".equipmentInstanceId");
            commands.Add(new(id, instance, Matrix(Get(command, "equipmentWorld", cp), cp + ".equipmentWorld"),
                Matrix(Get(command, "coverageWorld", cp), cp + ".coverageWorld"), Single(Get(command, "transitionWeight", cp), cp + ".transitionWeight")));
        }
        return new(actor, palette, commands.ToArray());
    }

    static ulong Unsigned(JsonElement value, string path) => value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)
        ? number : throw Error(CoverageBuildInputRefusal.InvalidValue, path);
    static int Integer(JsonElement value, string name, string path)
    {
        var item = Get(value, name, path);
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number) && number >= 0 ? number
            : throw Error(CoverageBuildInputRefusal.InvalidValue, path + "." + name);
    }
    static bool Flag(JsonElement value, string name, string path)
    {
        var item = Get(value, name, path);
        return item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean()
            : throw Error(CoverageBuildInputRefusal.InvalidValue, path + "." + name);
    }
    static CoverageBuildDataException ReferenceError(string path, int? chunk = null, int? record = null) =>
        new(CoverageBuildInputRefusal.PackedReferenceInvalid, "Packed references must cover each record exactly once.")
        { Path = path, ChunkIndex = chunk, RecordIndex = record };
    static CoverageBuildDataException Capacity(string path, long count, long limit, int? chunk = null) =>
        new(CoverageBuildInputRefusal.CapacityExceeded, "Input inventory exceeds capacity.") { Path = path, Count = count, Limit = limit, ChunkIndex = chunk };
    static CoverageBuildDataException Error(CoverageBuildInputRefusal reason, string path) => new(reason, "Coverage input was refused.") { Path = path };
}
