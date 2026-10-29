using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Counts all failures independently of the bounded example payloads.</summary>
internal sealed class SceneFailureAggregation
{
    readonly Dictionary<string, Dictionary<SceneFailureClassification, int>> _categories = new(StringComparer.Ordinal);

    internal void Add(string category, string detailJson, SceneFailureClassification? classification = null)
    {
        var identity = classification ?? Infer(category, detailJson);
        if (!_categories.TryGetValue(category, out var counts)) _categories.Add(category, counts = []);
        counts.TryGetValue(identity, out int count); counts[identity] = checked(count + 1);
    }

    static SceneFailureClassification Infer(string category, string detailJson)
    {
        using var json = JsonDocument.Parse(detailJson);
        var detail = json.RootElement;
        string kind = "Scene", keyClass = "scene", field = category;
        if (detail.ValueKind == JsonValueKind.Object)
        {
            if (detail.TryGetProperty("differingField", out var differing)) field = differing.GetString() ?? category;
            if (detail.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object)
            {
                if (key.TryGetProperty("kind", out var value)) kind = value.ToString();
                if (key.TryGetProperty("partId", out var part)) keyClass = SceneFailureClassification.Class(kind, part.GetString());
            }
        }
        return new(kind, keyClass, field);
    }

    internal IEnumerable<string> Summaries() => _categories.OrderBy(p => p.Key, StringComparer.Ordinal).Select(category =>
        JsonSerializer.Serialize(new
        {
            category = category.Key,
            total = category.Value.Values.Sum(),
            classifications = category.Value.OrderBy(p => p.Key.ResourceKind, StringComparer.Ordinal)
                .ThenBy(p => p.Key.KeyClass, StringComparer.Ordinal).ThenBy(p => p.Key.DifferingField, StringComparer.Ordinal)
                .Select(p => new { resourceKind = p.Key.ResourceKind, keyClass = p.Key.KeyClass, differingField = p.Key.DifferingField, count = p.Value })
        }));
}
