using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// #1832 probe. Modes:
//   bake      every candidate, compiled by every compile kind on a thread with each flush control, read at default
//   fold      constant folds compiled under each rounding and flush control, read at default
//   identity  default-environment identity of every candidate against today's expression
//   sites     one representative site per use class, compiled fully optimized, for JitDisasm
static class Program
{
    internal static float s_positiveZero = 0f;
    internal static float s_negativeZero = -0f;
    internal static ulong s_sink;

    static readonly Type[] Candidates =
    {
        typeof(Bcl), typeof(IntegerSteps), typeof(OpaqueSteps), typeof(HybridSteps), typeof(ReadonlyStaticSteps),
        typeof(VolatileReadonlySteps), typeof(RvaSteps), typeof(BitsLiteralSteps),
    };
    static readonly Type[] Slots = { typeof(S0), typeof(S1), typeof(S2), typeof(S3), typeof(S4), typeof(S5), typeof(S6) };

    static string Platform => $"{(OperatingSystem.IsMacOS() ? "macos" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    static string Tiering => Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0" ? "tc-off" : "tc-on";

    static int Main(string[] args)
    {
        Console.WriteLine($"# {Platform} {RuntimeInformation.FrameworkDescription} {RuntimeInformation.OSDescription} {Tiering} " +
            $"osr-initial={Environment.GetEnvironmentVariable("DOTNET_TC_OnStackReplacement_InitialCounter")} " +
            $"osr-hit={Environment.GetEnvironmentVariable("DOTNET_OSR_HitLimit")}");
        RuntimeHelpers.RunClassConstructor(typeof(OpaqueBits).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(ReadonlyBits).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(KhaozEngine.Numerics.FloatUlp).TypeHandle);
        FloatEnvironment.Save();
        Console.WriteLine($"# start controls=0x{FloatEnvironment.Current:X}");
        switch (args[0])
        {
            case "bake": BakeMatrix(); break;
            case "fold": FoldMatrix(); break;
            case "identity": Identity.Run(args.Length > 1 ? args[1] : "stride"); break;
            case "sites": SitesRun(); break;
            default: Console.WriteLine("unknown mode"); return 2;
        }
        return 0;
    }

