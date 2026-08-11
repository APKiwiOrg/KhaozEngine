using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT THE EMISSION CACHE ACTUALLY SAVES, OVER THE WHOLE SHIPPED CORPUS, MEASURED RATHER THAN CLAIMED
    /// (#592). One cold pass that emits and writes every program, then one warm pass through a fresh cache
    /// instance on the same directory, with both times printed.
    ///
    /// <para>
    /// THE ASSERTION IS STRUCTURAL AND THE MEASUREMENT IS A REPORT, deliberately. Asserting that the warm pass is
    /// faster would make a shared CI runner under load able to fail a correctness suite, which is how a timing
    /// assertion becomes a test everyone learns to re-run. What IS asserted is the thing that makes the saving
    /// real: on the warm pass every program came out of the cache, so the emission ran ZERO times. The counters
    /// say so per program, and <c>MetalMslCacheTests.ACacheHit_NeverReachesTheEmitter</c> is what proves a hit
    /// cannot have reached the emitter at all, by hitting on sources the emitter would refuse.
    /// </para>
    /// <para>
    /// THE CONTENT IS COMPARED PROGRAM BY PROGRAM, because a fast wrong answer is the failure this whole area
    /// exists to prevent. Every stage's MSL, every entry-point name and every table's content key have to match
    /// what the cold pass emitted, or the saving is being bought with the exact "everything compiles and every
    /// pixel is wrong" class section 2.2b closes.
    /// </para>
    /// <para>
    /// DEVICE-FREE, so this runs on every leg. The numbers it prints are the machine's, not a golden.
    /// </para>
    /// </summary>
    public sealed class MetalMslCacheCorpusTests
    {
        readonly ITestOutputHelper _output;

        public MetalMslCacheCorpusTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void TheWholeCorpus_LoadsBackFromTheCacheWithoutEmittingAnything()
        {
            using var temp = new TempCacheDirectory();

            var cold = new MetalMslCache(temp.Path);
            var coldClock = Stopwatch.StartNew();
            List<(string Name, MetalMslProgram Program, uint X, uint Y, uint Z)> emitted = BuildEverything(cold);
            coldClock.Stop();

            // A FRESH INSTANCE ON THE SAME DIRECTORY, which is what a second launch is. Nothing is remembered in
            // memory, so a hit here is a hit off disk.
            var warm = new MetalMslCache(temp.Path);
            var warmClock = Stopwatch.StartNew();
            List<(string Name, MetalMslProgram Program, uint X, uint Y, uint Z)> loaded = BuildEverything(warm);
            warmClock.Stop();

            long bytes = Directory.GetFiles(temp.Path).Sum(f => new FileInfo(f).Length);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"programs={emitted.Count} cold={coldClock.ElapsedMilliseconds} ms warm={warmClock.ElapsedMilliseconds} ms "
                + $"entries={Directory.GetFiles(temp.Path).Length} payload={bytes / 1024} KiB\n"
                + $"cold: hits={cold.Hits} misses={cold.Misses} writes={cold.Writes} discards={cold.Discards}\n"
                + $"warm: hits={warm.Hits} misses={warm.Misses} writes={warm.Writes} discards={warm.Discards}"));

            // NOT VACUOUS. An emptied catalog would satisfy every assertion below by having nothing in it.
            Assert.True(emitted.Count > 30, "the shipped-program walk found almost nothing, so the counters below "
                + "mean nothing: " + emitted.Count.ToString(CultureInfo.InvariantCulture) + " programs.");

            // The cold pass paid for everything and stored everything.
            Assert.Equal(0, cold.Hits);
            Assert.Equal(emitted.Count, cold.Misses);
            Assert.Equal(emitted.Count, cold.Writes);

            // THE STRUCTURAL CLAIM: the warm pass emitted NOTHING. Every program came off disk, nothing was
            // re-written, and no entry had to be discarded.
            Assert.Equal(emitted.Count, warm.Hits);
            Assert.Equal(0, warm.Misses);
            Assert.Equal(0, warm.Writes);
            Assert.Equal(0, warm.Discards);

            Assert.Equal(emitted.Count, loaded.Count);
            for (int i = 0; i < emitted.Count; i++) AssertSame(emitted[i], loaded[i]);
        }

        static List<(string Name, MetalMslProgram Program, uint X, uint Y, uint Z)> BuildEverything(
            MetalMslCache cache)
        {
            var built = new List<(string, MetalMslProgram, uint, uint, uint)>();

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                built.Add((program.Name,
                    MetalShaderBuild.Pair(program.VertexGlsl, program.FragmentGlsl, cache, program.Name),
                    0, 0, 0));
            }

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                (MetalMslProgram program, uint x, uint y, uint z) = MetalShaderBuild.Compute(
                    kernel.ComputeGlsl, cache, kernel.Name);
                built.Add((kernel.Name, program, x, y, z));
            }

            return built;
        }

        static void AssertSame(
            (string Name, MetalMslProgram Program, uint X, uint Y, uint Z) expected,
            (string Name, MetalMslProgram Program, uint X, uint Y, uint Z) actual)
        {
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal((expected.X, expected.Y, expected.Z), (actual.X, actual.Y, actual.Z));
            Assert.Equal(expected.Program.Stages.Count, actual.Program.Stages.Count);

            for (int i = 0; i < expected.Program.Stages.Count; i++)
            {
                MetalMslStage mine = expected.Program.Stages[i], theirs = actual.Program.Stages[i];
                Assert.Equal(mine.Stage, theirs.Stage);
                Assert.Equal(mine.EntryPointName, theirs.EntryPointName);
                Assert.True(string.Equals(mine.Msl, theirs.Msl, StringComparison.Ordinal),
                    $"{expected.Name} [{mine.Stage}]: the cached MSL is not the emitted MSL.");
            }

            Assert.Equal(expected.Program.Table.ContentKey, actual.Program.Table.ContentKey);
            Assert.Equal(expected.Program.Table.Entries().ToArray(), actual.Program.Table.Entries().ToArray());
        }

        sealed class TempCacheDirectory : IDisposable
        {
            internal TempCacheDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ke-metal-msl-corpus-" + Guid.NewGuid().ToString("N"));
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
