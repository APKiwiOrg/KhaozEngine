namespace KhaozEngine.Tests.MapDocCompatibility;

/// <summary>Reasons a test-only legacy water comparison refuses its source.</summary>
internal enum LegacyWaterComparisonRefusal
{
    /// <summary>Refuses comparison water outside plane zero.</summary>
    PlaneUnsupported,
    /// <summary>Refuses a comparison without a plane-zero source region.</summary>
    RegionMissing,
}
