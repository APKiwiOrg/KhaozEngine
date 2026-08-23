using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Which Direct3D 11 register file a binding lands in. The four are separate address spaces, so a
    /// <c>b0</c>, a <c>t0</c>, an <c>s0</c> and a <c>u0</c> are four different bindings and never collide.</summary>
    internal enum D3D11RegisterFile
    {
        /// <summary>Constant buffers, <c>bN</c>. Bound with <c>*SetConstantBuffers1</c>.</summary>
        ConstantBuffer,
        /// <summary>Shader resources, <c>tN</c>. Sampled textures AND read-only structured buffers share it.</summary>
        ShaderResource,
        /// <summary>Samplers, <c>sN</c>.</summary>
        Sampler,
        /// <summary>Unordered access, <c>uN</c>. Read-write structured buffers AND storage textures share it.</summary>
        UnorderedAccess,
    }

    /// <summary>One assigned register: a file plus an index within it. <see cref="ToString"/> renders it the way
    /// HLSL writes it (<c>t2</c>), which is what makes an assertion failure in the numbering table readable.</summary>
    internal readonly struct D3D11RegisterSlot : IEquatable<D3D11RegisterSlot>
    {
        internal D3D11RegisterFile File { get; }
        internal uint Index { get; }

        internal D3D11RegisterSlot(D3D11RegisterFile file, uint index)
        {
            File = file;
            Index = index;
        }

        /// <summary>The HLSL register letter for <see cref="File"/>.</summary>
        internal char Letter => File switch
        {
            D3D11RegisterFile.ConstantBuffer => 'b',
            D3D11RegisterFile.ShaderResource => 't',
            D3D11RegisterFile.Sampler => 's',
            D3D11RegisterFile.UnorderedAccess => 'u',
            _ => '?',
        };

        public bool Equals(D3D11RegisterSlot other) => File == other.File && Index == other.Index;
        public override bool Equals(object? obj) => obj is D3D11RegisterSlot other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)File, Index);
        public override string ToString() => Letter.ToString() + Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>How many registers of each file a layout consumes. Adding these across the layouts BEFORE a set,
    /// in pipeline-array order, is exactly how that set's base is found.</summary>
    internal readonly struct D3D11RegisterCounts
    {
        internal uint ConstantBuffers { get; }
        internal uint ShaderResources { get; }
        internal uint Samplers { get; }
        internal uint UnorderedAccess { get; }

        internal D3D11RegisterCounts(uint constantBuffers, uint shaderResources, uint samplers, uint unorderedAccess)
        {
            ConstantBuffers = constantBuffers;
            ShaderResources = shaderResources;
            Samplers = samplers;
            UnorderedAccess = unorderedAccess;
        }

        /// <summary>This plus <paramref name="other"/>, file by file.</summary>
        internal D3D11RegisterCounts Plus(in D3D11RegisterCounts other) => new(
            ConstantBuffers + other.ConstantBuffers,
            ShaderResources + other.ShaderResources,
            Samplers + other.Samplers,
            UnorderedAccess + other.UnorderedAccess);

        /// <summary>The count for one file, used as that file's base when this value is an accumulated total.</summary>
        internal uint For(D3D11RegisterFile file) => file switch
        {
            D3D11RegisterFile.ConstantBuffer => ConstantBuffers,
            D3D11RegisterFile.ShaderResource => ShaderResources,
            D3D11RegisterFile.Sampler => Samplers,
            D3D11RegisterFile.UnorderedAccess => UnorderedAccess,
            _ => 0u,
        };
    }

    /// <summary>
    /// DECISION S2, AND THE ONE PLACE IT IS WRITTEN DOWN. The emitted HLSL numbers its own registers and the CPU
    /// side has to agree exactly, or every shader compiles, every draw succeeds and every pixel is wrong. That
    /// failure has no symptom a test of anything else can catch, which is why this is a pure function over engine
    /// types with a device-free table test over every layout the renderers declare.
    /// <para>
    /// THE RULE, in full. WITHIN one layout, each element takes the next index from a counter chosen by its KIND,
    /// in DECLARATION order:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="GpuResourceKind.UniformBuffer"/> to <c>bN</c>.</description></item>
    /// <item><description><see cref="GpuResourceKind.Sampler"/> to <c>sN</c>.</description></item>
    /// <item><description><see cref="GpuResourceKind.TextureReadOnly"/> and
    /// <see cref="GpuResourceKind.StructuredBufferReadOnly"/> SHARE the <c>tN</c> counter.</description></item>
    /// <item><description><see cref="GpuResourceKind.TextureReadWrite"/> and
    /// <see cref="GpuResourceKind.StructuredBufferReadWrite"/> SHARE the <c>uN</c> counter.</description></item>
    /// </list>
    /// <para>
    /// ACROSS layouts, the sets flatten in PIPELINE-ARRAY order, per file: set <c>k</c>'s base for a file is the
    /// sum of every earlier set's count for that file. The GLSL <c>set=</c> number does NOT decide the base, and
    /// that is not a hypothetical: <c>SpriteBatch</c> deliberately declares its texture and sampler at
    /// <c>set = 0</c> and its view-projection UBO at <c>set = 1</c>, so any rule phrased as "set 0 comes first"
    /// is already false in shipped code. The pipeline's <c>ResourceLayouts</c> array is the authority, and the
    /// slot a set is bound at indexes into that same array.
    /// </para>
    /// <para>
    /// The sharing is the part that surprises people, so it is worth naming WHY. SPIRV-Cross emits a GLSL storage
    /// block as a <c>ByteAddressBuffer</c> or an <c>RWByteAddressBuffer</c>, and those occupy <c>t</c> and <c>u</c>
    /// alongside textures rather than a space of their own. Decision C2 keeps the RAW view forcing for exactly
    /// that reason, so the two halves agree by construction.
    /// </para>
    /// <para>
    /// THE OTHER HALF OF THIS RULE IS <c>KhaozEngine.Gpu.Internal.HlslRegisterRemap</c>, which makes the emitted
    /// HLSL name the registers this type assigns. SPIRV-Cross on its own emits the module's raw <c>Binding</c>
    /// decoration, which is not this numbering, so the two agreeing is arranged rather than given.
    /// <c>D3D11HlslRegisterAgreementTests</c> compares them over every shipped program, because the failure when
    /// they disagree is a black frame on the Windows leg and nothing else.
    /// </para>
    /// <para>
    /// WHAT LIVES ELSEWHERE. Turning an assigned register into a native bind call is the bind flush, which also
    /// owns the per-stage fan-out and the redundancy caches. This type answers only "which register", never "which
    /// call".
    /// </para>
    /// </summary>
    internal static class D3D11RegisterScheme
    {
        /// <summary>
        /// The register file a kind binds into. The two SHARING pairs are the whole content of this method, and
        /// they are the reason a kind cannot be mapped to a file by ordinal or by name.
        /// </summary>
        internal static D3D11RegisterFile FileFor(GpuResourceKind kind) => kind switch
        {
            GpuResourceKind.UniformBuffer => D3D11RegisterFile.ConstantBuffer,
            GpuResourceKind.Sampler => D3D11RegisterFile.Sampler,
            GpuResourceKind.TextureReadOnly => D3D11RegisterFile.ShaderResource,
            GpuResourceKind.StructuredBufferReadOnly => D3D11RegisterFile.ShaderResource,
            GpuResourceKind.TextureReadWrite => D3D11RegisterFile.UnorderedAccess,
            GpuResourceKind.StructuredBufferReadWrite => D3D11RegisterFile.UnorderedAccess,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unmapped GpuResourceKind. Every kind has to name a Direct3D 11 register file, because an "
                + "unassigned binding compiles and renders wrongly rather than failing."),
        };

        /// <summary>
        /// Assign the WITHIN-LAYOUT registers for <paramref name="elements"/>, writing one slot per element into
        /// <paramref name="slots"/> in the same order, and returning how many registers of each file the layout
        /// consumed. The indices are RELATIVE to the layout, so a layout is numbered once at creation and reused
        /// under any pipeline. <see cref="BaseFor"/> supplies the pipeline-dependent half.
        /// </summary>
        internal static D3D11RegisterCounts AssignWithinLayout(
            ReadOnlySpan<GpuResourceLayoutElement> elements, Span<D3D11RegisterSlot> slots)
        {
            if (slots.Length != elements.Length)
            {
                throw new ArgumentException(
                    "One register slot per layout element, in declaration order. The slot array is indexed by the "
                    + "element's position in the layout, which is also how a resource set's resources are ordered.",
                    nameof(slots));
            }

            uint b = 0, t = 0, s = 0, u = 0;
            for (int i = 0; i < elements.Length; i++)
            {
                D3D11RegisterFile file = FileFor(elements[i].Kind);
                uint index = file switch
                {
                    D3D11RegisterFile.ConstantBuffer => b++,
                    D3D11RegisterFile.ShaderResource => t++,
                    D3D11RegisterFile.Sampler => s++,
                    _ => u++,
                };
                slots[i] = new D3D11RegisterSlot(file, index);
            }

            return new D3D11RegisterCounts(b, t, s, u);
        }

        /// <summary>
        /// The base every register in set <paramref name="setIndex"/> is offset by: the per-file sum over the
        /// layouts BEFORE it in <paramref name="pipelineLayouts"/>, which is the pipeline's own
        /// <c>ResourceLayouts</c> array. Nothing here consults a GLSL set number, because there is none to consult.
        /// </summary>
        internal static D3D11RegisterCounts BaseFor(D3D11ResourceLayout[] pipelineLayouts, uint setIndex)
        {
            ArgumentNullException.ThrowIfNull(pipelineLayouts);
            // EQUAL is already out of range: the last valid slot is Length - 1. A setIndex of Length would sum
            // every layout in the pipeline and hand back a base that looks plausible, which is the one wrong
            // answer here that renders rather than throws.
            if (setIndex >= pipelineLayouts.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(setIndex), setIndex,
                    "The set index addresses the pipeline's resource-layout array, so a slot past its end is a "
                    + "pipeline and set mismatch rather than an empty base.");
            }

            var total = new D3D11RegisterCounts(0, 0, 0, 0);
            for (uint i = 0; i < setIndex; i++) total = total.Plus(pipelineLayouts[i].Counts);
            return total;
        }

        /// <summary>The absolute register for a layout-relative <paramref name="slot"/> under
        /// <paramref name="baseCounts"/>, which is what a bind call actually names.</summary>
        internal static D3D11RegisterSlot Absolute(in D3D11RegisterCounts baseCounts, in D3D11RegisterSlot slot)
            => new(slot.File, baseCounts.For(slot.File) + slot.Index);
    }
}
