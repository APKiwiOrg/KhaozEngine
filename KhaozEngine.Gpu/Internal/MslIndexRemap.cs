using System;
using System.Collections.Generic;
using Silk.NET.SPIRV.Cross;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Which of Metal's three argument tables one resource lands in. The three are INDEPENDENT address
    /// spaces per stage, so a <c>buffer(0)</c> and a <c>texture(0)</c> are different bindings and never
    /// collide.</summary>
    internal enum MslIndexSpace
    {
        /// <summary><c>[[buffer(n)]]</c>: uniform buffers and both structured kinds, which SHARE this space.
        /// Vertex streams live here too, pinned at the TOP of the table by decision M-B2 so they cannot collide
        /// with a resource index growing from 0.</summary>
        Buffer,

        /// <summary><c>[[texture(n)]]</c>: both texture kinds.</summary>
        Texture,

        /// <summary><c>[[sampler(n)]]</c>: samplers, which have a space to themselves.</summary>
        Sampler,
    }

    /// <summary>One resource's authored index: the module's own <c>(set, binding)</c> key, its POSITION in the
    /// reflected layout for that set, and the argument table plus index the emitted MSL is TOLD to name it
    /// at.</summary>
    /// <param name="Set">The <c>DescriptorSet</c> decoration.</param>
    /// <param name="Binding">The RAW <c>Binding</c> decoration, which is what SPIRV-Cross is keyed on.</param>
    /// <param name="Position">Where that resource sits in <c>SpirvCrossReflect</c>'s layout for its set, which
    /// is what every BACKEND is keyed on. The two differ whenever the GLSL leaves a gap in its binding numbers:
    /// the layout folds a set's bindings into dense positions, so <c>binding = 6</c> in a set of three resources
    /// is element 2. Carrying both is what lets one walk answer the emitter and the binder at once.</param>
    /// <param name="Space">Which argument table.</param>
    /// <param name="Index">The index within that table.</param>
    internal readonly record struct MslIndexAssignment(
        uint Set, uint Binding, uint Position, MslIndexSpace Space, uint Index);

    /// <summary>
    /// THE MSL ARGUMENT INDICES THE ENGINE AUTHORS, and the deletion that pays for this whole row (#693, M-B1,
    /// section 2.3 result 1 of <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c>). Metal carries no binding
    /// decorations, so the index a resource lands at is a fact about the EMISSION and nothing else. Until 18.0.0
    /// the engine DISCOVERED that fact: it parsed each emitted entry point's argument list for its
    /// <c>[[buffer(n)]]</c> attributes and joined each argument back to a declared element through that stage's
    /// own SPIR-V <c>DescriptorSet</c> and <c>Binding</c> decorations. It now STATES the fact instead, through
    /// <c>spvc_compiler_msl_add_resource_binding</c>, which the outgoing <c>libveldrid-spirv</c> exported no
    /// entry point for and which arrived with row 8's toolchain swap.
    /// <para>
    /// THE RULE IS <see cref="HlslRegisterRemap"/>'s, DELIBERATELY WORD FOR WORD. Walk every resource the program
    /// declares in ascending <c>(set, binding)</c> order. Each one takes the next index from the counter its
    /// argument table chooses, and the three counters run across the WHOLE program rather than restarting per set
    /// or per stage. One element therefore has ONE index, the same in both stages of a pair, which is what makes
    /// the table's <c>(set, binding, stage)</c> key a question about PRESENCE alone: the stage decides whether an
    /// element is bound, never where.
    /// </para>
    /// <para>
    /// ACROSS BOTH STAGES OF A PAIR, ALWAYS, for the same reason the HLSL half gives. The counters advance over
    /// the UNION of what the two stages declare, so a fragment-only texture still consumes the index the vertex
    /// stage skips past. That leaves a gap in the vertex stage's argument table, which costs nothing: Metal
    /// numbers each stage's table to 31 buffers and binds only what the table names.
    /// </para>
    /// <para>
    /// AND IT CANNOT COLLIDE WITH A VERTEX STREAM. Resource indices grow from 0 upward and
    /// <c>MetalVertexStreamIndex</c> pins streams at the top of the buffer table counting downward (M-B2), so the
    /// two numberings meet only on a pipeline declaring more than 31 combined buffer bindings on one stage. That
    /// remains a pipeline-creation assertion rather than an assumption, and it is checkable in exactly the form
    /// it was before, because the indices it reads are these.
    /// </para>
    /// <para>
    /// IT IS INSTALLED RATHER THAN PATCHED IN, again as the HLSL half is. Rewriting the <c>Binding</c> decoration
    /// on the module would reach <see cref="SpirvCrossReflect"/>, which reads those same decorations to build the
    /// layouts the backends index positionally, and the two would then disagree about what the module declared.
    /// </para>
    /// </summary>
    internal static class MslIndexRemap
    {
        /// <summary>
        /// The argument table a reflected kind binds into. Both structured kinds share the buffer table with
        /// uniform buffers, because SPIRV-Cross emits a GLSL storage block as a <c>device T&amp;</c> argument,
        /// which is a buffer argument like any other.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A kind with no argument table. Every declared kind is
        /// listed, so this is a new <see cref="GpuResourceKind"/> member, and defaulting one into the sampler
        /// table would bind it where another resource was expected.</exception>
        internal static MslIndexSpace SpaceFor(GpuResourceKind kind) => kind switch
        {
            GpuResourceKind.UniformBuffer => MslIndexSpace.Buffer,
            GpuResourceKind.StructuredBufferReadOnly => MslIndexSpace.Buffer,
            GpuResourceKind.StructuredBufferReadWrite => MslIndexSpace.Buffer,
            GpuResourceKind.TextureReadOnly => MslIndexSpace.Texture,
            GpuResourceKind.TextureReadWrite => MslIndexSpace.Texture,
            GpuResourceKind.Sampler => MslIndexSpace.Sampler,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unmapped GpuResourceKind. Every kind has to name a Metal argument table, because a resource "
                + "with no authored index is emitted at whatever index SPIRV-Cross counts to and is then bound "
                + "somewhere else."),
        };

        /// <summary>
        /// Assign every resource in <paramref name="resources"/> its index, by the rule in the type header. The
        /// input is the program's MERGED resource table, so a resource both stages declare appears once.
        /// </summary>
        internal static MslIndexAssignment[] Assign(
            IReadOnlyDictionary<(uint Set, uint Binding), GpuResourceKind> resources)
        {
            if (resources is null) throw new ArgumentNullException(nameof(resources));

            var keys = new List<(uint Set, uint Binding)>(resources.Count);
            foreach (KeyValuePair<(uint Set, uint Binding), GpuResourceKind> entry in resources) keys.Add(entry.Key);

            // ASCENDING (set, binding), never the order the reflection listed them in. SPIRV-Cross groups its
            // resource lists by TYPE, so the arrival order interleaves the tables and would number them by
            // accident of which list came back first.
            keys.Sort((a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));

            uint buffer = 0, texture = 0, sampler = 0, position = 0, set = keys.Count > 0 ? keys[0].Set : 0;
            var assigned = new MslIndexAssignment[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                // The position restarts at every set boundary, because a layout is per set and its elements are
                // that set's bindings in ascending order. The keys are already in that order.
                if (keys[i].Set != set) { set = keys[i].Set; position = 0; }

                MslIndexSpace space = SpaceFor(resources[keys[i]]);
                uint index = space switch
                {
                    MslIndexSpace.Buffer => buffer++,
                    MslIndexSpace.Texture => texture++,
                    _ => sampler++,
                };
                assigned[i] = new MslIndexAssignment(keys[i].Set, keys[i].Binding, position++, space, index);
            }

            return assigned;
        }

        /// <summary>
        /// Install <paramref name="assignments"/> on one stage's compiler, so its emission names those indices.
        /// Every assignment is installed on every stage, including the ones that stage does not declare: the
        /// lookup is keyed on <c>(stage, set, binding)</c> and an entry no resource matches is simply never read.
        /// </summary>
        internal static unsafe void Install(Cross cross, Context* context, Compiler* compiler,
            ReadOnlySpan<MslIndexAssignment> assignments, string tag)
        {
            var stage = cross.CompilerGetExecutionModel(compiler);
            foreach (MslIndexAssignment assignment in assignments)
            {
                MslResourceBinding binding;
                cross.MslResourceBindingInit(&binding);
                binding.Stage = stage;
                binding.DescSet = assignment.Set;
                binding.Binding = assignment.Binding;

                // ALL THREE FIELDS ARE WRITTEN, not only the one this resource's table needs. SPIRV-Cross reads
                // whichever of the three the resource's own type asks for, so the one that answers is chosen by
                // the module rather than by this switch. Writing one field and leaving the other two to the
                // initialiser's zero would be a numbering that is correct only while the kind mapping is, which
                // is the class of defect this row exists to close.
                binding.MslBuffer = assignment.Space == MslIndexSpace.Buffer ? assignment.Index : 0;
                binding.MslTexture = assignment.Space == MslIndexSpace.Texture ? assignment.Index : 0;
                binding.MslSampler = assignment.Space == MslIndexSpace.Sampler ? assignment.Index : 0;

                SpirvCrossCompile.Check(context, cross.CompilerMslAddResourceBinding(compiler, &binding), tag,
                    $"install the MSL index for (set {assignment.Set}, binding {assignment.Binding})");
            }
        }

        /// <summary>
        /// WHICH OF <paramref name="assignments"/> THIS STAGE ACTUALLY EMITTED AN ARGUMENT FOR, asked of
        /// SPIRV-Cross AFTER the emission rather than read back out of the text.
        /// <para>
        /// AN ELEMENT WITH NO ENTRY FOR A STAGE IS NOT BOUND FOR THAT STAGE, and this is where that fact now
        /// comes from. SPIRV-Cross omits an argument a stage does not reference, and binding one anyway is what
        /// an index-counting backend does that produces the off-by-one. Over the shipped set most stage/element
        /// slots are unreferenced, so this is the common case rather than the corner.
        /// </para>
        /// <para>
        /// IT MUST BE CALLED AFTER <c>spvc_compiler_compile</c>. The used flag on a resource binding is set as
        /// the emitter consumes it, so asking before the emission answers false for everything.
        /// </para>
        /// </summary>
        internal static unsafe MslResourceRef[] UsedBy(Cross cross, Compiler* compiler,
            ReadOnlySpan<MslIndexAssignment> assignments)
        {
            var stage = cross.CompilerGetExecutionModel(compiler);
            var used = new List<MslResourceRef>(assignments.Length);
            foreach (MslIndexAssignment assignment in assignments)
            {
                if (cross.CompilerMslIsResourceUsed(compiler, stage, assignment.Set, assignment.Binding) != 0)
                    used.Add(new MslResourceRef((int)assignment.Set, (int)assignment.Position));
            }

            return used.ToArray();
        }
    }
}
