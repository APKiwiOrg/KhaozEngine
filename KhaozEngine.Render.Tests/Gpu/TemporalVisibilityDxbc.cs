using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Reads FXC's DXBC disassembly for the visibility position chain (TAA shadow visibility plan, Task 7f.2). D's chain
/// is <c>fma(previousUv + (vec2(at) + 0.5 - J) / S - uv + Jp / S, S, -0.5)</c>, and FXC at the shipped flags
/// reassociated it (<c>itof, add -J, add 0.5, div S, add, add, add, mad S -0.5</c> in the probe's DXBC). The fast path
/// writes the same chain three times in straight-line code, and the Direct3D 11 bit identity rests on FXC reassociating
/// those copies the same way. <see cref="Chains"/> finds every such chain, one per component, by its shape: a
/// <c>mad</c> by a constant-buffer value with a literal -0.5, whose first source leads back through adds to a
/// <c>div</c> by a constant-buffer value, whose dividend leads back through adds of constants and literals to an
/// <c>itof</c>. Each chain reads as one signature: the opcodes in order, with the kind of each operand beside the
/// chain (a register, a constant-buffer element, a literal) and its sign. Registers are not compared, only shapes.
/// Platform-neutral text processing, so its own facts run on every leg.
/// </summary>
internal static partial class TemporalVisibilityDxbc
{
    internal readonly record struct Instruction(string Opcode, string[] Destinations, string[] Sources);

    static readonly HashSet<string> TwoDestinations = new(StringComparer.Ordinal)
    {
        "udiv", "sincos", "imul", "umul", "swapc", "uaddc", "usubb",
    };

    static readonly HashSet<string> NoDestination = new(StringComparer.Ordinal)
    {
        "if_nz", "if_z", "else", "endif", "loop", "endloop", "break", "breakc_nz", "breakc_z", "continue",
        "continuec_nz", "continuec_z", "ret", "retc_nz", "retc_z", "discard_nz", "discard_z", "switch", "case",
        "default", "endswitch", "label", "call", "callc_nz", "callc_z", "nop",
    };

