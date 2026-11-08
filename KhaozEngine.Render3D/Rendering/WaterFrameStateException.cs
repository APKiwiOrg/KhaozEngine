using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of WaterFrame input or state.</summary>
public sealed class WaterFrameStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public WaterFrameStateException(WaterFrameRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public WaterFrameStateException(WaterFrameRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public WaterFrameRefusal Reason { get; }

    /// <summary>Whether preparation requested the shared ocean, when its demand changed.</summary>
    public bool? PreparedOceanDemand { get; init; }

    /// <summary>Whether the draw requests the shared ocean, when its demand changed.</summary>
    public bool? ActualOceanDemand { get; init; }

    /// <summary>Whether optical preparation completed, when its inputs were refused.</summary>
    public bool? Prepared { get; init; }

    /// <summary>The last successfully prepared plane count, when optical inputs were refused.</summary>
    public int? PreparedPlaneCount { get; init; }

    /// <summary>The refused draw plane count, when optical inputs were refused.</summary>
    public int? ActualPlaneCount { get; init; }
}
