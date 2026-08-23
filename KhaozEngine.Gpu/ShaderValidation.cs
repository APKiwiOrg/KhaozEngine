using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Device-free validation of a GLSL 450 vertex/fragment shader pair (<see cref="ValidatePair"/>) or a single
    /// compute shader (<see cref="ValidateCompute"/>). Compiles the sources to SPIR-V and cross-compiles them to
    /// every backend shading language the engine targets (HLSL and MSL), entirely on the CPU via shaderc and
    /// SPIRV-Cross. No device is created, so this runs in a fast, GPU-free test loop and on CI machines with no
    /// graphics device at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the engine's own shader-source tests use to catch a syntax error or a backend miscompile at
    /// build time instead of at first run on a real device of that backend. Games can validate their own custom
    /// shaders the same way in their fast test suites: hand each vertex/fragment pair to
    /// <see cref="ValidatePair(string, string, string?)"/>, or each compute source to
    /// <see cref="ValidateCompute(string, string?)"/>, from a plain unit test.
    /// </para>
    /// <para>
    /// IT STOPS AT THE CROSS-COMPILE, AND THAT IS A REAL GAP ON DIRECT3D 11. This validator produces HLSL and
    /// has never COMPILED it, so HLSL that SPIRV-Cross emits happily and FXC rejects passes here, as does a
    /// vertex input signature with a hole in it (SPIRV-Cross drops an input the vertex stage never reads, and
    /// FXC plus WARP miscompile the result silently). Both have cost this engine a production incident. The
    /// missing half is <c>KhaozEngineD3D11.ValidateShaderPair</c> and <c>ValidateComputeShader</c> in the opt-in
    /// <c>KhaozEngine.Gpu.D3D11</c> package, which run the real FXC call plus the signature assertion with no
    /// device. They live there rather than here because FXC is <c>d3dcompiler</c>, so the leg is Windows-only,
    /// and because sharing one FXC call site with the shipped shader path is what stops a validator from
    /// validating a shader nobody ships. Call both, the second behind
    /// <c>KhaozEngineD3D11.IsPlatformSupported</c>.
    /// </para>
    /// <para>
    /// IT ALSO CHECKS THE METAL BINDING ORDER, which is not a compile failure anywhere and is the one class of
    /// shader bug that renders a wrong picture instead of throwing. Both entry points run
    /// <see cref="Internal.MslBindingOrder"/> over the Metal emission: per stage, the arguments in Metal index
    /// order must be the arguments in binding order, and for a pair each stage's resources must additionally be a
    /// PREFIX of the layout's, per index space. Read that type for the mechanism and for what it deliberately
    /// stays silent about.
    /// </para>
    /// <para>
    /// THE TARGETS ARE THE ONES THE ENGINE SHIPS, AND SINCE 18.0.0 THAT IS TWO: HLSL for the native Direct3D 11
    /// backend and MSL for the native Metal one. Vulkan is not a target because it consumes the SPIR-V directly,
    /// so the front-end compile IS its validation.
    /// </para>
    /// <para>
    /// GLSL AND ESSL WERE DROPPED WITH THE TOOLCHAIN SWAP, and that was row 8's open decision (section 9 of
    /// <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c>). SPIRV-Cross has both back ends, so keeping them
    /// would have cost two lines. What they cost instead is a FALSE STOP: with no OpenGL or GLES backend anywhere
    /// in the engine since the incumbents were deleted, a shader that emits fine for both shipped targets and
    /// trips a GLSL or ESSL back end is refused for a device nobody can run it on. A gate that can only produce
    /// false negatives is worse than no gate.
    /// </para>
    /// </remarks>
    public static class ShaderValidation
    {

        /// <summary>
        /// Validates a GLSL 450 vertex + fragment shader pair without a graphics device. First compiles each source
        /// to SPIR-V (entry point <c>main</c>, the same convention as the runtime SPIR-V path
        /// <c>CreateShadersFromSpirv</c>), then cross-compiles the pair to HLSL and MSL. A compile error at any
        /// stage throws, so calling this from a test is enough to prove the pair builds on every backend the
        /// engine ships. GLSL and ESSL are deliberately not swept: see the type's own note above.
        /// </summary>
        /// <param name="vertexGlsl">The vertex shader source, GLSL <c>#version 450</c>.</param>
        /// <param name="fragmentGlsl">The fragment shader source, GLSL <c>#version 450</c>.</param>
        /// <param name="label">Optional name for the pair, included in any error message so a failure points at the
        /// offending shader.</param>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V, the pair failed to
        /// cross-compile to one of the backend targets, or the Metal emission's binding order disagrees with the
        /// resource layout's (see <see cref="Internal.MslBindingOrder"/>). The message names the label and the
        /// failing stage/target.</exception>
        public static void ValidatePair(string vertexGlsl, string fragmentGlsl, string? label = null)
        {
            if (vertexGlsl is null) throw new ArgumentNullException(nameof(vertexGlsl));
            if (fragmentGlsl is null) throw new ArgumentNullException(nameof(fragmentGlsl));

            string tag = label ?? "shader pair";

            byte[] vertSpirv = CompileToSpirv(vertexGlsl, GpuShaderStages.Vertex, tag);
            byte[] fragSpirv = CompileToSpirv(fragmentGlsl, GpuShaderStages.Fragment, tag);

            // Both emitters run through the SAME seat the shipped shader path uses, so what is validated is what
            // the backends will actually compile, under the same pinned options. Each throws its own named
            // ShaderValidationException naming the tag and the target, which is why neither call is wrapped here.
            SpirvCrossCompile.VertexFragmentToHlsl(vertSpirv, fragSpirv, tag);
            CrossCompiledPair msl = SpirvCrossCompile.VertexFragmentToMsl(vertSpirv, fragSpirv, tag);

            // Both stages first, then the pair-wide prefix property, so a per-stage swap (the common case,
            // and the one with a one-line fix) is reported ahead of the layout-shaped constraint.
            var vertex = MslBindingOrder.CheckStage(vertSpirv, msl.VertexSource, MslBindingOrder.Vertex, tag);
            var fragment = MslBindingOrder.CheckStage(fragSpirv, msl.FragmentSource, MslBindingOrder.Fragment, tag);
            MslBindingOrder.CheckPrefix(vertex, fragment, tag);
        }

        /// <summary>
        /// Validates a GLSL 450 COMPUTE shader without a graphics device: compiles the source to SPIR-V (entry
        /// point <c>main</c>, the same convention as the runtime path
        /// <see cref="IGpuResourceFactory.CreateComputeShaderFromSpirv"/>), then cross-compiles the single stage to
        /// HLSL and MSL. The compute sibling of
        /// <see cref="ValidatePair(string, string, string?)"/>, and the same reason to use it: a compute shader that
        /// miscompiles on one backend otherwise only blows up at first dispatch on a real device of that backend,
        /// whereas this runs in the fast GPU-free test lane on every push.
        /// </summary>
        /// <param name="computeGlsl">The compute shader source, GLSL <c>#version 450</c>, with a
        /// <c>layout(local_size_x = ...) in;</c> workgroup declaration.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, or failed to
        /// cross-compile to one of the backend targets. The message names the label and the failing stage/target.</exception>
        public static void ValidateCompute(string computeGlsl, string? label = null)
        {
            if (computeGlsl is null) throw new ArgumentNullException(nameof(computeGlsl));

            string tag = label ?? "compute shader";
            byte[] spirv = CompileToSpirv(computeGlsl, GpuShaderStages.Compute, tag);

            // Same seat as the shipped compute path, for the same reason the pair validator uses it, and each
            // emitter throws its own named failure naming the tag and the target.
            SpirvCrossCompile.ComputeToHlsl(spirv, tag);
            CrossCompiledCompute msl = SpirvCrossCompile.ComputeToMsl(spirv, tag);

            // The id join first, because it is exact and sees a same-kind swap. The kind comparison below is
            // the fallback for the one case the join cannot answer: an index space carrying an argument whose
            // name is not the _<id> shape, where the join deliberately says nothing rather than guess.
            var resolved = MslBindingOrder.CheckStage(spirv, msl.ComputeSource, MslBindingOrder.Compute, tag);
            if (resolved is null || !resolved.ContainsKey("buffer")) CheckMslBufferSlots(msl, tag);
        }

        /// <summary>
        /// THE FALLBACK, since 17.36.0. <see cref="Internal.MslBindingOrder"/> answers the same question exactly,
        /// keyed on the SPIR-V id, and this runs only for a buffer space that join declined to read. Kept because
        /// the join's one refusal (an argument whose name is not the <c>_&lt;id&gt;</c> shape) leaves a shader
        /// with no check at all otherwise, and half a check beats none.
        /// <para>
        /// Reject a compute source whose cross-compiled Metal entry point puts its UNIFORM buffer at a different
        /// slot from the one the resource layout will bind it to. This is a real, silent miscompile rather than a
        /// style check, and it is why it exists.
        /// </para>
        /// <para>
        /// Metal has no binding decorations. The cross-compiler hands each resource a <c>[[buffer(n)]]</c> index of
        /// its own, assigned in SPIR-V id order, which follows where each resource is FIRST REFERENCED across the
        /// emitted function bodies. The backend, meanwhile, binds a resource set by counting the
        /// <see cref="GpuResourceLayoutDescription"/>'s elements in binding order. Those two agree only when
        /// first-reference order happens to match binding order - so a helper function that reads binding 1 before
        /// anything reads binding 0 silently swaps the two, on Metal ONLY, while Vulkan and Direct3D11 stay
        /// perfectly correct because they honour the decorations. The observed shape was a kernel reading its
        /// cascade tile size out of the spectrum buffer, getting 0, and producing a NaN surface.
        /// </para>
        /// <para>
        /// What is compared is the ORDER OF KINDS: the reflected layout lists its buffers in binding order, the
        /// entry point lists its buffer arguments in Metal-index order, and a uniform buffer that lands at a
        /// different position between the two is the bug. That catches a uniform/storage swap, which is the case a
        /// mixed resource set can hit. It does NOT distinguish two storage buffers from each other, since Metal
        /// spells both <c>device T&amp;</c> - a swap between two same-kind buffers is not visible from here. That
        /// gap is exactly what the id join above closes, which is why this is a fallback and no longer the guard.
        /// </para>
        /// <para>
        /// The fix in the shader is always the same shape: make the first reference to each resource happen in
        /// binding order, hoisting a first touch into <c>main</c> when a helper function reaches a later binding
        /// first.
        /// </para>
        /// </summary>
        static void CheckMslBufferSlots(CrossCompiledCompute result, string tag)
        {
            string[] declared = BufferKindsFromReflection(result);
            string[] emitted = BufferKindsFromEntryPoint(result.ComputeSource);
            if (declared.Length == 0 || emitted.Length != declared.Length) return;   // shapes disagree: nothing to say

            for (int i = 0; i < declared.Length; i++)
            {
                if (declared[i] == emitted[i]) continue;
                throw new ShaderValidationException(
                    $"{tag}: the Metal entry point binds a {emitted[i]} buffer at slot {i}, but the resource " +
                    $"layout puts a {declared[i]} buffer there. Metal buffer indices are assigned in " +
                    "first-reference order while the resource layout is counted in binding order, so this binds " +
                    "the wrong resource to each slot on Metal ONLY, and silently. Make the first reference to " +
                    "each resource happen in binding order - hoist a first touch into main when a helper function " +
                    "reaches a later binding first.");
            }
        }

        /// <summary>The kind of every BUFFER resource the module declares, in binding order, as
        /// <c>uniform</c>/<c>storage</c>. Textures and samplers have their own Metal index space and are skipped.</summary>
        static string[] BufferKindsFromReflection(CrossCompiledCompute result)
        {
            var kinds = new System.Collections.Generic.List<string>();
            foreach (GpuResourceLayoutDescription set in result.Reflection.ResourceLayouts)
            {
                foreach (GpuResourceLayoutElement element in set.Elements)
                {
                    if (element.Kind == GpuResourceKind.UniformBuffer) kinds.Add("uniform");
                    else if (element.Kind == GpuResourceKind.StructuredBufferReadOnly
                          || element.Kind == GpuResourceKind.StructuredBufferReadWrite) kinds.Add("storage");
                }
            }
            return kinds.ToArray();
        }

        /// <summary>The kind of every buffer argument of the Metal entry point, in <c>[[buffer(n)]]</c> index
        /// order. A <c>constant T&amp;</c> argument is a uniform buffer; <c>device</c> / <c>const device</c> is a
        /// storage buffer.</summary>
        static string[] BufferKindsFromEntryPoint(string msl)
        {
            int start = msl.IndexOf("kernel void", StringComparison.Ordinal);
            if (start < 0) return Array.Empty<string>();
            int open = msl.IndexOf('(', start);
            if (open < 0) return Array.Empty<string>();
            // Match the closing parenthesis by DEPTH, not by the first one: every argument carries an attribute
            // like [[buffer(0)]], so a naive scan stops inside the first one and sees a single argument.
            int close = -1, depth = 0;
            for (int i = open; i < msl.Length; i++)
            {
                if (msl[i] == '(') depth++;
                else if (msl[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0) return Array.Empty<string>();

            var byIndex = new System.Collections.Generic.SortedDictionary<int, string>();
            foreach (string argument in msl.Substring(open + 1, close - open - 1).Split(','))
            {
                int marker = argument.IndexOf("[[buffer(", StringComparison.Ordinal);
                if (marker < 0) continue;
                int numberStart = marker + "[[buffer(".Length;
                int numberEnd = argument.IndexOf(')', numberStart);
                if (numberEnd < 0) continue;
                if (!int.TryParse(argument.AsSpan(numberStart, numberEnd - numberStart), out int index)) continue;
                string text = argument.TrimStart();
                byIndex[index] = text.StartsWith("constant ", StringComparison.Ordinal) ? "uniform" : "storage";
            }

            var kinds = new string[byIndex.Count];
            byIndex.Values.CopyTo(kinds, 0);
            return kinds;
        }

        // THE ONE FRONT-END SEAT (decision V-S2). This validator used to make its own CompileGlslToSpirv call with
        // the library defaults, which meant a change to the pinned options would have moved the shipped shader
        // path and left the validator compiling under the old set, silently. Routing it through SpirvFrontEnd is
        // what makes SpirvFrontEndPin govern every ENGINE-OWNED front-end call rather than most of them. Until
        // 18.0.0 the incumbent VeldridGpuDevice compiled under the library defaults, deliberately, and the two
        // sets were asserted equal by VulkanSpirvIncumbentParityTests rather than by construction. Both went with
        // the incumbent.
        static byte[] CompileToSpirv(string glsl, GpuShaderStages stage, string tag)
            => Internal.SpirvFrontEnd.ToSpirv(glsl, stage, tag);
    }

    /// <summary>
    /// Thrown by <see cref="ShaderValidation.ValidatePair(string, string, string?)"/> or
    /// <see cref="ShaderValidation.ValidateCompute(string, string?)"/> when a shader source fails to compile to
    /// SPIR-V or fails to cross-compile to a backend target. The message names the shader label and the failing
    /// stage or target.
    /// </summary>
    public sealed class ShaderValidationException : Exception
    {
        /// <summary>Creates the exception with the given message.</summary>
        public ShaderValidationException(string message) : base(message) { }

        /// <summary>Creates the exception with the given message and the underlying compile failure.</summary>
        public ShaderValidationException(string message, Exception inner) : base(message, inner) { }
    }
}
