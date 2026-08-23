using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// <param name="Stage">Which stage's argument table the index is in. An element the emission gave that stage
    /// no argument for has NO entry, which is what the stage half of this key decides. Since 18.0.0 it does not
    /// decide the INDEX: an element's index is authored once for the whole program.</param>
    internal readonly record struct MetalIndexTableKey(int Set, int Binding, MetalShaderStage Stage);

    /// <summary>Where the engine told the emission to put one element.</summary>
    /// <param name="Space">Which of the three argument tables.</param>
    /// <param name="Index">The index within that table.</param>
    internal readonly record struct MetalIndexTableEntry(MetalIndexSpace Space, int Index);

    /// <summary>Which declared resources one emitted stage carries an argument for, as
    /// <c>SpirvCrossCompile</c> asked SPIRV-Cross after the emission.</summary>
    /// <param name="Stage">Which stage these belong to.</param>
    /// <param name="Used">The <c>(set, binding)</c> pairs that stage emitted an argument for.</param>
    internal readonly record struct MetalStageResourceUse(
        MetalShaderStage Stage, IReadOnlyList<MslResourceRef> Used);

    /// <summary>
    /// THE PER-PROGRAM BINDING TABLE, KEYED ON <c>(set, binding, stage)</c>, AND THE WHOLE OF WHAT M-B1 MEANS
    /// (decision M-B1, re-adjudicated in section 2.2b of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> and closed by row 10 of
    /// <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c>). Metal has no binding decorations, so the index a
    /// resource lands at is a fact about the EMISSION and nothing else.
    ///
    /// <para>
    /// 18.0.0 STATES THAT FACT INSTEAD OF DISCOVERING IT (#693). <see cref="MslIndexRemap"/> assigns every
    /// resource the program declares an index before the emission and installs it through
    /// <c>spvc_compiler_msl_add_resource_binding</c>, so this table is BUILT FROM THE ASSIGNMENT rather than read
    /// back out of the text. What that deleted, in one move: the parse of each entry point's argument list, the
    /// SPIR-V decoration walk that resolved each <c>_&lt;id&gt;</c> argument name to a declared element, and the
    /// whole question of whether the two agree. There is nothing left to join.
    /// </para>
    /// <para>
    /// THE FIVE REFUSALS THE JOIN OWNED COLLAPSE TO THREE, and the two that went are the two that can no longer
    /// happen. An argument name that is not <c>_&lt;id&gt;</c> and an id carrying no decorations were both
    /// failures to RESOLVE an argument, and nothing resolves an argument any more. What is left is structural and
    /// still throws, naming the program and the stage: a <c>(set, binding)</c> outside the declared layout array,
    /// a binding outside that set's elements, and two entries on one <c>(set, binding, stage)</c>. The kind check
    /// went with them for a better reason than disuse: the space an element binds in is now DERIVED from its
    /// kind, so an element in the wrong space is unconstructible rather than merely rejected.
    /// </para>
    /// <para>
    /// AN ELEMENT WITH NO ENTRY FOR A STAGE IS NOT BOUND FOR THAT STAGE, unchanged, and still correct by
    /// construction rather than a gap. SPIRV-Cross omits an argument a stage does not reference, and the
    /// engine asks it which ones those were (<see cref="MslIndexRemap.UsedBy"/>) rather than inferring it from a
    /// text it no longer reads. Over the shipped set 95 of 254 stage/element slots are unreferenced, so this is
    /// the common case rather than the corner.
    /// </para>
    /// <para>
    /// AND AN ELEMENT'S INDEX NO LONGER DEPENDS ON THE STAGE. The authored scheme numbers across the union of
    /// both stages, so the same element is at the same index in the vertex and the fragment argument table. The
    /// stage stays in the key because the binder resolves through it and because PRESENCE is still per stage,
    /// but a whole class of "right index, wrong stage" defect is gone by construction.
    /// </para>
    /// <para>
    /// WHAT LATER ROWS ADD. Row 10 of the Metal program (https://github.com/APKiwiOrg/KhaozEngine/issues/576)
    /// landed the content deduplication: <see cref="MetalIndexTableCache"/> keys on <see cref="ContentKey"/>,
    /// every table handed out goes through it at shader-set creation, and <see cref="SameIndicesAs"/> is the
    /// handle compare that buys. Row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) binds through
    /// <see cref="TryGetIndex"/>, one array call per (kind, stage), and invalidates a pipeline switch through
    /// <see cref="SameIndicesAs"/>. Neither changes what this holds.
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
        /// Build the table for one program from the AUTHORED assignment plus each stage's use list.
        /// <para>
        /// THE INDEX COMES FROM THE LAYOUTS ALONE, which is what makes this reproducible without the emission.
        /// <see cref="MslIndexRemap.Assign"/> is a pure function of the reflected resources, so the same layouts
        /// give the same indices on every call, in every process, forever. The use lists carry the only thing
        /// the layouts cannot answer: which stage emitted an argument for what.
        /// </para>
        /// </summary>
        /// <param name="layouts">The reflection's resource layouts, in set order.</param>
        /// <param name="stages">Each stage, with the resources its emission carries an argument for.</param>
        /// <param name="label">A name for the program, included in every error message.</param>
        /// <exception cref="ShaderValidationException">A use list names a set or a binding the layout array does
        /// not declare, or two entries land on one <c>(set, binding, stage)</c>.</exception>
        internal static MetalShaderIndexTable Build(GpuResourceLayoutDescription[] layouts,
            IReadOnlyList<MetalStageResourceUse> stages, string label)
        {
            ArgumentNullException.ThrowIfNull(layouts);
            ArgumentNullException.ThrowIfNull(stages);

            Dictionary<(uint Set, uint Binding), MetalIndexTableEntry> authored = Authored(layouts);
            var entries = new Dictionary<MetalIndexTableKey, MetalIndexTableEntry>();

            foreach (MetalStageResourceUse stage in stages)
            {
                foreach (MslResourceRef used in stage.Used)
                {
                    string where = $"{label} [{Name(stage.Stage)}] set={Text(used.Set)} binding={Text(used.Binding)}";

                    if (used.Set < 0 || used.Set >= layouts.Length)
                    {
                        throw Loud(where,
                            $"it names a set past the {Text(layouts.Length)} layouts the reflection declares. Set "
                            + "N is the layout at slot N, and that positional assumption is checked here rather "
                            + "than assumed.");
                    }

                    GpuResourceLayoutElement[] elements = layouts[used.Set].Elements;
                    if (used.Binding < 0 || used.Binding >= elements.Length)
                    {
                        throw Loud(where,
                            $"it names a binding in a set that declares {Text(elements.Length)} elements. Binding "
                            + "M is that layout's element M, and that positional assumption is checked here too.");
                    }

                    var key = new MetalIndexTableKey(used.Set, used.Binding, stage.Stage);
                    MetalIndexTableEntry entry = authored[((uint)used.Set, (uint)used.Binding)];
                    if (!entries.TryAdd(key, entry))
                    {
                        throw Loud(where,
                            "this stage's use list names it twice. One element is one argument in one stage, so a "
                            + "repeat means the list did not come from an emission.");
                    }
                }
            }

            return new MetalShaderIndexTable(entries, layouts);
        }

        /// <summary>
        /// REBUILD A TABLE THAT WAS ALREADY BUILT ONCE, from a cache payload rather than from an emission
        /// (<see cref="MetalMslCacheEntry"/>, pin 6 of 2.2b).
        /// <para>
        /// THE PAYLOAD NO LONGER CARRIES AN INDEX, and that is 18.0.0's change to this member. An index is a pure
        /// function of the layouts, which the payload does carry, so storing one would only create a second
        /// authority able to disagree with the scheme: a file written under one numbering and served under
        /// another binds every resource one slot out, silently. What the payload carries is the
        /// <c>(set, binding, stage)</c> triples, which is exactly the part no scheme can derive, and the indices
        /// are recomputed here through the same <see cref="Build"/> the emission path uses.
        /// </para>
        /// <para>
        /// THE STRUCTURAL CHECKS ARE STILL THE POINT, because a payload is a file and a file can be wrong in ways
        /// an emission cannot. A triple naming a set past the layout array, a binding past that set's elements,
        /// or two triples on one key throws exactly as it would have thrown in <see cref="Build"/>. The caller
        /// treats that throw as corruption, which is a miss and a delete rather than a wrong table.
        /// </para>
        /// <para>
        /// THE STAGE SET IS CHECKED TOO, AND CROSS-CHECKED AGAINST THE TRIPLES, which the layouts cannot do for
        /// it. The SOURCES a payload carries are what decide which stages exist, so the two halves of one payload
        /// have to agree: a compute payload carrying a fragment entry, or a graphics payload with one stage where
        /// the emission always produces a pair, is a file nothing this engine wrote could have produced.
        /// </para>
        /// </summary>
        /// <param name="used">The cached <c>(set, binding, stage)</c> triples, in any order.</param>
        /// <param name="layouts">The cached reflection layouts, in set order.</param>
        /// <param name="stages">The stages the payload carries a source for. A compute program is exactly one
        /// <see cref="MetalShaderStage.Compute"/>, a graphics program exactly
        /// <see cref="MetalShaderStage.Vertex"/> and <see cref="MetalShaderStage.Fragment"/>.</param>
        /// <param name="label">A name for the program, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The payload's triples, stages and layouts do not
        /// agree.</exception>
        internal static MetalShaderIndexTable FromCache(IReadOnlyList<MetalIndexTableKey> used,
            GpuResourceLayoutDescription[] layouts, IReadOnlySet<MetalShaderStage> stages, string label)
        {
            ArgumentNullException.ThrowIfNull(used);
            ArgumentNullException.ThrowIfNull(layouts);
            ArgumentNullException.ThrowIfNull(stages);

            RequireProgramShape(stages, label);

            var byStage = new Dictionary<MetalShaderStage, List<MslResourceRef>>();
            foreach (MetalIndexTableKey key in used)
            {
                if (!stages.Contains(key.Stage))
                {
                    throw Loud($"{label} [{Name(key.Stage)}] set={Text(key.Set)} binding={Text(key.Binding)}",
                        "the cached triple names a stage the payload carries no source for. The element it names "
                        + "was read out of an emission this payload does not contain.");
                }

                if (!byStage.TryGetValue(key.Stage, out List<MslResourceRef>? list))
                    byStage[key.Stage] = list = [];
                list.Add(new MslResourceRef(key.Set, key.Binding));
            }

            var rebuilt = new List<MetalStageResourceUse>(byStage.Count);
            foreach ((MetalShaderStage stage, List<MslResourceRef> list) in byStage)
                rebuilt.Add(new MetalStageResourceUse(stage, list));

            return Build(layouts, rebuilt, label);
        }

        /// <summary>
        /// THE AUTHORED ASSIGNMENT, DERIVED FROM THE LAYOUTS, which is the whole of what row 10 replaced a parse
        /// with. <see cref="MslIndexRemap"/> holds the rule and <c>SpirvCrossCompile</c> installs it on the
        /// compiler before emitting, so this call reproduces the numbering the emitted MSL actually carries.
        /// Two derivations of one scheme would be a thing to keep in step: there is one, called from both sides.
        /// </summary>
        static Dictionary<(uint Set, uint Binding), MetalIndexTableEntry> Authored(
            GpuResourceLayoutDescription[] layouts)
        {
            var kinds = new Dictionary<(uint Set, uint Binding), GpuResourceKind>();
            for (int set = 0; set < layouts.Length; set++)
            {
                GpuResourceLayoutElement[] elements = layouts[set].Elements ?? [];
                for (int binding = 0; binding < elements.Length; binding++)
                    kinds[((uint)set, (uint)binding)] = elements[binding].Kind;
            }

            var authored = new Dictionary<(uint Set, uint Binding), MetalIndexTableEntry>(kinds.Count);
            foreach (MslIndexAssignment assignment in MslIndexRemap.Assign(kinds))
            {
                authored[(assignment.Set, assignment.Binding)] =
                    new MetalIndexTableEntry(SpaceOf(assignment.Space), (int)assignment.Index);
            }

            return authored;
        }

        // The one place the engine's two spellings of Metal's three argument tables meet. MslIndexRemap's lives
        // in KhaozEngine.Gpu, beside the emitter that installs the indices, and MetalIndexSpace lives here,
        // beside the binder that reads them. MetalIndexSpaceAgreementTests pins the kind-to-space rule they each
        // state, so this mapping stays a rename rather than a second opinion.
        static MetalIndexSpace SpaceOf(MslIndexSpace space) => space switch
        {
            MslIndexSpace.Buffer => MetalIndexSpace.Buffer,
            MslIndexSpace.Texture => MetalIndexSpace.Texture,
            MslIndexSpace.Sampler => MetalIndexSpace.Sampler,
            _ => throw new ArgumentOutOfRangeException(nameof(space), space,
                "this MslIndexSpace has no Metal argument table. The two enums are the same three spaces, so a "
                + "new member on one is an engine change that has to visit the other."),
        };

        // THE TWO SHAPES A METAL PROGRAM CAN HAVE, and there is no third. A compute kernel is one compute stage,
        // dispatched on its own encoder. A graphics program is a vertex and fragment PAIR, because the authored
        // indices are assigned across the pair at once and neither half's emission exists without the other. A
        // payload of any other shape did not come from MetalShaderBuild, so refusing it here is refusing a file
        // rather than refusing a program.
        static void RequireProgramShape(IReadOnlySet<MetalShaderStage> stages, string label)
        {
            bool compute = stages.Contains(MetalShaderStage.Compute);
            bool pair = stages.Contains(MetalShaderStage.Vertex) && stages.Contains(MetalShaderStage.Fragment);
            if (compute ? stages.Count == 1 : pair && stages.Count == 2) return;

            throw new ShaderValidationException(
                $"{label}: the cached payload carries "
                + $"{stages.Count.ToString(CultureInfo.InvariantCulture)} stage(s) ["
                + string.Join(", ", stages.Select(Name))
                + "], which is neither of the two shapes a Metal program has. A compute program is exactly one "
                + "compute stage and a graphics program is exactly a vertex and fragment pair, because the pair "
                + "is cross-compiled together and its indices are assigned across both at once.");
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
        /// AN ELEMENT'S NAME AND STAGE VISIBILITY ARE DELIBERATELY NOT RENDERED, AND THAT IS ENFORCED RATHER
        /// THAN ASKED FOR. Nothing on this type reads either: the shape check compares kinds, and the join
        /// reaches an element only for its kind. <c>MetalIndexTableNameBlindnessTests</c> walks the IL of every
        /// member this type declares, its iterator's state machine included, and follows the calls those bodies
        /// make THROUGH this package, so a member added later is a red test whether it reads the name itself or
        /// reads it in a helper in another type here. What the walk does not follow is a call into another
        /// assembly, which is the scope that test states for itself: this rule is about what THIS package does.
        /// If
        /// a later row NEEDS to read a name off <see cref="Layouts"/>, the name becomes observable and belongs
        /// in here too, which is what makes that guard's failure the right place to have the conversation.
        /// <para>
        /// AND ROW 10 MEASURED WHAT THAT WOULD COST, so this is a constraint with a size rather than a caution.
        /// Measured over the shipped catalog at row 10, on 2026-08-10, 25 of 42 programs merged onto an earlier
        /// program's table and 16 of those 25 disagreed on at least one element NAME, so a member reading one off
        /// <see cref="Layouts"/> would be reading another program's name in the majority of merges. The catalog is
        /// a property of the shipped renderers rather than of this type, so read those numbers as the measurement
        /// they were and take them again before quoting them.
        /// <c>MetalIndexTableDedupTests.TwoTablesDifferingOnlyInElementNames_AreInterchangeable</c> pins the
        /// invariant behaviourally, which catches a change to this key or to
        /// <see cref="RequireLayoutShape"/> and NOT a new member that reads a name. The IL walk in
        /// <c>MetalIndexTableNameBlindnessTests</c> is the half that catches the new member
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/594).
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

            // A DECLARED LAYOUT THAT BINDS NOTHING IS THE SAME SHAPE AS NO LAYOUT AT ALL (#599). The reflection
            // stops at the highest set a resource declares, so a resource-free shader reflects zero sets, while a
            // pipeline may still be created with one empty layout, which is what the incumbent accepted and what
            // the engine's own tests do. Trailing empty declared layouts are trimmed before the count is compared,
            // so both ResourceLayouts = [] and [empty] are legal against zero reflected sets. An empty declared
            // layout in the MIDDLE is not trimmed: it has to match a reflected gap set positionally.
            int declaredCount = declared.Count;
            while (declaredCount > _layouts.Length && (declared[declaredCount - 1].Elements?.Length ?? 0) == 0)
                declaredCount--;

            if (declaredCount != _layouts.Length)
            {
                throw new ShaderValidationException(
                    $"{label}: the pipeline declares {declared.Count.ToString(CultureInfo.InvariantCulture)} "
                    + "resource layouts and its shader set reflected "
                    + $"{_layouts.Length.ToString(CultureInfo.InvariantCulture)}. The Metal binding table is keyed "
                    + "on (set, binding, stage) read out of the shader's own decorations, so a layout array of a "
                    + "different shape resolves every element through a key that means something else.");
            }

            for (int set = 0; set < declaredCount; set++)
            {
                GpuResourceLayoutElement[] mine = _layouts[set].Elements;
                GpuResourceLayoutElement[] theirs = declared[set].Elements ?? [];

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

        static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

        static ShaderValidationException Loud(string where, string why)
            => new($"{where}: {why}");
    }
}
