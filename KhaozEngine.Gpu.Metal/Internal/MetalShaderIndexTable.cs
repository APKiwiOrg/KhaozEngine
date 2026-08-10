using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>Which declared element, for which stage, one table entry answers for.</summary>
    /// <param name="Set">The layout's position in the pipeline's layout array, which is the SPIR-V
    /// <c>DescriptorSet</c> decoration.</param>
    /// <param name="Binding">The element's position within that layout, which is the <c>Binding</c>
    /// decoration.</param>
    /// <param name="Stage">Which stage's argument table the index is in. The same element has DIFFERENT indices
    /// in different stages, and no index at all in a stage that does not reference it.</param>
    internal readonly record struct MetalIndexTableKey(int Set, int Binding, MetalShaderStage Stage);

    /// <summary>Where the emission actually put one element for one stage.</summary>
    /// <param name="Space">Which of the three argument tables.</param>
    /// <param name="Index">The index within that table.</param>
    internal readonly record struct MetalIndexTableEntry(MetalIndexSpace Space, int Index);

    /// <summary>
    /// THE PER-PROGRAM BINDING TABLE, KEYED ON <c>(set, binding, stage)</c>, AND THE WHOLE OF WHAT M-B1 MEANS
    /// (decision M-B1 as re-adjudicated in section 2.2b of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). Metal has no binding decorations, so the
    /// index a resource actually landed at is a fact about the EMISSION and nothing else. This reads it.
    ///
    /// <para>
    /// THE JOIN IS KEYED ON THE SPIR-V ID, and section 2.2a is why it is not keyed on the name. Each emitted
    /// argument is named <c>_&lt;id&gt;</c> after its SPIR-V result id, and that id's <c>DescriptorSet</c> and
    /// <c>Binding</c> decorations resolve it to a declared element. Measured over the shipped set the name join
    /// reached 0 of 159 arguments and this one reached 159 of 159, with no failure class of any size. The three
    /// ways the name join died are all absent by construction here: a decoration is present whether or not a name
    /// is, an id needs no <c>{blockType}_{instance}</c> convention, and the per-stage renumbering that killed the
    /// suffix rule is the MECHANISM here because each stage's ids are read out of that stage's own module.
    /// </para>
    /// <para>
    /// IT DOMINATES THE ARITHMETIC IT REPLACES, and the margin is measured rather than argued. The incumbent
    /// assigns each element a per-kind declaration-order slot and sums the preceding layouts, which is right only
    /// where first-reference order happens to equal declaration order. Over the shipped set the two agree on all
    /// 159 arguments today, so this backend changes NO binding, and <c>MetalMslIdJoinSpikeTests</c> keeps that
    /// comparison standing through the rollout window as the evidence for that sentence. What separates them is
    /// the day a shader changes: the arithmetic then binds the wrong resource and renders a wrong pixel with no
    /// validation error, which is the class that produced `7.25.0`, `7.51.2` and the splat terrain, while this
    /// throws at shader-set creation, device-free, before any device runs.
    /// </para>
    /// <para>
    /// <b>NOTHING HERE EVER FALLS BACK TO A COUNT (2.2b, pin 1).</b> An argument name that is not
    /// <c>_&lt;id&gt;</c>, an id carrying no decorations in that stage's module, a <c>(set, binding)</c> outside
    /// the declared layout array, a kind that does not match its index space, or two arguments resolving to one
    /// element: every one of them throws, naming the program, the stage and the offending argument. A silent
    /// fallback would reintroduce the arithmetic's failure mode inside this mechanism, which is the worst of the
    /// three outcomes available. The <c>_&lt;id&gt;</c> spelling is a SPIRV-Cross emission convention that nothing
    /// promises, and throwing loudly is what makes that fragility safe. Those are the five classes THIS type
    /// owns. <see cref="MetalMslEntryPoint"/> owns two more in front of it, for the same reason and with the same
    /// answer: an argument whose index attribute cannot be read is a throw there rather than a dropped argument,
    /// because an argument dropped before this point is one none of the five refusals below can ever see.
    /// </para>
    /// <para>
    /// AN ELEMENT WITH NO ENTRY FOR A STAGE IS NOT BOUND FOR THAT STAGE, and that is correct by construction
    /// rather than a gap. SPIRV-Cross omits an argument a stage does not reference, and binding one anyway is what
    /// an index-counting backend does that produces the off-by-one. Over the shipped set 95 of 254 stage/element
    /// slots are unreferenced, so this is the common case rather than the corner.
    /// </para>
    /// <para>
    /// WHAT LATER ROWS ADD. Row 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/576) content-deduplicates
    /// these tables so M-R9's pipeline-switch comparison is a handle compare, and hangs one off each pipeline.
    /// <see cref="ContentKey"/> is the seam for that and is not consumed here. Row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579) binds through <see cref="TryGetIndex"/>, one array
    /// call per (kind, stage). Neither changes what this reads.
    /// </para>
    /// </summary>
    internal sealed class MetalShaderIndexTable
    {
        readonly Dictionary<MetalIndexTableKey, MetalIndexTableEntry> _entries;
        readonly GpuResourceLayoutDescription[] _layouts;

        MetalShaderIndexTable(Dictionary<MetalIndexTableKey, MetalIndexTableEntry> entries,
            GpuResourceLayoutDescription[] layouts)
        {
            _entries = entries;
            _layouts = layouts;
        }

        /// <summary>The reflected layout array this table was built against, in set order. Held so
        /// <see cref="RequireLayoutShape"/> can compare an ENGINE-declared array against it rather than against a
        /// memory of it.</summary>
        internal IReadOnlyList<GpuResourceLayoutDescription> Layouts => _layouts;

        /// <summary>How many (element, stage) pairs the emission actually referenced. The census the index-table
        /// test asserts over, and the number a diagnostic line quotes.</summary>
        internal int Count => _entries.Count;

        /// <summary>
        /// Build the table for one program from every stage's emitted MSL and that stage's own SPIR-V module.
        /// </summary>
        /// <param name="layouts">The reflection's resource layouts, in set order.</param>
        /// <param name="stages">Each stage's own SPIR-V module and the arguments already parsed out of its
        /// emitted entry point. The arguments are passed in rather than re-parsed here so the name the library is
        /// asked for and the indices the table is built from come from ONE read of the emission.</param>
        /// <param name="label">A name for the program, included in every error message.</param>
        /// <exception cref="ShaderValidationException">Any of the five failure classes pin 1 gives the JOIN. The
        /// two the argument parse owns have already fired before this runs.</exception>
        internal static MetalShaderIndexTable Build(GpuResourceLayoutDescription[] layouts,
            IReadOnlyList<MetalMslStageJoin> stages, string label)
        {
            ArgumentNullException.ThrowIfNull(layouts);
            ArgumentNullException.ThrowIfNull(stages);

            var entries = new Dictionary<MetalIndexTableKey, MetalIndexTableEntry>();

            foreach (MetalMslStageJoin stage in stages)
            {
                IReadOnlyDictionary<uint, SpirvResourceDecoration> decorations =
                    SpirvResourceDecorations.Read(stage.Spirv, $"{label} [{Name(stage.Stage)}]");

                foreach (MetalMslArgument argument in stage.Arguments)
                {
                    string where = $"{label} [{Name(stage.Stage)}] [[{argument.Space.Word()}({argument.Index})]] "
                        + $"'{argument.Name}'";

                    if (!MetalMslEntryPoint.TryReadId(argument.Name, out uint id))
                    {
                        throw Loud(where,
                            "its name is not the _<id> shape SPIRV-Cross gives a resource with no debug name, so "
                            + "there is no SPIR-V id to resolve it through. Something named this resource: check "
                            + "whether SpirvFrontEndPin still strips debug info and whether the Veldrid.SPIRV pin "
                            + "moved. There is deliberately no fallback to counting arguments.");
                    }

                    if (!decorations.TryGetValue(id, out SpirvResourceDecoration decoration))
                    {
                        throw Loud(where,
                            $"SPIR-V id {id.ToString(CultureInfo.InvariantCulture)} carries no DescriptorSet and "
                            + "Binding pair in THIS STAGE'S own module. Ids are renumbered per stage, so an id "
                            + "read against the wrong module is exactly this symptom.");
                    }

                    if (decoration.Set >= (uint)layouts.Length)
                    {
                        throw Loud(where,
                            $"it decorates set {decoration.Set.ToString(CultureInfo.InvariantCulture)}, past the "
                            + $"{layouts.Length.ToString(CultureInfo.InvariantCulture)} layouts the reflection "
                            + "declares. Set N is the layout at slot N, and that positional assumption is checked "
                            + "here rather than assumed.");
                    }

                    GpuResourceLayoutElement[] elements = layouts[(int)decoration.Set].Elements;
                    if (decoration.Binding >= (uint)elements.Length)
                    {
                        throw Loud(where,
                            $"it decorates binding {decoration.Binding.ToString(CultureInfo.InvariantCulture)} in "
                            + $"set {decoration.Set.ToString(CultureInfo.InvariantCulture)}, which declares "
                            + $"{elements.Length.ToString(CultureInfo.InvariantCulture)} elements. Binding M is "
                            + "that layout's element M, and that positional assumption is checked here too.");
                    }

                    GpuResourceKind kind = elements[(int)decoration.Binding].Kind;
                    if (!argument.Space.MatchesKind(kind))
                    {
                        throw Loud(where,
                            $"it resolved to a {kind} element, which does not belong in the "
                            + $"{argument.Space.Word()} index space. The join reached the wrong element, and "
                            + "binding it would put a resource of one kind where another was expected.");
                    }

                    var key = new MetalIndexTableKey((int)decoration.Set, (int)decoration.Binding, stage.Stage);
                    if (entries.TryGetValue(key, out MetalIndexTableEntry existing))
                    {
                        throw Loud(where,
                            $"a second argument in this stage already resolved to set "
                            + $"{decoration.Set.ToString(CultureInfo.InvariantCulture)} binding "
                            + $"{decoration.Binding.ToString(CultureInfo.InvariantCulture)}, at "
                            + $"[[{existing.Space.Word()}({existing.Index.ToString(CultureInfo.InvariantCulture)})]]. "
                            + "The join is a bijection within a stage, so two arguments collapsing onto one "
                            + "element means one of them would never be bound.");
                    }

                    entries[key] = new MetalIndexTableEntry(argument.Space, argument.Index);
                }
            }

            return new MetalShaderIndexTable(entries, layouts);
        }

        /// <summary>
        /// Where <paramref name="stage"/> reads the element declared at <paramref name="set"/> /
        /// <paramref name="binding"/>, or false when that stage does not reference it and therefore must not be
        /// bound for it.
        /// </summary>
        internal bool TryGetIndex(int set, int binding, MetalShaderStage stage, out MetalIndexTableEntry entry)
            => _entries.TryGetValue(new MetalIndexTableKey(set, binding, stage), out entry);

        /// <summary>Every entry, for the index-table test and for row 10's content dedup. Ordered by key, so two
        /// tables with the same content enumerate identically and <see cref="ContentKey"/> is stable.</summary>
        internal IEnumerable<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>> Entries()
        {
            var keys = new List<MetalIndexTableKey>(_entries.Keys);
            keys.Sort(static (a, b) =>
            {
                int c = a.Set.CompareTo(b.Set);
                if (c != 0) return c;
                c = a.Binding.CompareTo(b.Binding);
                return c != 0 ? c : ((int)a.Stage).CompareTo((int)b.Stage);
            });

            foreach (MetalIndexTableKey key in keys)
                yield return new KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>(key, _entries[key]);
        }

        /// <summary>
        /// A stable rendering of the table's CONTENT, for row 10's deduplication (M-R9). Two programs whose tables
        /// say the same thing produce the same string here, which is what lets row 10 hand both pipelines one
        /// shared instance and make the invalidation comparison a handle compare.
        /// <para>
        /// NOT CONSUMED IN THIS ROW, and named rather than left implicit so row 10 does not invent a second
        /// notion of table identity beside this one.
        /// </para>
        /// </summary>
        internal string ContentKey
        {
            get
            {
                var text = new StringBuilder(_entries.Count * 16);
                foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in Entries())
                {
                    text.Append(key.Set.ToString(CultureInfo.InvariantCulture)).Append(':')
                        .Append(key.Binding.ToString(CultureInfo.InvariantCulture)).Append(':')
                        .Append((int)key.Stage).Append('=')
                        .Append(entry.Space.Word()).Append(':')
                        .Append(entry.Index.ToString(CultureInfo.InvariantCulture)).Append(';');
                }
                return text.ToString();
            }
        }

        /// <summary>
        /// THE SHAPE CHECK (2.2b, pin 4). The table is keyed on <c>(set, binding, stage)</c> and the binder
        /// resolves an ENGINE-declared element through it, so the declared layout array a pipeline is created with
        /// has to be the same shape as the reflection the table was built from: the same number of sets, the same
        /// number of elements in each, and the same kind at each position.
        /// <para>
        /// WHY IT IS NOT OPTIONAL. Without it, a pipeline created with a layout array that disagrees with its
        /// shader's reflection silently resolves every element through a key that means something else, which is
        /// the same wrong-pixel-no-error class this whole mechanism exists to close, arriving through the one door
        /// the id join leaves open.
        /// </para>
        /// <para>
        /// CALLED AT PIPELINE CREATION, which is row 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577), because that is the first moment both arrays
        /// exist together. Row 9 writes the check and row 11 is its only caller.
        /// </para>
        /// </summary>
        /// <param name="declared">The layout array the pipeline was created with, in slot order.</param>
        /// <param name="label">A name for the pipeline or program, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The declared array is a different shape.</exception>
        internal void RequireLayoutShape(IReadOnlyList<GpuResourceLayoutDescription> declared, string label)
        {
            ArgumentNullException.ThrowIfNull(declared);

            if (declared.Count != _layouts.Length)
            {
                throw new ShaderValidationException(
                    $"{label}: the pipeline declares {declared.Count.ToString(CultureInfo.InvariantCulture)} "
                    + "resource layouts and its shader set reflected "
                    + $"{_layouts.Length.ToString(CultureInfo.InvariantCulture)}. The Metal binding table is keyed "
                    + "on (set, binding, stage) read out of the shader's own decorations, so a layout array of a "
                    + "different shape resolves every element through a key that means something else.");
            }

            for (int set = 0; set < declared.Count; set++)
            {
                GpuResourceLayoutElement[] mine = _layouts[set].Elements;
                GpuResourceLayoutElement[] theirs = declared[set].Elements;

                if (theirs.Length != mine.Length)
                {
                    throw new ShaderValidationException(
                        $"{label}: resource layout {set.ToString(CultureInfo.InvariantCulture)} declares "
                        + $"{theirs.Length.ToString(CultureInfo.InvariantCulture)} elements and the shader "
                        + $"reflected {mine.Length.ToString(CultureInfo.InvariantCulture)}. Binding M is that "
                        + "layout's element M on this backend, so the two arrays have to agree element for "
                        + "element.");
                }

                for (int i = 0; i < mine.Length; i++)
                {
                    if (theirs[i].Kind == mine[i].Kind) continue;
                    throw new ShaderValidationException(
                        $"{label}: resource layout {set.ToString(CultureInfo.InvariantCulture)} element "
                        + $"{i.ToString(CultureInfo.InvariantCulture)} is declared as {theirs[i].Kind} and the "
                        + $"shader reflected {mine[i].Kind} there. The kind decides which of Metal's three "
                        + "argument tables the element is bound in, so a disagreement binds it in the wrong "
                        + "space.");
                }
            }
        }

        static string Name(MetalShaderStage stage) => stage.ToString().ToLowerInvariant();

        static ShaderValidationException Loud(string where, string why)
            => new($"{where}: {why}");
    }
}
