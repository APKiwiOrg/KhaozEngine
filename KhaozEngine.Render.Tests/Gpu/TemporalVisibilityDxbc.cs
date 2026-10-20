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
/// chain (a register, a constant-buffer element, a literal) and its sign. A register beside the chain also carries
/// where its value comes from (<see cref="Provenance"/>): a division of two constant-buffer elements, such as
/// <c>Jp / S</c>, a constant-buffer element or shader input moved into it, or the opcode that computed it, such as
/// <c>previousUv</c>'s. So a copy that adds <c>Jp / S</c> where D adds <c>previousUv</c> reads apart from D. Register
/// names are not compared, only shapes and provenance. Platform-neutral text processing, so its own facts run on every
/// leg.
/// <para>What provenance tells apart and what it does not (review 7f r2, m-3). It goes one step back from a register
/// beside the chain, through plain moves only. Two such registers read apart where their provenances differ: a value
/// moved from a shader input or a constant-buffer element (<c>uv</c>), a division of two constant-buffer elements
/// (<c>Jp / S</c>, the elements named), and a value some other opcode computed (<c>previousUv</c>, read as that
/// opcode alone). So a copy that swaps any two of <c>previousUv</c>, <c>uv</c> and <c>Jp / S</c>, or divides other
/// elements, reads apart from D, and so does a flipped sign, which <see cref="Operand.Kind"/> carries. Two registers
/// that the same opcode computed read the same whatever their operands: a copy that adds another <c>add</c> result
/// where D adds <c>previousUv</c>, or <c>previousUv</c> computed from other inputs, reads as D's chain. A component
/// swizzle of a computed register is not compared either, nor anything past four moves. Those are held by the
/// equivalence and A/B facts on the device, not here.</para>
/// <para>Fix round 7f r3. A chain's head, from the <c>itof</c> to the division, also accepts a register beside it, read
/// through its provenance, which now names the operands of any opcode that reads only constants and literals. So a
/// head that adds a shared <c>0.5 - J</c> reads as such rather than as no chain. <see cref="Form"/> sorts a head into
/// the two association orders the binade rule is proved for, written <c>(at + 0.5) - J</c> and reassociated
/// <c>(at - J) + 0.5</c>, or neither, and <see cref="Remainder"/> is the chain from the division on. Every
/// <c>mad</c> by a literal -0.5 that reads as no chain is listed by <see cref="Unread"/> with the instructions behind
/// it, so a program whose chains the reader misses shows why.</para>
/// <para>Fix round 7f r4, after hosted run 39374926165 printed the full programs. FXC marks every instruction of the
/// shipped accumulate and resolve programs precise, so there it neither reassociates a chain nor fuses its last two
/// steps: the chain ends <c>mul S</c> then <c>add -0.5</c>, not <c>mad S -0.5</c>, and the r3 reader, which knew only
/// the <c>mad</c>, found no chain at all. It also hoists the fast path's heads above the call site's branch, four
/// components in one vector register, where the accumulation's own 3x3 positions share them. So a chain now ends in
/// either form, a precise marking reads per component as <c>[precise]</c> on the step it covers, a marking glued to a
/// resource opcode is parsed off it, the lines of an immediate constant buffer are skipped, and every chain carries the
/// loop depth of its last step (<see cref="Found"/>), so D's chains, inside D's loops, read apart from the fast
/// path's straight-line ones.</para>
/// </summary>
internal static partial class TemporalVisibilityDxbc
{
    /// <summary>One instruction: the opcode with any resource suffix, the components its precise marking covers
    /// (all four for a bare <c>[precise]</c>, none without one), its operands and how many loops enclose it.</summary>
    internal readonly record struct Instruction(string Opcode, string Precise, string[] Destinations, string[] Sources,
        int Depth);

    /// <summary>A position chain component: its signature and the loop depth of its last step.</summary>
    internal readonly record struct Chain(string Signature, int LoopDepth);

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