    static MethodInfo Generic(string name, params Type[] types) =>
        typeof(Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(types);

    static void BakeMatrix()
    {
        var controls = FloatEnvironment.FlushControls;
        var readback = new Dictionary<string, ulong>();
        // Phase 1: first compiles under each control (AggressiveOptimization, OSR, and the first call of the plain methods).
        // PROBE_FLUSH_FIRST=1 runs the changed controls before the default, so a callee compiled on its first call
        // (CoreLib with ReadyToRun off) is first compiled under a flush control.
        bool flushFirst = Environment.GetEnvironmentVariable("PROBE_FLUSH_FIRST") == "1";
        Console.WriteLine($"# flush-first={flushFirst}");
        for (int i = 0; i < controls.Length; i++)
        {
            int s = flushFirst ? controls.Length - 1 - i : i;
            readback[controls[s].Name] = FloatEnvironment.Apply(controls[s]);
            foreach (var c in Candidates) Generic(nameof(CompileFirst), c, Slots[s]).Invoke(null, null);
            FloatEnvironment.Restore();
        }
        foreach (var kv in readback) Console.WriteLine($"# control {kv.Key} read back 0x{kv.Value:X}");
        Console.WriteLine($"# restored controls=0x{FloatEnvironment.Current:X}");

        // The plain methods' first compile is tier 0 when tiering is on, fully optimized when it is off.
        var firstPlain = new List<string>();
        for (int s = 0; s < controls.Length; s++)
            foreach (var c in Candidates)
                foreach (var (kind, probe, bits) in (List<(string, string, uint)>)Generic(nameof(ReadPlain), c, Slots[s]).Invoke(null, null)!)
                    firstPlain.Add(Row(controls[s].Name, c.Name, Tiering == "tc-on" ? "tier0-first-call" : "tc-off-first-call", probe, bits));

        // Phase 2: tier-1 promotion requested while the caller thread has each control.
        if (Tiering == "tc-on")
        {
            for (int s = 0; s < controls.Length; s++)
            {
                FloatEnvironment.Apply(controls[s]);
                for (int round = 0; round < 20; round++)
                {
                    foreach (var c in Candidates) Generic(nameof(Hammer), c, Slots[s]).Invoke(null, null);
                    Thread.Sleep(50);
                }
                FloatEnvironment.Restore();
            }
            Thread.Sleep(3000);
        }

        foreach (string row in firstPlain) Console.WriteLine(row);
        for (int s = 0; s < controls.Length; s++)
            foreach (var c in Candidates)
                foreach (var (kind, probe, bits) in (List<(string, string, uint)>)Generic(nameof(ReadAll), c, Slots[s]).Invoke(null, null)!)
                    Console.WriteLine(Row(controls[s].Name, c.Name, kind, probe, bits));
    }

    static uint U(float f) => BitConverter.SingleToUInt32Bits(f);

    static string Row(string control, string candidate, string kind, string probe, uint bits)
    {
        uint expected = probe switch { "Down(+0)" or "Down(0f literal)" or "WidenBox.X(+0)" => 0x80000001u, "Up(-0)" or "Up(-0f literal)" => 1u, _ => 0u };
        return $"RESULT\t{Platform}\t{Tiering}\t{control}\t{candidate}\t{kind}\t{probe}\t0x{bits:X8}\t0x{expected:X8}\t{(bits == expected ? "exact" : "BAKED")}";
    }

    static void CompileFirst<C, S>() where C : struct, IStep where S : struct
    {
        s_sink += U(Bake<C, S>.AggDown(s_positiveZero)) + U(Bake<C, S>.AggUp(s_negativeZero)) + U(Bake<C, S>.AggKnownDown())
            + U(Bake<C, S>.AggKnownUp()) + Bake<C, S>.AggCompare(s_positiveZero);
        s_sink += U(Bake<C, S>.OsrDown(s_positiveZero, 5000)) + U(Bake<C, S>.OsrUp(s_negativeZero, 5000))
            + U(Bake<C, S>.OsrKnownDown(5000)) + Bake<C, S>.OsrCompare(s_positiveZero, 5000);
        s_sink += U(Bake<C, S>.PlainDown(s_positiveZero)) + U(Bake<C, S>.PlainUp(s_negativeZero)) + U(Bake<C, S>.PlainKnownDown())
            + Bake<C, S>.PlainCompare(s_positiveZero) + U(Bake<C, S>.PlainWidenBox(Vector3.Zero).X);
    }

    static List<(string, string, uint)> ReadPlain<C, S>() where C : struct, IStep where S : struct => new()
    {
        ("plain", "Down(+0)", U(Bake<C, S>.PlainDown(s_positiveZero))),
        ("plain", "Up(-0)", U(Bake<C, S>.PlainUp(s_negativeZero))),
        ("plain", "Down(0f literal)", U(Bake<C, S>.PlainKnownDown())),
        ("plain", "AtLeastEpsilon(+0)", Bake<C, S>.PlainCompare(s_positiveZero)),
        ("plain", "WidenBox.X(+0)", U(Bake<C, S>.PlainWidenBox(Vector3.Zero).X)),
    };

    static void Hammer<C, S>() where C : struct, IStep where S : struct
    {
        for (int i = 0; i < 100; i++)
            s_sink += U(Bake<C, S>.PlainDown(s_positiveZero)) + U(Bake<C, S>.PlainUp(s_negativeZero)) + U(Bake<C, S>.PlainKnownDown())
                + Bake<C, S>.PlainCompare(s_positiveZero) + U(Bake<C, S>.PlainWidenBox(Vector3.Zero).X);
    }

    static List<(string, string, uint)> ReadAll<C, S>() where C : struct, IStep where S : struct
    {
        string plain = Tiering == "tc-on" ? "tier1-promoted" : "tc-off";
        string osr = Tiering == "tc-on" ? "osr" : "tc-off-loop";
        return new()
        {
            ("aggressive", "Down(+0)", U(Bake<C, S>.AggDown(s_positiveZero))),
            ("aggressive", "Up(-0)", U(Bake<C, S>.AggUp(s_negativeZero))),
            ("aggressive", "Down(0f literal)", U(Bake<C, S>.AggKnownDown())),
            ("aggressive", "Up(-0f literal)", U(Bake<C, S>.AggKnownUp())),
            ("aggressive", "AtLeastEpsilon(+0)", Bake<C, S>.AggCompare(s_positiveZero)),
            (osr, "Down(+0)", U(Bake<C, S>.OsrDown(s_positiveZero, 1))),
            (osr, "Up(-0)", U(Bake<C, S>.OsrUp(s_negativeZero, 1))),
            (osr, "Down(0f literal)", U(Bake<C, S>.OsrKnownDown(1))),
            (osr, "AtLeastEpsilon(+0)", Bake<C, S>.OsrCompare(s_positiveZero, 1)),
            (plain, "Down(+0)", U(Bake<C, S>.PlainDown(s_positiveZero))),
            (plain, "Up(-0)", U(Bake<C, S>.PlainUp(s_negativeZero))),
            (plain, "Down(0f literal)", U(Bake<C, S>.PlainKnownDown())),
            (plain, "AtLeastEpsilon(+0)", Bake<C, S>.PlainCompare(s_positiveZero)),
            (plain, "WidenBox.X(+0)", U(Bake<C, S>.PlainWidenBox(Vector3.Zero).X)),
        };
    }

    static void FoldMatrix()
    {
        var controls = FloatEnvironment.RoundingAndFlushControls;
        for (int s = 0; s < controls.Length; s++)
        {
            ulong back = FloatEnvironment.Apply(controls[s]);
            Generic(nameof(FoldFirst), Slots[s]).Invoke(null, null);
            FloatEnvironment.Restore();
            Console.WriteLine($"# control {controls[s].Name} read back 0x{back:X}");
        }
        var firstPlain = new List<(string, ulong[])>();
        for (int s = 0; s < controls.Length; s++)
            firstPlain.Add((controls[s].Name, (ulong[])Generic(nameof(FoldPlain), Slots[s]).Invoke(null, null)!));
        if (Tiering == "tc-on")
        {
            for (int s = 0; s < controls.Length; s++)
            {
                FloatEnvironment.Apply(controls[s]);
                for (int round = 0; round < 20; round++) { Generic(nameof(FoldHammer), Slots[s]).Invoke(null, null); Thread.Sleep(50); }
                FloatEnvironment.Restore();
            }
            Thread.Sleep(3000);
        }
        ulong[] reference = Folds.Reference();
        void Print(string control, string kind, ulong[] values)
        {
            for (int i = 0; i < values.Length; i++)
                Console.WriteLine($"FOLD\t{Platform}\t{Tiering}\t{control}\t{kind}\t{Folds.Names[i]}\t0x{values[i]:X16}\t0x{reference[i]:X16}\t" +
                    (values[i] == reference[i] ? "same" : "DIFFERENT"));
        }
        foreach (var (control, values) in firstPlain) Print(control, Tiering == "tc-on" ? "tier0-first-call" : "tc-off-first-call", values);
        for (int s = 0; s < controls.Length; s++)
        {
            var all = (ulong[][])Generic(nameof(FoldRead), Slots[s]).Invoke(null, null)!;
            Print(controls[s].Name, "aggressive", all[0]);
            Print(controls[s].Name, Tiering == "tc-on" ? "osr" : "tc-off-loop", all[1]);
            Print(controls[s].Name, Tiering == "tc-on" ? "tier1-promoted" : "tc-off", all[2]);
        }
    }

    static void FoldFirst<S>() where S : struct
    {
        var o = new ulong[Folds.Names.Length];
        Fold<S>.Aggressive(o); Fold<S>.Osr(o, 5000); Fold<S>.Plain(o);
        s_sink += o[0];
    }

    static ulong[] FoldPlain<S>() where S : struct { var o = new ulong[Folds.Names.Length]; Fold<S>.Plain(o); return o; }

    static void FoldHammer<S>() where S : struct
    {
        var o = new ulong[Folds.Names.Length];
        for (int i = 0; i < 100; i++) Fold<S>.Plain(o);
    }

    static ulong[][] FoldRead<S>() where S : struct
    {
        var a = new ulong[Folds.Names.Length]; var b = new ulong[Folds.Names.Length]; var c = new ulong[Folds.Names.Length];
        Fold<S>.Aggressive(a); Fold<S>.Osr(b, 1); Fold<S>.Plain(c);
        return new[] { a, b, c };
    }

    static void SitesRun()
    {
        foreach (var c in new[] { typeof(Bcl), typeof(IntegerSteps), typeof(OpaqueSteps), typeof(HybridSteps), typeof(VolatileReadonlySteps) })
            typeof(Program).GetMethod(nameof(CallSites), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(c).Invoke(null, null);
        Console.WriteLine($"# sites compiled, sink {s_sink}");
    }

    static void CallSites<C>() where C : struct, IStep
    {
        s_sink += BitConverter.SingleToUInt32Bits(Sites<C>.SiteOutwardLower(0.1));
        s_sink += (ulong)Sites<C>.SiteWidenBox(new Vector3(1, 2, 3)).Length();
        s_sink += BitConverter.DoubleToUInt64Bits(Sites<C>.SiteMinOperand(0.5, 0.5f));
        s_sink += BitConverter.DoubleToUInt64Bits(Sites<C>.SiteUlpSpacing(1.5f));
        s_sink += (ulong)Sites<C>.SiteExclusiveEnd(16f);
        s_sink += BitConverter.SingleToUInt32Bits(Sites<C>.SiteIterate(1f));
        s_sink += Sites<C>.SiteCompare(1f) ? 1u : 0u;
    }
}

struct S0 { }
struct S1 { }
struct S2 { }
struct S3 { }
struct S4 { }
struct S5 { }
struct S6 { }

static class Bake<C, S> where C : struct, IStep where S : struct
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] public static float AggDown(float x) => C.Down(x);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] public static float AggUp(float x) => C.Up(x);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] public static float AggKnownDown() => C.Down(0f);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] public static float AggKnownUp() => C.Up(-0f);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] public static uint AggCompare(float x) => C.AtLeastEpsilon(x) ? 1u : 0u;

    [MethodImpl(MethodImplOptions.NoInlining)] public static float PlainDown(float x) => C.Down(x);
    [MethodImpl(MethodImplOptions.NoInlining)] public static float PlainUp(float x) => C.Up(x);
    [MethodImpl(MethodImplOptions.NoInlining)] public static float PlainKnownDown() => C.Down(0f);
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint PlainCompare(float x) => C.AtLeastEpsilon(x) ? 1u : 0u;

    // Engine-shaped site (TileColliderBounds.Widened): three steps in one method, so the inliner sees a larger caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector3 PlainWidenBox(Vector3 min) => new(C.Down(min.X), C.Down(min.Y), C.Down(min.Z));

    // Loop methods: with tiering on they start at tier 0 with patchpoints, and the OSR knobs move them early.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float OsrDown(float x, int n)
    {
        float last = 0;
        for (int i = 0; i < n; i++) { last = C.Down(x); Program.s_sink += (ulong)BitConverter.SingleToInt32Bits(last); }
        return last;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float OsrUp(float x, int n)
    {
        float last = 0;
        for (int i = 0; i < n; i++) { last = C.Up(x); Program.s_sink += (ulong)BitConverter.SingleToInt32Bits(last); }
        return last;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float OsrKnownDown(int n)
    {
        float last = 0;
        for (int i = 0; i < n; i++) { last = C.Down(0f); Program.s_sink += (ulong)BitConverter.SingleToInt32Bits(last); }
        return last;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static uint OsrCompare(float x, int n)
    {
        uint last = 0;
        for (int i = 0; i < n; i++) { last = C.AtLeastEpsilon(x) ? 1u : 0u; Program.s_sink += last; }
        return last;
    }
}

// Folds the JIT can do once the small helpers inline with constant arguments. Each result is read as bits.
static class Folds
{
    public static readonly string[] Names =
    {
        "float 1/3", "float 1.1*1.3", "float 1+3e-8", "float sqrt(2)", "float (float)0.1d", "float (float)16777217",
        "float 1e-38*0.01 (subnormal result)", "float fma(1.1,1.3,0.1)", "double 1/3", "double sqrt(2)",
        "double (double)(2^53+1)", "double 1e-300*1e-10 (subnormal result)", "double (double)float.Epsilon",
        "double -double.Epsilon literal", "double 0.1+0.2",
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Div(float a, float b) => a / b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Mul(float a, float b) => a * b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Add(float a, float b) => a + b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float ToSingle(double d) => (float)d;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float IntToSingle(int i) => i;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double DivD(double a, double b) => a / b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double MulD(double a, double b) => a * b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double AddD(double a, double b) => a + b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double LongToDouble(long l) => l;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double Widen(float f) => f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fill(ulong[] o)
    {
        o[0] = BitConverter.SingleToUInt32Bits(Div(1f, 3f));
        o[1] = BitConverter.SingleToUInt32Bits(Mul(1.1f, 1.3f));
        o[2] = BitConverter.SingleToUInt32Bits(Add(1f, 3e-8f));
        o[3] = BitConverter.SingleToUInt32Bits(MathF.Sqrt(2f));
        o[4] = BitConverter.SingleToUInt32Bits(ToSingle(0.1));
        o[5] = BitConverter.SingleToUInt32Bits(IntToSingle(16777217));
        o[6] = BitConverter.SingleToUInt32Bits(Mul(1e-38f, 0.01f));
        o[7] = BitConverter.SingleToUInt32Bits(MathF.FusedMultiplyAdd(1.1f, 1.3f, 0.1f));
        o[8] = BitConverter.DoubleToUInt64Bits(DivD(1.0, 3.0));
        o[9] = BitConverter.DoubleToUInt64Bits(Math.Sqrt(2.0));
        o[10] = BitConverter.DoubleToUInt64Bits(LongToDouble(9007199254740993L));
        o[11] = BitConverter.DoubleToUInt64Bits(MulD(1e-300, 1e-10));
        o[12] = BitConverter.DoubleToUInt64Bits(Widen(float.Epsilon));
        o[13] = BitConverter.DoubleToUInt64Bits(-double.Epsilon);
        o[14] = BitConverter.DoubleToUInt64Bits(AddD(0.1, 0.2));
    }

    // The same operations on run-time operands at the default environment: what an unfolded compile computes.
    public static ulong[] Reference()
    {
        float[] f = { 1f, 3f, 1.1f, 1.3f, 3e-8f, 2f, 1e-38f, 0.01f, 0.1f };
        double[] d = { 1.0, 3.0, 2.0, 0.1, 1e-300, 1e-10, 0.2 };
        int big = Volatile.Read(ref s_big); long huge = Volatile.Read(ref s_huge);
        float eps = BitConverter.UInt32BitsToSingle(Volatile.Read(ref s_epsBits));
        return new ulong[]
        {
            BitConverter.SingleToUInt32Bits(Opaque(f[0]) / Opaque(f[1])), BitConverter.SingleToUInt32Bits(Opaque(f[2]) * Opaque(f[3])),
            BitConverter.SingleToUInt32Bits(Opaque(f[0]) + Opaque(f[4])), BitConverter.SingleToUInt32Bits(MathF.Sqrt(Opaque(f[5]))),
            BitConverter.SingleToUInt32Bits((float)Opaque(d[3])), BitConverter.SingleToUInt32Bits((float)big),
            BitConverter.SingleToUInt32Bits(Opaque(f[6]) * Opaque(f[7])),
            BitConverter.SingleToUInt32Bits(MathF.FusedMultiplyAdd(Opaque(f[2]), Opaque(f[3]), Opaque(f[8]))),
            BitConverter.DoubleToUInt64Bits(Opaque(d[0]) / Opaque(d[1])), BitConverter.DoubleToUInt64Bits(Math.Sqrt(Opaque(d[2]))),
            BitConverter.DoubleToUInt64Bits((double)huge), BitConverter.DoubleToUInt64Bits(Opaque(d[4]) * Opaque(d[5])),
            BitConverter.DoubleToUInt64Bits((double)eps), 0x8000000000000001UL,
            BitConverter.DoubleToUInt64Bits(Opaque(d[3]) + Opaque(d[6])),
        };
    }

    static int s_big = 16777217;
    static long s_huge = 9007199254740993L;
    static uint s_epsBits = 1;
    [MethodImpl(MethodImplOptions.NoInlining)] static float Opaque(float v) => v;
    [MethodImpl(MethodImplOptions.NoInlining)] static double Opaque(double v) => v;
}

static class Fold<S> where S : struct
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] public static void Aggressive(ulong[] o) => Folds.Fill(o);
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Plain(ulong[] o) => Folds.Fill(o);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Osr(ulong[] o, int n)
    {
        for (int i = 0; i < n; i++) { Folds.Fill(o); Program.s_sink += o[i & 7]; }
    }
}

// One representative site per use class from the inventory, for machine code comparison.
static class Sites<C> where C : struct, IStep
{
    // Outward rounding bound (TileWaterColumnCoverage.Lower and its many siblings).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static float SiteOutwardLower(double value) { float f = (float)value; return f > value ? C.Down(f) : f; }

    // Widened box end (TileColliderBounds.Widened).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static Vector3 SiteWidenBox(Vector3 min) => new(C.Down(min.X), C.Down(min.Y), C.Down(min.Z));

    // Min or max operand (BoundedGeometryArithmetic).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static double SiteMinOperand(double lowerSoFar, float lower) => Math.Min(lowerSoFar, C.Down(lower));

    // Arithmetic operand: one ULP of spacing (FlatRangeRoundoff).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static double SiteUlpSpacing(float value) => (double)C.Up(value) - value;

    // Last value before an exclusive end (WorldMapChunking).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static int SiteExclusiveEnd(float right) => (int)MathF.Floor(C.Down(right) / 4f);

    // Neighbour iteration (TileWaterUniformBed).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static float SiteIterate(float coordinate) => C.Up(coordinate);

    // Comparison against the smallest subnormal (no engine site today, the shape a new one would take).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool SiteCompare(float x) => C.AtLeastEpsilon(x);
}
