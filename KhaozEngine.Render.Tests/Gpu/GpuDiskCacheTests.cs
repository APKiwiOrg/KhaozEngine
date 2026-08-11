using System;
using System.IO;
using System.Runtime.Versioning;
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

        // ---- pruning the version folders earlier releases left behind (#611) -----------------------------

        /// <summary>
        /// THE SWEEP AT CACHE OPEN. All three backends put the engine version in the PATH so an upgrade leaves
        /// one obviously prunable folder, and until #611 nothing ever pruned one, so a machine accumulated a
        /// folder per engine version it had ever run. The running version's own folder survives, with everything
        /// in it, and so does anything under the parent that is not a directory.
        /// </summary>
        [Fact]
        public void OldVersionFolders_GoAndTheRunningOneSurvives()
        {
            using var temp = new TempDirectory();
            string running = MakeVersionFolder(temp.Path, Version);
            string old = MakeVersionFolder(temp.Path, "1.2.2");
            string older = MakeVersionFolder(temp.Path, "0.9.0");
            string newer = MakeVersionFolder(temp.Path, "1.3.0");   // a downgrade in progress, not spared
            string loose = Path.Combine(temp.Path, "not-a-version-folder.txt");
            File.WriteAllText(loose, "a file beside them is not a version folder");

            Assert.Equal(3, GpuDiskCache.PruneOtherVersions(running));

            Assert.True(Directory.Exists(running));
            Assert.True(File.Exists(Path.Combine(running, "entry.bin")));
            Assert.False(Directory.Exists(old));
            Assert.False(Directory.Exists(older));
            Assert.False(Directory.Exists(newer));
            Assert.True(File.Exists(loose));

            // Idempotent: a second open has nothing left to do rather than something to fail on.
            Assert.Equal(0, GpuDiskCache.PruneOtherVersions(running));
        }

        /// <summary>A trailing separator is the same directory, so it must not read as a nameless leaf whose
        /// parent is the version folder itself, which would sweep the running version's own contents.</summary>
        [Fact]
        public void ATrailingSeparator_NamesTheSameVersionFolder()
        {
            using var temp = new TempDirectory();
            string running = MakeVersionFolder(temp.Path, Version);
            MakeVersionFolder(temp.Path, "1.2.2");

            Assert.Equal(1, GpuDiskCache.PruneOtherVersions(running + Path.DirectorySeparatorChar));
            Assert.True(File.Exists(Path.Combine(running, "entry.bin")));
        }

        /// <summary>
        /// A SIBLING THAT WILL NOT DELETE IS SKIPPED ON ITS OWN, so one locked folder cannot stop the others and
        /// nothing propagates. The arrangement is POSIX mode bits, which is why this leg is Unix-only, and it
        /// does not bind for a process running as root, so the survival half is asserted only where a probe
        /// proves the arrangement took. The never-throws half and the other sibling are asserted either way.
        /// </summary>
        [Fact]
        public void ASiblingThatWillNotDelete_IsSkippedRatherThanThrown()
        {
            if (OperatingSystem.IsWindows()) return;   // the arrangement below is POSIX mode bits

            AssertABlockedSiblingIsSkipped();
        }

        [UnsupportedOSPlatform("windows")]
        static void AssertABlockedSiblingIsSkipped()
        {
            using var temp = new TempDirectory();
            string running = MakeVersionFolder(temp.Path, Version);
            string deletable = MakeVersionFolder(temp.Path, "1.2.2");
            string blocked = MakeVersionFolder(temp.Path, "1.1.0");
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                GpuDiskCache.PruneOtherVersions(running);   // must not throw

                Assert.True(Directory.Exists(running));
                Assert.False(Directory.Exists(deletable));   // the refusal did not stop the sweep
                if (ModeBitsBindHere(temp.Path)) Assert.True(Directory.Exists(blocked));
            }
            finally
            {
                if (Directory.Exists(blocked)) File.SetUnixFileMode(blocked, UnixFileMode.UserRead
                    | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        /// <summary>A layout with no version folders under the parent has nothing to prune, and a parent that is
        /// not there at all is not a failure either.</summary>
        [Fact]
        public void ALayoutWithNoVersionFolders_PrunesNothing()
        {
            using var temp = new TempDirectory();
            string only = MakeVersionFolder(temp.Path, Version);
            File.WriteAllText(Path.Combine(temp.Path, "stray.bin"), "not a directory");

            Assert.Equal(0, GpuDiskCache.PruneOtherVersions(only));
            Assert.True(File.Exists(Path.Combine(temp.Path, "stray.bin")));

            Assert.Equal(0, GpuDiskCache.PruneOtherVersions(Path.Combine(temp.Path, "absent", Version)));
            Assert.Equal(0, GpuDiskCache.PruneOtherVersions("   "));
        }

        /// <summary>
        /// THE SWEEP IS GATED ON THE DEFAULT DIRECTORY. An explicitly configured one is taken verbatim with no
        /// version segment, so its neighbours are whatever the caller keeps there and deleting them would be
        /// deleting the caller's own files. A disable word still resolves to no cache and prunes nothing.
        /// </summary>
        [Fact]
        public void AnExplicitlyConfiguredDirectory_IsOpenedWithoutPruningItsNeighbours()
        {
            using var temp = new TempDirectory();
            string configured = MakeVersionFolder(temp.Path, Version);
            string neighbour = MakeVersionFolder(temp.Path, "something-else-the-caller-keeps");

            Assert.Equal(configured, GpuDiskCache.OpenDirectory(configured, Subfolder, Version));

            Assert.True(Directory.Exists(neighbour));
            Assert.True(File.Exists(Path.Combine(neighbour, "entry.bin")));
            Assert.Null(GpuDiskCache.OpenDirectory("off", Subfolder, Version));
        }

        /// <summary>The default location is opened AND swept, which is the whole point, and the answer is still
        /// the directory <see cref="GpuDiskCache.ResolveDirectory"/> would have given.</summary>
        [Fact]
        public void TheDefaultDirectory_IsOpenedAndSwept()
        {
            string expected = GpuDiskCache.DefaultDirectory(OpenSubfolder, Version);
            if (expected.Length == 0) return;   // a platform with no local app data runs without a cache

            string? parent = Path.GetDirectoryName(expected);
            Assert.NotNull(parent);

            try
            {
                string stale = MakeVersionFolder(parent!, "0.0.1");

                Assert.Equal(expected, GpuDiskCache.OpenDirectory(null, OpenSubfolder, Version));
                Assert.False(Directory.Exists(stale));
            }
            finally
            {
                if (Directory.Exists(parent!)) Directory.Delete(parent!, recursive: true);
            }
        }

        /// <summary>A subfolder of this test's own, so the sweep above can only ever touch a tree this test
        /// made. Sharing <see cref="Subfolder"/> with the pure resolution cases would be a test that deletes
        /// whatever another one left behind.</summary>
        const string OpenSubfolder = "test-disk-cache-open";

        static string MakeVersionFolder(string parent, string name)
        {
            string path = Path.Combine(parent, name);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[] { 1 });
            return path;
        }

        // True when POSIX mode bits actually deny this process, which they do not for root.
        [UnsupportedOSPlatform("windows")]
        static bool ModeBitsBindHere(string parent)
        {
            string probe = Path.Combine(parent, "mode-probe");
            Directory.CreateDirectory(probe);
            File.WriteAllBytes(Path.Combine(probe, "entry.bin"), new byte[] { 1 });
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                Directory.Delete(probe, recursive: true);
                return false;   // it deleted anyway, so the arrangement proves nothing here
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
            finally
            {
                if (Directory.Exists(probe))
                {
                    File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute);
                    Directory.Delete(probe, recursive: true);
                }
            }
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
