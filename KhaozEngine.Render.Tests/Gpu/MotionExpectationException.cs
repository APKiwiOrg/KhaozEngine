using System.Numerics;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Gpu;

/// <summary>An xUnit failure carrying the refused motion rule and offending pixel.</summary>
internal sealed class MotionExpectationException : XunitException
{
    internal MotionExpectationException(MotionExpectationRefusal reason, string message)
        : base(TrueException.ForNonTrueValue(message, false).Message) => Reason = reason;

    /// <summary>The expectation the readback failed.</summary>
    public MotionExpectationRefusal Reason { get; }
    /// <summary>The offending pixel, absent when no pixel was checked.</summary>
    public (int X, int Y)? Pixel { get; init; }
    /// <summary>The number of pixels checked before the refusal.</summary>
    public int Count { get; init; }
    /// <summary>The complete worst-error scan for a tolerance failure.</summary>
    public MotionScan? Scan { get; init; }
    /// <summary>The raw motion at a covered pixel that failed the coverage check.</summary>
    public Vector2? Stored { get; init; }
}
