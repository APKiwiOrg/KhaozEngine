using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One corpus row: a key, the SHA-256 of what the toolchain produced for it, and a size.</summary>
    /// <param name="Key">The row's stable key, <c>program.stage.target</c> or <c>program.layout</c>.</param>
    /// <param name="Hash">SHA-256 of the artefact, lower-case hex. The layout rows hash their own text.</param>
    /// <param name="Size">Bytes for SPIR-V, characters for emitted text, element count for a layout.</param>
    /// <param name="Detail">The layout rows carry their canonical rendering here. Empty otherwise.</param>
    internal readonly record struct ShaderCorpusRow(string Key, string Hash, int Size, string Detail);

    /// <summary>
    /// THE OUT-OF-PROCESS MIGRATION INSTRUMENT FOR THE TOOLCHAIN SWAP (row 8 of the Veldrid removal,
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/691">#691</see>), and it is out of process
    /// because it has to be. Section 2.3 result 4 of
    /// <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> measured that <c>Veldrid.SPIRV</c> and
    /// <c>Silk.NET.Shaderc</c> CORRUPT EACH OTHER when both are loaded: both statically link glslang and
    /// SPIRV-Tools, the second one loaded interposes on the first, and the incumbent then read shuffle operands
    /// out of executable memory or aborts. So the obvious instrument, a test asserting that the new toolchain
    /// equals the old, is poisoned by its own existence and cannot be written at all.
    /// <para>
    /// WHAT REPLACES IT. This type compiles every shipped program through whichever toolchain the tree currently
    /// references and renders the result as a sorted text table. Run it before the swap, commit the table, run it
    /// after the swap, and diff the two files. That comparison happens in two separate processes, which is the
    /// only place the two libraries can both be measured.
    /// </para>
    /// <para>
    /// IT IS DELIBERATELY NOT A DRIFT TEST. <c>VulkanSpirvByteEqualityTests</c>,
    /// <c>D3D11HlslByteEqualityTests</c> and <c>MetalMslByteEqualityTests</c> already own that job for their own
    /// targets, and a fourth hash table asserting the same emissions would mean four bakes for every shader edit.
    /// What <see cref="ShaderCorpusTests"/> asserts on an ordinary run is COVERAGE: that the committed table has
    /// a row for every shipped program and no row for anything else. The hashes are history.
    /// </para>
    /// </summary>
    internal static class ShaderCorpus
    {
        /// <summary>Every row, for every shipped program, in the order they are written to the table.</summary>
        internal static IReadOnlyList<ShaderCorpusRow> Emit(Action<string, byte[]>? dump = null)
        {
            var rows = new List<ShaderCorpusRow>();
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
                EmitGraphics(program, rows, dump);
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
                EmitCompute(kernel, rows, dump);

            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return rows;
        }

        /// <summary>The key of every row <see cref="Emit"/> produces, without compiling anything. This is what an
        /// ordinary run asserts the committed table against, so the coverage check costs no toolchain calls.</summary>
        internal static IReadOnlyList<string> ExpectedKeys()
        {
            var keys = new List<string>();
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                foreach (string stage in new[] { "vertex", "fragment" })
                {
                    keys.Add($"{program.Name}.{stage}.spirv");
                    keys.Add($"{program.Name}.{stage}.msl");
                    keys.Add($"{program.Name}.{stage}.hlsl");
                }
                keys.Add($"{program.Name}.layout.msl");
                keys.Add($"{program.Name}.layout.hlsl");
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                keys.Add($"{kernel.Name}.compute.spirv");
                keys.Add($"{kernel.Name}.compute.msl");
                keys.Add($"{kernel.Name}.compute.hlsl");
                keys.Add($"{kernel.Name}.layout.msl");
                keys.Add($"{kernel.Name}.layout.hlsl");
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        static void EmitGraphics(ShippedGraphicsProgram program, List<ShaderCorpusRow> rows,
            Action<string, byte[]>? dump)
        {
            byte[] vertex = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name);
            byte[] fragment = SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name);
            rows.Add(Row($"{program.Name}.vertex.spirv", vertex, dump));
            rows.Add(Row($"{program.Name}.fragment.spirv", fragment, dump));

            CrossCompiledPair msl = SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, program.Name);
            rows.Add(Row($"{program.Name}.vertex.msl", msl.VertexSource, dump));
            rows.Add(Row($"{program.Name}.fragment.msl", msl.FragmentSource, dump));
            rows.Add(LayoutRow($"{program.Name}.layout.msl", msl.Reflection));

            CrossCompiledPair hlsl = SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, program.Name);
            rows.Add(Row($"{program.Name}.vertex.hlsl", hlsl.VertexSource, dump));
            rows.Add(Row($"{program.Name}.fragment.hlsl", hlsl.FragmentSource, dump));
            rows.Add(LayoutRow($"{program.Name}.layout.hlsl", hlsl.Reflection));
        }

        static void EmitCompute(ShippedComputeKernel kernel, List<ShaderCorpusRow> rows, Action<string, byte[]>? dump)
        {
            byte[] spirv = SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name);
            rows.Add(Row($"{kernel.Name}.compute.spirv", spirv, dump));

            CrossCompiledCompute msl = SpirvCrossCompile.ComputeToMsl(spirv, kernel.Name);
            rows.Add(Row($"{kernel.Name}.compute.msl", msl.ComputeSource, dump));
            rows.Add(LayoutRow($"{kernel.Name}.layout.msl", msl.Reflection));

            CrossCompiledCompute hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, kernel.Name);
            rows.Add(Row($"{kernel.Name}.compute.hlsl", hlsl.ComputeSource, dump));
            rows.Add(LayoutRow($"{kernel.Name}.layout.hlsl", hlsl.Reflection));
        }

        static ShaderCorpusRow Row(string key, byte[] spirv, Action<string, byte[]>? dump)
        {
            dump?.Invoke(key, spirv);
            return new ShaderCorpusRow(key, Sha256(spirv), spirv.Length, string.Empty);
        }

        static ShaderCorpusRow Row(string key, string text, Action<string, byte[]>? dump)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            dump?.Invoke(key, utf8);
            return new ShaderCorpusRow(key, Sha256(utf8), text.Length, string.Empty);
        }

        static ShaderCorpusRow LayoutRow(string key, ShaderReflection reflection)
        {
            string text = Canonical(reflection);
            int elements = reflection.VertexElements.Length
                + reflection.ResourceLayouts.Sum(l => l.Elements.Length);
            return new ShaderCorpusRow(key, Sha256(Encoding.UTF8.GetBytes(text)), elements, text);
        }

        /// <summary>
        /// The reflected shape as one space-free token, so a permuted layout is READABLE in the diff rather than
        /// only a moved hash. This is the row risk R5 exists for: SPIRV-Cross enumerates resources in neither
        /// declaration nor binding order, and a port that trusts the order silently permutes what the backends
        /// bind against.
        /// <para>
        /// The reflected NAMES are deliberately included even though nothing binds on them (#586 measured that
        /// 83 of 141 elements reflect with an empty name and that no join on them is possible). They are the
        /// cheapest signal that two toolchains named the same thing differently, and a name-only difference is
        /// exactly the kind of finding a hash cannot tell apart from a permutation.
        /// </para>
        /// </summary>
        internal static string Canonical(ShaderReflection reflection)
        {
            var text = new StringBuilder();
            text.Append("in[");
            for (int i = 0; i < reflection.VertexElements.Length; i++)
            {
                if (i > 0) text.Append(',');
                GpuVertexElement element = reflection.VertexElements[i];
                text.Append(Safe(element.Name)).Append(':').Append(element.Format);
            }
            text.Append(']');

            for (int set = 0; set < reflection.ResourceLayouts.Length; set++)
            {
                text.Append("|set").Append(set.ToString(CultureInfo.InvariantCulture)).Append('[');
                GpuResourceLayoutElement[] elements = reflection.ResourceLayouts[set].Elements;
                for (int i = 0; i < elements.Length; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append(Safe(elements[i].Name)).Append(':').Append(elements[i].Kind)
                        .Append(':').Append(Stages(elements[i].Stages));
                }
                text.Append(']');
            }
            return text.ToString();
        }

        // A reflected name can be empty or carry a space. Neither may reach a table whose columns are split on
        // whitespace, so both are rendered as a visible token instead of silently changing the row's shape.
        static string Safe(string? name)
            => string.IsNullOrEmpty(name) ? "<unnamed>" : name.Replace(' ', '_').Replace('|', '_');

        static string Stages(GpuShaderStages stages)
        {
            if (stages == GpuShaderStages.None) return "-";
            var text = new StringBuilder();
            if ((stages & GpuShaderStages.Vertex) != 0) text.Append('V');
            if ((stages & GpuShaderStages.Geometry) != 0) text.Append('G');
            if ((stages & GpuShaderStages.TessellationControl) != 0) text.Append('H');
            if ((stages & GpuShaderStages.TessellationEvaluation) != 0) text.Append('D');
            if ((stages & GpuShaderStages.Fragment) != 0) text.Append('F');
            if ((stages & GpuShaderStages.Compute) != 0) text.Append('C');
            return text.ToString();
        }

        static string Sha256(byte[] bytes)
            => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
