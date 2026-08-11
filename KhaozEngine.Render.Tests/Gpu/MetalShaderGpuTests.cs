using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SHADER PATH'S DEVICE HALF, ON REAL HARDWARE. Almost all of row 9 is device-free and asserted over
    /// every shipped program on every leg (<see cref="MetalShaderIndexTableTests"/>), which leaves exactly two
    /// claims a device has to settle: that Metal ACCEPTS the MSL this engine emits under the pinned compile
    /// options, and that the entry-point name read out of that MSL is the name the compiled library actually
    /// carries.
    ///
    /// <para>
    /// THE SECOND ONE IS THE INTERESTING ONE (M-S5). SPIRV-Cross renames the GLSL <c>main</c> because <c>main</c>
    /// is reserved in MSL, and the incumbent gets the resulting name from a Veldrid layer this backend does not
    /// have. So the backend reads it, and <c>-newFunctionWithName:</c> answering non-nil is the only thing that
    /// proves the read was right. A wrong name is not a compile error: the library builds and the function is
    /// nil, which is why that is a separate refusal in <c>MetalShaderCompiler</c> rather than folded into the
    /// compile failure.
    /// </para>
    /// <para>
    /// AND THIS IS WHERE THE MANAGED-TO-NATIVE <c>NSString</c> DIRECTION RUNS. It landed with this row and had no
    /// caller before it, and <c>NSString</c>'s own header records why an unexercised interop prototype is the one
    /// kind of dead code in that folder that can corrupt memory.
    /// </para>
    /// <para>
    /// Dormant off macOS rather than skipped, which is phase 3's row-19 lesson: under <c>KE_GPU_TESTS=1</c> the
    /// Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a failure.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalShaderGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalShaderGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Every shipped graphics program compiles on the device, under the pinned options, with both stages'
        /// functions resolving by the name the emission gave them. This is the whole corpus rather than a sample,
        /// because the failure it guards against is per-program: one shader using a construct Metal rejects under
        /// <c>languageVersion</c> 3.2 would be invisible in a sample of three.
        /// </summary>
        [GpuFact]
        public void EveryShippedGraphicsProgram_CompilesOnTheDeviceUnderThePinnedOptions()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            var stopwatch = Stopwatch.StartNew();
            int programs = 0;

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                using IGpuShaderSet set = device.Factory.CreateShadersFromSpirv(
                    program.VertexGlsl, program.FragmentGlsl);

                var metal = Assert.IsType<MetalShaderSet>(set);
                Assert.Equal(2, metal.Stages.Count);

                // Non-nil functions for both stages is the M-S5 claim. A library that compiled with a name the
                // parse got wrong would give a nil here rather than a compile error.
                Assert.False(metal.FunctionFor(MetalShaderStage.Vertex).IsNull, program.Name + " vertex");
                Assert.False(metal.FunctionFor(MetalShaderStage.Fragment).IsNull, program.Name + " fragment");

                // The table came through with the shader set rather than being rebuilt, which is what rows 10, 11
                // and 13 read.
                Assert.True(metal.Table.Count > 0, program.Name + " bound nothing at all");
                programs++;
            }

            stopwatch.Stop();
            _output.WriteLine($"{programs} graphics programs compiled in {stopwatch.ElapsedMilliseconds} ms");
            Assert.True(programs > 30, "the shipped-program walk found almost nothing.");
        }

        /// <summary>
        /// Every shipped compute kernel compiles, and reports the workgroup size its own module declares. The
        /// size is what <c>dispatchThreadgroups</c>'s <c>threadsPerThreadgroup</c> is built from, and MSL does not
        /// carry it, so a kernel that compiled and reported nothing would dispatch wrongly with no error.
        /// </summary>
        [GpuFact]
        public void EveryShippedComputeKernel_CompilesAndReportsItsOwnWorkgroupSize()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            int kernels = 0;

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                using IGpuComputeShader shader = device.Factory.CreateComputeShaderFromSpirv(kernel.ComputeGlsl);

                var metal = Assert.IsType<MetalComputeShader>(shader);
                Assert.False(metal.Function.IsNull, kernel.Name);
                Assert.True(shader.ThreadGroupSizeX >= 1 && shader.ThreadGroupSizeY >= 1
                    && shader.ThreadGroupSizeZ >= 1, kernel.Name + " reported a zero workgroup dimension");
                kernels++;
            }

            _output.WriteLine($"{kernels} compute kernels compiled");
            Assert.True(kernels > 0, "the shipped-kernel walk found nothing.");
        }

        /// <summary>
        /// MSL Metal REJECTS carries Metal's own message rather than an unexplained nil. The MSL that fails is
        /// SPIRV-Cross output rather than anything the caller wrote, so the diagnostic is the only route back to
        /// the GLSL, which is why this asserts on the content of the message and not just on the throw.
        /// <para>
        /// DRIVEN THROUGH A HAND-BUILT PROGRAM, because no shipped GLSL can reach it: a source that is not GLSL
        /// dies in the front end (the row below), and a source that IS GLSL cross-compiles to MSL Metal accepts.
        /// So the device-refusal branch has no coverage at all unless the emission is constructed, and an
        /// uncovered error path is one that says something wrong the first time it fires.
        /// </para>
        /// </summary>
        [GpuFact]
        public void MslMetalRejects_CarriesMetalsOwnDiagnostic()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            var metalDevice = (MetalGpuDevice)device;
            var compiler = new MetalShaderCompiler(metalDevice.Handle, metalDevice.Liveness, metalDevice.IndexTables);

            ShaderValidationException error = Rejected(
                compiler, HandBuilt("#include <metal_stdlib>\nthis is not MSL\n", "main0"));

            _output.WriteLine(error.Message);
            Assert.Contains("rejected the emitted MSL", error.Message, StringComparison.Ordinal);
            Assert.Contains("SPIRV-Cross output", error.Message, StringComparison.Ordinal);

            // Metal's own words came through, which is the whole point: without them a shader typo is "nil".
            Assert.Contains("error", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// AND A LIBRARY THAT COMPILES UNDER A NAME THE PARSE GOT WRONG IS A DIFFERENT FAILURE (M-S5), which is
        /// why <c>MetalShaderCompiler</c> keeps the two apart. This one is not a compile error at all: the
        /// library builds and <c>-newFunctionWithName:</c> answers nil.
        /// </summary>
        [GpuFact]
        public void AnEntryPointNameTheLibraryDoesNotCarry_IsItsOwnFailure()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            var metalDevice = (MetalGpuDevice)device;
            var compiler = new MetalShaderCompiler(metalDevice.Handle, metalDevice.Liveness, metalDevice.IndexTables);

            const string valid = "#include <metal_stdlib>\nusing namespace metal;\n"
                + "kernel void main0(uint i [[thread_position_in_grid]]) { }\n";

            ShaderValidationException error = Rejected(compiler, HandBuilt(valid, "notTheEntryPoint"));

            _output.WriteLine(error.Message);
            Assert.Contains("carries no function of that name", error.Message, StringComparison.Ordinal);
            Assert.Contains("M-S5", error.Message, StringComparison.Ordinal);
        }

        /// <summary>GLSL the FRONT END rejects never reaches the device, and its message names the stage that
        /// failed. The other half of the pair above, so a reader can tell the two failures apart.</summary>
        [GpuFact]
        public void GlslTheFrontEndRejects_NeverReachesTheDevice()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => device.Factory.CreateShadersFromSpirv("not glsl at all", "nor this"));

            _output.WriteLine(error.Message);
            Assert.Contains("GLSL to SPIR-V failed", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("rejected the emitted MSL", error.Message, StringComparison.Ordinal);
        }

        // The compile driven from a method that is itself macOS-only, because CA1416's guard analysis does not
        // follow [SupportedOSPlatformGuard] into a lambda, and the assertion needs one.
        [SupportedOSPlatform("macos")]
        static ShaderValidationException Rejected(MetalShaderCompiler compiler, MetalMslProgram program)
            => Assert.Throws<ShaderValidationException>(() => compiler.CompileOnMacOs(program));

        // A program with no resources at all, so the index table is trivially empty and the only thing under test
        // is the device compile. Built directly rather than through MetalShaderBuild, which is the point.
        static MetalMslProgram HandBuilt(string msl, string entryPointName)
            => new(
                new[] { new MetalMslStage(MetalShaderStage.Compute, entryPointName, msl) },
                MetalShaderIndexTable.Build(
                    Array.Empty<GpuResourceLayoutDescription>(),
                    new[]
                    {
                        new MetalMslStageJoin(MetalShaderStage.Compute, MinimalSpirv(),
                            Array.Empty<MetalMslArgument>()),
                    },
                    "hand-built"));

        // A SPIR-V header and nothing else, which is a valid input to the decoration walk: a module declaring no
        // resources decorates nothing, and the walk needs no types, no functions and no entry point.
        static byte[] MinimalSpirv()
        {
            uint[] words = { 0x07230203, 0x00010000, 0, 1, 0 };
            var bytes = new byte[words.Length * 4];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>
        /// The disposal contract: a set releases its functions and libraries once, a second Dispose is a no-op,
        /// and asking a disposed set for a function says so rather than handing back a released handle. Nothing
        /// downstream should ever do that, which is exactly why it is checked here rather than assumed.
        /// </summary>
        [GpuFact]
        public void ADisposedShaderSet_RefusesRatherThanHandingBackAReleasedFunction()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            ShippedGraphicsProgram program = D3D11ShaderProgramCatalog.GraphicsPrograms().First();

            IGpuShaderSet set = device.Factory.CreateShadersFromSpirv(program.VertexGlsl, program.FragmentGlsl);
            var metal = (MetalShaderSet)set;

            set.Dispose();
            set.Dispose();

            Assert.Empty(metal.Stages);
            Assert.Contains("disposed",
                Assert.Throws<InvalidOperationException>(
                    () => metal.FunctionFor(MetalShaderStage.Vertex)).Message,
                StringComparison.Ordinal);
        }

        static IGpuDevice CreateHeadless() => new MetalBackendProvider().CreateHeadless().Device;

        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                // KE_METAL_REQUIRED=1 turns this into a throw on the leg that declared a device mandatory.
                MetalDormancy.ThrowIfRequired("this is not macOS at all");
                _output.WriteLine("dormant: not macOS, so there is no Metal device to compile shaders on.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            MetalDormancy.ThrowIfRequired(missing);
            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }
}
