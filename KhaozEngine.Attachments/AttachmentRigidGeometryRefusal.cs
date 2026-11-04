namespace KhaozEngine.Attachments;

/// <summary>Stable reasons for refusing AttachmentRigidGeometry input data.</summary>
public enum AttachmentRigidGeometryRefusal
{
    /// <summary>Refuses skin data in rigid attachment geometry.</summary>
    SkinPresent,

    /// <summary>Refuses animation in rigid attachment geometry.</summary>
    AnimationPresent,

    /// <summary>Refuses a rigid node carrying skin or morph data.</summary>
    NodeDeformationPresent,

    /// <summary>Refuses mesh morph weights in rigid geometry.</summary>
    MeshMorphWeightsPresent,

    /// <summary>Refuses primitive morph targets in rigid geometry.</summary>
    PrimitiveMorphTargetsPresent,

    /// <summary>Refuses joint or weight vertex attributes in rigid geometry.</summary>
    SkinAttributePresent,
}
