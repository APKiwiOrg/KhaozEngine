namespace KhaozEngine.Attachments;

/// <summary>Stable reasons for refusing RigIdentity input data.</summary>
public enum RigIdentityRefusal
{
    /// <summary>Refuses rig identity without exactly one embedded buffer.</summary>
    EmbeddedBufferCountMismatch,

    /// <summary>Refuses rig identity without exactly one object skin.</summary>
    SkinCountMismatch,

    /// <summary>Refuses a fitted mesh node referencing another skin.</summary>
    MeshNodeSkinMismatch,

    /// <summary>Refuses a skinned node without mesh geometry.</summary>
    MeshNodeHasNoMesh,

    /// <summary>Refuses node or mesh morph weights in rig-v1.</summary>
    MorphWeightsUnsupported,

    /// <summary>Refuses rig identity without fitted skinned mesh geometry.</summary>
    SkinnedGeometryMissing,

    /// <summary>Refuses a fitted mesh without primitive geometry.</summary>
    PrimitivesEmpty,

    /// <summary>Refuses a non-object or morph-bearing fitted primitive.</summary>
    PrimitiveShapeOrMorphUnsupported,

    /// <summary>Refuses Draco-compressed fitted primitive geometry.</summary>
    CompressionUnsupported,

    /// <summary>Refuses a fitted primitive mode outside the glTF mode domain.</summary>
    PrimitiveModeInvalid,

    /// <summary>Refuses a noncanonical influence-set attribute suffix.</summary>
    InfluenceAttributeMalformed,

    /// <summary>Refuses fitted geometry missing influence set zero.</summary>
    FirstInfluenceSetMissing,

    /// <summary>Refuses a fitted vertex with no positive influence weight.</summary>
    WeightsAllZero,

    /// <summary>Refuses an index outside fitted POSITION data.</summary>
    VertexIndexOutsidePositionCount,

    /// <summary>Refuses an out-of-range joint or invalid weight in fitted geometry.</summary>
    JointIndexOrWeightInvalid,

    /// <summary>Refuses fitted attributes with differing vertex counts.</summary>
    VertexAccessorCountMismatch,

    /// <summary>Refuses a non-finite fitted accessor value.</summary>
    FitValueNonFinite,

    /// <summary>Refuses a rest transform authored as both matrix and TRS.</summary>
    MatrixAndTrsBothPresent,

    /// <summary>Refuses a rest transform with the wrong number of components.</summary>
    RestTransformLengthMismatch,

    /// <summary>Refuses a non-numeric or non-finite rest transform component.</summary>
    RestTransformComponentInvalid,
}
