using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Edits of FXC's real disassembly that each break one rule of <see cref="TemporalVisibilityDxbcComparison"/> and
/// leave the others satisfied (review 7f r4, I-1 and N-1). They work on the text as lines and find the chains to edit
/// through <see cref="TemporalVisibilityDxbc.Found"/>, so a mutation that missed its target shows as a wrong count in
/// the facts that use it rather than as a pass.
/// </summary>
internal static partial class TemporalVisibilityDxbcMutations
{
    /// <summary>A chain's last step: its line in the text, the destination component it ends and its signature.</summary>
    internal readonly record struct End(int Line, int Component, string Signature);

    /// <summary>The chains in loops, or the straight-line ones, of the text.</summary>
    internal static List<string> Side(string text, bool inLoop) => TemporalVisibilityDxbc.Found(text)
        .Where(c => c.LoopDepth > 0 == inLoop).Select(c => c.Signature).ToList();

    /// <summary>The loop depth of every line, as the reader counts it.</summary>
    static int[] Depths(string[] lines)
    {
        int depth = 0;
        return lines.Select(raw =>
        {
            string line = raw.Trim();
            if (line == "endloop") depth--;
            int at = depth;
            if (line == "loop") depth++;
            return at;
        }).ToArray();
    }

    /// <summary>Every chain end on one side, in text order: each component of an add or mad of a literal -0.5 whose
    /// removal takes exactly one chain away.</summary>
    internal static List<End> Ends(string text, bool inLoop)
    {
        string[] lines = text.Split('\n');
        int[] depths = Depths(lines);
        List<string> before = Side(text, inLoop);
        var ends = new List<End>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (depths[i] > 0 != inLoop || Written().Match(lines[i]) is not { Success: true } line
                || !lines[i].Contains("-0.500000", StringComparison.Ordinal)
                || Opcode(line) is not ("add" or "mad"))
                continue;
            foreach (char component in line.Groups["mask"].Value)
            {
                string[] copy = (string[])lines.Clone();
                copy[i] = WithoutComponent(lines[i], component);
                List<string> after = Side(string.Join('\n', copy), inLoop);
                List<string> gone = TemporalVisibilityDxbcComparison.Without(before, after);
                if (gone.Count == 1 && after.Count == before.Count - 1)
                    ends.Add(new End(i, "xyzw".IndexOf(component, StringComparison.Ordinal), gone[0]));
            }
        }
        return ends;
    }

    /// <summary>The text with one chain's last step no longer writing its component, so the chain is gone.</summary>
    internal static string Remove(string text, End end)
    {
        string[] lines = text.Split('\n');
        lines[end.Line] = WithoutComponent(lines[end.Line], "xyzw"[end.Component]);
        return string.Join('\n', lines);
    }

    /// <summary>The text with one chain's last step repeated into a fresh register just before it, so the chain
    /// reads twice.</summary>
    internal static string Duplicate(string text, End end)
    {
        List<string> lines = text.Split('\n').ToList();
        Match line = Written().Match(lines[end.Line]);
        lines.Insert(end.Line, $"{line.Groups["op"].Value} r98.{"xyzw"[end.Component]}{line.Groups["rest"].Value}");
        return string.Join('\n', lines);
    }

    /// <summary>The text with a <c>dcl_indexableTemp</c> declared after its <c>dcl_temps</c>.</summary>
    internal static string AddIndexableTemp(string text)
    {
        List<string> lines = text.Split('\n').ToList();
        lines.Insert(lines.FindIndex(l => l.StartsWith("dcl_temps ", StringComparison.Ordinal)) + 1, "dcl_indexableTemp x9[4], 4");
        return string.Join('\n', lines);
    }

    /// <summary>The text with the steps of one in-loop chain, from its <c>itof</c> to its last step, copied in
    /// straight-line code before the program's last <c>ret</c>.</summary>
    internal static string CopyOutOfLoop(string text, End end)
    {
        List<string> lines = text.Split('\n').ToList();
        int start = lines.FindLastIndex(end.Line, l => l.TrimStart().StartsWith("itof", StringComparison.Ordinal));
        List<string> block = lines.GetRange(start, end.Line - start + 1).Select(l => l.Trim()).ToList();
        lines.InsertRange(lines.FindLastIndex(l => l.Trim() == "ret"), block);
        return string.Join('\n', lines);
    }

    /// <summary>The text with the scale of a chain's last step, the constant its <c>mad</c> or the <c>mul</c> before
    /// its <c>add</c> multiplies by, read from constant-buffer element 99 instead.</summary>
    internal static string RescaleFrom99(string text, End end)
    {
        string[] lines = text.Split('\n');
        Match last = Written().Match(lines[end.Line]);
        int at = end.Line;
        if (Opcode(last) == "add")
        {
            string register = last.Groups["rest"].Value.Split(", ").First(o => o.StartsWith('r')).Split('.')[0];
            at = Array.FindLastIndex(lines, end.Line - 1, l => Written().Match(l) is { Success: true } m
                && Opcode(m) == "mul" && m.Groups["register"].Value == register);
        }
        lines[at] = ConstantElement().Replace(lines[at], m => $"cb{m.Groups[1].Value}[99]", 1);
        return string.Join('\n', lines);
    }

    /// <summary>The text with the first straight-line jitter subtract reading the other jitter element on every
    /// component: each chain it heads subtracts <c>.x</c> where it divides and scales by the <c>.y</c> elements, or
    /// the reverse (review 7f r4, M-3).</summary>
    internal static string SwapJitterElements(string text)
    {
        string[] lines = text.Split('\n');
        int[] depths = Depths(lines);
        int at = Enumerable.Range(0, lines.Length).First(i => depths[i] == 0 && Jitter().IsMatch(lines[i]));
        Match jitter = Jitter().Match(lines[at]);
        string swizzle = string.Concat(jitter.Groups["swizzle"].Value.Select(ch => ch == 'x' ? 'y' : ch == 'y' ? 'x' : ch));
        lines[at] = lines[at][..jitter.Groups["swizzle"].Index] + swizzle;
        return string.Join('\n', lines);
    }

    /// <summary>The text with a precise marking added to the half add beside the first jitter subtract in
    /// straight-line code, or in a loop, so its head markings differ from D's (review 7f r4, M-2).</summary>
    internal static string MarkHalf(string text, bool inLoop)
    {
        string[] lines = text.Split('\n');
        int half = HalfBesideJitter(lines, inLoop);
        lines[half] = lines[half].Replace("add ", "add [precise] ", StringComparison.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>The text with the in-loop chain's head, the jitter subtract and the half added beside it, in the other
    /// order (review 7f r3).</summary>
    internal static string SwapInLoopHead(string text)
    {
        string[] lines = text.Split('\n');
        int half = HalfBesideJitter(lines, inLoop: true);
        int jitter = Jitter().IsMatch(lines[half - 1]) ? half - 1 : half + 1;
        (lines[jitter], lines[half]) = (lines[half], lines[jitter]);
        return string.Join('\n', lines);
    }

    /// <summary>The text with the first straight-line head written <c>(at + 0.5) - J</c> rewritten to add
    /// <c>0.5 - J</c>, computed into a fresh register, on the components the half covers, a third association
    /// (review 7f r4, N-1). Any component the jitter subtract covers beyond the half's keeps its own subtract, so only
    /// the chains through the half change.</summary>
    internal static string ShareStraightLineHead(string text)
    {
        string[] lines = text.Split('\n');
        int[] depths = Depths(lines);
        for (int i = 1; i < lines.Length; i++)
        {
            if (depths[i] != 0 || Jitter().Match(lines[i]) is not { Success: true } sub
                || Half().Match(lines[i - 1]) is not { Success: true } add
                || add.Groups["register"].Value != sub.Groups["register"].Value) continue;
            string mask = add.Groups["mask"].Value, subMask = sub.Groups["mask"].Value;
            if (mask.Any(c => !subMask.Contains(c, StringComparison.Ordinal))) continue;
            string op = add.Groups["op"].Value, register = add.Groups["register"].Value;
            var result = lines.Take(i - 1).ToList();
            result.Add($"{op} r99.{mask}, {sub.Groups["jitter"].Value}, {add.Groups["half"].Value}");
            result.Add($"{op} {register}.{mask}, {add.Groups["source"].Value}, r99.xyzw");
            string rest = string.Concat(subMask.Where(c => !mask.Contains(c, StringComparison.Ordinal)));
            if (rest.Length > 0)
                result.Add($"{sub.Groups["op"].Value} {register}.{rest}, {sub.Groups["source"].Value}, {sub.Groups["jitter"].Value}");
            result.AddRange(lines.Skip(i + 1));
            return string.Join('\n', result);
        }
        throw new InvalidOperationException("no straight-line head written (at + 0.5) - J to rewrite");
    }

    // The half add beside the first jitter subtract on one side.
    static int HalfBesideJitter(string[] lines, bool inLoop)
    {
        int[] depths = Depths(lines);
        for (int i = 0; i < lines.Length; i++)
        {
            if (depths[i] > 0 != inLoop || !Jitter().IsMatch(lines[i])) continue;
            foreach (int j in new[] { i - 1, i + 1 })
                if (Half().IsMatch(lines[j])) return j;
        }
        throw new InvalidOperationException($"no {(inLoop ? "in-loop" : "straight-line")} jitter subtract beside a half");
    }

    // A line with its destination mask lacking one component, blank where nothing is left.
    static string WithoutComponent(string line, char component)
    {
        Match m = Written().Match(line);
        string mask = m.Groups["mask"].Value.Replace(component.ToString(), "", StringComparison.Ordinal);
        return mask.Length == 0 ? "" : $"{m.Groups["op"].Value} {m.Groups["register"].Value}.{mask}{m.Groups["rest"].Value}";
    }

    static string Opcode(Match line) => line.Groups["op"].Value.Trim().Split(' ')[0];

    [GeneratedRegex(@"^(?<op>\s*[a-z_]+(?: \[precise[^\]]*\])?) (?<register>r\d+)\.(?<mask>[xyzw]+)(?<rest>, .*)$")]
    private static partial Regex Written();

    [GeneratedRegex(@"^(?<op>\s*add(?: \[precise[^\]]*\])?) (?<register>r\d+)\.(?<mask>[xyzw]+), (?<source>r\d+\.[xyzw]+), (?<jitter>-cb\d+\[\d+\]\.(?<swizzle>[xyzw]+))$")]
    private static partial Regex Jitter();

    [GeneratedRegex(@"^(?<op>\s*add(?: \[precise[^\]]*\])?) (?<register>r\d+)\.(?<mask>[xyzw]+), (?<source>r\d+\.[xyzw]+), (?<half>l\((?:0\.500000|0\.000000)(?:, (?:0\.500000|0\.000000))*\))$")]
    private static partial Regex Half();

    [GeneratedRegex(@"cb(\d+)\[\d+\]")]
    private static partial Regex ConstantElement();
}
