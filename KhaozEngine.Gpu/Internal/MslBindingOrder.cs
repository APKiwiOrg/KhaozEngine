using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>One resource argument of an emitted Metal entry point, resolved back to what it was declared as.
    /// </summary>
    /// <param name="Space">The Metal index space: <c>buffer</c>, <c>texture</c> or <c>sampler</c>. Each is
    /// numbered independently.</param>
    /// <param name="Index">The index the cross-compiler chose within that space.</param>
    /// <param name="Set">The variable's <c>DescriptorSet</c> decoration.</param>
    /// <param name="Binding">The variable's <c>Binding</c> decoration, which is what the author wrote in the
    /// GLSL and what the resource layout is counted in.</param>
    internal readonly record struct MslBoundResource(string Space, int Index, uint Set, uint Binding);

    /// <summary>
    /// THE METAL BINDING-ORDER GUARD, shared by <see cref="ShaderValidation.ValidatePair"/> and
    /// <see cref="ShaderValidation.ValidateCompute"/>. Two independent checks, both device-free and both reading
    /// only what a cross-compile already produced.
    ///
    /// <para>
    /// <b>Why there is anything to check.</b> Metal has no binding decorations. The cross-compiler hands each
    /// resource an index of its own within one of three argument tables, assigned in SPIR-V id order, which
    /// follows where each resource is FIRST REFERENCED across the emitted function bodies. A backend that binds
    /// a resource set by counting the layout's elements in BINDING order, one counter per kind, agrees with that
    /// numbering only by luck, and where the two disagree Metal reads the wrong resource and returns zero rather
    /// than failing: Vulkan and Direct3D11 stay perfectly correct because they honour the decorations. That is
    /// the shape the engine shipped on Metal until <c>18.0.0</c>, and three separate shipped bugs of it are
    /// recorded in <c>docs/design/FFT-OCEAN-DESIGN-2026-07-26.md</c>, every one found by an image golden or a
    /// bisect rather than by a build failure. The native Metal backend binds at an AUTHORED index instead, so
    /// what these two checks hold up now is the engine's own one-uniform-buffer-per-pipeline rule, which
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> retires together with the
    /// shaders it blocks.
    /// </para>
    /// <para>
    /// <b>THE JOIN IS KEYED ON THE SPIR-V ID, PER STAGE.</b> Each emitted argument is named <c>_&lt;id&gt;</c>
    /// after its SPIR-V result id, and <see cref="SpirvResourceDecorations"/> resolves that id to the
    /// <c>(set, binding)</c> the author declared, read out of THAT STAGE'S OWN module because ids are renumbered
    /// per stage. That is the same key the native Metal backend's binding table joins on, and section 2.2a of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> is the measurement of why a NAME join is not
    /// available: every texture and sampler element reflects with an empty name. The id join is also what makes
    /// this guard see a SAME-KIND swap, which the kind-comparing fallback in
    /// <see cref="ShaderValidation"/> cannot: two storage buffers are both <c>device T&amp;</c> in Metal and both
    /// <c>StructuredBuffer</c> in the reflection, but they carry different binding decorations.
    /// </para>
    /// <para>
    /// <b>IT DEGRADES RATHER THAN FALSE-POSITIVES, WHICH IS THE ONE PLACE IT DIFFERS FROM THE BACKEND'S JOIN.</b>
    /// The backend throws loudly on an argument it cannot resolve, because a wrong bind there renders a wrong
    /// pixel. This runs on every shader the engine or a consumer compiles, so an unresolvable argument (a name
    /// that is not <c>_&lt;id&gt;</c> because debug info was left on, a cross-compiler helper argument such as a
    /// buffer-size buffer, an argument-buffer emission) drops THAT INDEX SPACE from the check and says nothing.
    /// A false positive here would be a build break on a correct shader.
    /// </para>
    /// </summary>
    internal static class MslBindingOrder
    {
        internal const string Vertex = "vertex";
        internal const string Fragment = "fragment";
        internal const string Compute = "compute";

        static readonly string[] Spaces = { "buffer", "texture", "sampler" };

        /// <summary>WHAT THE CONSTRAINT PROTECTS, carried in every message this type throws. Without it the
        /// rejection reads as a bug in the validator to anyone whose shader draws correctly on every device they
        /// own, and it would read that way fairly: no backend the engine ships needs this shape rejected. What is
        /// left is the engine's one-uniform-buffer rule, kept because lifting it moves goldens on all three
        /// backends rather than because a device requires it, and retired by issue #604 together with the shaders
        /// it blocks.</summary>
        const string RuleClause =
            "This enforces the engine's one-uniform-buffer-per-pipeline rule (issue #604) rather than a limit of "
            + "any backend it ships. The native Metal backend binds at an authored index, and Vulkan and "
            + "Direct3D11 honour the decorations, so all three render this correctly and a shader that looks fine "
            + "on every device you tested can still be rejected here.";

        /// <summary>
        /// Check one cross-compiled Metal stage: within each index space, the arguments in Metal index order must
        /// be the arguments in binding order.
        /// </summary>
        /// <param name="spirv">That stage's own SPIR-V module, for the id join.</param>
        /// <param name="msl">The emitted Metal source for that stage.</param>
        /// <param name="stage">Which stage, for the entry-point keyword and the error message.</param>
        /// <param name="tag">The shader's label, included in any error message.</param>
        /// <returns>The resolved arguments per index space, for the pair-wide prefix check, or null when the
        /// entry point could not be read at all.</returns>
        /// <exception cref="ShaderValidationException">A stage's Metal index order disagrees with its binding
        /// order.</exception>
        internal static Dictionary<string, List<MslBoundResource>>? CheckStage(
            byte[] spirv, string msl, string stage, string tag)
        {
            Dictionary<string, List<MslBoundResource>>? bySpace = Resolve(spirv, msl, stage, tag);
            if (bySpace is null) return null;

            foreach ((string space, List<MslBoundResource> resources) in bySpace)
            {
                var byBinding = new List<MslBoundResource>(resources);
                byBinding.Sort(static (a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));
                resources.Sort(static (a, b) => a.Index.CompareTo(b.Index));

                // A space whose emitted indices are not dense 0..n-1 has an argument this parse did not see, so
                // position and index are different numbers and every comparison below would be off by the gap.
                if (!IsDense(resources)) continue;

                for (int i = 0; i < resources.Count; i++)
                {
                    MslBoundResource emitted = resources[i], declared = byBinding[i];
                    if (emitted.Set == declared.Set && emitted.Binding == declared.Binding) continue;

                    int belongs = byBinding.IndexOf(emitted);
                    throw new ShaderValidationException(
                        $"{tag}: the Metal {stage} entry point puts layout(set={emitted.Set}, "
                        + $"binding={emitted.Binding}) at {space} index {i}, where binding order puts "
                        + $"layout(set={declared.Set}, binding={declared.Binding}) instead (binding order puts "
                        + $"binding {emitted.Binding} at {space} index {belongs}). Metal has no binding "
                        + $"decorations, so the cross-compiler numbers a stage's {space} arguments in "
                        + "FIRST-REFERENCE order while the resource layout is counted in binding order. Where the "
                        + "two disagree the wrong resource is bound to each slot, on Metal ONLY, and silently. "
                        + "Make the first reference to each resource happen in binding order - hoist a first touch "
                        + "into main when a helper function reaches a later binding first. "
                        + RuleClause);
                }
            }
            return bySpace;
        }

        /// <summary>
        /// THE PREFIX PROPERTY, which is a constraint on the LAYOUT rather than on first-reference order and is
        /// therefore not reachable by <see cref="CheckStage"/>. The rule counts a resource with one counter per
        /// kind across the WHOLE layout and expects it at that number in every stage its mask names, while the
        /// cross-compiler numbers each stage densely from 0 over only the bindings that stage declares. The two
        /// agree only when every stage's resources are a PREFIX of the layout's, per index space. A vertex-only
        /// texture placed after a fragment-only one cannot be made to work at any binding number.
        /// <para>
        /// WHAT IT CANNOT SEE: an element no stage references at all. That element still takes a slot in the
        /// whole-layout count and still shifts everything after it, but it is absent from both stages' emissions
        /// and from the reflection's kinds, so there is nothing sound to count. A false negative there is the
        /// price of never firing on a correct shader.
        /// </para>
        /// </summary>
        /// <exception cref="ShaderValidationException">One stage's resources are not a prefix of the layout's.
        /// </exception>
        internal static void CheckPrefix(Dictionary<string, List<MslBoundResource>>? vertex,
            Dictionary<string, List<MslBoundResource>>? fragment, string tag)
        {
            if (vertex is null || fragment is null) return;

            foreach (string space in Spaces)
            {
                // A space either stage dropped is a space whose full membership is unknown, so the union below
                // would be missing entries and every conclusion drawn from it would be arithmetic on a guess.
                if (!vertex.TryGetValue(space, out List<MslBoundResource>? vs)
                    || !fragment.TryGetValue(space, out List<MslBoundResource>? fs)) continue;

                var union = new List<(uint Set, uint Binding)>();
                foreach (MslBoundResource r in vs) union.Add((r.Set, r.Binding));
                foreach (MslBoundResource r in fs)
                    if (!union.Contains((r.Set, r.Binding))) union.Add((r.Set, r.Binding));
                union.Sort(static (a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));

                RequirePrefix(union, vs, Vertex, space, tag);
                RequirePrefix(union, fs, Fragment, space, tag);
            }
        }

        static void RequirePrefix(List<(uint Set, uint Binding)> union, List<MslBoundResource> stageResources,
            string stage, string space, string tag)
        {
            var used = new HashSet<(uint, uint)>();
            foreach (MslBoundResource r in stageResources) used.Add((r.Set, r.Binding));

            int skipped = -1;
            for (int i = 0; i < union.Count; i++)
            {
                if (!used.Contains(union[i])) { if (skipped < 0) skipped = i; continue; }
                if (skipped < 0) continue;

                (uint set, uint binding) = union[i];
                (uint missedSet, uint missedBinding) = union[skipped];
                throw new ShaderValidationException(
                    $"{tag}: the {stage} stage's {space} resources are not a PREFIX of the resource layout. It "
                    + $"reads layout(set={set}, binding={binding}) but never reads "
                    + $"layout(set={missedSet}, binding={missedBinding}), which comes before it in the {space} "
                    + "index space. The layout is counted one slot per kind across the WHOLE of it while the "
                    + "cross-compiler numbers each stage densely from 0 over only what that stage declares, so "
                    + $"this stage reaches for a lower {space} index than the layout binds binding {binding} at, "
                    + "and no binding number can reconcile the two. Reorder the layout so every resource this "
                    + $"stage reads comes before the ones it does not, or give it a reference to binding "
                    + $"{missedBinding} as well. "
                    + RuleClause);
            }
        }

        /// <summary>Resolve every entry-point resource argument to its declared <c>(set, binding)</c>, grouped by
        /// index space. A space containing any argument the id join cannot resolve is DROPPED rather than
        /// partially checked.</summary>
        static Dictionary<string, List<MslBoundResource>>? Resolve(byte[] spirv, string msl, string stage, string tag)
        {
            int open = EntryPointArguments(msl, stage);
            if (open < 0) return null;
            int close = MatchingParen(msl, open);
            if (close < 0) return null;

            IReadOnlyDictionary<uint, SpirvResourceDecoration> decorations =
                SpirvResourceDecorations.Read(spirv, $"{tag} [{stage}]");

            var bySpace = new Dictionary<string, List<MslBoundResource>>();
            var dropped = new HashSet<string>();

            foreach (string argument in SplitArguments(msl.Substring(open + 1, close - open - 1)))
            {
                foreach (string space in Spaces)
                {
                    string marker = "[[" + space + "(";
                    int at = argument.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;
                    int numberEnd = argument.IndexOf(')', at + marker.Length);
                    if (numberEnd < 0
                        || !int.TryParse(argument.AsSpan(at + marker.Length, numberEnd - at - marker.Length),
                                         out int index))
                    {
                        dropped.Add(space);
                        break;
                    }

                    // The declared name is the last identifier before the attribute, past any reference or
                    // pointer punctuation: "constant _68& _70 [[buffer(0)]]" names _70.
                    string declaration = argument[..at].TrimEnd();
                    int split = declaration.LastIndexOfAny(new[] { ' ', '&', '*' });
                    string name = split >= 0 ? declaration[(split + 1)..] : declaration;

                    if (!TryReadId(name, out uint id) || !decorations.TryGetValue(id, out SpirvResourceDecoration d))
                    {
                        dropped.Add(space);
                        break;
                    }

                    if (!bySpace.TryGetValue(space, out List<MslBoundResource>? list))
                        bySpace[space] = list = new List<MslBoundResource>();
                    list.Add(new MslBoundResource(space, index, d.Set, d.Binding));
                    break;
                }
            }

            foreach (string space in dropped) bySpace.Remove(space);
            return bySpace;
        }

        /// <summary>Whether a space's emitted indices are exactly <c>0..n-1</c> once sorted, which is what makes
        /// a position in the sorted list the same number as the Metal index it reports.</summary>
        static bool IsDense(List<MslBoundResource> resources)
        {
            for (int i = 0; i < resources.Count; i++)
                if (resources[i].Index != i) return false;
            return true;
        }

        /// <summary>The SPIR-V id an argument name carries. SPIRV-Cross names a variable with no debug name
        /// <c>_&lt;id&gt;</c>, which is what <see cref="SpirvFrontEndPin"/>'s stripped debug info guarantees for
        /// every engine-owned emission. Any other shape is unresolvable rather than wrong.</summary>
        static bool TryReadId(string name, out uint id)
        {
            id = 0;
            return name.Length >= 2 && name[0] == '_' && uint.TryParse(name.AsSpan(1), out id);
        }

        /// <summary>The position of the entry point's opening parenthesis, found only where a declaration can
        /// actually begin so a stage keyword occurring inside an emitted identifier cannot be mistaken for
        /// it.</summary>
        static int EntryPointArguments(string msl, string stage)
        {
            string keyword = stage == Compute ? "kernel " : stage + " ";
            for (int i = 0; i <= msl.Length - keyword.Length; i++)
            {
                if (!BeginsADeclaration(msl, i)) continue;
                if (string.CompareOrdinal(msl, i, keyword, 0, keyword.Length) != 0) continue;
                int open = msl.IndexOf('(', i);
                if (open >= 0) return open;
            }
            return -1;
        }

        /// <summary>
        /// Whether a declaration can begin at this position: the start of the source, the start of a line, or
        /// straight after a function attribute's closing bracket.
        /// <para>
        /// THE BRACKET CASE IS NOT A NICETY. SPIRV-Cross emits a function attribute on the SAME LINE as the
        /// entry point it decorates, so a fragment shader that declares <c>layout(early_fragment_tests) in;</c>
        /// comes out as <c>[[ early_fragment_tests ]] fragment main0_out main0(...)</c>. A line-start-only test
        /// misses that entry point entirely, and because <see cref="Resolve"/> returns null rather than throwing
        /// on an entry point it could not read, the shader would silently lose BOTH its fragment-stage index
        /// order check AND the pair's prefix check while validating clean.
        /// </para>
        /// </summary>
        static bool BeginsADeclaration(string msl, int i)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                char c = msl[j];
                if (c == '\n' || c == ']') return true;
                if (c != ' ' && c != '\t' && c != '\r') return false;
            }
            return true;
        }

        // Matched by DEPTH, not by the first closing parenthesis: every argument carries an attribute like
        // [[buffer(0)]], so a naive scan stops inside the first one and sees a single argument.
        static int MatchingParen(string msl, int open)
        {
            int depth = 0;
            for (int i = open; i < msl.Length; i++)
            {
                if (msl[i] == '(') depth++;
                else if (msl[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        // Split on TOP-LEVEL commas only. A template argument list is a comma too, and
        // "texture2d_array<float, access::write> _361 [[texture(0)]]" is one real argument that a plain Split
        // tears in half, losing the name.
        static List<string> SplitArguments(string arguments)
        {
            var parts = new List<string>();
            int start = 0, angle = 0, paren = 0;
            for (int i = 0; i < arguments.Length; i++)
            {
                char c = arguments[i];
                if (c == '<') angle++;
                else if (c == '>') angle--;
                else if (c == '(' || c == '[') paren++;
                else if (c == ')' || c == ']') paren--;
                else if (c == ',' && angle == 0 && paren == 0)
                {
                    parts.Add(arguments[start..i].Trim());
                    start = i + 1;
                }
            }
            parts.Add(arguments[start..].Trim());
            return parts;
        }
    }
}