    /// <summary>The instructions of a disassembly, declarations, an immediate constant buffer's lines and comments
    /// left out.</summary>
    internal static List<Instruction> Parse(string disassembly)
    {
        var instructions = new List<Instruction>();
        int depth = 0;
        foreach (string raw in disassembly.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0 || !char.IsLetter(line[0]) || line.StartsWith("dcl_", StringComparison.Ordinal)
                || line.StartsWith("ps_", StringComparison.Ordinal) || line.StartsWith("vs_", StringComparison.Ordinal))
                continue;
            int space = line.IndexOf(' ');
            string opcode = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[(space + 1)..].Trim();
            string precise = "";
            if (rest.StartsWith('['))
            {
                int close = rest.IndexOf(']');
                string marking = rest[1..close];
                precise = marking == "precise" ? "xyzw"
                    : marking.StartsWith("precise(", StringComparison.Ordinal) ? marking["precise(".Length..^1] : "";
                rest = rest[(close + 1)..];
                // A resource opcode's types follow the marking with no space, as in ld_indexable [precise](texture2d).
                int end = rest.IndexOf(' ');
                if (end < 0) end = rest.Length;
                opcode += rest[..end];
                rest = rest[end..].Trim();
            }
            string[] operands = SplitOperands(rest);
            int dests = NoDestination.Contains(opcode) || operands.Length == 0 ? 0
                : TwoDestinations.Contains(opcode) ? Math.Min(2, operands.Length) : 1;
            if (opcode == "endloop") depth--;
            instructions.Add(new Instruction(opcode, precise, operands[..dests], operands[dests..], depth));
            if (opcode == "loop") depth++;
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
    internal static List<string> Chains(string disassembly) => Found(disassembly).Select(c => c.Signature).ToList();

    /// <summary>Every position chain component found, in program order, with the loop depth of its last step.</summary>
    internal static List<Chain> Found(string disassembly)
    {
        List<Instruction> code = Parse(disassembly);
        var chains = new List<Chain>();
        for (int i = 0; i < code.Count; i++)
            foreach (int c in PositionComponents(code, i))
                if (Read(code, i, c) is { } found) chains.Add(new Chain(found, code[i].Depth));
        return chains;
    }

    const string MinusHalf = "-0.500000";

    // The destination components instruction `at` ends a -0.5 position in, a chain or not: a mad by a constant with
    // a literal -0.5, or an add of a literal -0.5 to a register that a multiplication by a constant wrote.
    static IEnumerable<int> PositionComponents(List<Instruction> code, int at)
    {
        Instruction step = code[at];
        if (step.Destinations.Length != 1) yield break;
        string op = Bare(step);
        if (op == "mad" && step.Sources.Length == 3)
        {
            foreach (int c in Operand.Parse(step.Destinations[0]).Mask())
                if (Operand.Parse(step.Sources[1]).IsConstant && Operand.Parse(step.Sources[2]).Literal(c) == MinusHalf)
                    yield return c;
        }
        else if (op == "add" && step.Sources.Length == 2)
        {
            foreach (int c in Operand.Parse(step.Destinations[0]).Mask())
                if (Scaled(code, at, c) is not null) yield return c;
        }
    }

    // For an add of a literal -0.5 at component c: the multiplication by a constant that wrote its other operand, and
    // the register component that multiplication scales. Null for any other add.
    static (int Mul, int Component, Operand Scale, Operand Value)? Scaled(List<Instruction> code, int at, int c)
    {
        Instruction add = code[at];
        for (int s = 0; s < 2; s++)
        {
            Operand half = Operand.Parse(add.Sources[s]), along = Operand.Parse(add.Sources[1 - s]);
            if (half.Literal(c) != MinusHalf || !along.IsRegister || along.Negated || along.Absolute) continue;
            int def = Moved(code, at, along.Name, along.Component(c), out int mc);
            if (def < 0 || Bare(code[def]) != "mul" || code[def].Sources.Length != 2) return null;
            for (int m = 0; m < 2; m++)
            {
                Operand scale = Operand.Parse(code[def].Sources[m]), value = Operand.Parse(code[def].Sources[1 - m]);
                if (scale.IsConstant && value.IsRegister && !value.Negated && !value.Absolute)
                    return (def, mc, scale, value);
            }
            return null;
        }
        return null;
    }

    // The signature of the chain ending at instruction `at`, component c, or null where it reads as no chain.
    static string? Read(List<Instruction> code, int at, int c)
    {
        Instruction last = code[at];
        if (Bare(last) == "mad")
        {
            Operand scale = Operand.Parse(last.Sources[1]), chain = Operand.Parse(last.Sources[0]);
            if (!chain.IsRegister || chain.Negated) return null;
            string? found = AfterDivision(code, at, chain.Name, chain.Component(c), 4);
            return found is null ? null : $"{found} > {Op(last, c)} {scale.Kind(c)} l({MinusHalf})";
        }
        if (Scaled(code, at, c) is not { } mul) return null;
        string? before = AfterDivision(code, mul.Mul, mul.Value.Name, mul.Value.Component(mul.Component), 4);
        return before is null ? null
            : $"{before} > {Op(code[mul.Mul], mul.Component)} {mul.Scale.Kind(mul.Component)} "
                + $"> {Op(last, c)} l({MinusHalf})";
    }

    // The last write of a register component before `at`, followed back through plain moves.
    static int Moved(List<Instruction> code, int at, string register, int component, out int destComponent)
    {
        int def = Definition(code, at, register, component, out destComponent);
        while (def >= 0 && Bare(code[def]) == "mov" && code[def].Sources.Length == 1)
        {
            Operand moved = Operand.Parse(code[def].Sources[0]);
            if (!moved.IsRegister || moved.Negated || moved.Absolute) break;
            def = Definition(code, def, moved.Name, moved.Component(destComponent), out destComponent);
        }
        return def;
    }

    // An opcode as a signature names it: bare, with [precise] where its marking covers the component.
    static string Op(Instruction step, int component) =>
        step.Precise.Contains("xyzw"[component], StringComparison.Ordinal) ? step.Opcode + " [precise]" : step.Opcode;

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
            return head is null ? null : $"{head} > {Op(step, destComponent)} {divisor.Kind(destComponent)}";
        }
        if (op != "add" || step.Sources.Length != 2 || adds == 0) return null;
        for (int s = 0; s < 2; s++)
        {
            Operand along = Operand.Parse(step.Sources[s]), beside = Operand.Parse(step.Sources[1 - s]);
            if (!along.IsRegister || along.Negated || along.Absolute) continue;
            string? before = AfterDivision(code, def, along.Name, along.Component(destComponent), adds - 1);
            if (before is not null)
                return $"{before} > {Op(step, destComponent)} {Beside(code, def, beside, destComponent)}";
        }
        return null;
    }

    // An operand beside the chain: its kind and sign, and a register's provenance.
    static string Beside(List<Instruction> code, int at, Operand operand, int component) => operand.IsRegister
        ? $"{operand.Kind(component)}({Provenance(code, at, operand.Name, operand.Component(component), 4)})"
        : operand.Kind(component);

    /// <summary>Where the value of a register component read at instruction <paramref name="at"/> comes from, through
    /// at most <paramref name="moves"/> plain moves: <c>div</c> and both operands where it divides two constant-buffer
    /// elements, the element or input a move read, otherwise the opcode that wrote it, or <c>?</c> where nothing
    /// before <paramref name="at"/> did.</summary>
    internal static string Provenance(List<Instruction> code, int at, string register, int component, int moves)
    {
        int def = Definition(code, at, register, component, out int destComponent);
        if (def < 0) return "?";
        Instruction step = code[def];
        string op = Bare(step);
        if (op == "mov" && step.Sources.Length == 1)
        {
            Operand moved = Operand.Parse(step.Sources[0]);
            if (moved.IsRegister && !moved.Negated && !moved.Absolute && moves > 0)
                return Provenance(code, def, moved.Name, moved.Component(destComponent), moves - 1);
            if (!moved.IsRegister) return moved.Kind(destComponent);
        }
        // An opcode that reads only constant-buffer elements, inputs and literals names them, such as Jp / S's
        // division or a shared 0.5 - J.
        Operand[] sources = step.Sources.Select(Operand.Parse).ToArray();
        if (op != "mov" && sources.Length > 0 && sources.All(o => !o.IsRegister))
            return op + " " + string.Join(" ", sources.Select(o => o.Kind(destComponent)));
        return op;
    }

    // Before the division: adds whose other operand is a constant or a literal, back to an itof.
    static string? BeforeDivision(List<Instruction> code, int at, string register, int component, int adds)
    {
        int def = Definition(code, at, register, component, out int destComponent);
        if (def < 0) return null;
        Instruction step = code[def];
        string op = Bare(step);
        if (op == "itof") return Op(step, destComponent);
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
            if (!along.IsRegister || along.Negated || along.Absolute) continue;
            string? before = BeforeDivision(code, def, along.Name, along.Component(destComponent), adds - 1);
            if (before is not null)
                return $"{before} > {Op(step, destComponent)} {Beside(code, def, beside, destComponent)}";
        }
        return null;
    }

    /// <summary>The association order of a chain's head.</summary>
    internal enum HeadForm { Written, Reassociated, Other }

    /// <summary>Which of the two analysed association orders a chain's head is, from the <c>itof</c> to the
    /// division: an <c>add</c> of the literal 0.5 and an <c>add</c> of a negated constant-buffer element, the jitter,
    /// in either order. A register beside the head counts as what it holds where that is one constant-buffer element or
    /// literal, moved. Precise markings are ignored here, and kept in <see cref="Remainder"/>.</summary>
    internal static HeadForm Form(string chain)
    {
        string[] head = Head(chain);
        if (head.Length != 3 || !head[0].StartsWith("itof", StringComparison.Ordinal)) return HeadForm.Other;
        string? first = Added(head[1]), second = Added(head[2]);
        if (first == Half && IsJitter(second)) return HeadForm.Written;
        if (IsJitter(first) && second == Half) return HeadForm.Reassociated;
        return HeadForm.Other;
    }

    /// <summary>The jitter element a chain's head subtracts, or null where it is not one of the analysed forms.</summary>
    internal static string? Jitter(string chain)
    {
        if (Form(chain) == HeadForm.Other) return null;
        string[] head = Head(chain);
        return IsJitter(Added(head[1])) ? Added(head[1]) : Added(head[2]);
    }

    /// <summary>The chain from its division on, the part the head's order does not change.</summary>
    internal static string Remainder(string chain)
    {
        int division = chain.IndexOf(" > div", StringComparison.Ordinal);
        return division < 0 ? chain : chain[(division + 3)..];
    }

    const string Half = "+l(0.500000)";

    static bool IsJitter(string? operand) => operand is not null && operand.StartsWith("-cb", StringComparison.Ordinal);

    // The steps before the division, precise markings dropped.
    static string[] Head(string chain)
    {
        int division = chain.IndexOf(" > div", StringComparison.Ordinal);
        string head = division < 0 ? chain : chain[..division];
        return head.Replace(" [precise]", "", StringComparison.Ordinal).Split(" > ");
    }

    // What an add step adds: its operand, or what a register beside it holds where that is one element or literal.
    static string? Added(string step)
    {
        if (!step.StartsWith("add ", StringComparison.Ordinal)) return null;
        string operand = step["add ".Length..];
        Match moved = Regex.Match(operand, @"^\+r\(([+-](?:cb\d+\[\d+\]\.[xyzw]|l\([^)]*\)))\)$");
        return moved.Success ? moved.Groups[1].Value : operand;
    }

    /// <summary>Every <c>mad</c> by a literal -0.5, and every <c>add</c> of a literal -0.5 to a register a
    /// multiplication by a constant wrote, that reads as no chain, with up to eight instructions behind its first
    /// register source, nearest first, so a log shows the shape the reader missed.</summary>
    internal static List<string> Unread(string disassembly)
    {
        List<Instruction> code = Parse(disassembly);
        var unread = new List<string>();
        for (int i = 0; i < code.Count; i++)
        {
            Instruction last = code[i];
            if (last.Destinations.Length != 1 || (Bare(last) != "mad" || last.Sources.Length != 3)
                && (Bare(last) != "add" || last.Sources.Length != 2)) continue;
            foreach (int c in Operand.Parse(last.Destinations[0]).Mask())
            {
                bool position = Bare(last) == "mad" ? Operand.Parse(last.Sources[2]).Literal(c) == MinusHalf
                    : PositionComponents(code, i).Contains(c);
                if (!position || PositionComponents(code, i).Contains(c) && Read(code, i, c) is not null) continue;
                var behind = new List<string> { Text(last) };
                int at = i;
                Operand along = last.Sources.Select(Operand.Parse).FirstOrDefault(o => o.IsRegister) ?? new Operand();
                int component = along.Component(c);
                for (int step = 0; step < 8 && along.IsRegister; step++)
                {
                    int def = Definition(code, at, along.Name, component, out int destComponent);
                    if (def < 0) break;
                    behind.Add(Text(code[def]));
                    Operand next = code[def].Sources.Select(Operand.Parse).FirstOrDefault(o => o.IsRegister) ?? new Operand();
                    (at, along, component) = (def, next, next.Component(destComponent));
                }
                unread.Add($"component {"xyzw"[c]}: " + string.Join(" < ", behind));
            }
        }
        return unread;
    }

    static string Text(Instruction instruction)
    {
        string marking = instruction.Precise.Length == 0 ? "" : $" [precise({instruction.Precise})]";
        return instruction.Opcode + marking + " "
            + string.Join(", ", instruction.Destinations.Concat(instruction.Sources));
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

    static string Bare(Instruction instruction) => instruction.Opcode;

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
        bool IsInput => Name.Length > 1 && Name[0] == 'v' && char.IsDigit(Name[1]);
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
        /// <c>r</c>, a constant-buffer element or shader input with the component read, or a literal's value.</summary>
        internal string Kind(int c)
        {
            string sign = Negated ? "-" : "+";
            if (IsLiteral) return $"{sign}l({Literal(c)})";
            string body = IsRegister ? "r" : IsConstant || IsInput ? $"{Name}.{"xyzw"[Component(c)]}" : Name;
            return sign + (Absolute ? $"|{body}|" : body);
        }

        static int Index(char ch) => "xyzw".IndexOf(ch, StringComparison.Ordinal);

        [GeneratedRegex(@"^(?<name>[a-zA-Z]+\d*(?:\[[^\]]*\])*)(?:\.(?<swizzle>[xyzw]+))?$")]
        private static partial Regex Shape();
    }
}
