using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Benchmarks;
using KhaozEngine.Simulation;

// jobs-0/1/2: a headless, repeatable benchmark of one server tick across a matrix of (cells C, entities/cell E,
// systems S). jobs-0 measured the single-threaded baseline; jobs-1 added the parallel cell-tick path (the cell
// axis), so each regime is run inline AND with cells fanned across cores; jobs-2 adds the entities axis - the
// "one hot cell" section below ticks that cell with its hot system fanning entity rows across cores via
// World.ParallelForEach (the cell axis can't help a single cell). Run with:
//   dotnet run --project KhaozEngine.Benchmarks -c Release
//   dotnet run --project KhaozEngine.Benchmarks -c Release -- --quick     (fast smoke: small N, few ticks)
//   dotnet run --project KhaozEngine.Benchmarks -c Release -- --gate      (jobs-3 system-scheduler GATE evaluation)
// See KhaozEngine.Benchmarks/README.md for how to read the output.

bool quick = Array.IndexOf(args, "--quick") >= 0;

CultureInfo ci = CultureInfo.InvariantCulture;

// jobs-3 gate: a separate, focused run that measures whether overlapping non-conflicting systems (layer 3) could
// beat what layers 1+2 already give on a single cell. It is the measurement the system-scheduler decision is gated
// on, so it runs alone (not part of the normal cell/entities benchmark).
if (Array.IndexOf(args, "--gate") >= 0)
{
    RunSystemAxisGate(ci, quick);
    return;
}

IReadOnlyList<BenchmarkConfig> matrix = quick ? QuickMatrix() : BenchmarkMatrix.Default();

Console.WriteLine("KhaozEngine server-tick benchmark - cell axis (jobs-1) + entities axis (jobs-2)");
Console.WriteLine($"cores={Environment.ProcessorCount}  framework={Environment.Version}  mode={(quick ? "quick" : "full")}");
Console.WriteLine();

// Column layout: regime | C | E | N | S | inline ms | par ms | speedup | par entities/sec
const string header = "{0,-18} {1,7} {2,9} {3,11} {4,3} {5,11} {6,11} {7,8} {8,18}";
Console.WriteLine(string.Format(ci, header,
    "regime", "C", "E", "N", "S", "inline ms", "par ms", "speedup", "par entities/sec"));
Console.WriteLine(new string('-', 18 + 7 + 9 + 11 + 3 + 11 + 11 + 8 + 18 + 8));

foreach (BenchmarkConfig config in matrix)
{
    BenchmarkResult inline = ServerTickBenchmark.Run(config, new SingleThreadedJobScheduler());
    BenchmarkResult par = ServerTickBenchmark.Run(config, new ThreadPoolJobScheduler());
    double speedup = par.PerTickMs > 0 ? inline.PerTickMs / par.PerTickMs : 0;

    Console.WriteLine(string.Format(ci, header,
        config.Name,
        config.CellCount,
        config.EntitiesPerCell,
        par.TotalEntities,
        config.Systems,
        inline.PerTickMs.ToString("F3", ci),
        par.PerTickMs.ToString("F3", ci),
        speedup.ToString("F2", ci) + "x",
        par.EntitiesPerSecond.ToString("N0", ci)));
}

Console.WriteLine();
Console.WriteLine($"speedup = inline ms/tick / parallel ms/tick (cells fanned across up to {Environment.ProcessorCount} cores).");
Console.WriteLine("Cells are the parallel axis here: many-cells scales ~linearly to core count; one-hot-cell can't (1 cell).");

// ---- Entities axis (jobs-2): one hot World of E entities (the single-cell case the cell axis cannot split), its
// dominant pass run single-threaded (World.ForEach) vs fanned across cores (World.ParallelForEach). Sweeping the
// per-row work shows the crossover: a fork/join has a fixed cost, so trivial per-row work (work=1) is overhead-bound
// and parallel LOSES; as the per-row compute grows toward a real hot system, it amortizes the fork/join and scales
// toward ~P×. Printing the whole curve keeps the win honest - it's only claimed where the measurement shows it. ----
int hotEntities = quick ? 8192 : 65536;
int warmup = quick ? 5 : 20;
int timed = quick ? 20 : 60;
int[] workLevels = { 1, 8, 32, 128, 512 };
var scheduler = new ThreadPoolJobScheduler();

// Prime both code paths (ForEach + ParallelForEach) to completion before timing: their first appearance triggers
// background tier-1 JIT, which otherwise lands mid-sweep and inflates an early row's inline figure.
_ = EntitiesAxisBenchmark.Measure(hotEntities, 64, warmup, timed, scheduler);

