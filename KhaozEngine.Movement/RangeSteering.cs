using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Requested world XZ input with magnitude at most one. Only Following requests movement.</summary>
public readonly record struct RangeSteering(Vector2 WorldDirection, RangeMoveStatus Status);
