using System;
using System.IO;
using System.Security.Cryptography;
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
            string label = args[1], directory = args[3], runId = args[4];
            bool stable = args[2] == "true";
            string resultPath = Path.Combine(directory, label + ".json");
            string bakePath = Path.Combine(directory, label + ".kenb");
            if (File.Exists(resultPath) || File.Exists(bakePath))
                throw new IOException("Refusing to overwrite child evidence.");
            ProofEnvironment environment = ProofHost.Describe();
            Console.WriteLine("Public fixture setup and capture begin.");
            using var fixture = new ProofFixture();
            var canonical = new CanonicalProfileFile(fixture.Capture);
            Console.WriteLine("Capture complete. One unprofiled degree-1 BuildProfile begins.");
            (GroundNavigation navigation, ProfileMetrics metrics) = ProfileMeasurement.Once(fixture.Capture, stable);
            Console.WriteLine("BuildProfile complete. Graph and canonical byte checks begin outside timing.");
            GraphCounts counts = ProofFixture.CheckGraph(navigation);
            byte[] bytes = canonical.WriteAndCheck(navigation);
            fixture.CheckScopeReleased();
            File.WriteAllBytes(bakePath, bytes);
            ProofJson.Write(resultPath, new ChildResult(runId, label, stable, ProofFixture.Identity,
                environment, metrics, counts, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), true, true));
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
