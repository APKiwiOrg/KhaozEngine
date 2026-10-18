using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    // Opt-in backends (AGENTS.md "in NO umbrella" list + the Commerce SQL backends from DEPENDENCY-SEAMS.md,
    // plus the Identity.Oidc / Identity.Discord opt-in provider packages README.md and DEPENDENCY-SEAMS.md
    // document as "add explicitly like Physics.Bepu"). Pay-for-what-you-use: a consumer that does not want the
    // heavy/platform-specific dependency must not drag it in transitively through any umbrella. Short names
    // (KhaozEngine. prefix stripped).
    static readonly string[] OptInBackends =
    {
        "Physics.Bepu", "WorldStore.Sqlite", "WorldStore.SqlServer",
        "Server.Admin", "Social.Discord", "Commerce.Sqlite", "Commerce.SqlServer",
        "Identity.Oidc", "Identity.Discord", "Catalog.Sqlite", "Catalog.SqlServer", "Catalog.AzureBlob",
        // The sign-in exchange core is needed only by a dedicated auth service, which should carry neither the sim
        // stack nor a client umbrella. IdentityExchange_ReferencesOnlyIdentityAccountsAndNetcode pins its edges.
        "Identity.Exchange",
        // The exchange's ASP.NET Core handler carries the shared web framework, which neither a sim server nor a client
        // may be made to carry. AspNetCoreFramework_IsContainedToWebPackages holds the framework to it and Server.Admin.
        "Identity.Exchange.AspNetCore",
        // The account registry seam is opt-in on the Commerce precedent: a server-only seam stays out of the
        // Server umbrella until more than its first consumers want it. Accounts_ReferencesOnlyNetcode pins its edge.
        // Its SQL backends follow the rule every SQL provider follows.
        "Accounts", "Accounts.Sqlite", "Accounts.SqlServer",
        // Tile world colliders are for a game that wants physics under a tile world, which no umbrella implies.
        "TileWorld.Physics",
        // Body reach composes the movement seams only when a consumer explicitly requests it.
        "Movement",
        // Native map physics is for a game that wants colliders under a MapDoc world, which no umbrella implies.
        "MapDoc.Physics",
        // World-map imagery is optional for authored worlds and must be added explicitly.
        "WorldMap", "TileWorld.WorldMap", "WorldMap.Gui",
        // The bug report relay and its Azure Storage backend serve only a report host, which no umbrella implies.
        "BugReports.Relay", "BugReports.Relay.Azure",
        // THE THREE NATIVE GPU BACKENDS ARE NOT ON THIS LIST ANY MORE, and their absence is asserted rather than
        // assumed: NativeGpuBackends_AreCarriedByEveryUmbrellaThatCarriesGpu in ArchitectureTests.cs requires the opposite of what
        // this list would have meant. They were opt-in from decisions P1 / V-P1 / M-P1, on pay-for-what-you-use
        // plus a second argument about not welding a Direct3D backend into the Linux server heads' graph. Both
        // arguments died with the Veldrid incumbent in 18.0.0: KhaozEngine.Gpu builds no device of its own any
        // more, so an umbrella carrying Gpu without a backend carries a stack that cannot create a device, and
        // the server heads are unaffected because the Foundation and Server umbrellas carry no Gpu at all
        // (FoundationUmbrella_StaysGpuFree and ServerUmbrella_StaysGpuFree are what hold that).
    };

    [Fact]
    public void OptInBackends_AreNotReachableFromAnyUmbrella()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        var violations = new List<string>();
        foreach (string umbrella in Umbrellas)
        {
            HashSet<string> closure = TransitiveClosure(umbrella, graph).Select(Short).ToHashSet(StringComparer.Ordinal);
            foreach (string backend in OptInBackends.Where(closure.Contains))
                violations.Add($"{Short(umbrella)} pulls in {backend}");
        }

        bool clean = violations.Count == 0;
        Assert.True(clean, "Opt-in backends must stay out of every umbrella (pay-for-what-you-use): " + string.Join("; ", violations));
    }
}
