using System;
using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Simulation;

namespace KhaozEngine.Benchmarks;

// Distinct components so the gate's systems below have a realistic conflict graph (not S copies of one system).
public struct GatePos : IComponent { public float X, Y; }
public struct GateVel : IComponent { public float X, Y; }
public struct GateHash : IComponent { public float V; }
public struct GateHealth : IComponent { public float V; }
public struct GateStatus : IComponent { public float V; }
public struct GateCooldown : IComponent { public float V; }

/// <summary>
/// jobs-3 <b>GATE evaluation</b> - the measurement that decides whether the system scheduler is worth building, NOT
/// the scheduler itself. Models one hot cell with several <em>distinct</em> systems (a realistic per-cell tick), each
/// declaring a jobs-2 <see cref="AccessSet"/>, and answers the scoping doc's gate question: with layers 1 (parallel
/// cells) and 2 (parallel <c>ForEach</c>) already shipped, is a single cell's tick still bottlenecked by the
/// <em>sum of distinct systems</em> such that overlapping the non-conflicting ones (layer 3) would meaningfully help?
/// </summary>
/// <remarks>
/// The two shipped axes set the bar layer 3 must clear:
/// <list type="bullet">
/// <item>Layer 2 parallelizes <em>within</em> each system (an archetype's rows across all P cores), so one system
/// already saturates the cores - its speedup is not capped by how many other systems exist.</item>
/// <item>Layer 3 parallelizes <em>across</em> systems (overlap non-conflicting ones), capped by the conflict-graph's
/// width and critical path - and it competes with layer 2 for the same cores.</item>
/// </list>
/// So the gate compares, for the same realistic system set: <c>T_seq</c> (all systems single-threaded, sequential),
/// <c>T_layer2</c> (each system via <c>ParallelForEach</c>, sequential between systems - what we ship), and the most
/// optimistic <c>T_layer3</c> (a list-schedule of the single-threaded systems that overlaps non-conflicting ones on
/// P cores, derived from each system's measured solo cost + the <see cref="AccessSet"/> conflict graph). If
/// <c>T_layer2 &lt;= T_layer3</c>, layer 2 alone already beats the best layer 3 could do alone, and since both are
/// floored by the same total-work / P bound (which layer 2 ~reaches for a cell with real work), layer 3 adds nothing
/// on top - the gate is not met.
/// </remarks>
public static class SystemAxisGate
{
    private const float Dt = 1f / 30f;

    // One modelled system: a name, its declared component access, and how to run its per-entity pass either
    // single-threaded (ForEach) or fanned across a scheduler (ParallelForEach).
    private sealed class GateSystem
    {
        public required string Name { get; init; }
        public required AccessSet Access { get; init; }
        public required Action<World> RunSeq { get; init; }
        public required Action<World, IJobScheduler> RunPar { get; init; }
    }

