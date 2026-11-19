#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Tests.WaterPrismCensus;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Benchmarks;

// Clock-off count pass for the water proof design. Each water boundary proof records its coverage, the answer
// the boundary derived from it and whether the proposed capture certificates would have held.
internal static partial class ShoreStepAttributionBenchmark
{
    static void RunProofDesign(string population, string[] policy)
    {
        int seats = population switch
        {
            "1" => 1,
            "16" => 16,
            "32" => 32,
            _ => throw new ArgumentException("Select one bounded count fixture: 1, 16 or 32.")
        };
        WaterTraversalMode? mode = policy switch
        {
            [] => null,
            ["--wade-only"] when seats == 1 => WaterTraversalMode.WadeOnly,
            _ => throw new ArgumentException("The only policy contrast is --wade-only at one seat.")
        };
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0" ||
            TileMediumSampler.BypassTraceMemoForTests || TileWaterSharedProjections.BypassForTests)
            throw new InvalidOperationException("Use the released water tiers with DOTNET_TieredCompilation=0.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "metadata", mode = "Counts", hz = (long?)null, seats,
            policy = (mode ?? WaterPrismScenario.Tuning.Water.Mode).ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            MovementQueryLease.MaxWaterDomainsPerLease, MovementQueryLease.CoverageSkinMetres
        }));
        WaterPrismScenario.WaterModeOverride = mode;
        try
        {
            CountProofDesign(new("bank-up", "bank-stand", -Vector2.UnitY, 237.55f), seats, BankAdvanceSteps, false);
            CountProofDesign(new("bank-down", "bank-stand", Vector2.UnitY, 235.45f), seats, BankAdvanceSteps, false);
            CountProofDesign(new("bank-up", "bank-stand", -Vector2.UnitY, 236.365f), seats, 6, true);
            CountProofDesign(new("flat-wade", "flat-wade", Vector2.UnitX), seats, ControlAdvanceSteps, false);
        }
        finally { WaterPrismScenario.WaterModeOverride = null; }
    }

    static void CountProofDesign(Case trajectory, int seats, int ticks, bool fixedBasis)
    {
        using var scene = new WaterPrismScenario(trajectory.Fixture);
        float surface = scene.Colliders.Medium.Extent.Maximum.Y;
        var states = new PlayerMoveState[seats];
        for (int seat = 0; seat < seats; seat++)
            states[seat] = trajectory.BankStart is { } depth
                ? BankBasis(scene, 110.375f + (seat - seats / 2) * 0.25f, depth)
                // Flat shelf seats share the X lane and sit 0.1 m apart in Z inside the uniform bed.
                : seats == 1 ? scene.Basis : BankBasis(scene, 110.375f, 252.85f + (seat - seats / 2) * 0.1f);
        scene.Colliders.Medium.ResetTraceMemoForTests();

        var command = new MoveCommand(trajectory.Axis, false, 0);
        var water = new TileWaterShoreCounts.Session();
        TileWaterShoreCounts.Current = water;
        ShoreStepProbe.TraceLabelsEnabled = true;
        try
        {
            for (int tick = 0; tick < ticks; tick++)
                for (int seat = 0; seat < seats; seat++)
                {
                    var input = Describe(scene, states[seat], surface);
                    water.Start();
                    ShoreProofCounts.Start();
                    PlayerMoveState next;
                    (TileWaterShoreCounts.Trace[] traces, ShoreProofCounts.Sampling[] samplings,
                        ShoreProofCounts.Proof[] proofs) recorded;
                    try { (next, _, _) = Sample(scene, states[seat], command, ShoreProbeMode.Counts); }
                    finally
                    {
                        var (samplings, proofs) = ShoreProofCounts.Stop();
                        recorded = (water.Stop(), samplings, proofs);
                    }
                    var output = Describe(scene, next, surface);
                    CheckFixture(trajectory, input, output);
                    if (recorded.samplings.Length != recorded.traces.Length ||
                        recorded.samplings.Zip(recorded.traces).Any(pair => pair.First.Prism != pair.Second.Prism) ||
                        recorded.proofs.Any(p => p.Sampler is int i && recorded.samplings[i].Label != p.Label))
                        throw new InvalidOperationException("Water proof records did not reconcile with the sampler.");
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "step", trajectory.Name, fixedBasis, seats, tick, seat, input, output,
                        traces = recorded.traces.Select(t => new { t.Label, t.Key, t.Prism, t.Outcome, t.Spans, t.Contacts }),
                        recorded.samplings, recorded.proofs
                    }));
                    if (!fixedBasis) states[seat] = next;
                }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "scene", trajectory.Name, fixedBasis, seats, ticks,
                domains = scene.Colliders.Medium.ShoreDomainCount
            }));
        }
        finally
        {
            TileWaterShoreCounts.Current = null;
            ShoreStepProbe.TraceLabelsEnabled = false;
        }
    }

    // Capture facts only. Loads an authored world read-only through the engine loaders and runs no movement.
    static void RunProofCapture(string directory)
    {
        string root = Path.GetFullPath(directory);
        TileWorldDocument document = TileWorldFile.Load(root);
        var catalogs = TileWorldCatalogs.Load(document.CatalogPaths.Select(p => Path.GetFullPath(Path.Combine(root, p))));
        RegionCoord[] regions = document.Regions.Keys.ToArray();
        var sampler = new TileMediumSampler(document, catalogs, regions);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "capture", world = document.Id, regions = regions.Length,
            minimum = new[] { sampler.Extent.Minimum.X, sampler.Extent.Minimum.Y, sampler.Extent.Minimum.Z },
            maximum = new[] { sampler.Extent.Maximum.X, sampler.Extent.Maximum.Y, sampler.Extent.Maximum.Z },
            census = new[] { 0.2f, 0.5f, 1f }.Select(sampler.ShoreCaptureCensus).ToArray()
        }));
    }
}
#endif
