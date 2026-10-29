using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static partial class ShippedFoliageComparison
{
    /// <summary>Aggregate counts and bounded mismatch details. Failure categories keep their first-seen order.</summary>
    sealed class FoliageTally
    {
        readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<string, long>> _breakdowns = new(StringComparer.Ordinal);
        readonly Dictionary<string, (long Count, List<Dictionary<string, object?>> Details)> _failures = new(StringComparer.Ordinal);
        readonly List<string> _order = new();
        readonly Dictionary<(string Category, SceneFailureClassification Classification), long> _classified = new();

        internal void Add(string name, long value) => _counts[name] = checked(_counts.GetValueOrDefault(name) + value);
        internal long Get(string name) => _counts.GetValueOrDefault(name);

        internal void Count(string breakdown, string key)
        {
            if (!_breakdowns.TryGetValue(breakdown, out var counts)) _breakdowns.Add(breakdown, counts = new(StringComparer.Ordinal));
            counts[key] = checked(counts.GetValueOrDefault(key) + 1);
        }

        internal void FailResource(string category, Dictionary<string, object?> detail, string keyClass, string field) =>
            Fail(category, detail, new("Foliage", keyClass, field));

        internal void Fail(string category, Dictionary<string, object?> detail, SceneFailureClassification? classification = null)
        {
            var key = (category, classification ?? new("Scene", "scene", category));
            _classified[key] = checked(_classified.GetValueOrDefault(key) + 1);
            if (!_failures.TryGetValue(category, out var entry))
            {
                _order.Add(category);
                entry = (0, new());
            }
            if (entry.Details.Count < RetainedDetails) entry.Details.Add(detail);
            _failures[category] = (checked(entry.Count + 1), entry.Details);
        }

        internal string Summary(int heads) => JsonSerializer.Serialize(new
        {
            scope = Scope,
            heads,
            counts = _counts,
            breakdowns = _breakdowns,
            failures = _order.ToDictionary(c => c, c => _failures[c].Count, StringComparer.Ordinal),
        });

        internal void Report(PrivateOracleReport report)
        {
            foreach (string category in _order)
            {
                bool retain = true;
                foreach (var entry in _classified.Where(p => p.Key.Category == category))
                {
                    report.Check(category, false, retain ? Details(category) : "{}", classification: entry.Key.Classification, failureCount: entry.Value);
                    retain = false;
                }
            }
        }

        string Details(string category) => JsonSerializer.Serialize(new { count = _failures[category].Count, details = _failures[category].Details });
    }
}
