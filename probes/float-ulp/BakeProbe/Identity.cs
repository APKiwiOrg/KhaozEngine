using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Default-environment identity: every candidate against today's expression, for the functions and for one
// site template per use class. "exhaustive" walks all 2^32 float bit patterns, "stride" a fixed subset.
static class Identity
{
    static readonly uint[] Special =
    {
        0x00000000, 0x80000000, 0x00000001, 0x80000001, 0x00000002, 0x80000002, 0x007FFFFF, 0x807FFFFF, 0x00800000,
        0x80800000, 0x00800001, 0x80800001, 0x3F800000, 0xBF800000, 0x7F7FFFFF, 0xFF7FFFFF, 0x7F7FFFFE, 0xFF7FFFFE,
        0x7F800000, 0xFF800000, 0x7FC00000, 0xFFC00000, 0x7F800001, 0xFF800001, 0x7FFFFFFF, 0xFFFFFFFF, 0x34000000,
        0x4B800000, 0xCB800000,
    };

    public static void Run(string scope)
    {
        int failures = 0;
        failures += Functions<IntegerSteps>(scope);
        failures += Functions<OpaqueSteps>(scope);
        failures += Functions<HybridSteps>(scope);
        failures += Functions<VolatileReadonlySteps>(scope);
        failures += SiteTemplates<IntegerSteps>();
        failures += SiteTemplates<OpaqueSteps>();
        failures += SiteTemplates<HybridSteps>();
        failures += SiteTemplates<VolatileReadonlySteps>();
        Console.WriteLine($"IDENTITY\ttotal-mismatches\t{failures}");
    }

    static IEnumerable<uint> Inputs(string scope)
    {
        foreach (uint s in Special) yield return s;
        if (scope == "exhaustive")
        {
            uint u = 0;
            do { yield return u; } while (++u != 0);
            yield break;
        }
        // Every pattern within 2^16 of each special value, then a stride over the whole space.
        foreach (uint s in Special)
            for (uint d = 0; d < 65536; d++) { yield return s + d; yield return s - d; }
        for (ulong u = 0; u <= uint.MaxValue; u += 4099) yield return (uint)u;
    }

    static int Functions<C>(string scope) where C : struct, IStep
    {
        long checkedCount = 0; int mismatches = 0;
        foreach (uint bits in Inputs(scope))
        {
            float x = BitConverter.UInt32BitsToSingle(bits);
            uint down = BitConverter.SingleToUInt32Bits(C.Down(x)), downRef = BitConverter.SingleToUInt32Bits(MathF.BitDecrement(x));
            uint up = BitConverter.SingleToUInt32Bits(C.Up(x)), upRef = BitConverter.SingleToUInt32Bits(MathF.BitIncrement(x));
            bool cmp = C.AtLeastEpsilon(x), cmpRef = x >= float.Epsilon;
            if (down != downRef || up != upRef || cmp != cmpRef)
            {
                if (mismatches++ < 10)
                    Console.WriteLine($"MISMATCH\t{typeof(C).Name}\tx=0x{bits:X8}\tdown 0x{down:X8}/0x{downRef:X8}\tup 0x{up:X8}/0x{upRef:X8}\tcmp {cmp}/{cmpRef}");
            }
            checkedCount++;
        }
        Console.WriteLine($"IDENTITY\t{typeof(C).Name}\tfunctions Down Up AtLeastEpsilon\t{scope}\tinputs={checkedCount}\tmismatches={mismatches}");
        return mismatches;
    }

    // Site templates from the inventory's use classes, today's expression beside the candidate.
    static int SiteTemplates<C>() where C : struct, IStep
    {
        var doubles = new List<double>();
        foreach (uint s in Special)
        {
            float f = BitConverter.UInt32BitsToSingle(s);
            doubles.Add(f);
            if (float.IsFinite(f))
            {
                doubles.Add(Math.BitIncrement((double)f)); doubles.Add(Math.BitDecrement((double)f));
                doubles.Add(((double)f + MathF.BitIncrement(f)) / 2); doubles.Add(((double)f + MathF.BitDecrement(f)) / 2);
            }
        }
        doubles.AddRange(new[] { 1e-46, -1e-46, 1e-50, -1e-50, 1e-45, -1e-45, 7.006492321624085e-46, -7.006492321624085e-46,
            double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue, 3.5e38, -3.5e38, 0.1, -0.1 });
        var random = new Random(1832);
        for (int i = 0; i < 1_000_000; i++)
        {
            ulong bits = (ulong)random.NextInt64();
            double d = BitConverter.UInt64BitsToDouble(bits);
            doubles.Add(i % 2 == 0 ? d : d * 1e-280);
        }

        int mismatches = 0; long count = 0;
        void Check(string site, ulong candidate, ulong today, double input)
        {
            count++;
            if (candidate != today && mismatches++ < 10)
                Console.WriteLine($"MISMATCH\t{typeof(C).Name}\t{site}\tinput {input:R}\t0x{candidate:X}/0x{today:X}");
        }
        foreach (double v in doubles)
        {
            float f = (float)v;
            Check("outward lower", Bits(f > v ? C.Down(f) : f), Bits(f > v ? MathF.BitDecrement(f) : f), v);
            Check("outward upper", Bits(f < v ? C.Up(f) : f), Bits(f < v ? MathF.BitIncrement(f) : f), v);
            Check("min operand", BitConverter.DoubleToUInt64Bits(Math.Min(v, C.Down(f))),
                BitConverter.DoubleToUInt64Bits(Math.Min(v, MathF.BitDecrement(f))), v);
            Check("max operand", BitConverter.DoubleToUInt64Bits(Math.Max(v, C.Up(f))),
                BitConverter.DoubleToUInt64Bits(Math.Max(v, MathF.BitIncrement(f))), v);
            Check("ulp spacing", BitConverter.DoubleToUInt64Bits((double)C.Up(f) - f),
                BitConverter.DoubleToUInt64Bits((double)MathF.BitIncrement(f) - f), v);
            Check("exclusive end", (ulong)(long)(int)MathF.Floor(C.Down(f) / 4f), (ulong)(long)(int)MathF.Floor(MathF.BitDecrement(f) / 4f), v);
            Check("iteration", Bits(C.Up(C.Up(f))), Bits(MathF.BitIncrement(MathF.BitIncrement(f))), v);
            Check("comparison", C.AtLeastEpsilon(f) ? 1u : 0u, f >= float.Epsilon ? 1u : 0u, v);
        }
        Console.WriteLine($"IDENTITY\t{typeof(C).Name}\tsite templates (8 classes)\tchecks={count}\tmismatches={mismatches}");
        return mismatches;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ulong Bits(float f) => BitConverter.SingleToUInt32Bits(f);
}