    // A realistic per-cell system set: a Pos/Vel "physics" cluster whose members conflict (share Position/Velocity),
    // plus several independent bookkeeping systems (health/status/cooldown) that touch disjoint components. The
    // per-row work (`Spin`) is non-trivial so layer 2 is a genuine win (past its fork/join crossover).
    private static List<GateSystem> Systems(int work) => new()
    {
        new GateSystem
        {
            Name = "Movement", Access = Access.Write<GatePos>().Read<GateVel>(),
            RunSeq = w => w.ForEach<GatePos, GateVel>((Entity _, ref GatePos p, ref GateVel v) => MovePos(ref p, v, work)),
            RunPar = (w, s) => w.ParallelForEach<GatePos, GateVel>((Entity _, ref GatePos p, ref GateVel v) => MovePos(ref p, v, work), s),
        },
        new GateSystem
        {
            Name = "Steering", Access = Access.Write<GateVel>().Read<GatePos>(),
            RunSeq = w => w.ForEach<GateVel, GatePos>((Entity _, ref GateVel v, ref GatePos p) => SteerVel(ref v, p, work)),
            RunPar = (w, s) => w.ParallelForEach<GateVel, GatePos>((Entity _, ref GateVel v, ref GatePos p) => SteerVel(ref v, p, work), s),
        },
        new GateSystem
        {
            Name = "Collision", Access = Access.Write<GatePos>().Read<GateVel>(),
            RunSeq = w => w.ForEach<GatePos, GateVel>((Entity _, ref GatePos p, ref GateVel v) => ClampPos(ref p, v, work)),
            RunPar = (w, s) => w.ParallelForEach<GatePos, GateVel>((Entity _, ref GatePos p, ref GateVel v) => ClampPos(ref p, v, work), s),
        },
        new GateSystem
        {
            Name = "SpatialHash", Access = Access.Write<GateHash>().Read<GatePos>(),
            RunSeq = w => w.ForEach<GateHash, GatePos>((Entity _, ref GateHash h, ref GatePos p) => HashPos(ref h, p, work)),
            RunPar = (w, s) => w.ParallelForEach<GateHash, GatePos>((Entity _, ref GateHash h, ref GatePos p) => HashPos(ref h, p, work), s),
        },
        new GateSystem
        {
            Name = "HealthRegen", Access = Access.Write<GateHealth>(),
            RunSeq = w => w.ForEach<GateHealth>((Entity _, ref GateHealth h) => Decay(ref h.V, work)),
            RunPar = (w, s) => w.ParallelForEach<GateHealth>((Entity _, ref GateHealth h) => Decay(ref h.V, work), s),
        },
        new GateSystem
        {
            Name = "StatusTick", Access = Access.Write<GateStatus>(),
            RunSeq = w => w.ForEach<GateStatus>((Entity _, ref GateStatus h) => Decay(ref h.V, work)),
            RunPar = (w, s) => w.ParallelForEach<GateStatus>((Entity _, ref GateStatus h) => Decay(ref h.V, work), s),
        },
        new GateSystem
        {
            Name = "CooldownTick", Access = Access.Write<GateCooldown>(),
            RunSeq = w => w.ForEach<GateCooldown>((Entity _, ref GateCooldown h) => Decay(ref h.V, work)),
            RunPar = (w, s) => w.ParallelForEach<GateCooldown>((Entity _, ref GateCooldown h) => Decay(ref h.V, work), s),
        },
    };

    /// <summary>One swept entity-count row: the three regimes' wall-clock + the implied speedups and the verdict.</summary>
    public sealed class Row
    {
        public required int Entities { get; init; }
        public required double SeqMs { get; init; }      // all systems single-threaded, sequential (the per-cell baseline)
        public required double Layer2Ms { get; init; }   // each system ParallelForEach, sequential between systems (shipped)
        public required double Layer3CeilMs { get; init; } // list-scheduled overlap of the single-threaded systems (optimistic)
        public required double WorkFloorMs { get; init; }  // SeqMs / P - the total-work / cores lower bound both layers share
        public double Layer2Speedup => Layer2Ms > 0 ? SeqMs / Layer2Ms : 0;
        public double Layer3Speedup => Layer3CeilMs > 0 ? SeqMs / Layer3CeilMs : 0;
        public bool Layer3BeatsLayer2 => Layer3CeilMs < Layer2Ms;   // the gate: can overlapping systems beat layer 2 alone?
    }

    public static Row Measure(int entities, int work, int warmup, int timed, IJobScheduler scheduler)
    {
        List<GateSystem> systems = Systems(work);

        // Per-system solo cost, both ways, on identically-seeded worlds (each measured in isolation).
        var soloSeq = new double[systems.Count];
        var soloPar = new double[systems.Count];
        for (int i = 0; i < systems.Count; i++)
        {
            GateSystem sys = systems[i];
            soloSeq[i] = Time(Build(entities), warmup, timed, w => sys.RunSeq(w));
            soloPar[i] = Time(Build(entities), warmup, timed, w => sys.RunPar(w, scheduler));
        }

        double seqMs = Sum(soloSeq);
        double layer2Ms = Sum(soloPar);
        double layer3Ms = ListScheduleMakespan(soloSeq, ConflictMatrix(systems), Environment.ProcessorCount);
        double workFloor = seqMs / Environment.ProcessorCount;

        return new Row
        {
            Entities = entities, SeqMs = seqMs, Layer2Ms = layer2Ms,
            Layer3CeilMs = layer3Ms, WorkFloorMs = workFloor,
        };
    }

    // ---- the conflict graph + an optimistic P-core list-schedule of the (single-threaded) systems ----

