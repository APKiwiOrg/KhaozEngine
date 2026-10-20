using System;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A JSON Schema evaluator pass over the marker payload cases that
/// <c>MapMarkerFormatTests.MarkerPayloadClosedSchema_Refuses</c> refuses at runtime. The schema refuses each case except
/// the recorded gaps, which the runtime refuses, so drift in either direction fails here.</summary>
public sealed class MarkerPayloadSchemaTests
{
    /// <summary>Cases the schema admits and the runtime refuses. JSON Schema numbers cannot tell -0 from +0, and minLength
    /// admits a whitespace-only ID. A repeated member is invisible to the parsed JSON value the evaluator checks. A JSON
    /// number beyond the binary32 range is still a schema number, while the runtime refuses the non-finite float.</summary>
    static readonly string[] SchemaGaps =
    {
        "support-negative-zero", "whitespace-id",
        "duplicate-member", "binding-duplicate-member",
        "nonfinite-y", "nonfinite-yaw", "nonfinite-search",
    };

    [Theory]
    [InlineData("future-payload")]
    [InlineData("unknown-member")]
    [InlineData("case-member")]
    [InlineData("duplicate-member")]
    [InlineData("binding-unknown-member")]
    [InlineData("binding-case-member")]
    [InlineData("binding-duplicate-member")]
    [InlineData("payload-two-missing-height")]
    [InlineData("payload-two-null-height")]
    [InlineData("payload-one-height")]
    [InlineData("payload-one-binding")]
    [InlineData("payload-one-archetype")]
    [InlineData("explicit-binding")]
    [InlineData("support-without-binding")]
    [InlineData("support-null-binding")]
    [InlineData("support-y-one")]
    [InlineData("support-negative-zero")]
    [InlineData("nonfinite-y")]
    [InlineData("nonfinite-yaw")]
    [InlineData("null-tags")]
    [InlineData("null-tag")]
    [InlineData("empty-archetype")]
    [InlineData("null-archetype")]
    [InlineData("unknown-height-mode")]
    [InlineData("unknown-binding-kind")]
    [InlineData("surface-reference-y")]
    [InlineData("surface-search-below")]
    [InlineData("surface-space-id")]
    [InlineData("space-missing-reference-y")]
    [InlineData("space-surface-id")]
    [InlineData("binding-null-member")]
    [InlineData("space-null-search")]
    [InlineData("negative-search")]
    [InlineData("nonfinite-search")]
    [InlineData("whitespace-id")]
    public void MarkerPayloadSchemaEvaluator_AgreesWithRuntimeRefusal(string variant)
    {
        var (form, mutate, duplicate) = Case(variant);
        var root = JsonNode.Parse(MapDocumentFile.SaveText(MarkerFixtures.Create("payload-two").Document))!.AsObject();
        string schema = MapDocumentSchema.GetJson();
        ValidationReport control = JsonSchemaValidator.Validate(root.ToJsonString(), schema);
        Assert.True(control.IsValid, string.Join("\n", control.Errors));

        mutate(root["buildingIntent"]!["markers"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == "w-" + form)!.AsObject());
        string json = Duplicated(root.ToJsonString(), duplicate);
        ValidationReport report = JsonSchemaValidator.Validate(json, schema);
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(json));
        if (SchemaGaps.Contains(variant)) Assert.True(report.IsValid, variant + " is a recorded schema gap: " + string.Join("\n", report.Errors));
        else Assert.False(report.IsValid, variant + " must be refused by the schema");
    }

    // The same mutations as the runtime test, applied to the saved document's marker of the named height form.
    static (string Form, Action<JsonObject> Mutate, string? Duplicate) Case(string variant)
    {
        static JsonObject Binding(JsonObject m) => m["supportBinding"]!.AsObject();
        static void Rename(JsonObject o, string from, string to) { JsonNode? v = o[from]; o.Remove(from); o[to] = v; }
        return variant switch
        {
            "future-payload" => ("explicit", m => m["payloadVersion"] = 3, null),
            "unknown-member" => ("explicit", m => m["extra"] = 1, null),
            "case-member" => ("explicit", m => Rename(m, "heightMode", "HeightMode"), null),
            "duplicate-member" => ("explicit", _ => { }, "\"heightMode\":\"Explicit\""),
            "binding-unknown-member" => ("surface", m => Binding(m)["extra"] = 1, null),
            "binding-case-member" => ("surface", m => Rename(Binding(m), "surfaceId", "SurfaceId"), null),
            "binding-duplicate-member" => ("surface", _ => { }, "\"surfaceId\":\"floor\""),
            "payload-two-missing-height" => ("explicit", m => m.Remove("heightMode"), null),
            "payload-two-null-height" => ("explicit", m => m["heightMode"] = null, null),
            "payload-one-height" => ("explicit", m => m["payloadVersion"] = 1, null),
            "payload-one-binding" => ("space", m => { m["payloadVersion"] = 1; m.Remove("heightMode"); }, null),
            "payload-one-archetype" => ("explicit", m => { m["payloadVersion"] = 1; m.Remove("heightMode"); m["archetypeId"] = "guard"; }, null),
            "explicit-binding" => ("explicit", m => m["supportBinding"] = JsonNode.Parse("""{"kind":"Surface","surfaceId":"floor"}"""), null),
            "support-without-binding" => ("surface", m => m.Remove("supportBinding"), null),
            "support-null-binding" => ("surface", m => m["supportBinding"] = null, null),
            "support-y-one" => ("surface", m => m["position"]!["y"] = 1, null),
            "support-negative-zero" => ("space", m => m["position"]!["y"] = JsonNode.Parse("-0.0"), null),
            "nonfinite-y" => ("explicit", m => m["position"]!["y"] = JsonNode.Parse("1e39"), null),
            "nonfinite-yaw" => ("explicit", m => m["yawRadians"] = JsonNode.Parse("-1e39"), null),
            "null-tags" => ("explicit", m => m["tags"] = null, null),
            "null-tag" => ("explicit", m => m["tags"] = new JsonArray("first", null), null),
            "empty-archetype" => ("surface", m => m["archetypeId"] = "", null),
            "null-archetype" => ("surface", m => m["archetypeId"] = null, null),
            "unknown-height-mode" => ("explicit", m => m["heightMode"] = "Floating", null),
            "unknown-binding-kind" => ("surface", m => Binding(m)["kind"] = "Volume", null),
            "surface-reference-y" => ("surface", m => Binding(m)["referenceY"] = 1, null),
            "surface-search-below" => ("surface", m => Binding(m)["searchBelow"] = 0.5, null),
            "surface-space-id" => ("surface", m => Binding(m)["spaceId"] = "level-2", null),
            "space-missing-reference-y" => ("space", m => Binding(m).Remove("referenceY"), null),
            "space-surface-id" => ("space", m => Binding(m)["surfaceId"] = "floor", null),
            "binding-null-member" => ("surface", m => Binding(m)["spaceId"] = null, null),
            "space-null-search" => ("space", m => Binding(m)["searchAbove"] = null, null),
            "negative-search" => ("space", m => Binding(m)["searchBelow"] = -0.5, null),
            "nonfinite-search" => ("space", m => Binding(m)["searchAbove"] = JsonNode.Parse("1e39"), null),
            "whitespace-id" => ("explicit", m => m["id"] = "   ", null),
            _ => throw new ArgumentException("Unknown marker case " + variant),
        };
    }

    static string Duplicated(string json, string? member)
    {
        if (member is null) return json;
        int at = json.IndexOf(member, StringComparison.Ordinal);
        Assert.True(at >= 0 && json.IndexOf(member, at + member.Length, StringComparison.Ordinal) < 0, "Duplicated member must be unique: " + member);
        return json.Insert(at, member + ",");
    }
}
