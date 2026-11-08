namespace KhaozEngine.Tests.Gpu;

/// <summary>Why a golden comparison or bake failed.</summary>
public enum GoldenComparisonRefusal
{
    /// <summary>The captured frame differed from the golden beyond tolerance.</summary>
    ImageRegressed,
    /// <summary>The named backend golden did not exist.</summary>
    MissingGolden,
    /// <summary>The bake was refused before any reference or evidence was written.</summary>
    BakeRefused,
}
