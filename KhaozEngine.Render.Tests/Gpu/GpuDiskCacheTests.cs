using System;
using System.IO;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SHARED FILE PLUMBING UNDER ALL THREE BACKEND CACHES, driven directly rather than through one of them.
    /// Row 18 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> refused sharing the KEY and recorded
    /// the plumbing as duplicated at two copies, and the Metal MSL cache
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/592">#592</see>) made it three, which is the
    /// trigger <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/606">#606</see> named.
    ///
    /// <para>
    /// THE THREE CALLERS' OWN TESTS STAY WHERE THEY ARE and are not thinned. <c>D3D11ShaderPathTests</c>,
    /// <c>VulkanPipelineCacheTests</c> and <c>MetalMslCacheTests</c> each assert the behaviour THEIR cache
    /// promises, which is what would catch a caller that stopped calling this correctly. What is here is the
    /// contract itself, once, including the two cases no caller can reach on purpose (a directory that is a file,
    /// and a delete of something that was never there).
    /// </para>
    /// </summary>
    public sealed class GpuDiskCacheTests
    {
        const string Subfolder = "test-disk-cache";
        const string Version = "1.2.3";

        [Theory]
        [InlineData("off")]
        [InlineData("0")]
        [InlineData("FALSE")]
        [InlineData(" no ")]
        [InlineData("none")]
        public void ADisableWord_TurnsTheCacheOff(string value)
            => Assert.Null(GpuDiskCache.ResolveDirectory(value, Subfolder, Version));

        [Fact]
        public void ADirectoryValue_IsTakenVerbatim()
        {
            string? resolved = GpuDiskCache.ResolveDirectory("/tmp/some-cache-dir", Subfolder, Version);

            Assert.Equal("/tmp/some-cache-dir", resolved);
            // No engine-version segment appended: a caller who names a directory means that directory.
            Assert.DoesNotContain(Version, resolved!, StringComparison.Ordinal);
        }

        [Fact]
        public void ABlankValue_MeansTheDefaultLocationWithTheVersionAsASegment()
        {
            string fallback = GpuDiskCache.DefaultDirectory(Subfolder, Version);
            if (fallback.Length == 0) return;   // a platform with no local app data runs without a cache

            Assert.Contains(Subfolder, fallback, StringComparison.Ordinal);
            Assert.Contains(Version, fallback, StringComparison.Ordinal);
            Assert.Equal(fallback, GpuDiskCache.ResolveDirectory(null, Subfolder, Version));
            Assert.Equal(fallback, GpuDiskCache.ResolveDirectory("   ", Subfolder, Version));
        }

        [Fact]
        public void AnEntry_RoundTripsAndTheDirectoryIsCreatedOnWrite()
        {
            using var temp = new TempDirectory();
            string path = Path.Combine(temp.Path, "nested", "entry.bin");
            byte[] payload = { 1, 2, 3, 4 };

            Assert.Null(GpuDiskCache.TryReadAllBytes(path));
            Assert.True(GpuDiskCache.TryWriteAtomic(path, payload));
            Assert.Equal(payload, GpuDiskCache.TryReadAllBytes(path));
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
        }

        /// <summary>A zero-length entry is a MISS rather than empty bytes: it is what a process that died
        /// mid-write leaves behind, and no backend's payload can legitimately be empty.</summary>
        [Fact]
        public void AZeroLengthFile_ReadsAsAMiss()
        {
            using var temp = new TempDirectory();
            string path = Path.Combine(temp.Path, "truncated.bin");
            File.WriteAllBytes(path, Array.Empty<byte>());

            Assert.Null(GpuDiskCache.TryReadAllBytes(path));
        }

        [Fact]
        public void AnEmptyPayload_IsNeverWritten()
        {
            using var temp = new TempDirectory();
            string path = Path.Combine(temp.Path, "empty.bin");

            Assert.False(GpuDiskCache.TryWriteAtomic(path, ReadOnlySpan<byte>.Empty));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void TheWrite_LeavesNoTemporaryBehind()
        {
            using var temp = new TempDirectory();
            string path = Path.Combine(temp.Path, "entry.bin");

            Assert.True(GpuDiskCache.TryWriteAtomic(path, new byte[] { 7 }));

            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
            Assert.Single(Directory.GetFiles(temp.Path));
        }

        /// <summary>Every failure is a miss and nothing propagates. Here the directory is a FILE, so both the
        /// create and the write fail at the OS.</summary>
        [Fact]
        public void ADirectoryThatCannotBeCreated_FailsSilentlyBothWays()
        {
            using var temp = new TempDirectory();
            string blocked = Path.Combine(temp.Path, "not-a-directory");
            File.WriteAllText(blocked, "this is a file");

            string path = Path.Combine(blocked, "entry.bin");
            Assert.False(GpuDiskCache.TryWriteAtomic(path, new byte[] { 1 }));
            Assert.Null(GpuDiskCache.TryReadAllBytes(path));
        }

        [Fact]
        public void DeletingSomethingThatWasNeverThere_IsNotAFailure()
        {
            using var temp = new TempDirectory();

            GpuDiskCache.TryDelete(Path.Combine(temp.Path, "absent.bin"));
            GpuDiskCache.TryDelete(temp.Path);   // a non-empty directory: also not a failure
        }

        sealed class TempDirectory : IDisposable
        {
            internal TempDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ke-disk-cache-" + Guid.NewGuid().ToString("N"));
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