    private static bool[,] ConflictMatrix(List<GateSystem> systems)
    {
        int n = systems.Count;
        var c = new bool[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                c[i, j] = c[j, i] = systems[i].Access.ConflictsWith(systems[j].Access);
        return c;
    }

    // Greedy P-core list schedule: each system is one indivisible task of its measured solo duration; two conflicting
    // systems may never run at the same instant; among ready tasks pick the lowest index (deterministic). Returns the
    // makespan - the most optimistic wall-clock a layer-3 scheduler could give by overlapping NON-conflicting systems
    // (it can't subdivide a system - that is layer 2's job). This is the strongest case we hand layer 3.
    private static double ListScheduleMakespan(double[] dur, bool[,] conflict, int cores)
    {
        int n = dur.Length;
        var done = new bool[n];
        var endAt = new double[n];      // finish time of a running task; only valid while running[i]
        var running = new bool[n];
        int finished = 0;
        double now = 0;
        double makespan = 0;

        while (finished < n)
        {
            // Start every ready task that fits: not done, not running, conflicts with nothing currently running, core free.
            bool started = true;
            while (started)
            {
                started = false;
                int active = CountTrue(running);
                for (int i = 0; i < n && active < cores; i++)
                {
                    if (done[i] || running[i]) continue;
                    if (ConflictsWithRunning(i, running, conflict)) continue;
                    running[i] = true;
                    endAt[i] = now + dur[i];
                    active++;
                    started = true;
                }
            }

            // Advance to the earliest finish; complete every task ending then.
            double next = double.MaxValue;
            for (int i = 0; i < n; i++) if (running[i] && endAt[i] < next) next = endAt[i];
            if (next == double.MaxValue) break;   // nothing runnable (shouldn't happen with a valid graph)
            now = next;
            makespan = Math.Max(makespan, now);
            for (int i = 0; i < n; i++)
                if (running[i] && endAt[i] <= now + 1e-12) { running[i] = false; done[i] = true; finished++; }
        }
        return makespan;
    }

    private static bool ConflictsWithRunning(int i, bool[] running, bool[,] conflict)
    {
        for (int j = 0; j < running.Length; j++) if (running[j] && conflict[i, j]) return true;
        return false;
    }

    private static int CountTrue(bool[] a) { int c = 0; foreach (bool b in a) if (b) c++; return c; }
    private static double Sum(double[] a) { double s = 0; foreach (double x in a) s += x; return s; }

    // ---- per-row work (non-trivial, bounded, dependent so the JIT can't fold it) ----

    private static void MovePos(ref GatePos p, in GateVel v, int work)
    {
        float x = p.X, y = p.Y;
        for (int k = 0; k < work; k++) { x = (x + v.X * Dt) * 0.9999f; y = (y + v.Y * Dt) * 0.9999f; }
        p.X = x; p.Y = y;
    }

    private static void SteerVel(ref GateVel v, in GatePos p, int work)
    {
        float x = v.X, y = v.Y;
        for (int k = 0; k < work; k++) { x = (x - p.X * 0.0001f) * 0.9999f; y = (y - p.Y * 0.0001f) * 0.9999f; }
        v.X = x; v.Y = y;
    }

    private static void ClampPos(ref GatePos p, in GateVel v, int work)
    {
        float x = p.X, y = p.Y;
        for (int k = 0; k < work; k++) { x = (x + v.Y * Dt) * 0.99995f; y = (y + v.X * Dt) * 0.99995f; }
        p.X = x; p.Y = y;
    }

    private static void HashPos(ref GateHash h, in GatePos p, int work)
    {
        float acc = h.V;
        for (int k = 0; k < work; k++) acc = (acc + p.X * 0.5f + p.Y * 0.5f) * 0.9999f;
        h.V = acc;
    }

    private static void Decay(ref float val, int work)
    {
        float a = val;
        for (int k = 0; k < work; k++) a = (a + 1f) * 0.9999f;
        val = a;
    }

    private static double Time(World w, int warmup, int timed, Action<World> pass)
    {
        for (int i = 0; i < warmup; i++) pass(w);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < timed; i++) pass(w);
        sw.Stop();
        return timed <= 0 ? 0 : sw.Elapsed.TotalMilliseconds / timed;
    }

    private static World Build(int entities)
    {
        var w = new World();
        var rng = new DeterministicRng(0xC0FFEEUL);
        for (int i = 0; i < entities; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new GatePos { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
            w.Set(e, new GateVel { X = (rng.NextFloat() - 0.5f) * 2f, Y = (rng.NextFloat() - 0.5f) * 2f });
            w.Set(e, new GateHash { V = 0f });
            w.Set(e, new GateHealth { V = rng.NextFloat() });
            w.Set(e, new GateStatus { V = rng.NextFloat() });
            w.Set(e, new GateCooldown { V = rng.NextFloat() });
        }
        return w;
    }
}
