using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class VulkanSpirvDiskCacheTests
    {
        const string Pin = "shaderc/spirv;test-pin=1";
        const string Source = "#version 450\nvoid main() {}";

        [Fact]
        public void ColdCompileIsStoredAndAFreshCacheReadsTheSameBytesWithoutCompiling()
        {
            using var temp = new TempCacheDirectory();
            byte[] expected = MinimalSpirv(7);
            int compiles = 0;

            var cold = new SpirvBytesCache(temp.Path);
            byte[] first = cold.GetOrCompile(Pin, GpuShaderStages.Vertex, Source,
                () => { compiles++; return expected; });
            var warm = new SpirvBytesCache(temp.Path);
            byte[] second = warm.GetOrCompile(Pin, GpuShaderStages.Vertex, Source,
                () => { compiles++; return MinimalSpirv(99); });

            Assert.Equal(expected, first);
            Assert.Equal(expected, second);
            Assert.Equal(1, compiles);
            Assert.Equal(1, cold.Writes);
            Assert.Equal(1, warm.Hits);
        }

        [Fact]
        public void KeyIncludesToolchainPinStageAndSource()
        {
            string control = SpirvBytesCache.KeyFor("1.2.3", "shaderc-1", Pin,
                GpuShaderStages.Vertex, Source);

            Assert.Equal(control, SpirvBytesCache.KeyFor("1.2.3", "shaderc-1", Pin,
                GpuShaderStages.Vertex, Source));
            Assert.NotEqual(control, SpirvBytesCache.KeyFor("1.2.4", "shaderc-1", Pin,
                GpuShaderStages.Vertex, Source));
            Assert.NotEqual(control, SpirvBytesCache.KeyFor("1.2.3", "shaderc-2", Pin,
                GpuShaderStages.Vertex, Source));
            Assert.NotEqual(control, SpirvBytesCache.KeyFor("1.2.3", "shaderc-1", Pin + ".moved",
                GpuShaderStages.Vertex, Source));
            Assert.NotEqual(control, SpirvBytesCache.KeyFor("1.2.3", "shaderc-1", Pin,
                GpuShaderStages.Fragment, Source));
            Assert.NotEqual(control, SpirvBytesCache.KeyFor("1.2.3", "shaderc-1", Pin,
                GpuShaderStages.Vertex, Source + "\n// moved"));
        }

        [Fact]
        public void FailedCompileWritesNothingAndTheNextCallRemainsAMiss()
        {
            using var temp = new TempCacheDirectory();
            var cache = new SpirvBytesCache(temp.Path);
            string path = cache.PathFor(SpirvBytesCache.KeyFor(Pin, GpuShaderStages.Compute, Source));

            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = cache.GetOrCompile(Pin, GpuShaderStages.Compute, Source,
                    () => throw new InvalidOperationException("compile failed"));
            });
            byte[] recovered = cache.GetOrCompile(
                Pin, GpuShaderStages.Compute, Source, () => MinimalSpirv(11));

            Assert.Equal(MinimalSpirv(11), recovered);
            Assert.True(File.Exists(path));
            Assert.Equal(2, cache.Misses);
            Assert.Equal(1, cache.Writes);
        }

        [Fact]
        public void CorruptEntryIsDeletedAndReplacedBeforeItsBytesCanBeReturned()
        {
            using var temp = new TempCacheDirectory();
            var planted = new SpirvBytesCache(temp.Path);
            _ = planted.GetOrCompile(Pin, GpuShaderStages.Fragment, Source, () => MinimalSpirv(3));
            string key = SpirvBytesCache.KeyFor(Pin, GpuShaderStages.Fragment, Source);
            string path = planted.PathFor(key);
            byte[] corrupt = File.ReadAllBytes(path);
            corrupt[^1] ^= 0xff;
            File.WriteAllBytes(path, corrupt);

            var reader = new SpirvBytesCache(temp.Path);
            byte[] recovered = reader.GetOrCompile(
                Pin, GpuShaderStages.Fragment, Source, () => MinimalSpirv(5));

            Assert.Equal(MinimalSpirv(5), recovered);
            Assert.Equal(1, reader.Discards);
            Assert.Equal(1, reader.Misses);
            Assert.Equal(1, reader.Writes);
        }

        [Fact]
        public void ShaderSetUsesWarmDiskBytesBeforeCallingTheFrontEnd()
        {
            using var temp = new TempCacheDirectory();
            const string tag = "warm path";
            const string brokenVertex = "not valid vertex GLSL";
            const string brokenFragment = "not valid fragment GLSL";
            var planted = new SpirvBytesCache(temp.Path);
            _ = planted.GetOrCompile(SpirvFrontEnd.OptionsIdentity(tag), GpuShaderStages.Vertex, brokenVertex,
                () => MinimalSpirv(17));
            _ = planted.GetOrCompile(SpirvFrontEnd.OptionsIdentity(tag), GpuShaderStages.Fragment, brokenFragment,
                () => MinimalSpirv(19));

            var api = new FakeVulkanShaderApi();
            var modules = new VulkanShaderModuleCache(api);
            var warm = new SpirvBytesCache(temp.Path);
            using var set = new VulkanShaderSet(modules, brokenVertex, brokenFragment, tag, warm);

            Assert.NotEqual(0ul, set.VertexModule);
            Assert.NotEqual(0ul, set.FragmentModule);
            Assert.Equal(2, warm.Hits);
            Assert.Equal(2, api.Created.Count);
        }

        [Theory]
        [InlineData("off")]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("no")]
        [InlineData("none")]
        public void CacheCanBeDisabled(string value) => Assert.Null(SpirvBytesCache.Resolve(value));

        static byte[] MinimalSpirv(uint generator)
        {
            uint[] words = [0x07230203, 0x00010000, generator, 1, 0];
            var bytes = new byte[words.Length * sizeof(uint)];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        sealed class TempCacheDirectory : IDisposable
        {
            internal TempCacheDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ke-spirv-cache-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
        }
    }
}
