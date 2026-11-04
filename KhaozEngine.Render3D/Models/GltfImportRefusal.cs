namespace KhaozEngine.Render3D;

/// <summary>The reason a GltfImport input was refused.</summary>
public enum GltfImportRefusal
{
    /// <summary>Refuses a model rejected by strict import validation after the index preflight.</summary>
    ValidationFailed,

    /// <summary>Refuses malformed imported model data without exposing a SharpGLTF exception type.</summary>
    MalformedModel,

    /// <summary>Refuses a rigid import with no triangle geometry.</summary>
    NoTriangles,

    /// <summary>Refuses an import with no usable skinned geometry.</summary>
    NoSkinnedMesh,

    /// <summary>Refuses a skin larger than the renderer joint limit.</summary>
    SkinJointLimitExceeded,

    /// <summary>Refuses a vertex influence outside the imported skin joint count.</summary>
    SkinJointIndexOutsideRange,
}
