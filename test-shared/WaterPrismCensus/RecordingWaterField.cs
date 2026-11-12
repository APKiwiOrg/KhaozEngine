using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.WaterPrismCensus;

internal sealed class RecordingWaterField(IMovementWaterField source) : IMovementWaterField
{
    internal Action<MovementMediumSweepQuery, MovementCoverageResult, MovementCoverageSpan[], MovementDomainContact[]>? Recorded = null;
    internal bool MeasureTime = false;
    internal readonly List<double> TraceMicroseconds = [];
    public string SemanticIdentity => source.SemanticIdentity;
    public long Generation => source.Generation;
    public MovementWaterExtent Extent => source.Extent;
    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) => source.SampleCentreWater(body, physicsOrigin);

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
#if WATER_PRISM_CENSUS
        using var trace = WaterPrismMeasurement.Begin(query, physicsOrigin);
#endif
        long start = MeasureTime ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var result = source.TraceWater(query, physicsOrigin, identity, spans, contacts);
        if (MeasureTime) TraceMicroseconds.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds);
#if WATER_PRISM_CENSUS
        trace?.Complete(result);
#endif
        Recorded?.Invoke(query, result, spans[..result.SpansWritten].ToArray(), contacts[..result.ContactsWritten].ToArray());
        return result;
    }
}
