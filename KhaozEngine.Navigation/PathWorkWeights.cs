using System;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Navigation;

/// <summary>Provisional integer work weights. A heap operation may cost more than a simple comparison.</summary>
internal static class PathWorkWeights
{
    /// <summary>The largest indivisible operation weight, including a candidate with a possible heap push.</summary>
    internal const int Largest = 6;

    /// <summary>The fixed charge for the next operation, independent of its outcome and current quota.</summary>
    internal static int For(Phase phase) => phase switch
    {
        Phase.Layer or Phase.Snap or Phase.MemberBox or Phase.Member => 2,
        Phase.CertificateLabel or Phase.CertificateProgress or Phase.SearchProgress => 1,
        Phase.Seed or Phase.Pop => 4,
        Phase.Neighbor or Phase.Link => Largest,
        Phase.Parent or Phase.Reverse or Phase.Copy => 1,
        Phase.Wrap or Phase.Decode or Phase.Allocate => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}
