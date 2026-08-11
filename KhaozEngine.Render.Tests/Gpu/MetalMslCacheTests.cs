using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE EMISSION CACHE (#592): its file discipline, its round trip, and every way a payload can be wrong.
    /// Section 12.5's row-9 addendum in <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> is why this
    /// caches the EMISSION rather than a <c>.metallib</c>, and 2.2b's pin 6 is why the payload carries the
    /// binding table and the entry-point names rather than only the MSL.
    ///
    /// <para>
    /// DEVICE-FREE, ON EVERY LEG, like everything else in this backend's shader path. The cache stores text and a
    /// table, so nothing here needs Metal and the whole contract runs on the free Linux leg.
    /// </para>
    /// <para>
    /// THE CORRUPTION CASES ARE THE POINT rather than a completeness exercise. A mangled DXBC fails inside
    /// <c>CreateVertexShader</c> and a mangled <c>VkPipelineCache</c> blob fails the driver's own header check, so
    /// both sibling caches have a downstream reader that refuses a bad payload for them. This payload has none: a
    /// mangled binding table would bind the wrong resource and render a wrong pixel with no error anywhere, which
    /// is the class section 2.2b exists to close. So every case below asserts the same three things: the read is a
    /// MISS, the file is DELETED, and nothing threw.
    /// </para>
    /// </summary>
    public sealed class MetalMslCacheTests
    {
        const string ComputeGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 4, local_size_z = 2) in;
layout(set=0, binding=0) buffer Out { uint Values[]; };
void main() { Values[gl_GlobalInvocationID.x] = gl_GlobalInvocationID.x; }
";

        // ---- where the cache lives (the sibling caches' conventions, followed) ----------------------------

        [Theory]
        [InlineData("off")]
        [InlineData("0")]
        [InlineData("FALSE")]
        [InlineData(" no ")]
        [InlineData("none")]
        public void TheCacheCanBeTurnedOff(string value) => Assert.Null(MetalMslCache.Resolve(value));

        [Fact]
        public void AnExplicitCacheDirectory_IsUsedVerbatim()
        {
            MetalMslCache? cache = MetalMslCache.Resolve("/tmp/some-msl-cache-dir");

            Assert.NotNull(cache);
            Assert.Equal("/tmp/some-msl-cache-dir", cache!.Directory);
            Assert.DoesNotContain(MetalShaderKey.EngineVersion, cache.Directory, StringComparison.Ordinal);
        }

        /// <summary>The engine version rides the KEY and the cache DIRECTORY, so an upgrade cannot inherit an
        /// emission from the release before it however complete the rest of the key is.</summary>
        [Fact]
        public void TheDefaultDirectory_CarriesTheEngineVersion()
        {
            Assert.NotEqual("unknown", MetalShaderKey.EngineVersion);

            string directory = MetalMslCache.DefaultDirectory();
            if (directory.Length == 0) return;   // a platform with no local app data runs without a cache

            Assert.Contains(MetalMslCache.Subfolder, directory, StringComparison.Ordinal);
            Assert.Contains(MetalShaderKey.EngineVersion, directory, StringComparison.Ordinal);
        }

        // ---- the key names the code that produces the payload, not only the toolchain --------------------

        /// <summary>
        /// THE KEY MOVES WHEN EITHER PRODUCING ASSEMBLY DOES. Four of the payload's fields are produced by engine
        /// code the pins do not cover: the entry-point name and the arguments (<c>MetalMslEntryPoint.Parse</c>),
        /// the binding table (<c>MetalShaderIndexTable.Build</c> over <c>SpirvResourceDecorations.Read</c>), the
        /// layouts (<c>SpirvCrossCompile</c>'s reflect) and the workgroup size (<c>SpirvLocalSize.Parse</c>).
        /// Within one engine version, editing any of them would otherwise keep serving the OLD payload, which is
        /// the wrong-pixel-no-error class arriving through the cache.
        /// <para>
        /// DEVICE-FREE AND ENGINE-FREE: the MVIDs are passed in rather than read off the loaded assemblies, so
        /// the claim is asserted without building two engines. That both real assemblies feed the shipped
        /// overload is the last assertion here.
        /// </para>
        /// </summary>
        [Fact]
        public void TheKey_MovesWhenEitherProducingAssemblyDoes()
        {
            var metal = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var gpu = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var other = Guid.Parse("33333333-3333-3333-3333-333333333333");
            const string source = "#version 450\nvoid main() {}\n";

            string baseline = MetalShaderKey.For(metal, gpu, source);

            Assert.Equal(baseline, MetalShaderKey.For(metal, gpu, source));      // pure
            Assert.NotEqual(baseline, MetalShaderKey.For(other, gpu, source));   // the parse and table half
            Assert.NotEqual(baseline, MetalShaderKey.For(metal, other, source)); // the reflect and decorations half
            Assert.NotEqual(
                MetalShaderKey.For(other, gpu, source), MetalShaderKey.For(metal, other, source));

            // The shipped overload is the same key under the assemblies this process actually loaded, so a
            // rebuild of either one invalidates every entry rather than serving a payload the old code wrote.
            Assert.Equal(MetalShaderKey.For(MetalShaderKey.MetalModuleId, MetalShaderKey.GpuModuleId, source),
                MetalShaderKey.For(source));
            Assert.NotEqual(Guid.Empty, MetalShaderKey.MetalModuleId);
            Assert.NotEqual(Guid.Empty, MetalShaderKey.GpuModuleId);
            Assert.NotEqual(MetalShaderKey.MetalModuleId, MetalShaderKey.GpuModuleId);
        }

        // ---- the round trip ------------------------------------------------------------------------------

        /// <summary>
        /// THE WHOLE CLAIM, ASSERTED FIELD BY FIELD: a program read back from disk is the one that was emitted.
        /// Every stage's MSL and entry-point name, the layouts the reflection produced (their element NAMES and
        /// stage visibility included, which the table's own content key does not render), and every table entry.
        /// </summary>
        [Fact]
        public void AGraphicsProgram_RoundTripsThroughDisk()
        {
            using var temp = new TempCacheDirectory();
            var cache = new MetalMslCache(temp.Path);

            MetalMslProgram fresh = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, cache, "round-trip");
            Assert.Equal(0, cache.Hits);
            Assert.Equal(1, cache.Misses);
            Assert.Equal(1, cache.Writes);

            var second = new MetalMslCache(temp.Path);   // a fresh instance: nothing is remembered in memory
            MetalMslProgram loaded = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, second, "round-trip");

            Assert.Equal(1, second.Hits);
            Assert.Equal(0, second.Misses);
            Assert.Equal(0, second.Writes);
            AssertSameProgram(fresh, loaded);
        }

        [Fact]
        public void AComputeKernel_RoundTripsWithItsWorkgroupSize()
        {
            using var temp = new TempCacheDirectory();
            var cache = new MetalMslCache(temp.Path);

            (MetalMslProgram fresh, uint x, uint y, uint z) = MetalShaderBuild.Compute(
                ComputeGlsl, cache, "round-trip-compute");
            Assert.Equal((8u, 4u, 2u), (x, y, z));

            var second = new MetalMslCache(temp.Path);
            (MetalMslProgram loaded, uint cx, uint cy, uint cz) = MetalShaderBuild.Compute(
                ComputeGlsl, second, "round-trip-compute");

            Assert.Equal(1, second.Hits);
            Assert.Equal((x, y, z), (cx, cy, cz));
            AssertSameProgram(fresh, loaded);
        }

        /// <summary>
        /// A HIT NEVER REACHES THE EMITTER, proven with two sources the emitter CANNOT compile. The key is a pure
        /// hash of the sources, so it is computable before anything looks at them, which means an entry can be
        /// planted under the key of text that is not GLSL at all. If the read path were consulted after the front
        /// end rather than before it, this would throw instead of answering.
        /// </summary>
        [Fact]
        public void ACacheHit_NeverReachesTheEmitter()
        {
            using var temp = new TempCacheDirectory();
            var cache = new MetalMslCache(temp.Path);

            MetalMslProgram real = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, null, "donor");

            const string notGlsl = "this is not a shader and never was";
            string key = MetalShaderKey.For(notGlsl, notGlsl);
            Assert.True(cache.TryStore(key, new MetalMslCacheEntry(real, 0, 0, 0)));

            MetalMslProgram loaded = MetalShaderBuild.Pair(notGlsl, notGlsl, cache, "planted");

            Assert.Equal(1, cache.Hits);
            AssertSameProgram(real, loaded);

            // THE CONTROL, so the assertion above is not vacuous: those sources really are uncompilable, and the
            // same call with no cache says so.
            Assert.Throws<ShaderValidationException>(
                () => MetalShaderBuild.Pair(notGlsl, notGlsl, null, "planted"));
        }

        /// <summary>
        /// M-R9's DEDUPLICATION SURVIVES THE CACHE, which is the property row 10 rests on and the one a cache in
        /// front of the emission could silently break. A table rebuilt from a payload has to reach
        /// <c>MetalIndexTableCache</c> exactly as a fresh one does, so two programs with the same content still
        /// share ONE instance and the pipeline-switch comparison stays a handle compare.
        /// </summary>
        [Fact]
        public void ATableFromACacheHit_DeduplicatesLikeAFreshOne()
        {
            using var temp = new TempCacheDirectory();
            var tables = new MetalIndexTableCache();

            MetalShaderIndexTable fresh = tables.Canonical(MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl,
                new MetalMslCache(temp.Path), "dedup").Table);

            MetalShaderIndexTable cached = tables.Canonical(MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl,
                new MetalMslCache(temp.Path), "dedup").Table);

            Assert.Same(fresh, cached);
            Assert.True(fresh.SameIndicesAs(cached));
            Assert.Equal(1, tables.Count);
        }

        /// <summary>
        /// FREE-THREADED CREATION (M-W8): concurrent misses on one key all emit and all write, which is benign
        /// rather than raced. The key is a content hash and the emission under the pinned options is
        /// deterministic, so every writer writes the same bytes and the last rename wins with them.
        /// </summary>
        [Fact]
        public void ConcurrentMissesOnOneKey_AllSucceedAndLeaveOneGoodEntry()
        {
            using var temp = new TempCacheDirectory();
            var cache = new MetalMslCache(temp.Path);

            Parallel.For(0, 8, _ => MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, cache, "threads"));

            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
            Assert.Single(Directory.GetFiles(temp.Path));

            var after = new MetalMslCache(temp.Path);
            MetalMslProgram loaded = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, after, "threads");

            Assert.Equal(1, after.Hits);
            Assert.Equal(0, after.Discards);
            AssertSameProgram(
                MetalShaderBuild.Pair(
                    MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, null, "threads"),
                loaded);
        }

        // ---- corruption: every one is a miss AND a delete ------------------------------------------------

        [Fact]
        public void AMutatedPayloadByte_IsAMissAndTheEntryIsDeleted()
        {
            using var temp = new TempCacheDirectory();
            (MetalMslCache cache, string key, string path) = Planted(temp);

            byte[] file = File.ReadAllBytes(path);
            file[file.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, file);

            AssertRefusedAndDeleted(cache, key, path);
        }

        [Fact]
        public void AMutatedHashByte_IsAMissAndTheEntryIsDeleted()
        {
            using var temp = new TempCacheDirectory();
            (MetalMslCache cache, string key, string path) = Planted(temp);

            byte[] file = File.ReadAllBytes(path);
            file[^1] ^= 0xFF;
            File.WriteAllBytes(path, file);

            AssertRefusedAndDeleted(cache, key, path);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(64)]
        public void ATruncatedEntry_IsAMissAndTheEntryIsDeleted(int keep)
        {
            using var temp = new TempCacheDirectory();
            (MetalMslCache cache, string key, string path) = Planted(temp);

            byte[] file = File.ReadAllBytes(path);
            File.WriteAllBytes(path, file.AsSpan(0, Math.Min(keep, file.Length)).ToArray());

            if (keep == 0)
            {
                // A zero-length file is a miss in the file store itself, which never claims to have read
                // anything, so there is nothing to discard and the file stays for the next write to replace.
                Assert.Null(cache.TryLoad(key, "truncated"));
                Assert.Equal(0, cache.Discards);
                return;
            }

            AssertRefusedAndDeleted(cache, key, path);
        }

        /// <summary>
        /// AN ENTRY UNDER ANOTHER PROGRAM'S NAME IS REFUSED, which is why the key is written INSIDE the payload
        /// as well as being the file name. A copy, a rename or a sync tool that shuffles files would otherwise
        /// hand one program another program's emission, with a hash that authenticates perfectly.
        /// </summary>
        [Fact]
        public void AnEntryFiledUnderAnotherKey_IsAMissAndTheEntryIsDeleted()
        {
            using var temp = new TempCacheDirectory();
            (MetalMslCache cache, _, string path) = Planted(temp);

            string otherKey = MetalShaderKey.For("#version 450\nvoid main() {}\n");
            string otherPath = cache.PathFor(otherKey);
            File.Copy(path, otherPath);

            AssertRefusedAndDeleted(cache, otherKey, otherPath);
        }

        /// <summary>
        /// A PAYLOAD FROM ANOTHER FORMAT VERSION IS REFUSED EVEN THOUGH IT AUTHENTICATES. This is the case a
        /// developer iterating on the payload hits, and the only way to produce it is to re-hash after the edit,
        /// which is exactly what this does: the file is well formed and its hash is right, and it is still not
        /// this format.
        /// </summary>
        [Fact]
        public void AnEntryFromAnotherFormatVersion_IsAMissAndTheEntryIsDeleted()
        {
            using var temp = new TempCacheDirectory();
            (MetalMslCache cache, string key, string path) = Planted(temp);

            byte[] file = File.ReadAllBytes(path);
            // The format version is the int32 straight after the magic.
            file[MetalMslCacheEntry.Magic.Length] = (byte)(MetalMslCacheEntry.FormatVersion + 1);
            File.WriteAllBytes(path, ReHashed(file));

            AssertRefusedAndDeleted(cache, key, path);
        }

        /// <summary>
        /// A TABLE THE STRUCTURAL CHECKS REFUSE IS CORRUPTION TOO, not a table to bind through. The last four
        /// bytes of the body are the last entry's index, so setting them to -1 produces a payload that hashes
        /// correctly and describes a table nothing may bind. Pin 1's discipline is what refuses it, at the second
        /// door: <c>MetalShaderIndexTable.FromCache</c> throws and the entry is discarded.
        /// </summary>
        [Fact]
        public void AnEntryWhoseTableIsRefused_IsAMissAndTheEntryIsDeleted()
        {
            using var temp = new TempCacheDirectory();
            (MetalMslCache cache, string key, string path) = Planted(temp);

            byte[] file = File.ReadAllBytes(path);
            int lastIndex = file.Length - MetalMslCacheEntry.HashLength - sizeof(int);
            BitConverter.GetBytes(-1).CopyTo(file, lastIndex);
            File.WriteAllBytes(path, ReHashed(file));

            AssertRefusedAndDeleted(cache, key, path);
        }

        /// <summary>The refusals the rebuild owns, driven at their own seat rather than through a patched file,
        /// because a payload cannot be built in memory that names a set nothing declares.</summary>
        [Fact]
        public void TheTableRebuild_RefusesEntriesTheLayoutsDoNotDeclare()
        {
            var layouts = new[]
            {
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)),
            };

            Assert.Throws<ShaderValidationException>(() => MetalShaderIndexTable.FromCache(
                Entry(1, 0, MetalIndexSpace.Buffer, 0), layouts, "bad-set"));
            Assert.Throws<ShaderValidationException>(() => MetalShaderIndexTable.FromCache(
                Entry(0, 3, MetalIndexSpace.Buffer, 0), layouts, "bad-binding"));
            Assert.Throws<ShaderValidationException>(() => MetalShaderIndexTable.FromCache(
                Entry(0, 0, MetalIndexSpace.Texture, 0), layouts, "wrong-space"));
            Assert.Throws<ShaderValidationException>(() => MetalShaderIndexTable.FromCache(
                Entry(0, 0, MetalIndexSpace.Buffer, -1), layouts, "negative-index"));

            // And the shape it accepts, so the four above are refusals rather than a rebuild that never works.
            MetalShaderIndexTable table = MetalShaderIndexTable.FromCache(
                Entry(0, 0, MetalIndexSpace.Buffer, 2), layouts, "fine");
            Assert.True(table.TryGetIndex(0, 0, MetalShaderStage.Vertex, out MetalIndexTableEntry entry));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 2), entry);
        }

        // ---- the null edge -------------------------------------------------------------------------------

        /// <summary>
        /// A KEY THAT IS NOT A KEY IS A MISS AND A "NO", NEVER A THROW. Both members promise they never raise,
        /// and the path used to be computed inside the guard that made that true. Moving the file plumbing into
        /// <c>GpuDiskCache</c> lifted the computation out of it, which turned one caller mistake into an
        /// exception out of a cache whose only job is to save time.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ANullOrBlankKey_IsAMissAndAFailedStoreRatherThanAThrow(string? key)
        {
            using var temp = new TempCacheDirectory();
            var cache = new MetalMslCache(temp.Path);
            MetalMslProgram program = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, null, "null-key");

            Assert.Null(cache.TryLoad(key, "null-key"));
            Assert.False(cache.TryStore(key, new MetalMslCacheEntry(program, 0, 0, 0)));

            Assert.Equal(0, cache.Hits);
            Assert.Equal(1, cache.Misses);
            Assert.Equal(0, cache.Writes);
            Assert.Empty(Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void ACacheThatCannotBeUsed_FailsSilentlyBothWays()
        {
            using var temp = new TempCacheDirectory();
            string blocked = Path.Combine(temp.Path, "not-a-directory");
            File.WriteAllText(blocked, "this is a file");

            var cache = new MetalMslCache(blocked);
            MetalMslProgram program = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, cache, "blocked");

            Assert.NotNull(program);
            Assert.Equal(0, cache.Hits);
            Assert.Equal(1, cache.Misses);
            Assert.Equal(0, cache.Writes);
        }

        // ---- helpers -------------------------------------------------------------------------------------

        static (MetalMslCache Cache, string Key, string Path) Planted(TempCacheDirectory temp)
        {
            var cache = new MetalMslCache(temp.Path);
            MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, cache, "planted");

            string key = MetalShaderKey.For(MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl);
            string path = cache.PathFor(key);
            Assert.True(File.Exists(path));
            return (new MetalMslCache(temp.Path), key, path);
        }

        static void AssertRefusedAndDeleted(MetalMslCache cache, string key, string path)
        {
            Assert.Null(cache.TryLoad(key, "corrupt"));
            Assert.Equal(1, cache.Discards);
            Assert.Equal(1, cache.Misses);
            Assert.Equal(0, cache.Hits);
            Assert.False(File.Exists(path));
        }

        static byte[] ReHashed(byte[] file)
        {
            int bodyLength = file.Length - MetalMslCacheEntry.HashLength;
            SHA256.HashData(file.AsSpan(0, bodyLength), file.AsSpan(bodyLength));
            return file;
        }

        static List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>> Entry(
            int set, int binding, MetalIndexSpace space, int index)
            => new()
            {
                new KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>(
                    new MetalIndexTableKey(set, binding, MetalShaderStage.Vertex),
                    new MetalIndexTableEntry(space, index)),
            };

        static void AssertSameProgram(MetalMslProgram expected, MetalMslProgram actual)
        {
            Assert.Equal(expected.Stages.Count, actual.Stages.Count);
            for (int i = 0; i < expected.Stages.Count; i++)
            {
                Assert.Equal(expected.Stages[i].Stage, actual.Stages[i].Stage);
                Assert.Equal(expected.Stages[i].EntryPointName, actual.Stages[i].EntryPointName);
                Assert.Equal(expected.Stages[i].Msl, actual.Stages[i].Msl);
            }

            // The table's own notion of identity, which is what the per-device dedup keys on.
            Assert.Equal(expected.Table.ContentKey, actual.Table.ContentKey);
            Assert.Equal(expected.Table.Count, actual.Table.Count);
            Assert.Equal(expected.Table.Entries().ToArray(), actual.Table.Entries().ToArray());

            // And the parts the content key does NOT render, which a payload carrying only what it renders would
            // have quietly dropped.
            Assert.Equal(expected.Table.Layouts.Count, actual.Table.Layouts.Count);
            for (int set = 0; set < expected.Table.Layouts.Count; set++)
            {
                GpuResourceLayoutElement[] mine = expected.Table.Layouts[set].Elements;
                GpuResourceLayoutElement[] theirs = actual.Table.Layouts[set].Elements;
                Assert.Equal(mine.Length, theirs.Length);

                for (int i = 0; i < mine.Length; i++)
                {
                    Assert.Equal(mine[i].Name, theirs[i].Name);
                    Assert.Equal(mine[i].Kind, theirs[i].Kind);
                    Assert.Equal(mine[i].Stages, theirs[i].Stages);
                    Assert.Equal(mine[i].Dynamic, theirs[i].Dynamic);
                }
            }
        }

        sealed class TempCacheDirectory : IDisposable
        {
            internal TempCacheDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ke-metal-msl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // A temp directory that will not delete is litter in the temp folder, not a test failure.
                }
            }
        }
    }
}
