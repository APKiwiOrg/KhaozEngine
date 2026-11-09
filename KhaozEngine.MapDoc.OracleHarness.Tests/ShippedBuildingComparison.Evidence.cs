using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedBuildingComparison
{
    sealed class Evidence
    {
        readonly List<string> _failures = new();
        readonly List<object> _records = new();
        readonly List<OracleComparisonFailure> _classified = new();
        internal OracleResourceChecks Resource(string kind, string keyClass) => new(CheckClassified, kind, keyClass);
        internal void Check(string category, bool ok, object detail) => CheckClassified(category, ok, detail, new("Scene", "scene", category));
        void CheckClassified(string category, bool ok, object detail, SceneFailureClassification classification)
        {
            if (ok) return;
            _failures.Add(category); _records.Add(new { category, detail });
            _classified.Add(new(category, detail, classification));
        }
        internal void Record(string category, object detail) => _records.Add(new { category, detail });
        internal ShippedBuildingReport Finish() => new(_failures.AsReadOnly(), JsonSerializer.Serialize(new { failures = _failures, records = _records },
            new JsonSerializerOptions { IncludeFields = true }), _classified.AsReadOnly());
    }
}
