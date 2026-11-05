namespace KhaozEngine.Render3D;

/// <summary>The stable reasons WaterFrame input or state is refused.</summary>
public enum WaterFrameRefusal
{
    /// <summary>Refuses a draw whose ocean demand differs from its prepared frame.</summary>
    OceanDemandChanged,

    /// <summary>Refuses an absolute water queue of a different length than the prepared frame.</summary>
    AbsoluteQueueCountMismatch,

    /// <summary>Refuses recording bound optics inputs that changed or were not prepared.</summary>
    OpticsInputsChangedOrUnprepared,
}
