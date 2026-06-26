using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Benchmarks;
using KhaozEngine.Simulation;

// jobs-0/1: a headless, repeatable benchmark of one server tick across a matrix of (cells C, entities/cell E,
// systems S). jobs-0 measured the single-threaded baseline; jobs-1 adds the parallel cell-tick path, so each
// regime is now run inline AND across cores with the speedup reported. Run with:
//   dotnet run --project KhaozEngine.Benchmarks -c Release
//   dotnet run --project KhaozEngine.Benchmarks -c Release -- --quick     (fast smoke: small N, few ticks)
// See KhaozEngine.Benchmarks/README.md for how to read the output.

bool quick = Array.IndexOf(args, "--quick") >= 0;

IReadOnlyList<BenchmarkConfig> matrix = quick ? QuickMatrix() : BenchmarkMatrix.Default();

CultureInfo ci = CultureInfo.InvariantCulture;
Console.WriteLine("KhaozEngine server-tick benchmark - parallel cell ticks (jobs-1)");
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
Console.WriteLine("Cells are the parallel axis: many-cells scales ~linearly to core count; one-hot-cell can't (1 cell) - that's layer 2.");

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
