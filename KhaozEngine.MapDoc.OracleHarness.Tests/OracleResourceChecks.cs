using System;
using System.Runtime.Versioning;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record OracleComparisonFailure(string Category, object Detail, SceneFailureClassification Classification);

/// <summary>Attaches resource identity at the check site, before composite evidence is serialized.</summary>
internal sealed class OracleResourceChecks(Action<string, bool, object, SceneFailureClassification> check,
    string resourceKind, string keyClass, string fieldPrefix = "")
{
    internal void Check(string category, bool ok, object detail)
    {
        string field = category.StartsWith(fieldPrefix, StringComparison.Ordinal) ? category[fieldPrefix.Length..] : category;
        check(category, ok, detail, new(resourceKind, keyClass, field));
    }

    internal void Near(string category, double[] expected, double[] actual, double tolerance, object owner)
    {
        for (int component = 0; component < expected.Length; component++)
        {
            double error = Math.Abs(expected[component] - actual[component]);
            Check(category, double.IsFinite(error) && error <= tolerance,
                new { owner, component, expected = expected[component], actual = actual[component], error, tolerance });
        }
    }

    [UnsupportedOSPlatform("windows")]
    internal static OracleResourceChecks ForReport(PrivateOracleReport report, string kind, string keyClass) =>
        new((category, ok, detail, classification) => ShippedSceneComparison.Check(report, category, ok, detail, classification), kind, keyClass);
}
