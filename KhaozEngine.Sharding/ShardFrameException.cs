using System;
using System.Numerics;

namespace KhaozEngine.Sharding;

/// <summary>A refused cell physics frame. Existing <see cref="ArgumentException"/> catches remain valid.
/// The reason and identifiers describe the rule. The message is developer prose.</summary>
public sealed class ShardFrameException : ArgumentException
{
    /// <summary>Creates a refusal of the named factory parameter with its required reason.</summary>
    public ShardFrameException(ShardFrameRefusal reason, string message, string paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the factory world.</summary>
    public ShardFrameRefusal Reason { get; }
    /// <summary>The cell the factory world was being admitted into.</summary>
    public CellCoord? Cell { get; init; }
    /// <summary>The consumer configuration member, PhysicsWorldFactory, whose factory returned the world.</summary>
    public string? MemberName { get; init; }
    /// <summary>The compared origin, captured once before disposing the refused world.</summary>
    public Vector3? ActualOrigin { get; init; }
    /// <summary>The cell's fixed frame anchor the world must use.</summary>
    public Vector3? ExpectedOrigin { get; init; }
}
