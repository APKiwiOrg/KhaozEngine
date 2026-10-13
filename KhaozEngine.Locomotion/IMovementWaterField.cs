using System;

namespace KhaozEngine.Locomotion;

/// <summary>Certified absolute-coordinate water facts. Mutable producers advance Generation before changing answers.</summary>
public interface IMovementWaterField
{
    string SemanticIdentity { get; }
    long Generation { get; }
    MovementWaterExtent Extent { get; }
    MovementWaterPoint SampleCentreWater(in MovementBodyQuery body);
    MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
}
