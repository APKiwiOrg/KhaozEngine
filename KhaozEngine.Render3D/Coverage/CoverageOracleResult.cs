namespace KhaozEngine.Render3D;

/// <summary>The failing phase of a headless reference check. Construction always precedes certification.</summary>
public enum CoverageOracleStage
{
    /// <summary>No refusal.</summary>
    None = 0,
    /// <summary>Reference skinning, command admission or candidate construction refused.</summary>
    Construction = 1,
    /// <summary>Exact geometry certification refused the constructed reference surface.</summary>
    Certification = 2,
}

/// <summary>A headless refusal with the original phase's complete failure record. Default means success.</summary>
public readonly record struct CoverageOracleFailure(CoverageOracleStage Stage, CoverageFailure Failure);

/// <summary>Exact geometry evidence for the supplied palette, model and command bits. This grants no drawable
/// permission. Partial transitions report FullCoreClearance false and MinimumCoreClearance NaN.</summary>
public readonly record struct CoverageOracleCertificate(bool FullCoreClearance, double MinimumCoreClearance, int DisplacedVertices);
