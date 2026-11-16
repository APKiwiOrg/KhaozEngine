using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

internal sealed class CoverageArtifactFixture : IDisposable
{
    internal string Folder { get; } = Path.Combine(Path.GetTempPath(),
        "khaozengine-coverage-packed-input", "artifact-" + Guid.NewGuid().ToString("N"));
    internal string Root => Path.Combine(Folder, "evidence.json");
    internal CoverageEvidenceManifest Evidence { get; }
    internal CoverageArtifactFixture()
    {
        Directory.CreateDirectory(Folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var files = CoverageVerifyProcessTests.Files();
        var input = CoverageBuildInput.Read(files.ManifestPath);
        var build = CoverageTableBuilder.Build(input);
        Evidence = build.Evidence;
        Evidence.IncludeReplay(CoverageReplay.Verify(input, build.Table));
    }
    internal CoverageStagedArtifact Stage(int limit = 8192, int part = 1024) =>
        CoverageArtifactWriter.Stage(Root, Evidence.CreateHeader(), Evidence.WriteTo, Folder, limit, part);
    internal static void Publish(CoverageStagedArtifact staged)
    {
        foreach (var part in staged.Parts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part.DestinationPath)!);
            File.Copy(part.StagePath, part.DestinationPath, true);
        }
        File.Copy(staged.RootStagePath, staged.RootDestinationPath, true);
    }
    internal static byte[] Bytes(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
    internal void ReplacePayload(byte[] bytes)
    {
        var root = JsonNode.Parse(File.ReadAllText(Root))!;
        int target = (int)root["parts"]![0]!["byteLength"]!;
        string payloadHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var parts = new JsonArray();
        for (int offset = 0, ordinal = 0; offset < bytes.Length; offset += target, ordinal++)
        {
            byte[] chunk = bytes.AsSpan(offset, Math.Min(target, bytes.Length - offset)).ToArray();
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(chunk));
            string path = Path.GetFileName(Root) + ".parts/" + payloadHash + "/" + ordinal.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "-" + hash + ".part";
            string file = Path.Combine(Folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, chunk);
            parts.Add(new JsonObject { ["ordinal"] = ordinal, ["path"] = path, ["byteLength"] = chunk.Length, ["sha256"] = hash });
        }
        root["parts"] = parts; root["partCount"] = parts.Count;
        root["payloadByteLength"] = bytes.Length; root["payloadSha256"] = payloadHash;
        File.WriteAllText(Root, root.ToJsonString());
    }
    internal static void WriteEvidence(Stream destination, int sampleCount, int entryCount, int sampleIdLength)
    {
        var evidence = CoverageTableBuilder.Build(CoverageBuildFixture.Input()).Evidence;
        var source = JsonNode.Parse(evidence.Write())!.AsObject();
        using var writer = new Utf8JsonWriter(destination, new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        string Id(int i) => i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture).PadRight(sampleIdLength, 'x');
        foreach (var property in source)
        {
            writer.WritePropertyName(property.Key);
            if (property.Key is not ("samples" or "entries" or "pairResults" or "selectedClaims" or "candidateSampleIds" or "candidates"))
            { if (property.Value == null) writer.WriteNullValue(); else property.Value.WriteTo(writer); continue; }
            writer.WriteStartArray();
            if (property.Key == "samples")
                for (int i = 0; i < sampleCount; i++) JsonSerializer.Serialize(writer, new CoverageSampleInventory(Id(i), "clip", "frame-a", null, 1), CoverageArtifactSet.Options);
            if (property.Key == "entries")
                for (int i = 0; i < entryCount; i++) JsonSerializer.Serialize(writer, new CoverageEntryEvidence(i, CoverageBuildFixture.Hash, i < 2 ? i : -1), CoverageArtifactSet.Options);
            if (property.Key == "pairResults")
                for (int e = 0; e < entryCount; e++)
                    for (int i = 0; i < sampleCount; i++)
                    { JsonSerializer.Serialize(writer, new CoveragePairResult(e, Id(i), true, default), CoverageArtifactSet.Options); writer.Flush(); }
            if (property.Key == "selectedClaims")
                for (int i = 0; i < sampleCount; i++) JsonSerializer.Serialize(writer, new CoverageSelectedClaim(1, 0, Id(i), true, default), CoverageArtifactSet.Options);
            if (property.Key == "candidateSampleIds") writer.WriteStringValue(Id(0));
            if (property.Key == "candidates") JsonSerializer.Serialize(writer, new CoverageCandidateEvidence(Id(0), CoverageBuildFixture.Hash), CoverageArtifactSet.Options);
            writer.WriteEndArray(); writer.Flush();
        }
        writer.WriteEndObject();
    }
    public void Dispose() => Directory.Delete(Folder, true);
}
