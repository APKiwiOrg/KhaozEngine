namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Rules enforced by the test-only attested comparison readers.</summary>
public enum PrivateOracleDataRefusal
{
    /// <summary>Refuses captured bytes or semantic data differing from their attested digest.</summary>
    AttestedDigestMismatch,
    /// <summary>Refuses incomplete or inconsistent source and native correspondences.</summary>
    CorrespondenceMismatch,
    /// <summary>Refuses a required dependency absent from the attested capture.</summary>
    DependencyUnattested,
    /// <summary>Refuses an extraction changed before capture or during loading.</summary>
    ExtractionChanged,
    /// <summary>Refuses duplicate or noncanonical identities or JSON member names.</summary>
    IdentityNotUniqueOrCanonical,
    /// <summary>Refuses input whose room-cell closure exceeds the comparison work budget.</summary>
    InputBudgetExceeded,
    /// <summary>Refuses a kind, binding mode or convention outside the comparison contract.</summary>
    KindUnsupported,
    /// <summary>Refuses source ownership, membership or support assigned to the wrong scope or plane.</summary>
    OwnershipMismatch,
    /// <summary>Refuses a dependency path outside the permitted extraction-relative shape.</summary>
    PathUnsafe,
    /// <summary>Refuses a reference absent from its declared source or correspondence set.</summary>
    ReferenceMissing,
    /// <summary>Refuses a value whose JSON shape does not match the required schema.</summary>
    SchemaShapeInvalid,
    /// <summary>Refuses source units, coordinates or region ownership outside the comparison contract.</summary>
    SourceCoordinateContractMismatch,
    /// <summary>Refuses a symlink in an attested dependency path.</summary>
    SymlinkUnsupported,
    /// <summary>Refuses a numeric value that must be finite.</summary>
    ValueNonfinite,
    /// <summary>Refuses a finite value outside its permitted domain.</summary>
    ValueOutsideDomain,
    /// <summary>Refuses an absent or empty required value.</summary>
    ValueRequired,
    /// <summary>Refuses an unsupported comparison, document, resolver or payload version.</summary>
    VersionUnsupported,
}
