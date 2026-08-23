using System;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The memo in front of glslang (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/640">#640</see>):
    /// that it answers a repeat with the same bytes, that it keeps two option sets and two stages apart, that it
    /// hands every caller its own array, and that it stops growing at its capacity.
    /// <para>
    /// DEVICE-FREE AND ON EVERY LEG. glslang ships per RID and runs on the CPU, so these are plain
    /// <see cref="FactAttribute"/>s in the fast <c>ci.yml</c> loop. Every case asserts on a cache IT built, never on
    /// <c>SpirvCompileCache.Shared</c>, so no counter another class can move is read here and the class needs no
    /// serial collection. The miss callback goes through <see cref="SpirvFrontEnd"/>, which has a memo of its own,
    /// and that is deliberate: it makes these cases cost one compile between them, and every assertion below is on
    /// the instance's counters, which count callback invocations rather than glslang runs.
    /// </para>
    /// </summary>
    public sealed class SpirvCompileCacheTests
    {
        const string TrivialVert = @"#version 450
void main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }";

        const string OtherVert = @"#version 450
void main() { gl_Position = vec4(1.0, 0.0, 0.0, 1.0); }";

        /// <summary>Valid as either stage, which is what the stage-in-the-key case needs: a source only one stage
        /// accepts would prove the key by failing rather than by producing two modules.</summary>
        const string StageAgnostic = @"#version 450
void main() { }";

        static SpirvCompileCache New(int capacity = SpirvCompileCache.DefaultCapacity)
            => new(enabled: true, capacity);

        static byte[] Compile(SpirvCompileCache cache, string glsl, GpuShaderStages stage = GpuShaderStages.Vertex,
            string identity = "test") =>
            cache.GetOrCompile(identity, stage, glsl, () => SpirvFrontEnd.ToSpirv(glsl, stage, "probe"));

        [Fact]
        public void A_repeat_is_served_from_the_memo_and_is_byte_identical()
        {
            SpirvCompileCache cache = New();

            byte[] first = Compile(cache, TrivialVert);
            byte[] second = Compile(cache, TrivialVert);

            Assert.Equal(1L, cache.CompileCount);
            Assert.Equal(1L, cache.HitCount);
            Assert.True(first.AsSpan().SequenceEqual(second),
                "A memo hit must be the module the miss produced, byte for byte, or the cache is changing what "
                + "the engine renders rather than only how long it takes to get there.");
        }

        [Fact]
        public void Every_caller_gets_its_own_array()
        {
            SpirvCompileCache cache = New();

            byte[] first = Compile(cache, TrivialVert);
            byte[] second = Compile(cache, TrivialVert);

            Assert.NotSame(first, second);

            // Mutating one caller's copy cannot reach the next caller's. The cached module is shared state a
            // caller has no way of knowing it is holding, so the copy is what keeps the old contract intact.
            first[0] = unchecked((byte)~first[0]);
            byte[] third = Compile(cache, TrivialVert);
            Assert.True(second.AsSpan().SequenceEqual(third));
        }

        [Fact]
        public void A_different_source_and_a_different_options_identity_are_different_entries()
        {
            SpirvCompileCache cache = New();

            Compile(cache, TrivialVert);
            Compile(cache, OtherVert);
            Compile(cache, TrivialVert, identity: "another options set");

            Assert.Equal(3L, cache.CompileCount);
            Assert.Equal(0L, cache.HitCount);
            Assert.Equal(3, cache.Count);
        }

        [Fact]
        public void The_stage_is_part_of_the_key()
        {
            // One source compiled as two stages is two modules, and they are not interchangeable: the execution
            // model is in the SPIR-V itself. A key that dropped the stage would hand the second caller the first
            // caller's module, which is a wrong module rather than a slow one.
            SpirvCompileCache cache = New();

            byte[] asVertex = Compile(cache, StageAgnostic);
            byte[] asFragment = Compile(cache, StageAgnostic, GpuShaderStages.Fragment);

            Assert.Equal(2L, cache.CompileCount);
            Assert.Equal(2, cache.Count);
            Assert.False(asVertex.AsSpan().SequenceEqual(asFragment));
        }

        [Fact]
        public void A_failed_compile_is_not_cached()
        {
            SpirvCompileCache cache = New();

            Assert.Throws<ShaderValidationException>(() => Compile(cache, "not glsl at all"));
            Assert.Throws<ShaderValidationException>(() => Compile(cache, "not glsl at all"));

            Assert.Equal(2L, cache.CompileCount);
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void Past_its_capacity_it_stops_inserting_and_keeps_answering()
        {
            SpirvCompileCache cache = New(capacity: 1);

            byte[] held = Compile(cache, TrivialVert);
            byte[] overflow = Compile(cache, OtherVert);
            byte[] overflowAgain = Compile(cache, OtherVert);

            Assert.Equal(1, cache.Count);
            Assert.Equal(3L, cache.CompileCount);
            Assert.True(held.AsSpan().SequenceEqual(Compile(cache, TrivialVert)));
            Assert.True(overflow.AsSpan().SequenceEqual(overflowAgain),
                "A source past the capacity is compiled every time, and every one of those compiles has to be the "
                + "same module the first one produced.");
        }

        [Fact]
        public void Disabled_it_compiles_every_time()
        {
            var cache = new SpirvCompileCache(enabled: false, SpirvCompileCache.DefaultCapacity);

            byte[] first = Compile(cache, TrivialVert);
            byte[] second = Compile(cache, TrivialVert);

            Assert.Equal(2L, cache.CompileCount);
            Assert.Equal(0L, cache.HitCount);
            Assert.Equal(0, cache.Count);
            Assert.True(first.AsSpan().SequenceEqual(second));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("1", true)]
        [InlineData("on", true)]
        [InlineData("off", false)]
        [InlineData("OFF", false)]
        [InlineData(" 0 ", false)]
        [InlineData("false", false)]
        [InlineData("no", false)]
        [InlineData("none", false)]
        public void The_kill_switch_takes_the_same_words_the_disk_caches_take(string? value, bool expected)
        {
            Assert.Equal(expected, SpirvCompileCache.IsEnabled(value));
        }

        [Fact]
        public async Task Concurrent_callers_all_get_a_correct_module()
        {
            // The insert is outside a lock, so two threads that both miss both compile and one entry is dropped.
            // What must never happen is a caller getting bytes that are not its source's module.
            SpirvCompileCache cache = New();
            byte[] expected = SpirvFrontEnd.ToSpirv(TrivialVert, GpuShaderStages.Vertex, "probe");

            var running = new Task<byte[]>[8];
            for (int i = 0; i < running.Length; i++) running[i] = Task.Run(() => Compile(cache, TrivialVert));
            byte[][] results = await Task.WhenAll(running);

            Assert.All(results, r => Assert.True(r.AsSpan().SequenceEqual(expected)));
            Assert.Equal(1, cache.Count);
        }
    }
}
