using System;

namespace KhaozEngine.Locomotion;

/// <summary>Captures the configured absolute play area once per physics frame, as the explicit heads do.
/// Queries then run on the local certificate with no per-query translation.</summary>
internal static class OrdinaryMovementBoundary
{
    internal static void Validate(MovementBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        if (boundary is not AnalyticBoundary)
            throw new ArgumentException("An ordinary boundary must come from MovementBoundary.Rectangle or " +
                "MovementBoundary.Circle so that each physics frame can capture it.", nameof(boundary));
    }

    internal static MovementBoundary Capture(MovementBoundary absolute, in MovementFrameDescriptor frame) =>
        ((AnalyticBoundary)absolute).InFrame(frame.PhysicsOrigin);
}
