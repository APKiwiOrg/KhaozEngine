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

    bool TryCertifyNoProgress<TGoal>(int startLayer, int startX, int startZ, int startId, ref TGoal goal)
        where TGoal : struct, ISearchGoal
    {
        int component = CertificateComponent(startLayer, startX, startZ, startId);
        if (component < 0) return false;
        ReadOnlySpan<int> members = _connectivity.Members(component);
        if (!FitsCertificate(members.Length, goal.MemberCount) || !goal.IsDisconnected(_connectivity, component))
            return false;

        double startProgress = goal.Progress(startLayer, startX, startZ);
        if (!double.IsFinite(startProgress)) return false;
        foreach (int node in members)
        {
            if (node == startId) continue;
            (int layer, int x, int z) = Decode(node);
            double progress = goal.Progress(layer, x, z);
            if (!SupportsCertificate(progress, startProgress)) return false;
        }
        return true;
    }

    // Rounded legacy snaps can resolve to another cell. Only the actual search node anchors a proof.
    int CertificateComponent(int layer, int x, int z, int node)
        => _space.Layers[layer].InBounds(x, z) ? _connectivity.ComponentOf(node) : -1;

    static bool FitsCertificate(int nodes, int members)
        => nodes <= CertificateNodeLimit && members > 0 && (long)nodes * members <= CertificateComparisonLimit;

    static bool CertificateMemberIsDisconnected(PlannerConnectivity connectivity, int startComponent, int node)
    {
        int label = connectivity.ComponentOf(node);
        return label >= 0 && label != startComponent;
    }

    static bool SupportsCertificate(double progress, double startProgress)
        => double.IsFinite(progress) && progress >= startProgress;
}
