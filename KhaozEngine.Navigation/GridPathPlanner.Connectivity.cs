using System;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    const int CertificateNodeLimit = 32_768;
    const int CertificateComparisonLimit = 262_144;
    readonly PlannerConnectivity _connectivity;
    int _searchesForTests;
    int _certificatesForTests;

    // Counts actual A* entries and certified refusals, independent of the build configuration.
    internal (int Searches, int Certificates) WorkForTests
        => (Volatile.Read(ref _searchesForTests), Volatile.Read(ref _certificatesForTests));

    internal bool SharesConnectivityForTests(GridPathPlanner other) => ReferenceEquals(_connectivity, other._connectivity);

    bool TryCertifyNoProgress<TGoal>(int startLayer, int startX, int startZ, int startId, TGoal goal)
        where TGoal : struct, ISearchGoal
    {
        // A rounded legacy snap can resolve to another cell, even a blocked or out-of-bounds one.
        // Only the actual RunSearch node can anchor a proof. An unlabelled start retains the old search.
        if (!_space.Layers[startLayer].InBounds(startX, startZ)) return false;
        int component = _connectivity.ComponentOf(startId);
        if (component < 0) return false;
        ReadOnlySpan<int> members = _connectivity.Members(component);
        if (members.Length > CertificateNodeLimit || goal.MemberCount <= 0 ||
            (long)members.Length * goal.MemberCount > CertificateComparisonLimit ||
            !goal.IsDisconnected(_connectivity, component)) return false;

        double startProgress = goal.Progress(startLayer, startX, startZ);
        if (!double.IsFinite(startProgress)) return false;
        foreach (int node in members)
        {
            if (node == startId) continue;
            (int layer, int x, int z) = Decode(node);
            double progress = goal.Progress(layer, x, z);
            if (!double.IsFinite(progress) || progress < startProgress) return false;
        }
        return true;
    }
}
