using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Veldrid;
using Veldrid.SPIRV;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-S4: PARITY WITH THE INCUMBENT'S MSL, ASSERTED RATHER THAN REMEMBERED. Every shipped program is
    /// cross-compiled to MSL twice in one process, once through the engine's own pinned back end and once through
    /// a faithful replication of what the incumbent Veldrid Metal device does, and the emitted text is compared
    /// byte for byte. Section 12.3 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para>
    /// THIS IS THE FACT THAT LICENSES "NO REBAKE". The 36 committed <c>*.metal.txt</c> goldens were baked on the
    /// incumbent's emission, and the native backend is going into that same golden family as a guest. If the two
    /// paths emit the same MSL then the goldens are testing the BACKEND, which is the point. If they do not, every
    /// one of them is testing the compiler instead, silently, and a diff nobody can explain shows up in whichever
    /// pass happens to be most sensitive.
    /// </para>
    /// <para>
    /// THE EQUALITY IS NOT TRUE BY CONSTRUCTION, which is exactly why it is checked.
    /// <see cref="MslCrossCompilePin"/> governs every engine-owned MSL emission, while <c>VeldridGpuDevice</c>
    /// hands GLSL to the three-argument <c>CreateFromSpirv</c>, which constructs <c>new CrossCompileOptions()</c>
    /// and forwards it. The two sets are maintained INDEPENDENTLY, so a flip of one pin value moves one side of
    /// an equality nothing else was watching.
    /// </para>
    /// <para>
    /// AND IT IS A STANDING TEST RATHER THAN ONLY THE ONE-OFF MEASUREMENT, which is phase 3's upgrade inherited
    /// (<see cref="VulkanSpirvIncumbentParityTests"/> made the same move). The measurement taken when this landed
    /// is the historical record of the decision and cannot license anything that happens afterwards. This costs
    /// one in-process cross-compile pass and turns the claim into a fact about the current tree.
    /// </para>
    /// <para>
    /// WHAT A RED RUN MEANS, AND THE FIRST INSTINCT IS THE WRONG ONE. It means the pin and the incumbent's
    /// defaults have DIVERGED. Re-baking the hash table in <see cref="MetalMslByteEqualityTests"/> is NOT the fix:
    /// it turns this green and leaves the goldens standing on a claim that stopped being true. Decide which side
    /// moved and whether it moved on purpose, and a deliberate move on the engine's side is a deliberate golden
    /// rebake too.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND ON EVERY LEG. SPIRV-Cross runs on the CPU through a native that ships per RID, so this is
    /// a plain <c>[Fact]</c> in the fast <c>ci.yml</c> loop and it runs on Linux and Windows where there is no
    /// Metal at all.
    /// </para>
    /// </summary>
    public sealed class MetalMslIncumbentParityTests
    {
        readonly ITestOutputHelper _output;

        public MetalMslIncumbentParityTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// EVERY SHIPPED PROGRAM, UNDER BOTH OPTION SETS, BYTE FOR BYTE. The comparison count is asserted
        /// alongside the equality so a catalog that stopped enumerating cannot pass this by comparing nothing.
        /// </summary>
        [Fact]
        public void EveryShippedProgram_EmitsTheSameMslUnderThePinAndTheIncumbentsDefaults()
        {
            var problems = new List<string>();
            int compared = 0;

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                byte[] vertexSpirv = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name);
                byte[] fragmentSpirv = SpirvFrontEnd.ToSpirv(
                    program.FragmentGlsl, GpuShaderStages.Fragment, program.Name);

                CrossCompiledPair pinned = SpirvCrossCompile.VertexFragmentToMsl(
                    vertexSpirv, fragmentSpirv, program.Name);
                VertexFragmentCompilationResult incumbent = TheIncumbentsOwnPairCall(vertexSpirv, fragmentSpirv);

                compared += 2;
                Compare(problems, program.Name + ".vertex", pinned.VertexSource, incumbent.VertexShader);
                Compare(problems, program.Name + ".fragment", pinned.FragmentSource, incumbent.FragmentShader);
            }

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                byte[] spirv = SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name);

                CrossCompiledCompute pinned = SpirvCrossCompile.ComputeToMsl(spirv, kernel.Name);
                ComputeCompilationResult incumbent = TheIncumbentsOwnComputeCall(spirv);

                compared++;
                Compare(problems, kernel.Name + ".compute", pinned.ComputeSource, incumbent.ComputeShader);
            }

            _output.WriteLine($"compared {compared} emitted stages, {problems.Count} differ");

            Assert.Equal(76, compared);
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {compared} shipped stages no longer emit the same MSL under "
                + $"{nameof(MslCrossCompilePin)} as under the incumbent Veldrid Metal device's own defaults, so "
                + "the two have DIVERGED. The committed *.metal.txt goldens were baked on the incumbent's "
                + "emission and the native backend is a guest in that family, so from this moment they are baked "
                + "against one emission and asserted against another and each of them tests the compiler rather "
                + "than the backend. Decide which side moved and whether it moved on purpose. Re-baking the hash "
                + $"table in {nameof(MetalMslByteEqualityTests)} is not the fix here: it turns this green and "
                + "leaves the goldens standing on a claim that stopped being true.\n"
                + string.Join("\n", problems));
        }

        static void Compare(List<string> problems, string key, string pinned, string incumbent)
        {
            if (string.Equals(pinned, incumbent, StringComparison.Ordinal)) return;
            problems.Add($"  {key}: the pin emitted {pinned.Length} character(s) and the incumbent's defaults "
                + $"emitted {incumbent.Length}, and the two are not equal.");
        }

        // ---- the incumbent's side ---------------------------------------------------------------------------
        //
        // Replicated argument for argument. VeldridGpuDevice hands GLSL to the three-argument CreateFromSpirv,
        // which compiles the front end internally and then calls exactly these two members with
        // `new CrossCompileOptions()`. The default-constructed options are deliberately NOT routed through the
        // pin: the whole point is that the two sets are maintained separately and asserted equal.

        static VertexFragmentCompilationResult TheIncumbentsOwnPairCall(byte[] vertexSpirv, byte[] fragmentSpirv)
            => SpirvCompilation.CompileVertexFragment(
                vertexSpirv, fragmentSpirv, CrossCompileTarget.MSL, new CrossCompileOptions());

        static ComputeCompilationResult TheIncumbentsOwnComputeCall(byte[] computeSpirv)
            => SpirvCompilation.CompileCompute(computeSpirv, CrossCompileTarget.MSL, new CrossCompileOptions());
    }
}
