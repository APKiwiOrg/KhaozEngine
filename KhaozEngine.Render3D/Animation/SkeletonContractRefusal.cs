namespace KhaozEngine.Render3D;

/// <summary>Stable reasons a skeleton contract joint table is refused.</summary>
public enum SkeletonContractRefusal
{
    /// <summary>Refuses a contract with no root joint.</summary>
    EmptyJointTable,
    /// <summary>Refuses an unnamed contract-table joint.</summary>
    JointUnnamed,
    /// <summary>Refuses a first joint that is not the root.</summary>
    FirstJointHasParent,
    /// <summary>Refuses a second root in the joint table.</summary>
    AdditionalRoot,
    /// <summary>Refuses a parent absent from the preceding joint declarations.</summary>
    ParentNotDeclaredBeforeChild,
    /// <summary>Refuses duplicate contract-joint names.</summary>
    DuplicateJointName,
}
