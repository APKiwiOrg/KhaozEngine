using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Veldrid;
using Veldrid.SPIRV;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// PARITY WITH THE INCUMBENT, ASSERTED RATHER THAN REMEMBERED. Every shipped stage is compiled twice in one
    /// process, once through the engine's own pinned front end and once through a faithful replication of the
    /// incumbent Veldrid device's SPIR-V production, and the two modules are compared byte for byte. Section 12.1
    /// of <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>, work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para>
    /// WHY IT IS A STANDING TEST AND NOT ONLY THE ONE-OFF MEASUREMENT. The measurement taken on 2026-08-08 is
    /// what LICENSED the committed <c>vulkan</c> goldens carrying over to the native backend without a rebake,
    /// and it stands as the historical record of that decision. It cannot license anything that happens later.
    /// The equality it found is not true by construction: <see cref="SpirvFrontEndPin"/> governs every
    /// ENGINE-OWNED front-end call and the incumbent <c>VeldridGpuDevice</c> deliberately keeps the library
    /// defaults, so the two sets are maintained independently and a flip of one pin value moves one side of an
    /// equality nothing was checking. This test is that check, and it costs one in-process compile pass.
    /// </para>
    /// <para>
    /// WHAT A RED RUN MEANS, AND THE FIRST INSTINCT IS THE WRONG ONE. It means the pin and the incumbent's
    /// defaults have DIVERGED. The 36 committed <c>*.vulkan.txt</c> goldens were baked on the incumbent's
    /// emission and are asserted against the native backend's, so from that moment they are baked against one
    /// emission and asserted against another, and every one of them is testing the compiler rather than the
    /// backend. The fix is to decide WHICH SIDE moved and whether that move was deliberate. Re-baking
    /// <c>VulkanSpirvByteEqualityTests</c>' hash table is NOT the fix and turns the failure green while leaving
    /// the goldens standing on a claim that is no longer true. A deliberate move on the engine's side is a
    /// deliberate golden rebake as well.
    /// </para>
    /// <para>
    /// IT IS THE OTHER HALF OF <see cref="VulkanSpirvByteEqualityTests"/> AND NEITHER SUBSTITUTES FOR THE OTHER.
    /// That test is a DRIFT detector baked from this path's own emission, so it catches a shader source or a pin
    /// value moving and cannot see the incumbent at all. This one compares the two paths and says nothing about
    /// whether either has moved since the table was baked. A wrong emission produced identically by both paths
    /// passes here and fails there. A right emission that only the engine produces passes there and fails here.
    /// </para>
    /// <para>
    /// THE TWO CALL SHAPES DIFFER IN EXACTLY ONE ARGUMENT, the diagnostic FILE NAME, which the incumbent leaves
    /// null and the engine sets to <c>&lt;label&gt;.&lt;stage&gt;</c>. So this test is also what keeps that
    /// difference from mattering: the name reaches the module only when debug information is generated, and
    /// <see cref="SpirvFrontEndPin.Debug"/> is what turns that off. Flipping it fails here first, which is the
    /// right place for it to fail.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND ON EVERY LEG, like the drift table beside it. The front end runs on the CPU through a
    /// native that ships per RID and already runs on macOS and Linux, so this is a plain <c>[Fact]</c> in the
    /// fast <c>ci.yml</c> loop rather than a <c>[GpuFact]</c>, and it runs on the legs with no Vulkan loader at
    /// all.
    /// </para>
    /// </summary>
    public sealed class VulkanSpirvIncumbentParityTests
    {
        /// <summary>
        /// EVERY SHIPPED STAGE, UNDER BOTH SETS, BYTE FOR BYTE. The stage count is asserted alongside the
        /// equality so a catalog that stopped enumerating cannot pass this by comparing nothing.
        /// </summary>
        [Fact]
        public void EveryShippedStage_CompilesToTheSameSpirvUnderThePinAndTheIncumbentsDefaults()
        {
            var problems = new List<string>();
            int compared = 0;

            foreach ((string key, string glsl, string label, GpuShaderStages stage) in EveryShippedStage())
            {
                compared++;

                byte[] pinned = SpirvFrontEnd.ToSpirv(glsl, stage, label);
                byte[] incumbent = TheIncumbentsOwnCall(glsl, stage);

                if (!pinned.AsSpan().SequenceEqual(incumbent))
                {
                    problems.Add($"  {key}: the pinned front end emitted {pinned.Length} byte(s) and the "
                        + $"incumbent's defaults emitted {incumbent.Length}, and the modules are not equal.");
                }
            }

            Assert.Equal(76, compared);
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {compared} shipped stages no longer compile to the same SPIR-V under "
                + $"{nameof(SpirvFrontEndPin)} as under the incumbent Veldrid device's own defaults, so the two "
                + "have DIVERGED. The 36 committed *.vulkan.txt goldens were baked on the incumbent's emission "
                + "and are asserted against the native backend's, so they are now baked against one emission and "
                + "asserted against another, and each of them tests the compiler rather than the backend. Decide "
                + "which side moved and whether it moved on purpose. Re-baking the hash table in "
                + $"{nameof(VulkanSpirvByteEqualityTests)} is not the fix here: it turns this green and leaves "
                + "the goldens standing on a claim that stopped being true.\n"
                + string.Join("\n", problems));
        }

        // ---- the two sides ---------------------------------------------------------------------------------

        // THE INCUMBENT'S OWN CALL, replicated argument for argument, and since #640 it IS the call, not a
        // replica of one: both of VeldridGpuDevice's shader paths compile each stage's GLSL themselves under
        // GlslCompileOptions.Default before handing the module on, and the graphics one passes this exact null
        // file name. It is also what Veldrid.SPIRV's CreateFromSpirv would do with GLSL bytes on a Vulkan
        // device, where the short path hands the compiled SPIR-V straight to vkCreateShaderModule with no
        // cross-compilation. GlslCompileOptions.Default is the library's set and is deliberately NOT routed
        // through the pin: the whole point is that the two are maintained separately and asserted equal.
        static byte[] TheIncumbentsOwnCall(string glsl, GpuShaderStages stage)
            => SpirvCompilation.CompileGlslToSpirv(glsl, null, ToVeldrid(stage), GlslCompileOptions.Default)
                .SpirvBytes;

        static ShaderStages ToVeldrid(GpuShaderStages stage) => stage switch
        {
            GpuShaderStages.Vertex => ShaderStages.Vertex,
            GpuShaderStages.Fragment => ShaderStages.Fragment,
            GpuShaderStages.Compute => ShaderStages.Compute,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No shipped source is this stage."),
        };

        // The same 76-stage walk the drift table covers, yielding the LABEL the shipped path passes as well as
        // the source, because the label is the one argument the two call shapes disagree on.
        static IEnumerable<(string Key, string Glsl, string Label, GpuShaderStages Stage)> EveryShippedStage()
        {
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                yield return (program.Name + ".vertex", program.VertexGlsl, program.Name,
                    GpuShaderStages.Vertex);
                yield return (program.Name + ".fragment", program.FragmentGlsl, program.Name,
                    GpuShaderStages.Fragment);
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
                yield return (kernel.Name + ".compute", kernel.ComputeGlsl, kernel.Name, GpuShaderStages.Compute);
        }
    }
}
