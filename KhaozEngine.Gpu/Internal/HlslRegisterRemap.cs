using System;
using System.Collections.Generic;
using Silk.NET.SPIRV.Cross;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Which Direct3D 11 register file one resource lands in, as SPIRV-Cross spells the four spaces.
    /// The letters are the whole content: <c>b</c>, <c>t</c>, <c>s</c> and <c>u</c> are four independent
    /// address spaces, so a <c>b0</c> and a <c>t0</c> are different bindings and never collide.</summary>
    internal enum HlslRegisterSpace
    {
        /// <summary>Constant buffers, <c>bN</c>.</summary>
        ConstantBuffer,

        /// <summary>Shader resources, <c>tN</c>. Sampled textures AND read-only storage buffers share it.</summary>
        ShaderResource,

        /// <summary>Samplers, <c>sN</c>.</summary>
        Sampler,

        /// <summary>Unordered access, <c>uN</c>. Read-write storage buffers AND storage images share it.</summary>
        UnorderedAccess,
    }

    /// <summary>One resource's assigned register: the module's own <c>(set, binding)</c> key, and the register
    /// file plus index the emitted HLSL must name it at.</summary>
    internal readonly record struct HlslRegisterAssignment(
        uint Set, uint Binding, HlslRegisterSpace Space, uint Index);

    /// <summary>
    /// THE REGISTER NUMBERING THE EMITTED HLSL HAS TO CARRY, and the reason row 8's toolchain swap turned the
    /// Windows leg black. SPIRV-Cross names a resource's register with the module's RAW <c>Binding</c>
    /// decoration: a GLSL block declared <c>binding = 6</c> emits as <c>register(b6)</c>, a sampler at
    /// <c>binding = 4</c> as <c>register(s4)</c>. The outgoing toolchain did not. <c>Veldrid.SPIRV</c> re-numbered
    /// every resource into a PER-FILE counter before emitting, so the same two came out as <c>b0</c> and
    /// <c>s0</c>, and <see cref="T:KhaozEngine.Gpu.D3D11.Internal.D3D11RegisterScheme"/> was built to agree with
    /// that: it walks a layout in declaration order and takes the next index from the counter the element's KIND
    /// chooses. Emitting raw bindings leaves the CPU side binding a texture at <c>t0</c> that the shader reads at
    /// <c>t1</c>, which compiles, draws, and renders black.
    /// <para>
    /// THE RULE, restated so it can be checked against its counterpart. Walk every resource the program declares
    /// in ascending <c>(set, binding)</c> order. Each one takes the next index from the counter its register file
    /// chooses, and the four counters run across the WHOLE program rather than restarting per set. That is
    /// exactly what the register scheme computes from the reflected layouts, because those layouts are indexed by
    /// set and sorted by binding within a set, and its per-set base is the sum of every earlier set's count for
    /// the file. Two derivations of one numbering, and
    /// <c>D3D11RegisterSchemeAgreesWithHlslEmissionTests</c> asserts they agree over every shipped program.
    /// </para>
    /// <para>
    /// ACROSS BOTH STAGES OF A PAIR, ALWAYS. The counters are advanced over the UNION of what the two stages
    /// declare, not over one stage's own list, because the CPU side binds one register number per layout element
    /// for both stages. A fragment-only texture still consumes the index the vertex stage skips past, which is
    /// why <c>Water</c>'s vertex stage names <c>t0</c>, <c>s0</c>, <c>t1</c>, <c>s1</c>, <c>b0</c> while
    /// declaring bindings 0, 1, 2, 3 and 6: bindings 4 and 5 belong to the fragment stage and are counted here
    /// anyway.
    /// </para>
    /// <para>
    /// IT IS INSTALLED RATHER THAN PATCHED IN. SPIRV-Cross takes an explicit
    /// <c>spvc_hlsl_resource_binding</c> per <c>(stage, set, binding)</c>, which the emitter consults in place of
    /// the decoration. Rewriting the <c>Binding</c> decoration on the module instead would reach
    /// <see cref="SpirvCrossReflect"/>, which reads the same decorations to build the layouts the backends index
    /// positionally, and the two would then disagree about what the module declared.
    /// </para>
    /// </summary>
    internal static class HlslRegisterRemap
    {
        /// <summary>
        /// The register file a reflected kind binds into. The two SHARING pairs are the whole content, and they
        /// are the reason a kind cannot be mapped to a file by ordinal: SPIRV-Cross emits a GLSL storage block as
        /// a <c>ByteAddressBuffer</c> or an <c>RWByteAddressBuffer</c>, which occupy <c>t</c> and <c>u</c>
        /// alongside textures rather than a space of their own.
        /// </summary>
        internal static HlslRegisterSpace SpaceFor(GpuResourceKind kind) => kind switch
        {
            GpuResourceKind.UniformBuffer => HlslRegisterSpace.ConstantBuffer,
            GpuResourceKind.Sampler => HlslRegisterSpace.Sampler,
            GpuResourceKind.TextureReadOnly => HlslRegisterSpace.ShaderResource,
            GpuResourceKind.StructuredBufferReadOnly => HlslRegisterSpace.ShaderResource,
            GpuResourceKind.TextureReadWrite => HlslRegisterSpace.UnorderedAccess,
            GpuResourceKind.StructuredBufferReadWrite => HlslRegisterSpace.UnorderedAccess,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unmapped GpuResourceKind. Every kind has to name a register file, because an unnumbered "
                + "resource emits at its raw binding and renders wrongly rather than failing."),
        };

        /// <summary>
        /// Assign every resource in <paramref name="resources"/> its register, by the rule in the type header.
        /// The input is the program's merged resource table, so a resource both stages declare appears once.
        /// </summary>
        internal static HlslRegisterAssignment[] Assign(
            IReadOnlyDictionary<(uint Set, uint Binding), GpuResourceKind> resources)
        {
            if (resources is null) throw new ArgumentNullException(nameof(resources));

            var keys = new List<(uint Set, uint Binding)>(resources.Count);
            foreach (KeyValuePair<(uint Set, uint Binding), GpuResourceKind> entry in resources) keys.Add(entry.Key);

            // ASCENDING (set, binding), never the order the reflection listed them in. SPIRV-Cross groups its
            // resource lists by TYPE, so the arrival order interleaves the files and would number them by
            // accident of which list came back first.
            keys.Sort((a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));

            uint b = 0, t = 0, s = 0, u = 0;
            var assigned = new HlslRegisterAssignment[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                HlslRegisterSpace space = SpaceFor(resources[keys[i]]);
                uint index = space switch
                {
                    HlslRegisterSpace.ConstantBuffer => b++,
                    HlslRegisterSpace.ShaderResource => t++,
                    HlslRegisterSpace.Sampler => s++,
                    _ => u++,
                };
                assigned[i] = new HlslRegisterAssignment(keys[i].Set, keys[i].Binding, space, index);
            }

            return assigned;
        }

        /// <summary>
        /// Install <paramref name="assignments"/> on one stage's compiler, so its emission names those registers.
        /// Every assignment is installed on every stage, including the ones that stage does not declare: the
        /// lookup is keyed on <c>(stage, set, binding)</c> and an entry no resource matches is simply never read.
        /// </summary>
        internal static unsafe void Install(Cross cross, Context* context, Compiler* compiler,
            ReadOnlySpan<HlslRegisterAssignment> assignments, string tag)
        {
            var stage = cross.CompilerGetExecutionModel(compiler);
            foreach (HlslRegisterAssignment assignment in assignments)
            {
                HlslResourceBinding binding;
                cross.HlslResourceBindingInit(&binding);
                binding.Stage = stage;
                binding.DescSet = assignment.Set;
                binding.Binding = assignment.Binding;

                // register_space stays 0 on every mapping: shader model 5.0 has no register spaces, and the
                // emitter only reads the space at model 5.1 and above.
                var mapping = new HlslResourceBindingMapping { RegisterSpace = 0, RegisterBinding = assignment.Index };
                switch (assignment.Space)
                {
                    case HlslRegisterSpace.ConstantBuffer: binding.Cbv = mapping; break;
                    case HlslRegisterSpace.ShaderResource: binding.Srv = mapping; break;
                    case HlslRegisterSpace.Sampler: binding.Sampler = mapping; break;
                    default: binding.Uav = mapping; break;
                }

                SpirvCrossCompile.Check(context, cross.CompilerHlslAddResourceBinding(compiler, &binding), tag,
                    $"install the register for (set {assignment.Set}, binding {assignment.Binding})");
            }
        }
    }
}
