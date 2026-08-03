using System;
using Veldrid;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// The engine's single seat for GLSL 450 to SPIR-V to HLSL cross-compilation, device-free and entirely on the
    /// CPU. This is the native Direct3D 11 backend's only SPIRV-Cross entry point, and the one place the phase 3
    /// SPIRV-Cross replacement changes for that path: nothing outside this file in the engine-owned Direct3D 11
    /// path names a <c>Veldrid.SPIRV</c> type. Two other seats still call <c>SpirvCompilation</c> directly,
    /// <see cref="ShaderValidation"/> and the Veldrid wrapper <see cref="VeldridGpuDevice"/>, and both remain
    /// direct callers because they leave the graph only when Veldrid itself does.
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
        /// Compile one GLSL 450 source to SPIR-V, entry point <c>main</c>, the same convention the runtime SPIR-V
        /// path uses.
        /// </summary>
        /// <param name="glsl">The shader source, GLSL <c>#version 450</c>.</param>
        /// <param name="stage">Which stage the source is. Exactly one stage flag.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V. The message names
        /// the label and the stage.</exception>
        internal static byte[] ToSpirv(string glsl, GpuShaderStages stage, string? label = null)
        {
            if (glsl is null) throw new ArgumentNullException(nameof(glsl));

            string tag = label ?? "shader";
            ShaderStages veldridStage = VeldridMap.ToVeldrid(stage);
            try
            {
                return SpirvCompilation.CompileGlslToSpirv(glsl, $"{tag}.{stage}", veldridStage,
                    GlslCompileOptions.Default).SpirvBytes;
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: {stage} GLSL to SPIR-V failed: {ex.Message}", ex);
            }
        }

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

        /// <summary>The GLSL-source convenience over <see cref="ToSpirv"/> plus
        /// <see cref="VertexFragmentToHlsl"/>, which is the shape every call site actually has: the engine's
        /// shaders are GLSL constants, not SPIR-V blobs.</summary>
        internal static CrossCompiledPair GlslPairToHlsl(string vertexGlsl, string fragmentGlsl, string? label = null)
        {
            string tag = label ?? "shader pair";
            byte[] vertexSpirv = ToSpirv(vertexGlsl, GpuShaderStages.Vertex, tag);
            byte[] fragmentSpirv = ToSpirv(fragmentGlsl, GpuShaderStages.Fragment, tag);
            return VertexFragmentToHlsl(vertexSpirv, fragmentSpirv, tag);
        }

        /// <summary>The compute twin of <see cref="GlslPairToHlsl"/>.</summary>
        internal static CrossCompiledCompute GlslComputeToHlsl(string computeGlsl, string? label = null)
        {
            string tag = label ?? "compute shader";
            return ComputeToHlsl(ToSpirv(computeGlsl, GpuShaderStages.Compute, tag), tag);
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
