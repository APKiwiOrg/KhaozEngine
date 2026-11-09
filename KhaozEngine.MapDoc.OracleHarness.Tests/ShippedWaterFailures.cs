using System;
using System.Runtime.Versioning;
using KhaozEngine.Tests.MapDocCompatibility;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static class ShippedWaterFailures
{
    internal static void Report(PrivateOracleReport report, LegacyWaterReport water)
    {
        foreach (string failure in water.Failures)
            ShippedSceneComparison.Check(report, "water", false, new { failure }, Classify(failure));
    }

    // The shared legacy comparator exposes text failures. Keep its verdicts and classify every known check.
    static SceneFailureClassification Classify(string failure)
    {
        if (failure == "native water section is missing") return new("Water", "section", "presence");
        if (failure == "legacy and native body piece counts differ") return new("Water", "body", "inventory");
        foreach (var (pattern, keyClass, field) in new[]
        {
            (": expected one native body, found ", "body", "presence"),
            (": level differs", "body", "level"),
            (": rectangle pieces differ", "body", "domain"),
            (": boundary point is not representable as a query input", "sample", "boundary-query-input"),
            (": cell centre for cell (", "cell", "query-input"),
            (": membership ", "sample", "membership"),
            (": water column belongs to another body or has multiple intervals", "cell", "ownership"),
            (": water column ", "cell", "status"),
            (": wet interval or level differs", "cell", "interval"),
            (": midpoint sample ", "sample", "midpoint"),
            (": expected a wet interval above the floor in its covered space", "cell", "wet-interval")
        })
            if (failure.Contains(pattern, StringComparison.Ordinal)) return new("Water", keyClass, field);
        // An unfamiliar legacy diagnostic remains a distinct private field, so no failure loses its identity.
        return new("Water", "body", failure);
    }
}
