using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Every closed payload 1 case evaluated against both the published document schema and the runtime decoder. The
/// schema refuses everything the runtime refuses except the documented runtime-only checks, which JSON Schema cannot
/// express over a parsed value, and it never refuses what the runtime accepts.</summary>
public sealed class FoliageSchemaParityTests
{
    // The cases the published schema accepts and the decoder refuses, in case order. A repeated member vanishes when the
    // schema parses the text. Uniqueness by a key inside array items, ordering, cross-references, cross-field ranges,
    // sums and checks against the decoded density span several values, which JSON Schema cannot relate. Three are
    // expressible in principle and are reported for the schema owner: a number beyond binary32 range (cell-nonfinite)
    // and whitespace-only support IDs (mask-surface-blank, owner-surface-blank, owner-space-blank).
    static readonly string[] RuntimeOnly =
    [
        "set-duplicate", "id-duplicate", "cell-nonfinite", "scale-reversed", "model-duplicate", "weight-sum", "material-unknown",
        "allowed-unsorted", "material-duplicate", "chunk-duplicate", "chunk-outside-x", "chunk-outside-z", "chunk-padding-x",
        "chunk-padding-z", "rule-rect-reversed", "rule-rect-empty", "rule-id-duplicate", "mask-id-duplicate", "mask-rect-reversed",
        "mask-surface-blank", "owner-surface-blank", "owner-space-blank", "owner-duplicate", "ordinal-duplicate",
    ];

    [Fact]
    public void EveryPayloadCase_SchemaAndRuntimeAgreeOutsideTheRuntimeOnlyChecks()
    {
        string schema = MapDocumentSchema.GetJson();
        var runtimeOnly = new List<string>();
        foreach (var (variant, fragment) in FoliageFixtures.PayloadCases)
        {
            string text = FoliageFixtures.Malformed(variant);
            bool schemaValid = JsonSchemaValidator.Validate(text, schema).IsValid;
            MapDocumentException? refusal = Record.Exception(() => MapDocumentFile.LoadText(text)) as MapDocumentException;
            if (fragment is null) Assert.Null(refusal);
            else Assert.Contains(fragment, Assert.IsType<MapDocumentException>(refusal).Message, StringComparison.Ordinal);
            if (fragment is null) Assert.True(schemaValid, variant + ": the schema refuses a document the runtime accepts");
            else if (schemaValid) runtimeOnly.Add(variant);
        }
        Assert.True(RuntimeOnly.SequenceEqual(runtimeOnly), "runtime-only cases: " + string.Join(", ", runtimeOnly));
        Assert.Equal(99, FoliageFixtures.PayloadCases.Select(c => c.Variant).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void StoredForm_SchemaAndRuntimeAgreeOnTheSameCases()
    {
        // The tiled manifest carries the same layer members with chunk digests instead of density. Every inline case that
        // changes a layer member other than density is replayed against the manifest schema and the tiled loader.
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(FoliageFixtures.Create("decorated").Document, dir.Root);
        JsonObject manifest = FoliageStorageFixtures.Manifest(dir.Root);
        string schema = MapDocumentSchema.GetManifestJson();
        Assert.True(JsonSchemaValidator.Validate(manifest.ToJsonString(), schema).IsValid);
        JsonObject control = JsonNode.Parse(FoliageFixtures.Malformed("control"))!["foliage"]!["layers"]![0]!.AsObject();
        int replayed = 0;
        foreach (var (variant, fragment) in FoliageFixtures.PayloadCases.Where(c => c.Fragment is not null && Replayable(c.Variant)))
        {
            JsonObject inline = JsonNode.Parse(FoliageFixtures.Malformed(variant))!.AsObject();
            JsonObject layer = inline["foliage"]!["layers"]![0]!.AsObject();
            JsonObject stored = manifest.DeepClone().AsObject();
            JsonObject target = stored["foliage"]!["layers"]![0]!.AsObject();
            foreach (var (name, value) in layer.ToArray())
                if (name != "chunks" && !JsonNode.DeepEquals(value, control[name])) target[name] = value?.DeepClone();
            foreach (string name in control.Select(p => p.Key).Where(n => n != "chunks" && !layer.ContainsKey(n)).ToArray()) target.Remove(name);
            foreach (string name in layer.Select(p => p.Key).Where(n => !control.ContainsKey(n)).ToArray()) target[name] = layer[name]?.DeepClone();
            string text = stored.ToJsonString();
            bool schemaValid = JsonSchemaValidator.Validate(text, schema).IsValid;
            File.WriteAllText(Path.Combine(dir.Root, "map.json"), text);
            var refusal = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(dir.Root));
            Assert.Contains(fragment!, refusal.Message, StringComparison.Ordinal);
            Assert.True(RuntimeOnly.Contains(variant) == schemaValid, variant + (schemaValid ? ": the stored schema accepts" : ": the stored schema refuses"));
            replayed++;
        }
        File.WriteAllText(Path.Combine(dir.Root, "map.json"), manifest.ToJsonString());
        Assert.NotNull(MapDocumentFile.LoadTiled(dir.Root).Foliage);
        Assert.Equal(70, replayed);
    }

    // Layer-member cases with a stored counterpart. Root, set, identity, density, chunk and multi-layer ceiling cases
    // change parts the manifest stores differently, since its packs and chunk digests are keyed by layer ID, so the inline
    // proof covers them.
    static bool Replayable(string variant) => !(variant.StartsWith("density", StringComparison.Ordinal) ||
        variant.StartsWith("chunk", StringComparison.Ordinal) || variant.StartsWith("set-", StringComparison.Ordinal) ||
        variant.StartsWith("id-", StringComparison.Ordinal) ||
        variant is "format-ten" or "root-null" or "ceiling-layers" or "ceiling-chunks" or "ceiling-density" or "layer-case-duplicate");
}
