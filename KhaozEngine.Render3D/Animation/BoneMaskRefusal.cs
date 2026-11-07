namespace KhaozEngine.Render3D;

/// <summary>Stable reasons named bone mask inputs are refused.</summary>
public enum BoneMaskRefusal
{
    /// <summary>Refuses a joint mask name absent from the skeleton.</summary>
    JointNameMissing,
    /// <summary>Refuses an explicit node-name table with the wrong length.</summary>
    NameCountMismatch,
    /// <summary>Refuses a subtree root absent from the supplied name table.</summary>
    SubtreeRootMissing,
}
