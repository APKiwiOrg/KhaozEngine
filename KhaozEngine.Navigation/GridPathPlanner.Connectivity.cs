using System;
using System.Runtime.CompilerServices;
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
        if (!CertificateStartsInBounds(startLayer, startX, startZ)) return false;
        int component = _connectivity.ComponentOf(startId);
        if (component < 0) return false;
        ReadOnlySpan<int> members = _connectivity.Members(component);
        if (ExceedsCertificateNodeLimit(members.Length) || goal.MemberCount <= 0 ||
            ExceedsCertificateComparisonLimit(members.Length, goal.MemberCount) ||
            !goal.IsDisconnected(_connectivity, component)) return false;

        double startProgress = goal.Progress(startLayer, startX, startZ);
        if (!double.IsFinite(startProgress)) return false;
        foreach (int node in members)
        {
            if (node == startId) continue;
            (int layer, int x, int z) = Decode(node);
            double progress = goal.Progress(layer, x, z);
            if (RejectsCertificate(progress, startProgress)) return false;
        }
        return true;
    }

    // Rounded legacy snaps can resolve to another cell. Only the actual search node anchors a proof.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool CertificateStartsInBounds(int layer, int x, int z) => _space.Layers[layer].InBounds(x, z);

    static bool ExceedsCertificateNodeLimit(int nodes) => nodes > CertificateNodeLimit;

    static bool ExceedsCertificateComparisonLimit(int nodes, int members)
        => (long)nodes * members > CertificateComparisonLimit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool CertificateMemberIsDisconnected(PlannerConnectivity connectivity, int startComponent, int node)
    {
        int label = connectivity.ComponentOf(node);
        return label >= 0 && label != startComponent;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool RejectsCertificate(double progress, double startProgress)
        => !double.IsFinite(progress) || progress < startProgress;
}
