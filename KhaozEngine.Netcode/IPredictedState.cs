using System.Numerics;

namespace KhaozEngine.Netcode;

/// <summary>
/// A predicted local state whose position participates in reconciliation error smoothing.
/// </summary>
/// <typeparam name="TSelf">The implementing state type (CRTP), so WithPosition stays strongly typed.</typeparam>
public interface IPredictedState<TSelf>
{
    /// <summary>World position used to measure and smooth reconciliation error.</summary>
    Vector2 Position { get; }

    /// <summary>Returns a copy of this state with <paramref name="position"/> applied.</summary>
    TSelf WithPosition(Vector2 position);
}
