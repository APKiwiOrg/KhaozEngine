using System;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The device-free half of the native Direct3D 11 shader path (decisions S1, S3, S4 and S5, section 8 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>): the FXC target profiles, the
    /// <c>KE_D3D11_DEBUG</c> flag gate, the cache key, the disk cache itself and the holed-signature rule.
    /// <para>
    /// Everything here is a plain <c>[Fact]</c> that runs on macOS and Linux, because the interesting cases are
    /// all in the policy rather than in the interop. The FXC call is the only genuinely Windows-only part, and it
    /// has its own leg in <see cref="D3D11FxcValidationTests"/>.
    /// </para>
    /// </summary>
    public sealed class D3D11ShaderPathTests
    {
        // ---- profiles and flags (S1) ---------------------------------------------------------------------

        /// <summary>Shader Model 5.0 per stage. Not a knob: DXC emits DXIL and D3D11 consumes DXBC, so 6.x is
        /// unreachable from this backend at all.</summary>
        [Fact]
        public void TheFxcProfiles_AreShaderModelFivePerStage()
        {
            Assert.Equal("vs_5_0", D3D11ShaderProfile.For(D3D11ShaderStage.Vertex));
            Assert.Equal("ps_5_0", D3D11ShaderProfile.For(D3D11ShaderStage.Fragment));
            Assert.Equal("cs_5_0", D3D11ShaderProfile.For(D3D11ShaderStage.Compute));
            Assert.Equal("main", D3D11ShaderProfile.EntryPoint);
        }

        [Fact]
        public void AnUnmappedStage_ThrowsRatherThanPickingAProfile()
            => Assert.Throws<ArgumentOutOfRangeException>(() => D3D11ShaderProfile.For((D3D11ShaderStage)99));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("OFF")]
        public void WithoutTheDebugGate_ShadersCompileOptimized(string? value)
        {
            Assert.Equal(D3D11ShaderDebug.Optimized, D3D11ShaderDebug.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData(" Yes ")]
        [InlineData("ON")]
        public void WithTheDebugGate_ShadersCompileWithDebugInfoAndNoOptimization(string value)
        {
            Assert.Equal(D3D11ShaderDebug.DebugBuild, D3D11ShaderDebug.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// A mistyped gate comes back verbatim so the caller can WARN, and the default is used. A debug gate that
        /// silently compiled optimized is indistinguishable from a correct run, so a whole capture session can be
        /// spent looking at a disassembly that was never going to line up.
        /// </summary>
        [Fact]
        public void AMistypedDebugGate_IsReportedRatherThanGuessedAt()
        {
            Assert.Equal(D3D11ShaderDebug.Optimized, D3D11ShaderDebug.Resolve("verbose", out string? bad));
            Assert.Equal("verbose", bad);
            Assert.Contains("verbose", D3D11ShaderDebug.UnrecognizedWarning(bad!), StringComparison.Ordinal);
            Assert.Contains(D3D11ShaderDebug.EnvVarName, D3D11ShaderDebug.UnrecognizedWarning(bad!),
                StringComparison.Ordinal);
        }

        /// <summary>The two flag sets must differ, or the gate is decorative. The debug set also carries
        /// skip-optimization, which is the flag that makes a capture's disassembly map back to the source.
        /// </summary>
        [Fact]
        public void TheDebugAndOptimizedFlagSets_AreDifferentValues()
        {
            Assert.NotEqual(D3D11ShaderDebug.Optimized, D3D11ShaderDebug.DebugBuild);
            // D3DCOMPILE_OPTIMIZATION_LEVEL3 = 0x8000, DEBUG = 0x1, SKIP_OPTIMIZATION = 0x4. Pinned to the
            // documented Windows SDK values so a Vortice rename or repoint fails here instead of silently
            // changing which flags the engine sets.
            Assert.Equal(0x8000u, D3D11ShaderDebug.Optimized);
            Assert.Equal(0x5u, D3D11ShaderDebug.DebugBuild);
        }

        // ---- the cache key (S4) --------------------------------------------------------------------------

        const string VertGlsl = "#version 450\nlayout(location=0) in vec3 P;\nvoid main(){gl_Position=vec4(P,1);}";
        const string FragGlsl = "#version 450\nlayout(location=0) out vec4 C;\nvoid main(){C=vec4(1);}";

        [Fact]
        public void AShaderKey_IsStableAndIsALowercaseHexSha256()
        {
            string a = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0x8000u, VertGlsl, FragGlsl);
            string b = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0x8000u, VertGlsl, FragGlsl);

            Assert.Equal(a, b);
            Assert.Equal(64, a.Length);
            Assert.All(a, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'), $"'{c}' is not hex"));
        }

        /// <summary>
        /// THE KEY COVERS THE WHOLE PROGRAM, not just the stage's own source. A pair is cross-compiled together,
        /// so the emitted VERTEX HLSL is a function of the fragment source too: SPIRV-Cross assigns registers
        /// across both stages at once. A key that ignored the sibling source would serve stale vertex bytes the
        /// moment a fragment change renumbered a register, which renders wrongly and fails nothing.
        /// </summary>
        [Fact]
        public void AShaderKey_ChangesWhenTheOtherStagesSourceChanges()
        {
            string original = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, VertGlsl, FragGlsl);
            string sibling = D3D11ShaderKey.For(
                D3D11ShaderStage.Vertex, 0u, VertGlsl, FragGlsl + "\n// a fragment-side edit");

            Assert.NotEqual(original, sibling);
        }

        [Fact]
        public void AShaderKey_ChangesWithTheStageAndWithTheCompileFlags()
        {
            string vertex = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, VertGlsl, FragGlsl);
            string fragment = D3D11ShaderKey.For(D3D11ShaderStage.Fragment, 0u, VertGlsl, FragGlsl);
            string debug = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, D3D11ShaderDebug.DebugBuild,
                VertGlsl, FragGlsl);

            Assert.NotEqual(vertex, fragment);
            Assert.NotEqual(vertex, debug);
        }

        /// <summary>
        /// Sources are LENGTH-PREFIXED into the key, so moving the boundary between two of them cannot produce
        /// the same digest. A plain separator would be forgeable by a source that contained the separator.
        /// </summary>
        [Fact]
        public void AShaderKey_CannotBeForgedByMovingTheBoundaryBetweenSources()
        {
            string split = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, "abc", "def");
            string moved = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, "ab", "cdef");

            Assert.NotEqual(split, moved);
        }

        [Fact]
        public void AShaderKey_NeedsAtLeastOneSource()
            => Assert.Throws<ArgumentException>(
                () => D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, Array.Empty<string>()));

        /// <summary>The engine version rides the key AND the cache directory, so an upgrade cannot inherit a
        /// stale compiled module however complete the rest of the key is.</summary>
        [Fact]
        public void TheKeyAndTheCacheDirectory_CarryTheEngineVersion()
        {
            Assert.NotEqual("unknown", D3D11ShaderKey.EngineVersion);
            string dir = D3D11DxbcCache.DefaultDirectory();
            if (dir.Length != 0) Assert.Contains(D3D11ShaderKey.EngineVersion, dir, StringComparison.Ordinal);
        }

        // ---- the disk cache (S4) -------------------------------------------------------------------------

        [Theory]
        [InlineData("off")]
        [InlineData("0")]
        [InlineData("FALSE")]
        [InlineData(" no ")]
        [InlineData("none")]
        public void TheCacheCanBeTurnedOff(string value) => Assert.Null(D3D11DxbcCache.Resolve(value));

        [Fact]
        public void AnExplicitCacheDirectory_IsUsedVerbatim()
        {
            D3D11DxbcCache? cache = D3D11DxbcCache.Resolve("/tmp/some-cache-dir");

            Assert.NotNull(cache);
            Assert.Equal("/tmp/some-cache-dir", cache!.Directory);
            // No engine-version segment appended: a caller who names a directory means that directory.
            Assert.DoesNotContain(D3D11ShaderKey.EngineVersion, cache.Directory, StringComparison.Ordinal);
        }

        [Fact]
        public void ACachedModule_RoundTripsThroughDisk()
        {
            using var temp = new TempCacheDirectory();
            var cache = new D3D11DxbcCache(temp.Path);
            byte[] dxbc = { 0x44, 0x58, 0x42, 0x43, 1, 2, 3, 4 };

            Assert.Null(cache.TryRead("abc123"));
            Assert.True(cache.TryWrite("abc123", dxbc));
            Assert.Equal(dxbc, cache.TryRead("abc123"));
            // The directory is created on WRITE, not on construction: a process that only ever reads should not
            // leave one behind.
            Assert.True(Directory.Exists(temp.Path));
        }

        /// <summary>
        /// A zero-length entry is a MISS, not empty bytes. It is what a process that died mid-write leaves
        /// behind, and a zero-length DXBC handed to <c>CreateVertexShader</c> fails somewhere far less
        /// informative than here.
        /// </summary>
        [Fact]
        public void AHalfWrittenCacheEntry_ReadsAsAMiss()
        {
            using var temp = new TempCacheDirectory();
            var cache = new D3D11DxbcCache(temp.Path);
            Directory.CreateDirectory(temp.Path);
            File.WriteAllBytes(cache.PathFor("truncated"), Array.Empty<byte>());

            Assert.Null(cache.TryRead("truncated"));
        }

        /// <summary>Every failure is a miss and nothing propagates: a cache that cannot be read or written is a
        /// slower start and nothing else. Here the directory is a FILE, so both operations fail at the OS.
        /// </summary>
        [Fact]
        public void ACacheThatCannotBeUsed_FailsSilentlyBothWays()
        {
            using var temp = new TempCacheDirectory();
            string blocked = Path.Combine(temp.Path, "not-a-directory");
            Directory.CreateDirectory(temp.Path);
            File.WriteAllText(blocked, "this is a file");

            var cache = new D3D11DxbcCache(blocked);
            Assert.Null(cache.TryRead("anything"));
            Assert.False(cache.TryWrite("anything", new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public void AnEmptyModuleIsNeverCached()
        {
            using var temp = new TempCacheDirectory();
            var cache = new D3D11DxbcCache(temp.Path);

            Assert.False(cache.TryWrite("empty", ReadOnlySpan<byte>.Empty));
            Assert.Null(cache.TryRead("empty"));
        }

        // ---- the holed-signature rule (S5) ---------------------------------------------------------------

        [Fact]
        public void AContiguousSignature_Passes()
        {
            D3D11ShaderInputSemantic[] signature = Texcoords(0, 1, 2, 3);

            Assert.Null(D3D11ShaderSignature.DescribeHole(signature));
            D3D11ShaderSignature.RequireContiguousUserSemantics(signature, "fine");
        }

        /// <summary>A pass with no vertex inputs at all is legal. The fullscreen passes declare none.</summary>
        [Fact]
        public void AnEmptySignature_Passes()
            => Assert.Null(D3D11ShaderSignature.DescribeHole(Array.Empty<D3D11ShaderInputSemantic>()));

        /// <summary>
        /// THE SHADOW INCIDENT'S EXACT SHAPE. The shadow depth vertex reads only Position and IModel0 to 3, so
        /// without its sink SPIRV-Cross drops locations 1 to 4 and 9 to 11 and emits TEXCOORD0 then TEXCOORD5 to
        /// 8. Building that pipeline corrupted WARP so the main model and splat passes rendered no colour.
        /// </summary>
        [Fact]
        public void TheShadowIncidentsHoledSignature_IsRejectedAndDiagnosed()
        {
            D3D11ShaderInputSemantic[] holed = Texcoords(0, 5, 6, 7, 8);

            string? problem = D3D11ShaderSignature.DescribeHole(holed);
            Assert.NotNull(problem);
            Assert.Contains("HOLED", problem!, StringComparison.Ordinal);
            Assert.Contains("first missing index is 1", problem, StringComparison.OrdinalIgnoreCase);

            ShaderValidationException ex = Assert.Throws<ShaderValidationException>(
                () => D3D11ShaderSignature.RequireContiguousUserSemantics(holed, "ShadowDepth"));
            Assert.Contains("ShadowDepth", ex.Message, StringComparison.Ordinal);
            // The message has to say what to DO, since the fix is in the GLSL and is not obvious from a hole.
            Assert.Contains("ShaderSources.Shadow.cs", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ShaderSources.Terrain.cs", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>A hole anywhere fails, including one that only misses the last index of a run.</summary>
        [Fact]
        public void AHoleAtTheEndOfTheRun_IsRejectedToo()
            => Assert.NotNull(D3D11ShaderSignature.DescribeHole(Texcoords(0, 1, 3)));

        /// <summary>System-value inputs have their own index space and never participate. A vertex shader that
        /// reads <c>gl_VertexIndex</c> and nothing else is contiguous by having no user inputs.</summary>
        [Fact]
        public void SystemValueSemantics_DoNotParticipate()
        {
            var signature = new[]
            {
                new D3D11ShaderInputSemantic("SV_VertexID", 0),
                new D3D11ShaderInputSemantic("TEXCOORD", 0),
                new D3D11ShaderInputSemantic("SV_InstanceID", 0),
                new D3D11ShaderInputSemantic("TEXCOORD", 1),
            };

            Assert.Null(D3D11ShaderSignature.DescribeHole(signature));
        }

        [Fact]
        public void ARepeatedSemanticIndex_IsDiagnosedAsItsOwnProblem()
        {
            string? problem = D3D11ShaderSignature.DescribeHole(Texcoords(0, 1, 1));

            Assert.NotNull(problem);
            Assert.Contains("REPEATED", problem!, StringComparison.Ordinal);
        }

        // ---- the platform boundary -----------------------------------------------------------------------

        /// <summary>
        /// THE CLAIM DECISION P1 RESTS ON, checked for everything this row added: the whole device-free shader
        /// path runs off Windows without putting the Direct3D interop into the process. That is what lets these
        /// be plain facts, and it holds only while the policy types stay free of Vortice VALUE-TYPE fields (the
        /// two compile-flag constants are folded to literals, which is the point of writing them as
        /// <c>const uint</c>) and every body that names one stays behind the platform guard.
        /// </summary>
        [Fact]
        public void OffWindows_TheDeviceFreeShaderPathPullsInNoDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            _ = D3D11ShaderProfile.For(D3D11ShaderStage.Compute);
            _ = D3D11ShaderDebug.Resolve("1", out _);
            _ = D3D11ShaderDebug.Optimized;
            _ = D3D11ShaderDebug.DebugBuild;
            _ = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, D3D11ShaderDebug.Optimized, VertGlsl, FragGlsl);
            _ = D3D11DxbcCache.Resolve("off");
            using (var temp = new TempCacheDirectory())
            {
                var cache = new D3D11DxbcCache(temp.Path);
                cache.TryWrite("k", new byte[] { 1 });
                _ = cache.TryRead("k");
            }
            _ = D3D11ShaderSignature.DescribeHole(Texcoords(0, 1));

            D3D11InteropLoad.AssertNotLoaded();
        }

        /// <summary>
        /// The validation entry points are reachable everywhere and say what to do off Windows rather than
        /// failing inside the interop. Asking them loads nothing, which is what lets a game call them from a
        /// cross-platform test suite behind the platform guard.
        /// </summary>
        [Fact]
        public void OffWindows_TheValidationEntryPointsExplainThemselvesAndLoadNothing()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;

            PlatformNotSupportedException pair = Assert.Throws<PlatformNotSupportedException>(
                () => KhaozEngineD3D11.ValidateShaderPair(VertGlsl, FragGlsl, "anything"));
            PlatformNotSupportedException compute = Assert.Throws<PlatformNotSupportedException>(
                () => KhaozEngineD3D11.ValidateComputeShader("#version 450\nvoid main(){}", "anything"));

            foreach (PlatformNotSupportedException ex in new[] { pair, compute })
            {
                Assert.Contains(nameof(KhaozEngineD3D11.IsPlatformSupported), ex.Message, StringComparison.Ordinal);
                Assert.Contains("ShaderValidation", ex.Message, StringComparison.Ordinal);
            }

            D3D11InteropLoad.AssertNotLoaded();
        }

        // ---- the pinned cross-compile options (S3) -------------------------------------------------------

        /// <summary>
        /// The pinned values, stated here as well as in the pin, so flipping one is a two-file change with a
        /// visible diff rather than an edit inside a doc comment. The identity string is what a cache key uses to
        /// tell entries emitted under one set from entries emitted under another, so it MUST move when a value
        /// does.
        /// </summary>
        [Fact]
        public void TheCrossCompileOptions_ArePinnedToTheLibraryDefaults()
        {
            Assert.False(HlslCrossCompilePin.FixClipSpaceZ);
            Assert.False(HlslCrossCompilePin.InvertVertexOutputY);
            Assert.False(HlslCrossCompilePin.NormalizeResourceNames);
            Assert.Equal(0, HlslCrossCompilePin.SpecializationConstantCount);

            Assert.Contains("fixClipSpaceZ=0", HlslCrossCompilePin.Identity, StringComparison.Ordinal);
            Assert.Contains("invertVertexOutputY=0", HlslCrossCompilePin.Identity, StringComparison.Ordinal);
            Assert.Contains("normalizeResourceNames=0", HlslCrossCompilePin.Identity, StringComparison.Ordinal);
        }

        /// <summary>
        /// The identity string goes into a cache key line by line, so it has to BE one line: a value carrying a
        /// newline would split the key's own framing and let two different option sets hash the same.
        /// </summary>
        [Fact]
        public void ThePinnedOptionsIdentity_IsASingleLineToken()
        {
            Assert.NotEmpty(HlslCrossCompilePin.Identity);
            Assert.DoesNotContain('\n', HlslCrossCompilePin.Identity);
            Assert.DoesNotContain('\r', HlslCrossCompilePin.Identity);
            Assert.StartsWith("spirv-cross/hlsl", HlslCrossCompilePin.Identity, StringComparison.Ordinal);
        }

        static D3D11ShaderInputSemantic[] Texcoords(params uint[] indices)
            => indices.Select(i => new D3D11ShaderInputSemantic("TEXCOORD", i)).ToArray();

        sealed class TempCacheDirectory : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ke-dxbc-cache-" + Guid.NewGuid().ToString("N"));

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