    /// <summary>The instructions of a disassembly, declarations and comments left out.</summary>
    internal static List<Instruction> Parse(string disassembly)
    {
        var instructions = new List<Instruction>();
        foreach (string raw in disassembly.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("dcl_", StringComparison.Ordinal)
                || line.StartsWith("ps_", StringComparison.Ordinal) || line.StartsWith("vs_", StringComparison.Ordinal))
                continue;
            int space = line.IndexOf(' ');
            string opcode = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[(space + 1)..].Trim();
            if (rest.StartsWith('['))
            {
                int close = rest.IndexOf(']');
                opcode += " " + rest[..(close + 1)];
                rest = rest[(close + 1)..].Trim();
            }
            string[] operands = SplitOperands(rest);
            string bare = opcode.Split(' ')[0];
            int dests = NoDestination.Contains(bare) || operands.Length == 0 ? 0
                : TwoDestinations.Contains(bare) ? Math.Min(2, operands.Length) : 1;
            instructions.Add(new Instruction(opcode, operands[..dests], operands[dests..]));
        }
        return instructions;
    }

    /// <summary>The <c>dcl_temps</c> count and every <c>dcl_indexableTemp</c> declaration.</summary>
    internal static (int Temps, string[] IndexableTemps) Declarations(string disassembly)
    {
        int temps = 0;
        var indexable = new List<string>();
        foreach (string raw in disassembly.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("dcl_temps ", StringComparison.Ordinal))
                temps = int.Parse(line["dcl_temps ".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            else if (line.StartsWith("dcl_indexableTemp", StringComparison.Ordinal)) indexable.Add(line);
        }
        return (temps, indexable.ToArray());
    }

    /// <summary>One signature for every position chain component found, in program order.</summary>
    internal static List<string> Chains(string disassembly)
    {
        List<Instruction> code = Parse(disassembly);
        var chains = new List<string>();
        for (int i = 0; i < code.Count; i++)
        {
            Instruction mad = code[i];
            if (Bare(mad) != "mad" || mad.Sources.Length != 3 || mad.Destinations.Length != 1) continue;
            Operand dest = Operand.Parse(mad.Destinations[0]);
            foreach (int c in dest.Mask())
            {
                Operand scale = Operand.Parse(mad.Sources[1]), bias = Operand.Parse(mad.Sources[2]);
                if (!scale.IsConstant || bias.Literal(c) != "-0.500000") continue;
                Operand chain = Operand.Parse(mad.Sources[0]);
                if (!chain.IsRegister || chain.Negated) continue;
                string tail = $"{Bare(mad)} {scale.Kind(c)} l({bias.Literal(c)})";
                string? found = AfterDivision(code, i, chain.Name, chain.Component(c), 4);
                if (found is not null) chains.Add(found + " > " + tail);
            }
        }
        return chains;
    }

    // From the chain value read at instruction `at`, back through at most `adds` adds to the division by a constant,
    // then back to the itof. Returns the signature from the itof up to the value, or null where this is no chain.
    static string? AfterDivision(List<Instruction> code, int at, string register, int component, int adds)
    {
        int def = Definition(code, at, register, component, out int destComponent);
        if (def < 0) return null;
        Instruction step = code[def];
        string op = Bare(step);
        if (op == "mov" && step.Sources.Length == 1)
        {
            Operand moved = Operand.Parse(step.Sources[0]);
            return moved.IsRegister && !moved.Negated && !moved.Absolute
                ? AfterDivision(code, def, moved.Name, moved.Component(destComponent), adds) : null;
        }
        if (op == "div" && step.Sources.Length == 2)
        {
            Operand dividend = Operand.Parse(step.Sources[0]), divisor = Operand.Parse(step.Sources[1]);
            if (!dividend.IsRegister || dividend.Negated || !divisor.IsConstant) return null;
            string? head = BeforeDivision(code, def, dividend.Name, dividend.Component(destComponent), 3);
            return head is null ? null : $"{head} > {step.Opcode} {divisor.Kind(destComponent)}";
        }
        if (op != "add" || step.Sources.Length != 2 || adds == 0) return null;
        for (int s = 0; s < 2; s++)
        {
            Operand along = Operand.Parse(step.Sources[s]), beside = Operand.Parse(step.Sources[1 - s]);
            if (!along.IsRegister || along.Negated || along.Absolute) continue;
            string? before = AfterDivision(code, def, along.Name, along.Component(destComponent), adds - 1);
            if (before is not null) return $"{before} > {step.Opcode} {beside.Kind(destComponent)}";
        }
        return null;
    }

    // Before the division: adds whose other operand is a constant or a literal, back to an itof.
    static string? BeforeDivision(List<Instruction> code, int at, string register, int component, int adds)
    {
        int def = Definition(code, at, register, component, out int destComponent);
        if (def < 0) return null;
        Instruction step = code[def];
        string op = Bare(step);
        if (op == "itof") return step.Opcode;
        if (op == "mov" && step.Sources.Length == 1)
        {
            Operand moved = Operand.Parse(step.Sources[0]);
            return moved.IsRegister && !moved.Negated && !moved.Absolute
                ? BeforeDivision(code, def, moved.Name, moved.Component(destComponent), adds) : null;
        }
        if (op != "add" || step.Sources.Length != 2 || adds == 0) return null;
        for (int s = 0; s < 2; s++)
        {
            Operand along = Operand.Parse(step.Sources[s]), beside = Operand.Parse(step.Sources[1 - s]);
            if (!along.IsRegister || along.Negated || along.Absolute || beside.IsRegister) continue;
            string? before = BeforeDivision(code, def, along.Name, along.Component(destComponent), adds - 1);
            if (before is not null) return $"{before} > {step.Opcode} {beside.Kind(destComponent)}";
        }
        return null;
    }

    // The last instruction before `at` that writes the component, in program order, and which of its destination
    // components it is.
    static int Definition(List<Instruction> code, int at, string register, int component, out int destComponent)
    {
        destComponent = component;
        for (int j = at - 1; j >= 0; j--)
        {
            foreach (string d in code[j].Destinations)
            {
                Operand dest = Operand.Parse(d);
                if (dest.Name == register && dest.Mask().Contains(component)) return j;
            }
        }
        return -1;
    }

    static string Bare(Instruction instruction) => instruction.Opcode.Split(' ')[0];

    static string[] SplitOperands(string text)
    {
        var operands = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        foreach (char ch in text)
        {
            if (ch == '(' || ch == '[') depth++;
            else if (ch == ')' || ch == ']') depth--;
            if (ch == ',' && depth == 0)
            {
                operands.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(ch);
        }
        if (current.Length > 0) operands.Add(current.ToString().Trim());
        return operands.Where(o => o.Length > 0).ToArray();
    }

    internal sealed partial class Operand
    {
        internal string Name = "";
        internal string Swizzle = "";
        internal bool Negated, Absolute;
        internal string[] Values = [];

        internal bool IsRegister => Name.Length > 1 && Name[0] == 'r' && char.IsDigit(Name[1]);
        internal bool IsConstant => Name.StartsWith("cb", StringComparison.Ordinal);
        bool IsLiteral => Values.Length > 0;

        internal static Operand Parse(string text)
        {
            var operand = new Operand();
            string t = text.Trim();
            if (t.StartsWith('-')) { operand.Negated = true; t = t[1..]; }
            if (t.StartsWith('|') && t.EndsWith('|')) { operand.Absolute = true; t = t[1..^1]; }
            if (t.StartsWith("l(", StringComparison.Ordinal) && t.EndsWith(')'))
            {
                operand.Values = t[2..^1].Split(',').Select(v => v.Trim()).ToArray();
                return operand;
            }
            Match m = Shape().Match(t);
            if (m.Success)
            {
                operand.Name = m.Groups["name"].Value;
                operand.Swizzle = m.Groups["swizzle"].Value;
            }
            else operand.Name = t;
            return operand;
        }

        /// <summary>The destination components a write mask names, every one where it names none.</summary>
        internal int[] Mask() => Swizzle.Length == 0 ? [0, 1, 2, 3] : Swizzle.Select(Index).ToArray();

        /// <summary>The source component read for destination component <paramref name="c"/>.</summary>
        internal int Component(int c) => Swizzle.Length == 0 ? c : Swizzle.Length == 1 ? Index(Swizzle[0])
            : Index(Swizzle[Math.Min(c, Swizzle.Length - 1)]);

        internal string Literal(int c) => !IsLiteral ? "" : Values.Length == 1 ? Values[0]
            : c < Values.Length ? Values[c] : "";

        /// <summary>The operand's shape for destination component <paramref name="c"/>: its sign, a register as
        /// <c>r</c>, a constant-buffer element with the component read, or a literal's value.</summary>
        internal string Kind(int c)
        {
            string sign = Negated ? "-" : "+";
            if (IsLiteral) return $"{sign}l({Literal(c)})";
            string body = IsRegister ? "r" : IsConstant ? $"{Name}.{"xyzw"[Component(c)]}" : Name;
            return sign + (Absolute ? $"|{body}|" : body);
        }

        static int Index(char ch) => "xyzw".IndexOf(ch, StringComparison.Ordinal);

        [GeneratedRegex(@"^(?<name>[a-zA-Z]+\d*(?:\[[^\]]*\])*)(?:\.(?<swizzle>[xyzw]+))?$")]
        private static partial Regex Shape();
    }
}
