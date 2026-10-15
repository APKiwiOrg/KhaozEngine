using System;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Research;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ProofHost.RequireHostedSelection();
            if (args.Length != 5 || args[0] != "--child" ||
                !((args[1] == "A" && args[2] == "false") || (args[1] == "B" && args[2] == "true")) ||
                args[4] != ProofHost.Required("KHAOZ_NAV_PROOF_CHILD") || args[4].Length != 32 ||
                ProofHost.Required("DOTNET_EnableDiagnostics") != "0")
                throw new ArgumentException("Only the hosted wrapper may start a proof child with its exact ordered arguments.");
            bool stable = args[2] == "true";
            ProofEnvironment environment = ProofHost.Describe();
            var evidence = new ProofChildEvidence(args[3],
                new ChildResult(args[4], args[1], stable, ProofFixture.Identity, environment));
            evidence.Run(state =>
            {
                Console.WriteLine("Public fixture setup and capture begin.");
                using var fixture = new ProofFixture();
                state.Begin("CanonicalAdapter");
                var canonical = new CanonicalProfileFile(fixture.Capture);
                state.Begin("BuildProfile");
                Console.WriteLine("Capture complete. One unprofiled degree-1 BuildProfile begins.");
                (GroundNavigation navigation, ProfileMetrics metrics) = ProfileMeasurement.Once(fixture.Capture, stable);
                state.RecordMeasurement(metrics);
                Console.WriteLine("BuildProfile complete. Graph and canonical byte checks begin outside timing.");
                state.Begin("GraphInspection");
                state.CheckGraph(ProofFixture.InspectGraph(navigation));
                state.Begin("CanonicalSerialization");
                byte[] bytes = canonical.Write(navigation);
                state.RecordCanonical(bytes);
                state.Begin("CanonicalRoundTrip");
                canonical.CheckRoundTrip(bytes);
                state.Result.CanonicalRoundTrip = true;
                state.Begin("ScopeRelease");
                fixture.CheckScopeReleased();
                state.Result.ScopeReleasedAfterBuild = true;
                state.Begin("FixtureDisposal");
            });
            Console.WriteLine("Child evidence retained.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
