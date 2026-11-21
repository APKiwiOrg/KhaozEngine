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
}
