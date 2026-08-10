using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DEVICE-FACING HALF OF THE SHADER PATH: turns the MSL <see cref="MetalShaderBuild"/> emits into the
    /// <c>MTLLibrary</c> and <c>MTLFunction</c> objects a pipeline is created from. One per device, built by the
    /// resource factory.
    ///
    /// <para>
    /// EVERYTHING INTERESTING HAPPENED BEFORE THIS TYPE RUNS, which is the shape section 12 designed for. The
    /// front end, the cross-compile, the entry-point read and the whole binding-table join are device-free and
    /// tested on the free Linux leg over every shipped program. What is left here is one
    /// <c>newLibraryWithSource:options:error:</c> per stage and one <c>newFunctionWithName:</c> per entry point,
    /// and that is genuinely all of it.
    /// </para>
    /// <para>
    /// A FAILED COMPILE CARRIES METAL'S OWN MESSAGE. <c>newLibraryWithSource:</c> answers nil and writes an
    /// <c>NSError</c> whose localized description has the source line and the diagnostic, so the throw quotes it.
    /// Without that a broken shader is an unexplained nil, and the MSL that failed is not something the caller
    /// wrote: it is SPIRV-Cross output, so "which line" is the only way anyone works backwards to the GLSL.
    /// </para>
    /// <para>
    /// AND A NIL FUNCTION IS A SEPARATE FAILURE FROM A NIL LIBRARY, deliberately. A library that compiled but
    /// carries no function of the emitted name means the name read out of the MSL and the name Metal saw
    /// disagree, which is M-S5's whole subject, so it says so rather than folding into a generic compile
    /// failure.
    /// </para>
    /// <para>
    /// EVERY BODY OPENS AN AUTORELEASE POOL (M-N5). The <c>NSString</c>s this creates are autoreleased, the
    /// <c>NSError</c> Metal writes is autoreleased, and the class lookups underneath return autoreleased objects.
    /// <c>MetalAutoreleaseArchitectureTests</c> walks the IL rather than trusting this paragraph.
    /// </para>
    /// </summary>
    internal sealed class MetalShaderCompiler
    {
        readonly MTLDevice _device;
        readonly IMetalDeviceLiveness _liveness;
        readonly MetalIndexTableCache _tables;

        /// <param name="device">The device to compile on.</param>
        /// <param name="liveness">The device's identity token, handed to every shader set this makes.</param>
        /// <param name="tables">The device's index-table cache. THIS is the one site that deduplicates
        /// (row 10), because the table is a property of the emission and this is where an emission becomes a
        /// shader set: a table canonicalised any later would already have been handed out twice.</param>
        internal MetalShaderCompiler(MTLDevice device, IMetalDeviceLiveness liveness, MetalIndexTableCache tables)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(tables);

            _device = device;
            _liveness = liveness;
            _tables = tables;
        }

        /// <summary>Emit a GLSL 450 vertex and fragment pair to MSL and compile both stages.</summary>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V, the pair failed to
        /// cross-compile, its emission could not be read, or Metal rejected the emitted MSL.</exception>
        [SupportedOSPlatform("macos")]
        internal MetalShaderSet CreateShaderSet(string vertexGlsl, string fragmentGlsl)
        {
            MetalMslProgram program = MetalShaderBuild.Pair(vertexGlsl, fragmentGlsl);

            // THE TABLE IS CANONICALISED AFTER THE COMPILE RATHER THAN BEFORE IT, so a program Metal rejects
            // leaves nothing in the cache. The cache is never evicted, so an entry made for a shader set that was
            // never handed out would sit there for the device's life.
            return new MetalShaderSet(_liveness, CompileOnMacOs(program), _tables.Canonical(program.Table));
        }

        /// <summary>Emit a GLSL 450 compute source to MSL and compile it.</summary>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, failed to
        /// cross-compile, declares no resolvable workgroup size, its emission could not be read, or Metal
        /// rejected the emitted MSL.</exception>
        [SupportedOSPlatform("macos")]
        internal MetalComputeShader CreateComputeShader(string computeGlsl)
        {
            (MetalMslProgram program, uint x, uint y, uint z) = MetalShaderBuild.Compute(computeGlsl);
            MetalCompiledStage[] stages = CompileOnMacOs(program);
            return new MetalComputeShader(_liveness, stages[0], _tables.Canonical(program.Table), x, y, z);
        }

        /// <summary>
        /// THE BOUNDARY BETWEEN THE TWO HALVES, and the only member here that touches Metal. Everything above it
        /// is device-free; everything it does is one <c>newLibraryWithSource:</c> and one
        /// <c>newFunctionWithName:</c> per stage.
        /// <para>
        /// INTERNAL RATHER THAN PRIVATE because it is the seat of the one claim only a device can settle: that
        /// Metal accepts what this engine emits. <c>MetalShaderGpuTests</c> drives it with a hand-built program
        /// to reach the REJECTION path, which no shipped shader can produce and which would otherwise be the one
        /// branch on the device half with no coverage at all.
        /// </para>
        /// <para>
        /// A failure part way through releases what already landed rather than leaking it: a shader set is only
        /// ever handed out whole, so there is no owner for a partial one.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MetalCompiledStage[] CompileOnMacOs(MetalMslProgram program)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLCompileOptions options = NewPinnedOptions();
            var compiled = new List<MetalCompiledStage>(program.Stages.Count);
            try
            {
                foreach (MetalMslStage stage in program.Stages)
                    compiled.Add(CompileStage(stage, options));

                return compiled.ToArray();
            }
            catch
            {
                foreach (MetalCompiledStage stage in compiled)
                {
                    stage.Function.Release();
                    stage.Library.Release();
                }
                throw;
            }
            finally
            {
                options.Release();
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        MetalCompiledStage CompileStage(MetalMslStage stage, MTLCompileOptions options)
        {
            string where = stage.Stage.ToString().ToLowerInvariant() + " stage '" + stage.EntryPointName + "'";

            MTLLibrary library = MTLLibrary.NewWithSource(
                _device, NSString.FromManaged(stage.Msl), options, out NSError error);

            if (library.IsNull)
            {
                throw new ShaderValidationException(
                    "the native Metal device rejected the emitted MSL for the " + where + ": "
                    + (error.IsNull ? "-newLibraryWithSource:options:error: answered nil and wrote no NSError, "
                        + "which means the failure is not a compile error at all." : error.LocalizedDescription())
                    + " The source that failed is SPIRV-Cross output rather than anything hand-written, so the "
                    + "line number above is a line of emitted MSL: cross-compile the GLSL with "
                    + "ShaderValidation.ValidatePair to see it.");
            }

            MTLFunction function = library.NewFunction(NSString.FromManaged(stage.EntryPointName));
            if (function.IsNull)
            {
                library.Release();
                throw new ShaderValidationException(
                    "the emitted MSL for the " + where + " compiled, but the library carries no function of that "
                    + "name. The name was READ out of the emission rather than assumed (M-S5), so this means the "
                    + "entry-point parse and Metal disagree about what the function is called.");
            }

            return new MetalCompiledStage(stage.Stage, library, function);
        }

        // The pin, applied. Written every time rather than cached on the device, because MTLCompileOptions is
        // mutable and a shared instance is a shared mutable object on a path M-W8 says is free-threaded.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MTLCompileOptions NewPinnedOptions()
        {
            MTLCompileOptions options = MTLCompileOptions.New();
            if (options.IsNull)
            {
                throw new InvalidOperationException(
                    "The Objective-C runtime has no MTLCompileOptions class, which means the Metal framework did "
                    + "not load. Nothing about this shader caused it.");
            }

            options.SetLanguageVersion(MslCompilePin.LanguageVersion);
            options.SetFastMathEnabled(MslCompilePin.FastMathEnabled);
            options.SetPreserveInvariance(MslCompilePin.PreserveInvariance);
            return options;
        }
    }
}
