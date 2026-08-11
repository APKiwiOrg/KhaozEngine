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
    /// WHAT LATER ROWS ADD. Row 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/576) landed the content
    /// deduplication: <see cref="MetalIndexTableCache"/> keys on <see cref="ContentKey"/>, every table handed out
    /// goes through it at shader-set creation, and <see cref="SameIndicesAs"/> is the handle compare that buys.
    /// Row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) binds through <see cref="TryGetIndex"/>, one
    /// array call per (kind, stage), and invalidates a pipeline switch through <see cref="SameIndicesAs"/>.
    /// Neither changes what this reads.
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
        /// REBUILD A TABLE THAT WAS ALREADY BUILT ONCE, from a cache payload rather than from an emission
        /// (<see cref="MetalMslCacheEntry"/>, pin 6 of 2.2b). The join is not re-run, because the emission a hit
        /// skips is the only thing that could answer it. What IS re-run is every check the join's OUTPUT has to
        /// satisfy, because a payload is a file and a file can be wrong in ways an emission cannot.
        /// <para>
        /// THE STRUCTURAL CHECKS ARE THE POINT, and they are pin 1's discipline applied to a second way in.
        /// A cached entry naming a set past the layout array, a binding past that set's elements, an index space
        /// that does not match the element's kind, or two entries on one <c>(set, binding, stage)</c> throws here
        /// exactly as it would have thrown in <see cref="Build"/>. The caller treats that throw as corruption,
        /// which is a miss and a delete rather than a wrong table: a table is the one thing in this backend whose
        /// silent corruption renders wrong pixels with no error anywhere.
        /// </para>
        /// </summary>
        /// <param name="entries">The cached entries, in any order.</param>
        /// <param name="layouts">The cached reflection layouts, in set order.</param>
        /// <param name="label">A name for the program, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The payload's entries and layouts do not agree.</exception>
        internal static MetalShaderIndexTable FromCache(
            IReadOnlyList<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>> entries,
            GpuResourceLayoutDescription[] layouts, string label)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(layouts);

            var rebuilt = new Dictionary<MetalIndexTableKey, MetalIndexTableEntry>(entries.Count);
            foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in entries)
            {
                string where = $"{label} [{Name(key.Stage)}] [[{entry.Space.Word()}"
                    + $"({entry.Index.ToString(CultureInfo.InvariantCulture)})]] "
                    + $"set={key.Set.ToString(CultureInfo.InvariantCulture)} "
                    + $"binding={key.Binding.ToString(CultureInfo.InvariantCulture)}";

                if (key.Set < 0 || key.Set >= layouts.Length)
                {
                    throw Loud(where, "the cached entry names a set outside the cached layout array.");
                }

                GpuResourceLayoutElement[] elements = layouts[key.Set].Elements;
                if (key.Binding < 0 || key.Binding >= elements.Length)
                {
                    throw Loud(where, "the cached entry names a binding outside that set's cached elements.");
                }

                if (!entry.Space.MatchesKind(elements[key.Binding].Kind))
                {
                    throw Loud(where,
                        $"the cached entry resolves to a {elements[key.Binding].Kind} element, which does not "
                        + "belong in that index space.");
                }

                if (entry.Index < 0) throw Loud(where, "the cached entry carries a negative index.");
                if (!rebuilt.TryAdd(key, entry))
                {
                    throw Loud(where, "the payload carries two entries for one (set, binding, stage), so one of "
                        + "them would never be bound.");
                }
            }

            return new MetalShaderIndexTable(rebuilt, layouts);
        }

        /// <summary>
        /// Where <paramref name="stage"/> reads the element declared at <paramref name="set"/> /
        /// <paramref name="binding"/>, or false when that stage does not reference it and therefore must not be
        /// bound for it.
        /// </summary>
        internal bool TryGetIndex(int set, int binding, MetalShaderStage stage, out MetalIndexTableEntry entry)
            => _entries.TryGetValue(new MetalIndexTableKey(set, binding, stage), out entry);

        /// <summary>
        /// M-R9's PIPELINE-SWITCH COMPARISON, AND IT IS REFERENCE IDENTITY ON PURPOSE. Two pipelines whose
        /// programs map every element to the same index invalidate nothing on a switch, and Metal's argument
        /// tables are absolute and per encoder, so the bound resources are still there to keep.
        /// <para>
        /// A STRUCTURAL WALK WOULD BE THE SAME ANSWER AT A PER-SWITCH COST, and a per-switch cost is what this
        /// whole row exists to remove. What makes the cheap form correct is that every table handed out is
        /// canonical: <see cref="MetalIndexTableCache"/> deduplicates on <see cref="ContentKey"/> at shader-set
        /// creation, so equal content IS one instance. A table built outside that cache compares unequal to its
        /// own twin, which is the safe direction (invalidate too much rather than too little) and is still a bug
        /// in whoever built it.
        /// </para>
        /// <para>
        /// ROW 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) IS THE CALLER. Written here so the
        /// comparison's reasoning lives with the object it compares rather than inside a recorder.
        /// </para>
        /// </summary>
        internal bool SameIndicesAs(MetalShaderIndexTable? other) => ReferenceEquals(this, other);

        /// <summary>Every entry, for the index-table test and for the content dedup's equivalence check. Ordered
        /// by key, so two tables with the same content enumerate identically and <see cref="ContentKey"/> is
        /// stable.</summary>
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
        /// A stable rendering of EVERYTHING THIS OBJECT ANSWERS FOR, for row 10's deduplication (M-R9): the
        /// layout shape first, then every entry. Two programs that produce the same string here are
        /// interchangeable through every member of this type, which is what lets row 10 hand both pipelines one
        /// shared instance and make the invalidation comparison a handle compare.
        /// <para>
        /// THE LAYOUT SHAPE IS IN THE KEY BECAUSE <see cref="RequireLayoutShape"/> IS AN AUTHORITY, and the
        /// entries alone are not a complete identity of this object. It compares a pipeline's declared array
        /// against <see cref="Layouts"/>, and two programs can agree on every entry while disagreeing in
        /// <see cref="Layouts"/>: an element no stage references contributes no entry at all, and that is the
        /// common case rather than the corner (95 of 254 stage/element slots over the shipped set). Dedup on the
        /// entries alone would hand pipeline B a table carrying program A's layouts, and pin 4 would then refuse
        /// B's own perfectly correct declared array. Loud and wrong, which is a better failure than the silent
        /// one and still not one row 10 should be able to reach. So set count, each set's element count and each
        /// element's kind are rendered here, which is exactly the triple that check compares.
        /// </para>
        /// <para>
        /// AN ELEMENT'S NAME AND STAGE VISIBILITY ARE DELIBERATELY NOT RENDERED. Nothing on this type reads
        /// either: the shape check compares kinds, and the join reaches an element only for its kind. If a later
        /// row starts reading a layout element's name or its visibility off <see cref="Layouts"/>, that becomes
        /// observable and belongs in here too.
        /// <para>
        /// AND ROW 10 MEASURED WHAT THAT WOULD COST, so this is a constraint with a size rather than a caution.
        /// Measured over the shipped catalog at row 10, on 2026-08-10, 25 of 42 programs merged onto an earlier
        /// program's table and 16 of those 25 disagreed on at least one element NAME, so a member reading one off
        /// <see cref="Layouts"/> would be reading another program's name in the majority of merges. The catalog is
        /// a property of the shipped renderers rather than of this type, so read those numbers as the measurement
        /// they were and take them again before quoting them.
        /// <c>MetalIndexTableDedupTests.TwoTablesDifferingOnlyInElementNames_AreInterchangeable</c> pins the
        /// invariant behaviourally, which catches a change to this key or to
        /// <see cref="RequireLayoutShape"/> and NOT a new member that reads a name. Making that mechanical is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/594.
        /// </para>
        /// </para>
        /// <para>
        /// CONSUMED BY <see cref="MetalIndexTableCache"/> AND BY NOTHING ELSE, which is what keeps it the one
        /// notion of table identity this backend has. It is read once per shader-set creation and never on a
        /// bind path.
        /// </para>
        /// </summary>
        internal string ContentKey
        {
            get
            {
                var text = new StringBuilder(_entries.Count * 16 + _layouts.Length * 16);
                text.Append(_layouts.Length.ToString(CultureInfo.InvariantCulture)).Append('|');
                foreach (GpuResourceLayoutDescription layout in _layouts)
                {
                    text.Append(layout.Elements.Length.ToString(CultureInfo.InvariantCulture)).Append(':');
                    foreach (GpuResourceLayoutElement element in layout.Elements)
                        text.Append(((int)element.Kind).ToString(CultureInfo.InvariantCulture)).Append(',');
                    text.Append('|');
                }

                foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in Entries())
                {
                    text.Append(key.Set.ToString(CultureInfo.InvariantCulture)).Append(':')
                        .Append(key.Binding.ToString(CultureInfo.InvariantCulture)).Append(':')
                        .Append(((int)key.Stage).ToString(CultureInfo.InvariantCulture)).Append('=')
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
