namespace KhaozEngine.Gpu.D3D11;

/// <summary>Why D3D11CompilerEvidence refused an operation.</summary>
public enum D3D11CompilerEvidenceRefusal
{
    /// <summary>Refuses retaining combined compiler flags beyond the bounded pipeline evidence profile.</summary>
    CombinedFlagsTooLarge,

    /// <summary>Refuses compiler evidence without valid construction correspondence identity.</summary>
    ConstructionIdentityInvalid,

    /// <summary>Refuses invalid compiler evidence that has no originating failure event.</summary>
    FailureOriginMissing,

    /// <summary>Refuses reading diagnostics from a disposed or lost pipeline, shader or device.</summary>
    PipelineUnavailable,

    /// <summary>Refuses a compiler read using a different fixed evidence profile.</summary>
    ReadProfileMismatch,

    /// <summary>Refuses a compiler read without captured reflection for every stage.</summary>
    ReflectionNotCaptured,
}