Console.WriteLine();
Console.WriteLine($"entities axis - one hot World (E={hotEntities}), ForEach vs ParallelForEach, sweeping per-row work (jobs-2)");
const string ehdr = "{0,12} {1,13} {2,13} {3,10} {4,18}";
Console.WriteLine(string.Format(ci, ehdr, "work/row", "ForEach ms", "Par ms", "speedup", "par entities/sec"));
Console.WriteLine(new string('-', 12 + 13 + 13 + 10 + 18 + 5));
foreach (int work in workLevels)
{
    EntitiesAxisBenchmark.Point pt = EntitiesAxisBenchmark.Measure(hotEntities, work, warmup, timed, scheduler);
    double entsPerSec = pt.ParMs > 0 ? hotEntities / (pt.ParMs / 1000.0) : 0;
    Console.WriteLine(string.Format(ci, ehdr,
        work,
        pt.InlineMs.ToString("F3", ci),
        pt.ParMs.ToString("F3", ci),
        pt.Speedup.ToString("F2", ci) + "x",
        entsPerSec.ToString("N0", ci)));
}
Console.WriteLine();
Console.WriteLine("Entities are the parallel axis for a single hot cell: ParallelForEach splits the archetype's rows across");
Console.WriteLine($"up to {Environment.ProcessorCount} cores. Trivial work is fork/join-bound (parallel < 1x); a realistic hot system scales toward ~P×.");

// A lighter matrix for a quick smoke: same three shapes, smaller N and fewer ticks so a run is sub-second.
static IReadOnlyList<BenchmarkConfig> QuickMatrix()
{
    const int n = 8192;
    return new[]
    {
        new BenchmarkConfig { Name = "many small cells", GridWidth = 16, GridHeight = 16, EntitiesPerCell = n / 256, WarmupTicks = 5, TimedTicks = 20 },
        new BenchmarkConfig { Name = "one hot cell",     GridWidth = 1,  GridHeight = 1,  EntitiesPerCell = n,        WarmupTicks = 5, TimedTicks = 20 },
        new BenchmarkConfig { Name = "mid (4x4 cells)",  GridWidth = 4,  GridHeight = 4,  EntitiesPerCell = n / 16,   WarmupTicks = 5, TimedTicks = 20 },
    };
}

// jobs-3 GATE: for a single cell of distinct systems, compare the per-cell tick three ways - all systems
// single-threaded sequential (T_seq, the baseline), each system via ParallelForEach sequential between systems
// (T_layer2, what we ship), and the most optimistic overlap of the single-threaded systems honouring the
// AccessSet conflict graph (T_layer3 ceiling). Build layer 3 only if T_layer3 < T_layer2 at an entity count where
// the cell costs real time. See SystemAxisGate.
static void RunSystemAxisGate(CultureInfo ci, bool quick)
{
    int work = quick ? 16 : 48;
    int warmup = quick ? 5 : 15;
    int timed = quick ? 20 : 50;
    int[] entityLevels = quick ? new[] { 256, 4096, 65536 } : new[] { 256, 1024, 4096, 16384, 65536, 262144 };
    var scheduler = new ThreadPoolJobScheduler();

    Console.WriteLine("KhaozEngine jobs-3 system-scheduler GATE evaluation");
    Console.WriteLine($"cores={Environment.ProcessorCount}  framework={Environment.Version}  per-row work={work}  systems=7 (4 Pos/Vel cluster + 3 independent)");
    Console.WriteLine();

    // Prime both code paths to completion before timing (background tier-1 JIT otherwise inflates an early row).
    _ = SystemAxisGate.Measure(4096, work, warmup, timed, scheduler);

    const string h = "{0,9} {1,11} {2,11} {3,9} {4,13} {5,9} {6,13}";
    Console.WriteLine(string.Format(ci, h, "entities", "T_seq ms", "T_l2 ms", "l2 x", "T_l3ceil ms", "l3 x", "verdict"));
    Console.WriteLine(new string('-', 9 + 11 + 11 + 9 + 13 + 9 + 13 + 6));
    foreach (int e in entityLevels)
    {
        SystemAxisGate.Row r = SystemAxisGate.Measure(e, work, warmup, timed, scheduler);
        Console.WriteLine(string.Format(ci, h,
            e,
            r.SeqMs.ToString("F3", ci),
            r.Layer2Ms.ToString("F3", ci),
            r.Layer2Speedup.ToString("F2", ci) + "x",
            r.Layer3CeilMs.ToString("F3", ci),
            r.Layer3Speedup.ToString("F2", ci) + "x",
            r.Layer3BeatsLayer2 ? "L3 WINS" : "L2 wins"));
    }
    Console.WriteLine();
    Console.WriteLine("T_seq    = all 7 systems single-threaded, sequential (today's per-cell tick).");
    Console.WriteLine("T_l2     = each system via ParallelForEach, sequential between systems (jobs-2, shipped).");
    Console.WriteLine("T_l3ceil = most optimistic overlap of the single-threaded systems (list-schedule of measured");
    Console.WriteLine("           solo costs honouring the AccessSet conflict graph on all cores) - the best layer 3 alone.");
    Console.WriteLine("GATE: build the system scheduler only if 'L3 WINS' at an entity count where the cell costs real time.");
}
