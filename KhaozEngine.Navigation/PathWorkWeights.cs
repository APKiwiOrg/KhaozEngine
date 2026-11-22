using System;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Navigation;

/// <summary>Provisional integer work weights. A heap operation may cost more than a simple comparison.</summary>
internal static class PathWorkWeights
{
    /// <summary>The largest indivisible operation weight, including a candidate with a possible heap push.</summary>
    internal const int Largest = 6;

    /// <summary>Validate a footprint's endpoints and set up the scan rectangle and row bounds.</summary>
    internal const int FootprintSetup = 1;

    /// <summary>Project one row's conservative candidate bounds.</summary>
    internal const int FootprintRow = 2;

    /// <summary>Test exact geometry and, on contact, one captured column's surface bands.</summary>
    internal const int FootprintColumn = 3;

    /// <summary>Test one endpoint layer's surface and update the nearest candidate.</summary>
    internal const int EndpointLayer = 2;

    /// <summary>Check one directed graph crossing, including a cross-layer Stair.</summary>
    internal const int CellCrossing = 3;

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
