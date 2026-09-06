using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION S5, ENFORCED: run FXC on the HLSL the engine's own shader path emits, for every shipped program,
    /// and assert the reflected vertex input signature has contiguous <c>TEXCOORD</c> indices from 0. Section 8.3
    /// of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>.
    ///
    /// <para>
    /// WHY THIS IS NOT A <c>[GpuFact]</c> AND NOT NAMED "Golden". It needs no device: FXC is a CPU compiler, and
    /// the whole point is that HLSL which SPIRV-Cross emits happily and FXC rejects should fail on a Windows
    /// runner with no GPU work at all. A <c>[GpuFact]</c> would gate it behind <c>KE_GPU_TESTS</c> and a live
    /// device, and a name carrying "Golden" would put it in the matrix's golden filter, which is for device-backed
    /// pixel comparisons. It is instead selected by name in <c>cross-platform-gpu.yml</c>'s incumbent Windows
    /// leg, on every trigger, so it runs on every push that touches the path filter rather than only on the
    /// weekly schedule.
    /// </para>
    /// <para>
    /// OFF WINDOWS EVERY FXC CASE RETURNS EARLY, because <c>d3dcompiler</c> exists nowhere else. The
    /// device-free incident coverage below it does NOT return early: those cases read the emitted HLSL rather
    /// than compiling it, so they run on every leg and are what keeps the three workarounds asserted in the fast
    /// loop as well as on Windows.
    /// </para>
    /// <para>
    /// WHAT THE INCIDENTS WERE. SPIRV-Cross drops a vertex input the vertex stage does not read, and names
    /// each survivor <c>TEXCOORD&lt;location&gt;</c>, so dropping the middle of a declared range holes the
    /// emitted signature and FXC plus WARP miscompile it silently. The shadow depth vertex reads only Position
    /// and IModel0 to 3, and building that pipeline corrupted WARP so the MAIN model and splat passes rendered no
    /// colour. The terrain vertex had a fragment-unused interpolant below the live block, and the highest live
    /// interpolant then read garbage, blowing the terrain to flat white. The overlay mesh vertex declared the
    /// full ModelVertex stream and meant only Position and Color, holing its signature at TEXCOORD0 then
    /// TEXCOORD2, and that one was caught by this gate's first Windows run before it could corrupt anything.
    /// All three workarounds STAY: the native
    /// backend uses the same SPIRV-Cross and the same FXC, so it inherits the same intolerance, and the Veldrid
    /// leg ships alongside indefinitely.
    /// </para>
    /// </summary>
    public sealed class D3D11FxcValidationTests
    {
        // ---- the Windows FXC leg -------------------------------------------------------------------------

        /// <summary>
        /// Every shipped graphics program compiles through the real path: cross-compiled under the pinned
        /// options, FXC-compiled at <c>vs_5_0</c> and <c>ps_5_0</c>, and its reflected vertex input signature
        /// checked for a hole. One test over the whole catalog rather than one per program, so a new renderer
        /// cannot ship a program that nothing compiles.
        /// </summary>
        [Fact]
        public void EveryShippedGraphicsProgram_SurvivesFxcWithAContiguousSignature()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) return;   // no d3dcompiler off Windows

            var failures = new List<string>();
            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                try
                {
                    KhaozEngineD3D11.ValidateShaderPair(
                        program.VertexGlsl, program.FragmentGlsl, program.Name);
                }
                catch (ShaderValidationException ex)
                {
                    failures.Add($"  {program.Name}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "FXC rejected HLSL that SPIRV-Cross emitted, or a vertex input signature is holed. This is the "
                + "class of failure that used to reach WARP and corrupt a frame instead of failing a build "
                + "(decision S5).\n" + string.Join("\n", failures));
        }

        /// <summary>The compute kernels, across the four cascade resolutions shipped code can reach.</summary>
        [Fact]
        public void EveryShippedComputeKernel_SurvivesFxc()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) return;

            var failures = new List<string>();
            foreach (ShippedComputeKernel kernel in ShippedShaderPrograms.ComputeKernels())
            {
                try
                {
                    KhaozEngineD3D11.ValidateComputeShader(kernel.ComputeGlsl, kernel.Name);
                }
                catch (ShaderValidationException ex)
                {
                    failures.Add($"  {kernel.Name}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "FXC rejected a cross-compiled compute kernel.\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// The leg actually runs FXC rather than waving sources through, proved with HLSL that cross-compiles
        /// fine and that FXC must reject. Without this, a validation that silently did nothing would pass every
        /// case above and read as coverage.
        /// </summary>
        [Fact]
        public void HlslThatFxcRejects_FailsTheLeg()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) return;

            // A compute kernel whose workgroup size exceeds the Shader Model 5.0 limit of 1024 threads per group.
            // Legal GLSL, legal SPIR-V, cross-compiles to legal-looking HLSL, and FXC refuses it. That makes it a
            // genuine "SPIRV-Cross says yes and FXC says no" case rather than a syntax error either could catch.
            const string tooManyThreads = @"#version 450
layout(local_size_x = 1024, local_size_y = 2, local_size_z = 1) in;
layout(set = 0, binding = 0) buffer Values { float Data[]; };
void main() { Data[gl_GlobalInvocationID.x] = 1.0; }";

            ShaderValidation.ValidateCompute(tooManyThreads, "oversized workgroup");   // the seam is happy
            Assert.Throws<ShaderValidationException>(
                () => KhaozEngineD3D11.ValidateComputeShader(tooManyThreads, "oversized workgroup"));
        }

        // ---- the incidents, device-free on every leg -----------------------------------------------------

        /// <summary>
        /// THE SHADOW INCIDENT, ASSERTED. Every shadow depth vertex declares the model pass's full instance
        /// stream and reads only a few of it, so without the sink SPIRV-Cross would drop the rest and hole the
        /// signature. What is checked is the emitted HLSL's own input semantics, so this holds on macOS and Linux
        /// too and the sink cannot be removed anywhere without a red test.
        /// </summary>
        [Theory]
        [InlineData("ShadowDepth")]
        [InlineData("ShadowDepthDissolve")]
        [InlineData("ShadowDepthDissolveInverted")]
        [InlineData("SkinnedShadowDepth")]
        public void TheShadowVertexSink_KeepsTheEmittedInputSignatureGapFree(string programName)
        {
            ShippedGraphicsProgram program = Program(programName);
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                program.VertexGlsl, program.FragmentGlsl, programName);

            uint[] inputs = Semantics(pair.VertexSource, "SPIRV_Cross_Input");

            Assert.NotEmpty(inputs);
            AssertContiguousFromZero(inputs, programName + " vertex inputs",
                "The negligible-but-live sink in ShaderSources.Shadow.cs is what keeps this contiguous: it reads "
                + "every declared input with a 1e-30 weight so SPIRV-Cross cannot drop one. Removing it holes the "
                + "signature, and a holed signature is what corrupted WARP so the main model and splat passes "
                + "rendered no colour at all.");
        }

        /// <summary>
        /// THE TERRAIN INCIDENT, ASSERTED. The splat vertex orders its outputs so the fragment-USED interpolants
        /// occupy a contiguous 0..5 block and the fragment-unused ones sit above at 6..8, and the fragment
        /// declares only the gap-free prefix. What made the terrain go flat white was a fragment-unused
        /// interpolant sitting BELOW the live block, being dropped, and leaving a gap the highest live
        /// interpolant then read garbage through.
        /// <para>
        /// Two things are checked, because either alone would pass the wrong shader. The fragment's own inputs
        /// must be gap-free from 0, and every one of them must be an output the vertex declares at the SAME
        /// index, which is what stops a future edit from reintroducing an unused interpolant low in the block and
        /// shifting the live ones up.
        /// </para>
        /// </summary>
        [Fact]
        public void TheTerrainInterpolantOrdering_KeepsTheFragmentInputsAGapFreePrefix()
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                ShaderSources.SplatVert, ShaderSources.SplatFrag, "Splat");

            uint[] vertexOutputs = Semantics(pair.VertexSource, "SPIRV_Cross_Output");
            uint[] fragmentInputs = Semantics(pair.FragmentSource, "SPIRV_Cross_Input");

            Assert.NotEmpty(fragmentInputs);
            AssertContiguousFromZero(fragmentInputs, "Splat fragment inputs",
                "The interpolant ORDERING in ShaderSources.Terrain.cs is what keeps this contiguous: the "
                + "fragment-used outputs occupy 0..5 and the fragment-unused ones sit above at 6..8. Putting a "
                + "fragment-unused interpolant below the live block is what blew the terrain to flat white.");

            Assert.All(fragmentInputs, index => Assert.Contains(index, vertexOutputs));
        }

        /// <summary>
        /// THE SAME RULE ON THE TILE-GROUND PAIR, which states it in its own comment and is the newest pass to
        /// depend on it. It takes the terrain lesson one step further: the fragment reads EVERY interpolant the
        /// vertex declares, so the two signatures are the same set and there is no fragment-unused output for
        /// SPIRV-Cross to drop in the first place. That equality is asserted rather than just the contiguity,
        /// because contiguity alone would still pass a future edit that added an unused output above the live
        /// block and then, one edit later, below it.
        /// </summary>
        [Fact]
        public void TheTileGroundInterpolants_AreGapFreeAndEntirelyRead()
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                ShaderSources.TileGroundVert, ShaderSources.TileGroundFrag, "TileGround");

            uint[] vertexOutputs = Semantics(pair.VertexSource, "SPIRV_Cross_Output");
            uint[] fragmentInputs = Semantics(pair.FragmentSource, "SPIRV_Cross_Input");

            Assert.NotEmpty(fragmentInputs);
            AssertContiguousFromZero(fragmentInputs, "TileGround fragment inputs",
                "ShaderSources.TileGround.cs states this rule in its own comment: the fragment reads every "
                + "interpolant the vertex emits, so the pixel-input block is gap-free by construction. A hole "
                + "there is what blew the terrain to flat white on FXC and WARP.");
            AssertContiguousFromZero(vertexOutputs, "TileGround vertex outputs",
                "An output the fragment does not read would be dropped here and leave the gap this pair is "
                + "written to avoid.");

            Assert.Equal(vertexOutputs, fragmentInputs);
        }

        /// <summary>
        /// THE OVERLAY INCIDENT, ASSERTED. The overlay mesh vertex declares the full ModelVertex stream so the
        /// model pass's own vertex buffer binds unchanged, and means only Position and Color. Normal sits at
        /// location 1 between them, so dropping it holed the emitted signature at TEXCOORD0 then TEXCOORD2. This
        /// one never reached a frame: the FXC gate's first Windows run caught it, and this case is what keeps it
        /// caught on every leg rather than only on the path-gated Windows one.
        /// </summary>
        [Fact]
        public void TheOverlayMeshVertexSink_KeepsTheEmittedInputSignatureGapFree()
        {
            ShippedGraphicsProgram program = Program("OverlayMesh");
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                program.VertexGlsl, program.FragmentGlsl, program.Name);

            uint[] inputs = Semantics(pair.VertexSource, "SPIRV_Cross_Input");

            Assert.NotEmpty(inputs);
            AssertContiguousFromZero(inputs, "OverlayMesh vertex inputs",
                "The negligible-but-live sink in ShaderSources.Post.cs is what keeps this contiguous: it reads "
                + "Normal, TexCoord and Tangent with a 1e-30 weight so SPIRV-Cross cannot drop them. Without it "
                + "the signature is TEXCOORD0 then TEXCOORD2, which FXC and WARP miscompile silently.");
        }

        /// <summary>
        /// THE CATALOG-WIDE SWEEP. The three named cases above pin history: shadow, terrain and overlay mesh are
        /// the specific programs that already holed a signature, each with its own fix and its own regression
        /// test. This sweep is for the next one: it walks every program in <c>ShippedShaderPrograms</c>
        /// rather than only the three with a named incident, so a future program's holed signature is caught here
        /// instead of needing the path-gated Windows FXC leg to find it, the way the overlay mesh's own hole was
        /// first caught only on that leg's first run.
        /// </summary>
        [Fact]
        public void EveryCatalogProgram_EmitsAGapFreeVertexInputSignature()
        {
            var failures = new List<string>();
            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                    program.VertexGlsl, program.FragmentGlsl, program.Name);

                uint[] inputs = Semantics(pair.VertexSource, "SPIRV_Cross_Input");
                var want = Enumerable.Range(0, inputs.Length).Select(i => (uint)i).ToArray();
                if (!inputs.SequenceEqual(want))
                {
                    failures.Add($"  {program.Name}: found ["
                        + string.Join(", ", inputs.Select(i => "TEXCOORD" + i.ToString(CultureInfo.InvariantCulture)))
                        + "], expected contiguous from 0");
                }
            }

            Assert.True(failures.Count == 0,
                "A program's vertex input signature is holed, the same class of bug that corrupted WARP for the "
                + "shadow and overlay mesh passes and blew the terrain to flat white before each got its own sink "
                + "or ordering fix.\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// The parse the contiguity cases above rest on actually finds something, proved against a shader whose
        /// signature is known by construction. A regex that silently matched nothing would make every contiguity
        /// assertion above vacuous, and <c>Assert.NotEmpty</c> at each site would not say why.
        /// </summary>
        [Fact]
        public void TheEmittedSignatureParse_ReadsTheSemanticsItClaimsTo()
        {
            const string vert = @"#version 450
layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 0) out vec2 vUv;
void main() { vUv = TexCoord; gl_Position = vec4(Position, 1); }";
            const string frag = @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;
void main() { oColor = vec4(vUv, 0, 1); }";

            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(vert, frag, "parse probe");

            Assert.Equal(new uint[] { 0, 1 }, Semantics(pair.VertexSource, "SPIRV_Cross_Input"));
            Assert.Equal(new uint[] { 0 }, Semantics(pair.VertexSource, "SPIRV_Cross_Output"));
            Assert.Equal(new uint[] { 0 }, Semantics(pair.FragmentSource, "SPIRV_Cross_Input"));
        }

        // ---- helpers -------------------------------------------------------------------------------------

        static ShippedGraphicsProgram Program(string name)
            => ShippedShaderPrograms.GraphicsPrograms()
                .Single(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        // The TEXCOORD indices declared inside one of SPIRV-Cross's generated stage-interface structs, sorted.
        // A text read of the EMITTED HLSL rather than a reflection of the compiled DXBC, deliberately: the
        // reflection is Windows-only and the shipped checks use it, while this runs everywhere and is what keeps
        // the three workarounds asserted in the fast loop. The struct body is taken up to its closing brace so a
        // later struct's members cannot leak in.
        static uint[] Semantics(string hlsl, string structName)
        {
            int start = hlsl.IndexOf("struct " + structName, StringComparison.Ordinal);
            if (start < 0) return Array.Empty<uint>();
            int open = hlsl.IndexOf('{', start);
            int close = open < 0 ? -1 : hlsl.IndexOf('}', open);
            if (close < 0) return Array.Empty<uint>();

            return Regex.Matches(hlsl.Substring(open, close - open), @":\s*TEXCOORD(\d+)\s*;")
                .Select(m => uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .OrderBy(i => i)
                .ToArray();
        }

        static void AssertContiguousFromZero(uint[] indices, string what, string why)
        {
            var want = Enumerable.Range(0, indices.Length).Select(i => (uint)i).ToArray();
            Assert.True(indices.SequenceEqual(want),
                $"{what} are not contiguous from 0: found "
                + $"[{string.Join(", ", indices.Select(i => "TEXCOORD" + i.ToString(CultureInfo.InvariantCulture)))}], "
                + $"expected [{string.Join(", ", want.Select(i => "TEXCOORD" + i.ToString(CultureInfo.InvariantCulture)))}]. "
                + why);
        }
    }
}
