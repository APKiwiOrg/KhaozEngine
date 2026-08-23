using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>One input-assembler element, engine-side: everything a Direct3D 11 input element description needs
    /// except the DXGI format, which <see cref="D3D11Formats"/> derives from <see cref="Format"/> at the Windows
    /// boundary. Keeping the shape here is what makes the semantic numbering testable without a device.</summary>
    internal readonly struct D3D11InputElement
    {
        internal D3D11InputElement(string semanticName, uint semanticIndex, GpuVertexElementFormat format,
            uint offsetBytes, uint slot, uint instanceStepRate)
        {
            SemanticName = semanticName;
            SemanticIndex = semanticIndex;
            Format = format;
            OffsetBytes = offsetBytes;
            Slot = slot;
            InstanceStepRate = instanceStepRate;
        }

        /// <summary>Always <see cref="D3D11InputLayoutPlan.Semantic"/>. See the plan for why there is only one.</summary>
        internal string SemanticName { get; }
        /// <summary>The index after the semantic name, counted across ALL buffer slots in order.</summary>
        internal uint SemanticIndex { get; }
        /// <summary>The engine component format.</summary>
        internal GpuVertexElementFormat Format { get; }
        /// <summary>Byte offset within its own buffer slot.</summary>
        internal uint OffsetBytes { get; }
        /// <summary>Which vertex buffer slot the element is read from.</summary>
        internal uint Slot { get; }
        /// <summary>0 for per-vertex data, else the per-instance step rate.</summary>
        internal uint InstanceStepRate { get; }

        /// <summary>True when this element advances per instance rather than per vertex.</summary>
        internal bool PerInstance => InstanceStepRate != 0;

        public override string ToString()
            => SemanticName + SemanticIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The vertex input layout, computed from the pipeline's vertex layouts alone. Built at
    /// <c>CreateGraphicsPipeline</c> and stored on the pipeline, because that is the one moment the compiled
    /// vertex shader bytecode is in hand, and Direct3D 11 validates an input layout against a real vertex shader
    /// signature at creation.
    /// <para>
    /// EVERY ELEMENT IS A <c>TEXCOORD</c>, AND THAT IS NOT LAZINESS. The engine's GLSL binds vertex inputs by
    /// LOCATION, and SPIRV-Cross emits each location as <c>TEXCOORD&lt;location&gt;</c> in the HLSL it produces.
    /// The incumbent already declared every element with the texture-coordinate semantic for the same reason, so
    /// the semantic name carries no meaning and the INDEX carries all of it. The index therefore counts across all
    /// buffer slots in array order rather than restarting per slot: slot 1's first element continues where slot 0
    /// left off, exactly as a second GLSL location would.
    /// </para>
    /// <para>
    /// WHY THE CONTIGUITY MATTERS, and it is two shipped incidents rather than a style preference. SPIRV-Cross
    /// DROPS a vertex input the shader never reads, so a shader that reads locations 0 and 5 to 8 emits a
    /// signature holed at 1 to 4. Building a pipeline against that on WARP corrupted the context so unrelated
    /// passes rendered no colour, and the interpolant twin turned terrain flat white. The enforcement (an FXC leg
    /// plus a reflected-signature assertion at validation and at pipeline creation) belongs to the shader path.
    /// This type's job is to make the CPU side's numbering contiguous from zero by construction, so a hole can
    /// only ever come from the shader.
    /// </para>
    /// </summary>
    internal static class D3D11InputLayoutPlan
    {
        /// <summary>The only semantic name the engine emits. See the type remarks.</summary>
        internal const string Semantic = "TEXCOORD";

        /// <summary>Component size of a vertex element format, which is what packs a slot's elements.</summary>
        internal static uint SizeInBytes(GpuVertexElementFormat format) => format switch
        {
            GpuVertexElementFormat.Float1 => 4,
            GpuVertexElementFormat.Float2 => 8,
            GpuVertexElementFormat.Float3 => 12,
            GpuVertexElementFormat.Float4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unmapped GpuVertexElementFormat."),
        };

        /// <summary>
        /// Flatten <paramref name="layouts"/> into input elements plus the per-slot vertex strides.
        /// <para>
        /// A layout's declared <see cref="GpuVertexLayoutDescription.Stride"/> wins when it is non-zero, so an
        /// interleaved buffer with padding keeps its real stride. Zero means "compute it", which sums the element
        /// sizes and is what almost every call site relies on. Element offsets are always packed within their own
        /// slot, since the seam has no per-element offset to honour.
        /// </para>
        /// </summary>
        internal static D3D11InputElement[] Build(IReadOnlyList<GpuVertexLayoutDescription>? layouts, out uint[] strides)
        {
            if (layouts is null || layouts.Count == 0)
            {
                strides = Array.Empty<uint>();
                return Array.Empty<D3D11InputElement>();
            }

            int total = 0;
            for (int slot = 0; slot < layouts.Count; slot++) total += ElementsOf(layouts[slot]).Length;

            var elements = new D3D11InputElement[total];
            strides = new uint[layouts.Count];

            uint semanticIndex = 0;   // across ALL slots, deliberately
            int next = 0;
            for (int slot = 0; slot < layouts.Count; slot++)
            {
                GpuVertexElement[] declared = ElementsOf(layouts[slot]);
                uint offset = 0;
                for (int i = 0; i < declared.Length; i++)
                {
                    elements[next++] = new D3D11InputElement(
                        Semantic, semanticIndex++, declared[i].Format, offset,
                        (uint)slot, layouts[slot].InstanceStepRate);
                    offset += SizeInBytes(declared[i].Format);
                }

                strides[slot] = layouts[slot].Stride != 0 ? layouts[slot].Stride : offset;
            }

            return elements;
        }

        static GpuVertexElement[] ElementsOf(in GpuVertexLayoutDescription layout)
            => layout.Elements ?? Array.Empty<GpuVertexElement>();
    }
}
