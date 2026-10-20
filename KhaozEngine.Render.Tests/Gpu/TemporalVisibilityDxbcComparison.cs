using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The check the hosted Windows fact makes on each visibility program, kept platform neutral so its own facts run it
/// on real FXC text on every leg (TAA shadow visibility plan, Task 7f, review 7f r3 I-1). A program's chains split by
/// where their last step sits. D's chains run inside D's loops. The fast path's are straight-line code. Since every
/// binade block runs D's function inside the shipped program, D's in-loop chains must compile there exactly as they
/// do in D's own program, so the shipped program's in-loop chains, as a multiset of head form, head precise markings,
/// jitter element and remainder, must equal D's. The fast path adds exactly six straight-line chains, three per jitter
/// element, each with its head in one of the two orders the binade rule is proved for, written or reassociated, and
/// with the head markings, jitter element and remainder of one of D's chains together (review 7f r4, M-2 and M-3).
/// D's own program holds no straight-line chain. Both programs declare the same indexable temps. A rule runs only
/// where the rules it presumes hold, so each failure names its own cause: the rules that read D's chains wait for D
/// to read as a baseline, and the jitter balance waits for six chains that each head in an analysed order.
/// </summary>
internal static class TemporalVisibilityDxbcComparison
{
    /// <summary>The fast path's position chain components: three columns and three rows.</summary>
    internal const int FastPathComponents = 6;

    /// <summary>The failures of the shipped program <paramref name="shipped"/> against its D form
    /// <paramref name="d"/>, both FXC disassembly, each prefixed with <paramref name="name"/>. Every chain, and any
    /// -0.5 position the reader could not read, goes to <paramref name="log"/>.</summary>
    internal static List<string> Compare(string name, string d, string shipped, Action<string>? log = null)
    {
        var failures = new List<string>();
        (int dTemps, string[] dIndexable) = TemporalVisibilityDxbc.Declarations(d);
        (int temps, string[] indexable) = TemporalVisibilityDxbc.Declarations(shipped);
        List<TemporalVisibilityDxbc.Chain> dChains = TemporalVisibilityDxbc.Found(d), chains = TemporalVisibilityDxbc.Found(shipped);
        List<string> dLoop = Keys(dChains, inLoop: true), dStraight = Keys(dChains, inLoop: false);
        List<string> loop = Keys(chains, inLoop: true), straight = Keys(chains, inLoop: false);
        log?.Invoke($"{name}: dcl_temps {temps} (D {dTemps}), indexable temps [{string.Join(" ", indexable)}] "
            + $"(D [{string.Join(" ", dIndexable)}]), {loop.Count} in-loop and {straight.Count} straight-line position "
            + $"chain components (D {dLoop.Count} and {dStraight.Count})");
        Log("D", d, dChains);
        Log("shipped", shipped, chains);

        if (!indexable.SequenceEqual(dIndexable))
            failures.Add($"{name}: indexable temps [{string.Join(" ", indexable)}], D's [{string.Join(" ", dIndexable)}]");
        bool baseline = dLoop.Count >= 2 && dStraight.Count == 0;
        if (dLoop.Count < 2) failures.Add($"{name}: D's DXBC shows {dLoop.Count} in-loop position chain components, not 2 or more");
        if (dStraight.Count != 0)
            failures.Add($"{name}: D's DXBC shows {dStraight.Count} straight-line position chain components, not 0");
        foreach ((string side, List<TemporalVisibilityDxbc.Chain> list) in new[] { ("D's", dChains), ("the shipped", chains) })
            foreach (string chain in list.Select(c => c.Signature).Distinct()
                .Where(c => TemporalVisibilityDxbc.Form(c) == TemporalVisibilityDxbc.HeadForm.Other))
                failures.Add($"{name}: {side} program has a position chain whose head is neither analysed order: {chain}");

        // D's slow path inside the shipped program, against D's program, chain for chain.
        if (baseline && !loop.SequenceEqual(dLoop, StringComparer.Ordinal))
        {
            failures.Add($"{name}: the shipped program's in-loop chains differ from D's");
            foreach (string missing in Without(dLoop, loop)) failures.Add($"{name}:   D's, missing from the shipped: {missing}");
            foreach (string extra in Without(loop, dLoop)) failures.Add($"{name}:   the shipped program's, not D's: {extra}");
        }

        // The fast path's six, each with the head markings, jitter element and remainder of one of D's chains.
        if (straight.Count != FastPathComponents)
            failures.Add($"{name}: {straight.Count} straight-line position chain components, {FastPathComponents} expected");
        if (!baseline) return failures;
        List<string> fast = chains.Where(c => c.LoopDepth == 0).Select(c => c.Signature).ToList();
        var remainders = dChains.Select(c => TemporalVisibilityDxbc.Remainder(c.Signature)).ToHashSet(StringComparer.Ordinal);
        var pairs = dChains.Select(c => Pair(c.Signature)).ToHashSet(StringComparer.Ordinal);
        var marked = dChains.Select(c => Marked(c.Signature)).ToHashSet(StringComparer.Ordinal);
        foreach (string chain in fast.Distinct())
        {
            if (!remainders.Contains(TemporalVisibilityDxbc.Remainder(chain)))
                failures.Add($"{name}: a straight-line position chain whose remainder no chain of D's has: {chain}");
            else if (TemporalVisibilityDxbc.Form(chain) == TemporalVisibilityDxbc.HeadForm.Other) continue;
            else if (!pairs.Contains(Pair(chain)))
                failures.Add($"{name}: a straight-line position chain whose remainder D's chains reach only from another "
                    + $"jitter element: {chain}");
            else if (!marked.Contains(Marked(chain)))
                failures.Add($"{name}: a straight-line position chain whose head precise markings differ from every chain "
                    + $"of D's with its jitter element and remainder: {chain}");
        }
        if (straight.Count != FastPathComponents
            || fast.Any(c => TemporalVisibilityDxbc.Form(c) == TemporalVisibilityDxbc.HeadForm.Other))
            return failures;
        string[] jitters = dChains.Select(c => TemporalVisibilityDxbc.Jitter(c.Signature)).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] straightJitters = fast.Select(c => TemporalVisibilityDxbc.Jitter(c) ?? "none")
            .Order(StringComparer.Ordinal).ToArray();
        string[] expected = jitters.SelectMany(j => Enumerable.Repeat(j, FastPathComponents / 2)).ToArray();
        if (jitters.Length != 2 || !straightJitters.SequenceEqual(expected, StringComparer.Ordinal))
            failures.Add($"{name}: the straight-line chains subtract [{string.Join(" ", straightJitters)}], three of each "
                + $"of D's two jitter elements [{string.Join(" ", jitters)}] expected");
        return failures;

