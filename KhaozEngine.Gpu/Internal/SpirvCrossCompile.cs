using System;
using Veldrid;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE BACK END: SPIR-V in, HLSL or MSL out, SPIRV-Cross and nothing else. Device-free and entirely on the
    /// CPU. This is the native Direct3D 11 and native Metal backends' only SPIRV-Cross entry point, and the one
    /// place the SPIRV-Cross replacement (#462) changes for both paths.
    /// <para>
    /// THE MSL HALF IS DECISION M-S1 (section 12.1 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>), and it is what phase 3's front-end split
    /// was paying for. <see cref="VertexFragmentToMsl"/> and <see cref="ComputeToMsl"/> sit beside the HLSL pair
    /// under their own <see cref="MslCrossCompilePin"/> and touch nothing else: the front end is untouched, so the
    /// SPIR-V byte-equality drift test and <c>VulkanSpirvIncumbentParityTests</c> both keep meaning what they
    /// meant. #462 is NOT taken here and section 12.2 is why: <c>libveldrid-spirv</c> exports three non-incidental
    /// C entry points, none of which carries a resource-binding table, so an engine-owned shim over that library
    /// would get exactly what the managed wrapper already gets.
    /// </para>
    /// <para>
    /// AND THE MSL HALF HAS NO GLSL-SOURCE CONVENIENCE, WHICH IS A DECISION RATHER THAN AN OMISSION. The
    /// <see cref="GlslPairToHlsl"/> shape exists because the Direct3D path never needs the SPIR-V again once the
    /// HLSL is out. The Metal path does: its binding table is keyed on SPIR-V ids resolved through each stage's
    /// own <c>DescriptorSet</c> and <c>Binding</c> decorations, so it must hold each module. A convenience that
    /// swallowed them would force the backend to compile the same GLSL twice to get them back.
    /// </para>
    /// <para>
    /// THE FRONT END LIVES IN <see cref="SpirvFrontEnd"/> NOW, and the split is decision V-S3 (section 12.3 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>). It was carved out of this file because the
    /// native Vulkan backend needs the front end ONLY: Vulkan consumes SPIR-V, so nothing on its shader path is
    /// cross-compiled and nothing in this file is reachable from it. An architecture test asserts that, over the
    /// built IL. The GLSL-source convenience members below still run both halves, because that is the shape every
    /// Direct3D 11 call site has.
    /// </para>
    /// <para>
    /// The Veldrid wrapper <see cref="VeldridGpuDevice"/> still calls <c>Veldrid.SPIRV</c> directly, and remains
    /// a direct caller because it leaves the graph only when Veldrid itself does. That is true of BOTH halves of
    /// the toolchain there, which is worth saying plainly: it hands GLSL to <c>CreateFromSpirv</c>, which runs
    /// the FRONT end internally, and it makes its own <c>SpirvCompilation.CompileGlslToSpirv</c> call with
    /// <c>GlslCompileOptions.Default</c> for the compute path. So neither pin governs that wrapper, and the
    /// front-end halves are asserted equal by <c>VulkanSpirvIncumbentParityTests</c> rather than shared.
    /// </para>
    /// <para>
    /// WHY IT LIVES IN <c>KhaozEngine.Gpu</c> RATHER THAN IN THE BACKEND (decision P2, section 3 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>). The shader path needs SPIRV-Cross, and
    /// SPIRV-Cross arrives as <c>Veldrid.SPIRV</c>. Referencing that from <c>KhaozEngine.Gpu.D3D11</c> would
    /// bless a Veldrid package inside a backend whose entire premise is being Veldrid-free, which is a bad signal
    /// no guard would ever catch. This package already owns <see cref="ShaderValidation"/>, which uses precisely
    /// this static API with no device in existence, so the edge is already at home here. And one seat is what
    /// makes the eventual SPIRV-Cross replacement a change to one package rather than three.
    /// </para>
    /// <para>
    /// THE SIGNATURES ARE VELDRID-FREE, and that is the load-bearing half rather than a nicety. The backend
    /// consumes these members across <c>InternalsVisibleTo</c>. A Veldrid type in any parameter or return shape
    /// would put a Veldrid assembly reference in the backend's IL through an internal API, and internal API is
    /// exactly what a public-surface scan does not check. Everything crosses the boundary as
    /// <see cref="CrossCompiledPair"/> / <see cref="CrossCompiledCompute"/> over the engine's own mirrors.
    /// </para>
    /// </summary>
    internal static class SpirvCrossCompile
    {
        /// <summary>
        /// The cross-compile options every HLSL emission uses, in ONE place so the whole program is emitted
        /// under one set rather than under whatever each call site happened to pass. PRIVATE, because it is the
        /// one member of this class whose type is a Veldrid type, and every non-private member here is part of
        /// the Veldrid-free contract the backend consumes across <c>InternalsVisibleTo</c>.
        /// <para>
        /// PINNED, decision S3. The values are not written here: they are BUILT from
        /// <see cref="HlslCrossCompilePin"/>, which holds them as Veldrid-free constants together with the
        /// citation from the fork that says what <c>CreateFromSpirv</c> does with them (it forwards them
        /// verbatim, and derives nothing from <c>ResourceBindingModel</c>, which the design had assumed).
        /// <c>D3D11HlslByteEqualityTests</c> hashes what this set emits for every shipped program, so a changed
        /// value fails as a hash table that no longer matches rather than as a golden nobody can explain.
        /// </para>
        /// </summary>
        static readonly CrossCompileOptions _hlslOptions = new(
            HlslCrossCompilePin.FixClipSpaceZ,
            HlslCrossCompilePin.InvertVertexOutputY,
            HlslCrossCompilePin.NormalizeResourceNames);

        /// <summary>
        /// Cross-compile a vertex and fragment SPIR-V pair to HLSL, with the reflection the backend binds
        /// against. The pair is compiled TOGETHER rather than stage by stage, because the resource layouts are a
        /// property of the program and a per-stage compile would produce two disagreeing views of them.
        /// </summary>
        /// <param name="vertexSpirv">The vertex stage's SPIR-V module.</param>
        /// <param name="fragmentSpirv">The fragment stage's SPIR-V module.</param>
        /// <param name="label">Optional name for the pair, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The pair failed to cross-compile, or the module declares
        /// something the engine's own description mirrors do not model.</exception>
        internal static CrossCompiledPair VertexFragmentToHlsl(byte[] vertexSpirv, byte[] fragmentSpirv,
            string? label = null)
        {
            if (vertexSpirv is null) throw new ArgumentNullException(nameof(vertexSpirv));
            if (fragmentSpirv is null) throw new ArgumentNullException(nameof(fragmentSpirv));

            string tag = label ?? "shader pair";
            VertexFragmentCompilationResult result;
            try
            {
                result = SpirvCompilation.CompileVertexFragment(
                    vertexSpirv, fragmentSpirv, CrossCompileTarget.HLSL, _hlslOptions);
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: cross-compile to HLSL failed: {ex.Message}", ex);
            }

            return new CrossCompiledPair(result.VertexShader, result.FragmentShader, Reflect(result.Reflection, tag));
        }

        /// <summary>
        /// Cross-compile a compute SPIR-V module to HLSL, with its reflection. The compute sibling of
        /// <see cref="VertexFragmentToHlsl"/>.
        /// </summary>
        /// <param name="computeSpirv">The compute stage's SPIR-V module.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The module failed to cross-compile, or declares something
        /// the engine's own description mirrors do not model.</exception>
        internal static CrossCompiledCompute ComputeToHlsl(byte[] computeSpirv, string? label = null)
        {
            if (computeSpirv is null) throw new ArgumentNullException(nameof(computeSpirv));

            string tag = label ?? "compute shader";
            ComputeCompilationResult result;
            try
            {
                result = SpirvCompilation.CompileCompute(computeSpirv, CrossCompileTarget.HLSL, _hlslOptions);
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: compute cross-compile to HLSL failed: {ex.Message}", ex);
            }

            return new CrossCompiledCompute(result.ComputeShader, Reflect(result.Reflection, tag));
        }

        /// <summary>The GLSL-source convenience over <see cref="SpirvFrontEnd.ToSpirv"/> plus
        /// <see cref="VertexFragmentToHlsl"/>, which is the shape every call site actually has: the engine's
        /// shaders are GLSL constants, not SPIR-V blobs. It runs BOTH halves of the toolchain, which is why it
        /// stays on this side of the split rather than moving with the front end.</summary>
        internal static CrossCompiledPair GlslPairToHlsl(string vertexGlsl, string fragmentGlsl, string? label = null)
        {
            string tag = label ?? "shader pair";
            byte[] vertexSpirv = SpirvFrontEnd.ToSpirv(vertexGlsl, GpuShaderStages.Vertex, tag);
            byte[] fragmentSpirv = SpirvFrontEnd.ToSpirv(fragmentGlsl, GpuShaderStages.Fragment, tag);
            return VertexFragmentToHlsl(vertexSpirv, fragmentSpirv, tag);
        }

        /// <summary>The compute twin of <see cref="GlslPairToHlsl"/>.</summary>
        internal static CrossCompiledCompute GlslComputeToHlsl(string computeGlsl, string? label = null)
        {
            string tag = label ?? "compute shader";
            return ComputeToHlsl(SpirvFrontEnd.ToSpirv(computeGlsl, GpuShaderStages.Compute, tag), tag);
        }

        /// <summary>
        /// The cross-compile options every MSL emission uses, built from <see cref="MslCrossCompilePin"/> exactly
        /// as the HLSL set is built from <see cref="HlslCrossCompilePin"/>, and private for the same reason: it is
        /// a Veldrid type, and every non-private member of this class is part of the Veldrid-free contract the
        /// backends consume across <c>InternalsVisibleTo</c>.
        /// <para>
        /// A SEPARATE OBJECT FROM THE HLSL SET EVEN THOUGH THE VALUES MATCH TODAY, deliberately. The two pins are
        /// maintained independently and answer to different parity measurements, so sharing one options instance
        /// would silently couple a future Direct3D flag to the Metal goldens.
        /// </para>
        /// </summary>
        static readonly CrossCompileOptions _mslOptions = new(
            MslCrossCompilePin.FixClipSpaceZ,
            MslCrossCompilePin.InvertVertexOutputY,
            MslCrossCompilePin.NormalizeResourceNames);

        /// <summary>
        /// Cross-compile a vertex and fragment SPIR-V pair to MSL, with the reflection the backend binds against.
        /// The MSL sibling of <see cref="VertexFragmentToHlsl"/>, and the pair is compiled TOGETHER for the same
        /// reason: the resource layouts are a property of the program, and a per-stage compile would produce two
        /// disagreeing views of them.
        /// <para>
        /// THE EMITTED TEXT IS PER STAGE AND SO ARE ITS INDICES. Each stage's entry point carries only the
        /// resources that stage references, at indices SPIRV-Cross chose for that stage, which is why the native
        /// Metal backend reads a table per stage rather than one per program (2.2b). Nothing here interprets the
        /// emission: this member's whole job is to produce it under the pin.
        /// </para>
        /// </summary>
        /// <param name="vertexSpirv">The vertex stage's SPIR-V module.</param>
        /// <param name="fragmentSpirv">The fragment stage's SPIR-V module.</param>
        /// <param name="label">Optional name for the pair, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The pair failed to cross-compile, or the module declares
        /// something the engine's own description mirrors do not model.</exception>
        internal static CrossCompiledPair VertexFragmentToMsl(byte[] vertexSpirv, byte[] fragmentSpirv,
            string? label = null)
        {
            if (vertexSpirv is null) throw new ArgumentNullException(nameof(vertexSpirv));
            if (fragmentSpirv is null) throw new ArgumentNullException(nameof(fragmentSpirv));

            string tag = label ?? "shader pair";
            VertexFragmentCompilationResult result;
            try
            {
                result = SpirvCompilation.CompileVertexFragment(
                    vertexSpirv, fragmentSpirv, CrossCompileTarget.MSL, _mslOptions);
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: cross-compile to MSL failed: {ex.Message}", ex);
            }

            return new CrossCompiledPair(result.VertexShader, result.FragmentShader, Reflect(result.Reflection, tag));
        }

        /// <summary>
        /// Cross-compile a compute SPIR-V module to MSL, with its reflection. The compute sibling of
        /// <see cref="VertexFragmentToMsl"/>.
        /// </summary>
        /// <param name="computeSpirv">The compute stage's SPIR-V module.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The module failed to cross-compile, or declares something
        /// the engine's own description mirrors do not model.</exception>
        internal static CrossCompiledCompute ComputeToMsl(byte[] computeSpirv, string? label = null)
        {
            if (computeSpirv is null) throw new ArgumentNullException(nameof(computeSpirv));

            string tag = label ?? "compute shader";
            ComputeCompilationResult result;
            try
            {
                result = SpirvCompilation.CompileCompute(computeSpirv, CrossCompileTarget.MSL, _mslOptions);
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: compute cross-compile to MSL failed: {ex.Message}", ex);
            }

            return new CrossCompiledCompute(result.ComputeShader, Reflect(result.Reflection, tag));
        }

        // The Veldrid-to-engine boundary, and the only place it happens. VeldridMap owns the forward direction
        // (engine to Veldrid) because that is what device creation needs. Reflection is the one place the engine
        // reads a Veldrid description back, so the reverse maps live here with their single consumer rather than
        // as a second unused half of every VeldridMap entry.
        static ShaderReflection Reflect(SpirvReflection reflection, string tag)
        {
            var vertexElements = new GpuVertexElement[reflection.VertexElements.Length];
            for (int i = 0; i < vertexElements.Length; i++)
            {
                VertexElementDescription element = reflection.VertexElements[i];
                vertexElements[i] = new GpuVertexElement(element.Name, FromVeldrid(element.Format, tag));
            }

            var layouts = new GpuResourceLayoutDescription[reflection.ResourceLayouts.Length];
            for (int set = 0; set < layouts.Length; set++)
            {
                ResourceLayoutElementDescription[] source = reflection.ResourceLayouts[set].Elements;
                var elements = new GpuResourceLayoutElement[source.Length];
                for (int i = 0; i < elements.Length; i++)
                {
                    // dynamic: false always. A dynamic binding is a property of how the ENGINE declares a layout
                    // for a per-draw rebase, not something a SPIR-V module can express, so reflection can never
                    // report one and inventing a value here would be a guess that reads as a fact.
                    elements[i] = new GpuResourceLayoutElement(
                        source[i].Name, FromVeldrid(source[i].Kind, tag), FromVeldrid(source[i].Stages));
                }
                layouts[set] = new GpuResourceLayoutDescription(elements);
            }

            return new ShaderReflection(vertexElements, layouts);
        }

        static GpuShaderStages FromVeldrid(ShaderStages s)
        {
            GpuShaderStages r = GpuShaderStages.None;
            if ((s & ShaderStages.Vertex) != 0) r |= GpuShaderStages.Vertex;
            if ((s & ShaderStages.Geometry) != 0) r |= GpuShaderStages.Geometry;
            if ((s & ShaderStages.TessellationControl) != 0) r |= GpuShaderStages.TessellationControl;
            if ((s & ShaderStages.TessellationEvaluation) != 0) r |= GpuShaderStages.TessellationEvaluation;
            if ((s & ShaderStages.Fragment) != 0) r |= GpuShaderStages.Fragment;
            if ((s & ShaderStages.Compute) != 0) r |= GpuShaderStages.Compute;
            return r;
        }

        static GpuResourceKind FromVeldrid(ResourceKind k, string tag) => k switch
        {
            ResourceKind.UniformBuffer => GpuResourceKind.UniformBuffer,
            ResourceKind.StructuredBufferReadOnly => GpuResourceKind.StructuredBufferReadOnly,
            ResourceKind.StructuredBufferReadWrite => GpuResourceKind.StructuredBufferReadWrite,
            ResourceKind.TextureReadOnly => GpuResourceKind.TextureReadOnly,
            ResourceKind.TextureReadWrite => GpuResourceKind.TextureReadWrite,
            ResourceKind.Sampler => GpuResourceKind.Sampler,
            _ => throw new ShaderValidationException(
                $"{tag}: the module declares a resource of kind {k}, which the engine's GpuResourceKind mirror "
                + "does not model. Add the kind to the mirror and to both directions of the map, or the register "
                + "assignment will be counted against a shape the binder cannot express."),
        };

        // Named separately from the string-free flags map above so the failure can carry the shader label: a
        // format the mirror does not model is a real stop, and "which shader" is the first thing the reader asks.
        static GpuVertexElementFormat FromVeldrid(VertexElementFormat f, string tag) => f switch
        {
            VertexElementFormat.Float1 => GpuVertexElementFormat.Float1,
            VertexElementFormat.Float2 => GpuVertexElementFormat.Float2,
            VertexElementFormat.Float3 => GpuVertexElementFormat.Float3,
            VertexElementFormat.Float4 => GpuVertexElementFormat.Float4,
            _ => throw new ShaderValidationException(
                $"{tag}: the module declares a vertex input of format {f}, which the engine's "
                + "GpuVertexElementFormat mirror does not model (it covers Float1 to Float4, the set the "
                + "renderers declare). Add the format to the mirror and to both directions of the map."),
        };
    }
}
