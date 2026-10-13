using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Certified water facts for a body given in physics-local coordinates and its frame's physics origin.
/// Producers classify and trace the absolute body <c>(double)local + (double)physicsOrigin</c> per component
/// and compare it with their absolute geometry exactly, so a point answer has no rounding band. Reported
/// intervals, surfaces and contact columns stay absolute. Mutable producers advance Generation before changing
/// answers.</summary>
public interface IMovementWaterField
{
    string SemanticIdentity { get; }
    long Generation { get; }
    MovementWaterExtent Extent { get; }
    MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin);
    MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
}
