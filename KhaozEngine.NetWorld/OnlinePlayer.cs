using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>A point-in-time view of one connected player for an admin console.</summary>
public readonly record struct OnlinePlayer(
    int Slot, string AccountId, string DisplayName, Vector3 Position, bool Grounded, float VerticalVelocity, long NetId);
