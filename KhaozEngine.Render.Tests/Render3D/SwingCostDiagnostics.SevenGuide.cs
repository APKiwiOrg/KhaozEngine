using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class SwingCostDiagnostics
{
    [Fact]
    public void Report_seven_guide_swing_preparation_cost()
    {
        const int workerCount = 3;
        int[] caps = [8, 10, 12, 14, 16];
        int[] rates = [144, 60];
        (int warmFrames, int measuredFrames) = FrameCounts();
        using var rig = new OutfitRig();
        using var workers = new WorkerJobScheduler(workerCount);
        var options = new ChainSimulationOptions();
        SwingCostWearer[] wearers = CreateWearers(rig, rig.BodyRegions(new RegionRun(0, 4)),
            SwingCostFixture.LoadColliders(rig), LayeredSwingCostOutfit.Load(rig, sevenGuideCape: true), options);
        var measurements = new List<SevenGuideMeasurement>(caps.Length * rates.Length);
        try
        {
            if ((warmFrames - 1) * (1f / rates[0]) < options.MaxStepSeconds)
                PrimeSolver(wearers.AsSpan(0, caps[^1]), options.MaxStepSeconds);
            foreach (int cap in caps)
            {
                foreach (int rate in rates)
                {
                    var allocations = new Dictionary<int, TimedWearerJobs.WorkerAllocation>(workerCount);
                    Measurement cost = Measure(wearers, rate, warmFrames, measuredFrames, cap, workers, allocations);
                    WorkerBytes[] workerBytes = allocations.OrderBy(entry => entry.Key)
                        .Select(entry => new WorkerBytes(entry.Key, entry.Value.BytesAllocated)).ToArray();
                    measurements.Add(new SevenGuideMeasurement(cap, WearerCount - cap, cost, workerBytes));
                }
            }
        }
        finally
        {
            DisposeWearers(wearers);
        }

        output.WriteLine(JsonSerializer.Serialize(new
        {
            diagnostic = nameof(Report_seven_guide_swing_preparation_cost),
            wearerCount = WearerCount,
            parallelWorkerCount = workerCount,
            settleFrames = measuredFrames == MeasuredFrames ? SettleFrames : 0,
            warmFrames,
            measuredFrames,
            environment = new
            {
                processorCount = Environment.ProcessorCount,
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
            },
            measurements,
        }, JsonSerializerOptions.Web));
    }

    sealed record SevenGuideMeasurement(int SimulatedWearerCount, int HeldWearerCount, Measurement Cost,
        WorkerBytes[] Workers);

    sealed record WorkerBytes(int ThreadId, long ManagedBytesAllocated);
}
