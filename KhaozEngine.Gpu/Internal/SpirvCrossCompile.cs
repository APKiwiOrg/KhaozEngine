using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.SPIRV.Cross;

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
    /// SPIR-V byte-equality drift test keeps meaning what it meant, as did <c>VulkanSpirvIncumbentParityTests</c>
    /// until it went with the incumbent in 18.0.0. #462 is NOT taken here and section 12.2 is why:
    /// the outgoing <c>libveldrid-spirv</c> exported three
    /// non-incidental C entry points, none of which carried a resource-binding table, so an engine-owned shim
    /// over that library would have got exactly what the managed wrapper already got.
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
    /// WHY IT LIVES IN <c>KhaozEngine.Gpu</c> RATHER THAN IN THE BACKEND (decision P2, section 3 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>). The shader path needs SPIRV-Cross, and
    /// SPIRV-Cross arrives as a package: <c>Silk.NET.SPIRV.Cross</c> since 18.0.0, and <c>Veldrid.SPIRV</c>
    /// when the decision was taken. Referencing it from <c>KhaozEngine.Gpu.D3D11</c> would put a toolchain
    /// package inside a backend that declares none, which is a bad signal no guard would ever catch. The
    /// Veldrid wording the decision was written in outlived the package: what P2 keeps out of the backend is
    /// the toolchain, whichever one it is. This package already owns <see cref="ShaderValidation"/>, which
    /// uses precisely this static API with no device in existence, so the edge is already at home here. And
    /// one seat is what makes the eventual SPIRV-Cross replacement a change to one package rather than three.
    /// </para>
    /// <para>
    /// THE SIGNATURES NAME NO TOOLCHAIN TYPE, and that is the load-bearing half rather than a nicety. The
    /// backend consumes these members across <c>InternalsVisibleTo</c>. A toolchain type in any parameter or
    /// return shape would put that assembly reference in the backend's IL through an internal API, and internal
    /// API is exactly what a public-surface scan does not check. The rule was written as "Veldrid-free" while
    /// Veldrid was the toolchain, and it binds the same way against <c>Silk.NET.SPIRV.Cross</c>. Everything
    /// crosses the boundary as <see cref="CrossCompiledPair"/> / <see cref="CrossCompiledCompute"/> over the
    /// engine's own mirrors.
    /// </para>
    /// </summary>
    internal static class SpirvCrossCompile
    {
        /// <summary>
        /// THE ONE SPIRV-Cross API HANDLE. <c>GetApi</c> loads the native library, so it is created once for the
        /// process. Every context, compiler and options object below is created and destroyed per call: the C API
        /// is a tree of allocations owned by a context, and one long-lived context would accumulate every module
        /// the process ever compiled.
        /// PRIVATE, because it is a toolchain type, and every non-private member of this class is part of the
        /// toolchain-free contract the backends consume across <c>InternalsVisibleTo</c>.
        /// </summary>
        static readonly Cross _cross = Cross.GetApi();

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
            => Pair(vertexSpirv, fragmentSpirv, Backend.Hlsl, "HLSL", label ?? "shader pair");

        /// <summary>
        /// Cross-compile a compute SPIR-V module to HLSL, with its reflection. The compute sibling of
        /// <see cref="VertexFragmentToHlsl"/>.
        /// </summary>
        /// <param name="computeSpirv">The compute stage's SPIR-V module.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The module failed to cross-compile, or declares something
        /// the engine's own description mirrors do not model.</exception>
        internal static CrossCompiledCompute ComputeToHlsl(byte[] computeSpirv, string? label = null)
            => Compute(computeSpirv, Backend.Hlsl, "HLSL", label ?? "compute shader");

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
            => Pair(vertexSpirv, fragmentSpirv, Backend.Msl, "MSL", label ?? "shader pair");

        /// <summary>
        /// Cross-compile a compute SPIR-V module to MSL, with its reflection. The compute sibling of
        /// <see cref="VertexFragmentToMsl"/>.
        /// </summary>
        /// <param name="computeSpirv">The compute stage's SPIR-V module.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The module failed to cross-compile, or declares something
        /// the engine's own description mirrors do not model.</exception>
        internal static CrossCompiledCompute ComputeToMsl(byte[] computeSpirv, string? label = null)
            => Compute(computeSpirv, Backend.Msl, "MSL", label ?? "compute shader");

        // ---- the SPIRV-Cross session ---------------------------------------------------------------------

        // One context per call, holding both stage compilers, so the pair's two modules are parsed and emitted
        // under one allocation owner and freed together. spvc_context_destroy releases every compiler and every
        // string it handed out, which is why the emitted text is copied to managed strings before it returns.
        static unsafe CrossCompiledPair Pair(byte[] vertexSpirv, byte[] fragmentSpirv, Backend backend,
            string target, string tag)
        {
            if (vertexSpirv is null) throw new ArgumentNullException(nameof(vertexSpirv));
            if (fragmentSpirv is null) throw new ArgumentNullException(nameof(fragmentSpirv));

            Context* context = null;
            try
            {
                context = CreateContext(tag);
                Compiler* vertex = CreateCompiler(context, vertexSpirv, backend, tag);
                Compiler* fragment = CreateCompiler(context, fragmentSpirv, backend, tag);
                MslIndexAssignment[] msl = RemapResourceBindings(context, backend, tag, vertex, fragment);

                // THE EMISSION IS TAKEN BEFORE THE USE QUERY, and the order is the mechanism rather than style.
                // A resource binding is marked used as the emitter consumes it, so asking first answers false for
                // everything and every element would read as unreferenced by both stages.
                string vertexSource = Emit(context, vertex, tag);
                string fragmentSource = Emit(context, fragment, tag);
                RequireNoHelperBuffers(backend, vertex, "vertex", tag);
                RequireNoHelperBuffers(backend, fragment, "fragment", tag);

                return new CrossCompiledPair(vertexSource, fragmentSource,
                    SpirvCrossReflect.ForPair(_cross, context, vertex, fragment, tag),
                    MslUse(backend, msl, (GpuShaderStages.Vertex, (nint)vertex), (GpuShaderStages.Fragment, (nint)fragment)));
            }
            catch (ShaderValidationException ex)
            {
                throw new ShaderValidationException($"{tag}: cross-compile to {target} failed: {ex.Message}", ex);
            }
            finally
            {
                if (context is not null) _cross.ContextDestroy(context);
            }
        }

        static unsafe CrossCompiledCompute Compute(byte[] computeSpirv, Backend backend, string target, string tag)
        {
            if (computeSpirv is null) throw new ArgumentNullException(nameof(computeSpirv));

            Context* context = null;
            try
            {
                context = CreateContext(tag);
                Compiler* compute = CreateCompiler(context, computeSpirv, backend, tag);
                MslIndexAssignment[] msl = RemapResourceBindings(context, backend, tag, compute);
                string computeSource = Emit(context, compute, tag);
                RequireNoHelperBuffers(backend, compute, "compute", tag);

                return new CrossCompiledCompute(computeSource,
                    SpirvCrossReflect.ForCompute(_cross, context, compute, tag),
                    MslUse(backend, msl, (GpuShaderStages.Compute, (nint)compute)));
            }
            catch (ShaderValidationException ex)
            {
                throw new ShaderValidationException(
                    $"{tag}: compute cross-compile to {target} failed: {ex.Message}", ex);
            }
            finally
            {
                if (context is not null) _cross.ContextDestroy(context);
            }
        }

        /// <summary>
        /// THE STEP BETWEEN PARSING AND EMITTING, and both targets take it now. SPIRV-Cross would otherwise name
        /// each resource from a counter of its own: the module's raw <c>Binding</c> decoration on the HLSL path,
        /// and a per-stage running count on the MSL path. Neither is what the backend binds against, and
        /// <see cref="HlslRegisterRemap"/> and <see cref="MslIndexRemap"/> hold the two rules and the reason.
        /// <para>
        /// THE PROGRAM'S RESOURCES ARE READ ACROSS EVERY STAGE FIRST, then one numbering is installed on all of
        /// them, so the two stages of a pair emit the same index for the same resource. That is the whole of what
        /// "authored" means: 18.0.0's row 10 replaced the native Metal backend's parse of the emitted argument
        /// list, and the SPIR-V decoration walk that joined it back to a declared element, with this call.
        /// </para>
        /// </summary>
        /// <returns>The MSL assignments, for the post-emission use query. Empty on the HLSL path.</returns>
        static unsafe MslIndexAssignment[] RemapResourceBindings(Context* context, Backend backend, string tag,
            params Compiler*[] compilers)
        {
            if (backend != Backend.Hlsl && backend != Backend.Msl) return [];

            var resources = new Dictionary<(uint Set, uint Binding), GpuResourceKind>();
            foreach (Compiler* compiler in compilers)
                SpirvCrossReflect.ReadResourceKinds(_cross, context, compiler, resources, tag);

            if (backend == Backend.Hlsl)
            {
                HlslRegisterAssignment[] hlsl = HlslRegisterRemap.Assign(resources);
                foreach (Compiler* compiler in compilers)
                    HlslRegisterRemap.Install(_cross, context, compiler, hlsl, tag);
                return [];
            }

            MslIndexAssignment[] msl = MslIndexRemap.Assign(resources);
            foreach (Compiler* compiler in compilers)
                MslIndexRemap.Install(_cross, context, compiler, msl, tag);
            return msl;
        }

        // Which resources each emitted MSL stage actually carries an argument for. Empty on the HLSL path, where
        // nothing asks the question. Called AFTER the emission, which is what makes the answer true.
        static unsafe MslStageUse[] MslUse(Backend backend, MslIndexAssignment[] assignments,
            params (GpuShaderStages Stage, nint Compiler)[] stages)
        {
            if (backend != Backend.Msl) return [];

            var use = new MslStageUse[stages.Length];
            for (int i = 0; i < stages.Length; i++)
            {
                use[i] = new MslStageUse(stages[i].Stage,
                    MslIndexRemap.UsedBy(_cross, (Compiler*)stages[i].Compiler, assignments));
            }

            return use;
        }

        /// <summary>
        /// THE REFUSAL THAT KEEPS THE AUTHORED NUMBERING THE ONLY NUMBERING. SPIRV-Cross emits its own helper
        /// buffer arguments for a handful of features (a swizzle buffer for emulated texture swizzling, a
        /// buffer-size buffer for runtime array lengths, output buffers for tessellation and for
        /// vertex-as-compute), and it numbers them from the TOP of the argument table by default, which is
        /// exactly where decision M-B2 pins the vertex streams. Such an argument carries no
        /// <c>(set, binding)</c>, so it is in no layout, in no binding table, and invisible to the
        /// pipeline-creation collision assertion.
        /// <para>
        /// IT WAS LOUD BEFORE AND STAYS LOUD. The parse this row deleted threw on a helper argument, because its
        /// name is not the <c>_&lt;id&gt;</c> shape the id join needed. Nothing in the authored path would ever
        /// look at one, so the throw moves here rather than disappearing. No shipped program needs any of them.
        /// </para>
        /// </summary>
        static unsafe void RequireNoHelperBuffers(Backend backend, Compiler* compiler, string stage, string tag)
        {
            if (backend != Backend.Msl) return;

            string? needed =
                _cross.CompilerMslNeedsSwizzleBuffer(compiler) != 0 ? "a swizzle buffer"
                : _cross.CompilerMslNeedsBufferSizeBuffer(compiler) != 0 ? "a buffer-size buffer"
                : _cross.CompilerMslNeedsOutputBuffer(compiler) != 0 ? "a shader output buffer"
                : _cross.CompilerMslNeedsPatchOutputBuffer(compiler) != 0 ? "a patch output buffer"
                : _cross.CompilerMslNeedsInputThreadgroupMem(compiler) != 0 ? "input threadgroup memory"
                : null;

            if (needed is null) return;

            throw new ShaderValidationException(
                $"{tag} [{stage}]: the emitted MSL needs {needed}, which SPIRV-Cross adds as a buffer argument of "
                + "its own with no set or binding. Its default index is at the top of the buffer table, where "
                + "decision M-B2 pins the vertex streams, and the engine's authored indices cannot see it to "
                + "avoid it. Nothing shipped needs one, so this is a shader using a feature the native Metal "
                + "backend has not been taught to number.");
        }

        static unsafe Context* CreateContext(string tag)
        {
            Context* context;
            if (_cross.ContextCreate(&context) != Result.Success || context is null)
                throw new ShaderValidationException($"{tag}: spvc_context_create failed.");
            return context;
        }

        // Parse one module and stand a backend compiler on it, with the pinned options installed. TakeOwnership
        // hands the parsed IR to the compiler, which the context then owns: the alternative, Copy, would keep a
        // second copy of every module alive for the length of the call and buy nothing, since nothing here reads
        // the IR again afterwards.
        static unsafe Compiler* CreateCompiler(Context* context, byte[] spirv, Backend backend, string tag)
        {
            if (spirv.Length % 4 != 0)
                throw new ShaderValidationException(
                    $"{tag}: the module is {spirv.Length} bytes, which is not a whole number of SPIR-V words.");

            ParsedIr* ir;
            fixed (byte* bytes = spirv)
            {
                Check(context, _cross.ContextParseSpirv(context, (uint*)bytes, (nuint)(spirv.Length / 4), &ir), tag,
                    "parse the SPIR-V module");
            }

            Compiler* compiler;
            Check(context, _cross.ContextCreateCompiler(context, backend, ir, CaptureMode.TakeOwnership, &compiler),
                tag, "create the " + backend + " compiler");

            CompilerOptions* options;
            Check(context, _cross.CompilerCreateCompilerOptions(compiler, &options), tag, "create the options");
            Configure(options, backend);
            Check(context, _cross.CompilerInstallCompilerOptions(compiler, options), tag, "install the options");
            return compiler;
        }

        /// <summary>
        /// THE PINNED OPTION SET, in ONE place so the whole program is emitted under one set rather than under
        /// whatever each call site happened to pass. The values are not written here: they are BUILT from
        /// <see cref="HlslCrossCompilePin"/> and <see cref="MslCrossCompilePin"/>, which hold them as
        /// toolchain-free constants. <c>D3D11HlslByteEqualityTests</c> and <c>MetalMslByteEqualityTests</c> hash
        /// what this set emits for every shipped program, so a changed value fails as a hash table that no longer
        /// matches rather than as a golden nobody can explain.
        /// <para>
        /// THE TWO PINS STAY SEPARATE EVEN THOUGH THEIR VALUES MATCH TODAY, deliberately. They are maintained
        /// independently and answer to different parity measurements, so folding them into one set would silently
        /// couple a future Direct3D flag to the Metal goldens.
        /// </para>
        /// <para>
        /// THE LANGUAGE VERSIONS ARE PINNED HERE RATHER THAN IN A PIN FILE because they are properties of the
        /// EMITTER rather than options the engine chose between: shader model 5.0 is what the Direct3D 11 backend
        /// compiles with FXC, and SPIRV-Cross's own default is 3.0, which emits a dialect that backend cannot
        /// use. Leaving either to a default would let a SPIRV-Cross upgrade move every emitted program.
        /// </para>
        /// <para>
        /// <c>NormalizeResourceNames</c> HAS NO SPIRV-Cross OPTION AND NEVER DID. It was a flag on the outgoing
        /// wrapper, which implemented it by renaming resources itself before emitting. Both pins hold it false,
        /// which #586 measured is also the only value that keeps the emission byte-comparable, so there is
        /// nothing to install and a flip of either pin would need the renaming pass written here first.
        /// </para>
        /// </summary>
        static unsafe void Configure(CompilerOptions* options, Backend backend)
        {
            bool fixClipSpaceZ = backend == Backend.Hlsl
                ? HlslCrossCompilePin.FixClipSpaceZ : MslCrossCompilePin.FixClipSpaceZ;
            bool invertY = backend == Backend.Hlsl
                ? HlslCrossCompilePin.InvertVertexOutputY : MslCrossCompilePin.InvertVertexOutputY;

            _cross.CompilerOptionsSetBool(options, CompilerOption.FixupDepthConvention, Bit(fixClipSpaceZ));
            _cross.CompilerOptionsSetBool(options, CompilerOption.FlipVertexY, Bit(invertY));
            if (backend == Backend.Hlsl)
                _cross.CompilerOptionsSetUint(options, CompilerOption.HlslShaderModel, HlslShaderModel);
            else
                _cross.CompilerOptionsSetUint(options, CompilerOption.MslVersion, MslVersion);
        }

        /// <summary>Shader model 5.0, as SPIRV-Cross spells it. What FXC compiles the emitted HLSL under in
        /// <c>KhaozEngine.Gpu.D3D11</c>, and what the outgoing toolchain asked for.</summary>
        const uint HlslShaderModel = 50;

        /// <summary>MSL 1.2, as SPIRV-Cross spells it (major * 10000 + minor * 100). The outgoing toolchain's
        /// value and SPIRV-Cross's own default, pinned so it stays that whatever the default becomes.</summary>
        const uint MslVersion = 10200;

        static byte Bit(bool value) => value ? (byte)1 : (byte)0;

        // Emit one stage. The returned pointer belongs to the context and dies with it, so the text is copied
        // into a managed string before anything else happens.
        static unsafe string Emit(Context* context, Compiler* compiler, string tag)
        {
            byte* text;
            Check(context, _cross.CompilerCompile(compiler, &text), tag, "emit the stage");
            return Marshal.PtrToStringUTF8((IntPtr)text) ?? string.Empty;
        }

        /// <summary>
        /// The one place a SPIRV-Cross result code becomes an exception, and it reads the context's own last
        /// error string rather than only naming the code. Internal because
        /// <see cref="SpirvCrossReflect"/> shares it: the reflection pass makes the same kind of call and its
        /// failures read the same way.
        /// </summary>
        internal static unsafe void Check(Context* context, Result result, string tag, string what)
        {
            if (result == Result.Success) return;

            // The context carries the C++ side's own message, which is the half that says WHY. Without it a
            // failure reads as a bare error code, and the outgoing toolchain surfaced the exception text.
            string detail = _cross.ContextGetLastErrorStringS(context) ?? string.Empty;
            throw new ShaderValidationException(
                $"{tag}: could not {what} ({result})" + (detail.Length == 0 ? "." : ": " + detail.TrimEnd()));
        }
    }
}
