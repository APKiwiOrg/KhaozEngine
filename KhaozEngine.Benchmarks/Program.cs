using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Benchmarks;

// jobs-0: a headless, repeatable benchmark of one single-threaded server tick across a matrix of
// (cells C, entities/cell E, systems S). The number every later parallel layer must move. Run with:
//   dotnet run --project KhaozEngine.Benchmarks -c Release
//   dotnet run --project KhaozEngine.Benchmarks -c Release -- --quick     (fast smoke: small N, few ticks)
// See KhaozEngine.Benchmarks/README.md for how to read the output.

bool quick = Array.IndexOf(args, "--quick") >= 0;

IReadOnlyList<BenchmarkConfig> matrix = quick ? QuickMatrix() : BenchmarkMatrix.Default();

CultureInfo ci = CultureInfo.InvariantCulture;
Console.WriteLine("KhaozEngine server-tick benchmark - single-threaded baseline (jobs-0)");
Console.WriteLine($"cores={Environment.ProcessorCount}  framework={Environment.Version}  mode={(quick ? "quick" : "full")}");
Console.WriteLine();

// Column layout: regime | C | E | N | S | ms/tick | entities/sec | comp-visits/sec
const string header = "{0,-18} {1,7} {2,9} {3,11} {4,3} {5,11} {6,16} {7,18}";
Console.WriteLine(string.Format(ci, header,
    "regime", "C", "E", "N", "S", "ms/tick", "entities/sec", "comp-visits/sec"));
Console.WriteLine(new string('-', 18 + 7 + 9 + 11 + 3 + 11 + 16 + 18 + 7));

foreach (BenchmarkConfig config in matrix)
{
    BenchmarkResult r = ServerTickBenchmark.Run(config);
    Console.WriteLine(string.Format(ci, header,
        config.Name,
        config.CellCount,
        config.EntitiesPerCell,
        r.TotalEntities,
        config.Systems,
        r.PerTickMs.ToString("F3", ci),
        r.EntitiesPerSecond.ToString("N0", ci),
        r.ComponentVisitsPerSecond.ToString("N0", ci)));
}

Console.WriteLine();
Console.WriteLine("entities/sec = N processed per tick / per-tick seconds.  comp-visits/sec = S x entities/sec");
Console.WriteLine("(the real O(S*N) work rate: each of S systems passes over all N entities every tick).");

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