        void Log(string side, string disassembly, List<TemporalVisibilityDxbc.Chain> list)
        {
            foreach (bool inLoop in new[] { true, false })
            {
                List<string> part = list.Where(c => c.LoopDepth > 0 == inLoop).Select(c => c.Signature).ToList();
                log?.Invoke($"  {side} {(inLoop ? "in-loop" : "straight-line")}: "
                    + $"{part.Count(c => TemporalVisibilityDxbc.Form(c) == TemporalVisibilityDxbc.HeadForm.Written)} written, "
                    + $"{part.Count(c => TemporalVisibilityDxbc.Form(c) == TemporalVisibilityDxbc.HeadForm.Reassociated)} "
                    + $"reassociated, {part.Count(c => TemporalVisibilityDxbc.Form(c) == TemporalVisibilityDxbc.HeadForm.Other)} other");
                foreach (string chain in part.Distinct())
                    log?.Invoke($"    {part.Count(c => c == chain)} x {TemporalVisibilityDxbc.Form(chain)}: {chain}");
            }
            foreach (string unread in TemporalVisibilityDxbc.Unread(disassembly).Take(24))
                log?.Invoke($"    unread -0.5 position, {unread}");
        }
    }

    /// <summary>The sorted (head form, head precise markings, jitter element, remainder) keys of the chains inside a
    /// loop, or of the straight-line ones.</summary>
    internal static List<string> Keys(IEnumerable<TemporalVisibilityDxbc.Chain> chains, bool inLoop) => chains
        .Where(c => c.LoopDepth > 0 == inLoop)
        .Select(c => $"{TemporalVisibilityDxbc.Form(c.Signature)} | {Marked(c.Signature)}")
        .Order(StringComparer.Ordinal).ToList();

    // A chain's jitter element and remainder together.
    static string Pair(string chain) => $"{TemporalVisibilityDxbc.Jitter(chain) ?? "none"} | {TemporalVisibilityDxbc.Remainder(chain)}";

    // A chain's head precise markings, jitter element and remainder together.
    static string Marked(string chain) => $"{TemporalVisibilityDxbc.HeadMarking(chain)} | {Pair(chain)}";

    /// <summary>The keys of <paramref name="from"/> left once each key of <paramref name="taken"/> has removed one
    /// copy, a multiset difference.</summary>
    internal static List<string> Without(List<string> from, List<string> taken)
    {
        var left = new List<string>(from);
        foreach (string key in taken) left.Remove(key);
        return left;
    }
}
