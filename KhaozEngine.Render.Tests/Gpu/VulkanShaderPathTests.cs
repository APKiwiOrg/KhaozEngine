using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Render3D.Internal;
using Silk.NET.Shaderc;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE-FREE HALF OF THE NATIVE VULKAN SHADER PATH (decisions V-S1, V-S2 and V-S7, section 12 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>): the pinned front-end options, the module
    /// dedup, the two shader wrappers and their deliberately empty disposal. Work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>EVERYTHING INTERESTING IS ABOVE THE DRIVER.</b> The two native calls are a create and a destroy
    /// with nothing to decide, and every decision the row makes (which bytes are compiled, whether two programs
    /// share a module, what a wrapper's Dispose does, when the handles go) sits in engine types that run on a
    /// machine with no Vulkan loader. That is why these are plain facts rather than <c>[GpuFact]</c>s.</para>
    /// </summary>
    public sealed class VulkanShaderPathTests
    {
        const string VertGlsl =
            "#version 450\nlayout(location=0) in vec3 P;\nvoid main(){gl_Position=vec4(P,1);}";
        const string FragGlsl = "#version 450\nlayout(location=0) out vec4 C;\nvoid main(){C=vec4(1);}";
        const string OtherFragGlsl = "#version 450\nlayout(location=0) out vec4 C;\nvoid main(){C=vec4(0.5);}";
        const string ComputeGlsl =
            "#version 450\nlayout(local_size_x=8, local_size_y=4, local_size_z=2) in;\nvoid main(){}";

        // ---- the pinned front-end options (V-S2) ---------------------------------------------------------

        /// <summary>
        /// The pinned values, stated here as well as in the pin, so flipping one is a two-file change with a
        /// visible diff rather than an edit inside a doc comment. The identity string is what a derived cache key
        /// uses to tell artefacts emitted under one set from artefacts emitted under another, so it MUST move when
        /// a value does, and it is DERIVED from the values for exactly that reason.
        /// <para>
        /// The whole token is asserted rather than its parts, because it is a live component of the Direct3D 11
        /// DXBC cache key: a rendering change that means the same thing (an added field, a reordered pair,
        /// <c>true</c> for <c>1</c>) silently orphans every warm entry. Change it only alongside a value above,
        /// which is the case where orphaning them is the POINT.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFrontEndOptions_ArePinnedToTheSetTheShippedShadersWereCompiledUnder()
        {
            Assert.False(SpirvFrontEndPin.Debug);
            Assert.Equal(0, SpirvFrontEndPin.MacroCount);
            Assert.Equal("main", SpirvFrontEndPin.EntryPoint);

            // THE THREE VALUES 18.0.0 ADDED, and they are the reason this test grew rather than merely moved.
            // The outgoing toolchain chose all three underneath a two-field options object, so the engine shipped
            // every module it has ever shipped under an optimisation level, a client API and a SPIR-V version
            // that were recorded nowhere. Performance is the level the incumbent was measured to have been using
            // (section 2.3 result 3 of docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md), and Vulkan 1.0 with
            // SPIR-V 1.0 is what every committed module's header actually declares, so none of the three is a
            // preference. They are the status quo written down, and pinning them is what turns a shaderc default
            // moving under the engine into three moved hash tables instead of a quiet rebuild of every shader.
            Assert.Equal(OptimizationLevel.Performance, SpirvFrontEndPin.Optimization);
            Assert.Equal(TargetEnv.Vulkan, SpirvFrontEndPin.TargetEnvironment);
            Assert.Equal((uint)EnvVersion.Vulkan10, SpirvFrontEndPin.TargetEnvironmentVersion);
            Assert.Equal(SpirvVersion.Shaderc10, SpirvFrontEndPin.SpirvTarget);

            Assert.Equal(
                "shaderc/spirv;debug=0;opt=Performance;env=Vulkan.4194304;spirv=Shaderc10;macros=0;entryPoint=main",
                SpirvFrontEndPin.Identity);
        }

        /// <summary>
        /// The identity string goes into a cache key line by line, so it has to BE one line: a value carrying a
        /// newline would split the key's own framing and let two different option sets hash the same. Same
        /// requirement <c>HlslCrossCompilePin.Identity</c> carries, and it is stated separately from the value
        /// above because it is a property of the SHAPE rather than of the current contents.
        /// </summary>
        [Fact]
        public void ThePinnedOptionsIdentity_IsASingleLineToken()
        {
            Assert.NotEmpty(SpirvFrontEndPin.Identity);
            Assert.DoesNotContain('\n', SpirvFrontEndPin.Identity);
            Assert.DoesNotContain('\r', SpirvFrontEndPin.Identity);
            Assert.StartsWith("shaderc/spirv", SpirvFrontEndPin.Identity, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE TWO PINS ARE DISTINGUISHABLE, which matters because both go into the same Direct3D 11 cache key
        /// one after the other. Two tokens that could ever be equal, or one that is a prefix of the other in a way
        /// a reader could confuse, would make the key ambiguous about which half moved.
        /// </summary>
        [Fact]
        public void TheFrontAndBackEndPins_AreDifferentTokens()
            => Assert.NotEqual(HlslCrossCompilePin.Identity, SpirvFrontEndPin.Identity);

        // ---- module dedup (V-S7) -------------------------------------------------------------------------

        /// <summary>
        /// TWO PROGRAMS SHARING A STAGE SHARE ITS <c>VkShaderModule</c>, which is the whole of decision V-S7's
        /// module half. Asserted on the HANDLE and on the number of native creates, because a cache that returned
        /// the right handle after creating a second module would leak one for the device's life.
        /// </summary>
        [Fact]
        public void TwoProgramsWithTheSameVertexSource_ShareOneModule()
        {
            var api = new FakeVulkanShaderApi();
            var cache = new VulkanShaderModuleCache(api);

            var first = new VulkanShaderSet(cache, VertGlsl, FragGlsl, "first");
            var second = new VulkanShaderSet(cache, VertGlsl, OtherFragGlsl, "second");

            Assert.Equal(first.VertexModule, second.VertexModule);
            Assert.NotEqual(first.FragmentModule, second.FragmentModule);
            Assert.Equal(3, api.Created.Count);
            Assert.Equal(4, cache.RequestCount);
            Assert.Equal(3, cache.DistinctModuleCount);
        }

        /// <summary>
        /// AND THE LABEL DOES NOT SPLIT THE CACHE. The compile passes a diagnostic file name built from the
        /// caller's label, and if that reached the module then two programs built from one source would be two
        /// modules and the dedup would silently buy nothing. The one-off parity measurement established that it
        /// does not, and this is the assertion that keeps it true: it is exactly the property that would break if
        /// <see cref="SpirvFrontEndPin.Debug"/> were ever turned on.
        /// </summary>
        [Fact]
        public void TheCompileLabel_DoesNotReachTheModuleAndSoDoesNotSplitTheDedup()
        {
            byte[] underOneName = SpirvFrontEnd.ToSpirv(VertGlsl, GpuShaderStages.Vertex, "PostBlit");
            byte[] underAnother = SpirvFrontEnd.ToSpirv(VertGlsl, GpuShaderStages.Vertex, "PostFxaa");

            Assert.Equal(underOneName, underAnother);
        }

        /// <summary>
        /// THE ELEVEN FULLSCREEN POST PROGRAMS ARE ONE VERTEX MODULE, which is the shipped case the dedup exists
        /// for rather than a contrived one. Every one of them is built from <c>ShaderSources.FullscreenVert</c>.
        /// </summary>
        [Fact]
        public void TheShippedFullscreenPasses_ShareOneVertexModule()
        {
            var api = new FakeVulkanShaderApi();
            var cache = new VulkanShaderModuleCache(api);

            ShippedGraphicsProgram[] fullscreen = D3D11ShaderProgramCatalog.GraphicsPrograms()
                .Where(p => string.Equals(p.VertexGlsl, ShaderSources.FullscreenVert, StringComparison.Ordinal))
                .ToArray();

            Assert.True(fullscreen.Length >= 11,
                $"Only {fullscreen.Length} shipped programs share FullscreenVert, where there were 11. That is a "
                + "statement about the catalog rather than about the cache, so read the catalog diff.");

            ulong[] vertexModules = fullscreen
                .Select(p => new VulkanShaderSet(cache, p.VertexGlsl, p.FragmentGlsl, p.Name).VertexModule)
                .Distinct()
                .ToArray();

            Assert.Single(vertexModules);
        }

        /// <summary>
        /// DISPOSAL DESTROYS NOTHING, which looks like an oversight and is decision V-S7. A module is shared by
        /// every program compiled from the same SPIR-V, so ending a handle in a wrapper would leave the other
        /// programs naming a destroyed object. The same rule <c>VulkanResourceLayout</c> already applies to a
        /// shared <c>VkDescriptorSetLayout</c>.
        /// </summary>
        [Fact]
        public void DisposingAShaderSet_DestroysNothing()
        {
            var api = new FakeVulkanShaderApi();
            var cache = new VulkanShaderModuleCache(api);

            var set = new VulkanShaderSet(cache, VertGlsl, FragGlsl, "disposed");
            set.Dispose();
            set.Dispose();

            Assert.True(set.IsDisposed);
            Assert.Empty(api.Destroyed);
            Assert.Equal(2, cache.DistinctModuleCount);
        }

        /// <summary>
        /// AND THE CACHE ENDS THEM ALL, ONCE, which is the other half: the device's teardown window is the only
        /// place a module is destroyed, and a second call there destroys nothing rather than double-destroying.
        /// </summary>
        [Fact]
        public void TheCacheDestroysEveryModuleAtTeardown_AndIsIdempotent()
        {
            var api = new FakeVulkanShaderApi();
            var cache = new VulkanShaderModuleCache(api);

            using var set = new VulkanShaderSet(cache, VertGlsl, FragGlsl, "torn down");
            using var compute = new VulkanComputeShader(cache, ComputeGlsl, "torn down");

            Assert.Equal(3, cache.DestroyAll());
            Assert.Equal(3, api.Destroyed.Count);
            Assert.Equal(0, cache.DistinctModuleCount);

            Assert.Equal(0, cache.DestroyAll());
            Assert.Equal(3, api.Destroyed.Count);
        }

        /// <summary>The teardown line reports the hit rate, which is what makes the dedup observable in a log
        /// rather than only in a test.</summary>
        [Fact]
        public void TheTeardownDescription_ReportsStagesAgainstModules()
        {
            var cache = new VulkanShaderModuleCache(new FakeVulkanShaderApi());
            using var first = new VulkanShaderSet(cache, VertGlsl, FragGlsl, "first");
            using var second = new VulkanShaderSet(cache, VertGlsl, FragGlsl, "second");

            Assert.Equal("4 shader stages shared 2 VkShaderModules", cache.Describe());
        }

        // ---- the two wrappers ----------------------------------------------------------------------------

        /// <summary>
        /// THE COMPUTE WORKGROUP SIZE COMES OFF THE MODULE, not off a caller. Vulkan takes it from the SPIR-V and
        /// ignores anything a description carries, so a caller-supplied copy that disagreed would be invisible on
        /// this backend and would produce wrong results on Metal, which is the one backend that reads it.
        /// </summary>
        [Fact]
        public void AComputeShader_ReportsTheWorkgroupSizeItsSourceDeclares()
        {
            var cache = new VulkanShaderModuleCache(new FakeVulkanShaderApi());
            using var compute = new VulkanComputeShader(cache, ComputeGlsl, "sized");

            Assert.Equal(8u, compute.ThreadGroupSizeX);
            Assert.Equal(4u, compute.ThreadGroupSizeY);
            Assert.Equal(2u, compute.ThreadGroupSizeZ);
            Assert.NotEqual(0ul, compute.Module);
        }

        /// <summary>
        /// A BROKEN SOURCE STOPS WITH THE ENGINE'S OWN EXCEPTION naming the label and the stage, and creates
        /// NOTHING. The second half is the one worth asserting: a pair whose fragment source is broken must not
        /// leave a vertex module in the cache under a program that was never built, because nothing would ever
        /// name it and it would be destroyed only at device teardown.
        /// </summary>
        [Fact]
        public void ABrokenFragmentSource_ThrowsAndLeavesNoOrphanedVertexModule()
        {
            var api = new FakeVulkanShaderApi();
            var cache = new VulkanShaderModuleCache(api);

            ShaderValidationException ex = Assert.Throws<ShaderValidationException>(
                () => new VulkanShaderSet(cache, VertGlsl, "#version 450\nvoid main() { not glsl }", "broken"));

            Assert.Contains("broken", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Fragment", ex.Message, StringComparison.Ordinal);
            Assert.Empty(api.Created);
            Assert.Equal(0, cache.DistinctModuleCount);
        }

        /// <summary>
        /// The seam's own two members hand back real objects now, rather than refusing by naming this row. That is
        /// asserted through <c>IGpuResourceFactory</c> rather than on the wrappers directly, because the refusal
        /// this replaces was on the factory and a reader checking whether the row landed looks there.
        /// </summary>
        [Fact]
        public void TheResourceFactory_CreatesShaderSetsAndComputeShaders()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuShaderSet set = fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
            using IGpuComputeShader compute = fixture.Factory.CreateComputeShaderFromSpirv(ComputeGlsl);

            Assert.IsType<VulkanShaderSet>(set);
            Assert.Equal(8u, compute.ThreadGroupSizeX);
            Assert.Equal(3, fixture.Modules.DistinctModuleCount);
        }

        // ---- the one refusal above the driver ------------------------------------------------------------

        /// <summary>
        /// A BYTE LENGTH THAT IS NOT A WHOLE NUMBER OF WORDS IS REFUSED BEFORE THE DRIVER SEES IT.
        /// <c>vkCreateShaderModule</c> reads <c>codeSize</c> in bytes and <c>pCode</c> as a <c>uint*</c>, so a
        /// misaligned length makes the ICD read past the end of the buffer, which is a crash with no useful stack
        /// rather than an error. It sits on the CACHE rather than on the native seam precisely so it can be
        /// asserted here, with no loader, which is the same division every other subsystem in this package uses.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(7)]
        public void ASpirvBlobThatIsNotAWholeNumberOfWords_IsRefused(int length)
        {
            var api = new FakeVulkanShaderApi();
            var cache = new VulkanShaderModuleCache(api);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => cache.GetOrCreate(new byte[length]));

            Assert.Contains("32-bit words", ex.Message, StringComparison.Ordinal);
            Assert.Empty(api.Created);
        }
    }
}
