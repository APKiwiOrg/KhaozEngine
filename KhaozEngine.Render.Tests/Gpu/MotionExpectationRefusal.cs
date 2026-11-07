namespace KhaozEngine.Tests.Gpu;

/// <summary>Why a motion readback did not meet its expectation.</summary>
public enum MotionExpectationRefusal
{
    /// <summary>No drawn pixel was available to compare.</summary>
    NoDrawnPixels,
    /// <summary>A drawn pixel exceeded the permitted motion error.</summary>
    OutsideTolerance,
    /// <summary>A covered pixel stored background or non-finite motion.</summary>
    InvalidCoveredMotion,
    /// <summary>The coverage predicate admitted no pixel.</summary>
    NoAdmittedPixels,
}
