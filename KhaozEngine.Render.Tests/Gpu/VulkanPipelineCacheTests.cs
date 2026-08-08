using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PERSISTED <c>VkPipelineCache</c> (V-S7, 12.4), AND MEASUREMENT MV8's CORRUPTION HALF. The incumbent
    /// passes <c>VkPipelineCache.Null</c> at both creation sites, so every launch recompiles every pipeline from
    /// SPIR-V. This row persists one, and the bet MV8 states is that the vendor-supplied
    /// <c>pipelineCacheUUID</c> plus a header check is enough that a stale or corrupt file can never crash a
    /// launch. Work-breakdown row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>THE CORRUPTION TESTS ARE THE POINT OF THIS FILE.</b> MV8 asks for a deliberate corruption test
    /// that truncates and mutates the file and asserts a clean discard, and the reason it is a REQUIREMENT rather
    /// than a nice-to-have is that a corrupt cache is a crash class: the driver parses whatever
    /// <c>pCacheData</c> points at. The kill switch is that the whole path is best-effort by construction, so
    /// there is nothing to switch off, which is exactly what these assertions are checking.</para>
    ///
    /// <para><b>ALL DEVICE-FREE.</b> <see cref="VulkanPipelineCacheFile"/> names no Vulkan handle and makes no
    /// driver call, and the live wrapper is driven against the fake pipeline seam, so the whole lifecycle runs on
    /// a machine with no loader.</para>
    /// </summary>
    public sealed class VulkanPipelineCacheTests
    {
        static readonly byte[] uuid =
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        // ---- the header check ----

        /// <summary>A BLOB THIS DEVICE WROTE ROUND-TRIPS, which is the assertion every negative one below is
        /// measured against: a check that rejected everything would pass all of them.</summary>
        [Fact]
        public void ABlobThisDeviceWrote_RoundTrips()
        {
            using var directory = new TempCacheDirectory();
            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(directory.Path, identity);

            byte[] blob = Blob(identity, bodyBytes: 64);

            Assert.True(file.TryWrite(blob));
            Assert.Equal(blob, file.TryRead());
        }

        /// <summary>
        /// A TRUNCATED FILE IS DISCARDED, at every truncation point, which is MV8's first named case. A file cut
        /// short of a whole header cannot even be inspected, and one cut inside the body still has a valid header,
        /// so the length check has to come first and cover both.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(31)]
        public void ATruncatedFile_IsDiscarded(int length)
        {
            using var directory = new TempCacheDirectory();
            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(directory.Path, identity);

            Directory.CreateDirectory(directory.Path);
            File.WriteAllBytes(file.Path, Blob(identity, bodyBytes: 64).Take(length).ToArray());

            Assert.Null(file.TryRead());
        }

        /// <summary>
        /// A MUTATED HEADER IS DISCARDED, field by field, which is MV8's second named case. Every offset below is
        /// one of the five things the header states, and each is checked separately so a check that happened to
        /// cover four of them cannot pass by covering the fifth twice.
        /// </summary>
        [Theory]
        [InlineData(0)]    // headerSize
        [InlineData(4)]    // headerVersion
        [InlineData(8)]    // vendorID
        [InlineData(12)]   // deviceID
        [InlineData(16)]   // the first byte of pipelineCacheUUID
        [InlineData(31)]   // the last byte of pipelineCacheUUID
        public void AMutatedHeaderByte_IsDiscarded(int offset)
        {
            using var directory = new TempCacheDirectory();
            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(directory.Path, identity);

            byte[] blob = Blob(identity, bodyBytes: 64);
            Assert.True(file.TryWrite(blob));

            blob[offset] ^= 0xFF;
            File.WriteAllBytes(file.Path, blob);

            Assert.Null(file.TryRead());
        }

        /// <summary>
        /// A HEADER FROM ANOTHER DEVICE IS DISCARDED, which is the case the cache exists to survive rather than a
        /// corruption at all: a machine with two GPUs, or a driver update that moved the UUID. It is asserted
        /// through <see cref="VulkanPipelineCacheFile.Validate"/> directly, because the file NAME already differs
        /// so a full round trip would never open it.
        /// </summary>
        [Fact]
        public void AHeaderFromAnotherDevice_DoesNotValidate()
        {
            VulkanPipelineCacheIdentity mine = Identity();
            byte[] blob = Blob(mine, bodyBytes: 16);

            Assert.True(VulkanPipelineCacheFile.Validate(blob, mine));
            Assert.False(VulkanPipelineCacheFile.Validate(blob, Identity(vendor: 0x8086)));
            Assert.False(VulkanPipelineCacheFile.Validate(blob, Identity(device: 0x4321)));
            Assert.False(VulkanPipelineCacheFile.Validate(blob, Identity(uuidSeed: 99)));
        }

        /// <summary>A HEADER-ONLY BLOB IS VALID, because that is what a driver hands back for a cache nothing was
        /// ever compiled into. Treating it as corrupt would discard a legal file on every cold run.</summary>
        [Fact]
        public void AHeaderOnlyBlob_IsValid()
        {
            VulkanPipelineCacheIdentity identity = Identity();

            Assert.True(VulkanPipelineCacheFile.Validate(Blob(identity, bodyBytes: 0), identity));
        }

        /// <summary>A BLOB THAT DOES NOT VALIDATE IS NEVER WRITTEN, so the read side cannot be handed a file the
        /// write side made and then rejected.</summary>
        [Fact]
        public void ABlobThatDoesNotValidate_IsNotWritten()
        {
            using var directory = new TempCacheDirectory();
            var file = new VulkanPipelineCacheFile(directory.Path, Identity());

            Assert.False(file.TryWrite([1, 2, 3]));
            Assert.False(File.Exists(file.Path));
        }

        // ---- the file itself ----

        /// <summary>A MISSING FILE IS A MISS, and reading one leaves no directory behind: a process that only ever
        /// reads should not litter.</summary>
        [Fact]
        public void AMissingFile_IsAMissAndCreatesNothing()
        {
            using var directory = new TempCacheDirectory();
            var file = new VulkanPipelineCacheFile(directory.Path, Identity());

            Assert.Null(file.TryRead());
            Assert.False(Directory.Exists(directory.Path));
        }

        /// <summary>
        /// THE WRITE LEAVES NO TEMPORARY BEHIND, which is the observable half of the atomic write. A plain write
        /// leaves a truncated file when the process dies mid-write, and a truncated pipeline cache is precisely
        /// the shape this whole type exists to keep away from a driver.
        /// </summary>
        [Fact]
        public void TheWrite_LeavesNoTemporaryBehind()
        {
            using var directory = new TempCacheDirectory();
            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(directory.Path, identity);

            Assert.True(file.TryWrite(Blob(identity, bodyBytes: 32)));
            Assert.True(file.TryWrite(Blob(identity, bodyBytes: 48)));

            Assert.Single(Directory.GetFiles(directory.Path));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        /// <summary>
        /// A DIRECTORY THAT CANNOT BE CREATED IS A MISS RATHER THAN A THROW. Every failure on this path is a
        /// slower start and nothing else, which is the whole risk posture of a cache whose only job is to save
        /// time.
        /// </summary>
        [Fact]
        public void ADirectoryThatCannotBeCreated_IsAMissRatherThanAThrow()
        {
            using var directory = new TempCacheDirectory();
            Directory.CreateDirectory(directory.Path);

            // A FILE where the cache wants a directory, which no platform will let a directory be created inside.
            string blocked = Path.Combine(directory.Path, "blocked");
            File.WriteAllText(blocked, "not a directory");

            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(blocked, identity);

            Assert.False(file.TryWrite(Blob(identity, bodyBytes: 16)));
            Assert.Null(file.TryRead());
        }

        /// <summary>
        /// THE KEY COVERS THE UUID, THE IDS AND THE DRIVER VERSION, which is the issue's
        /// <c>(pipelineCacheUUID, driverVersion, engine version)</c> key with the engine version riding the
        /// directory instead. The driver version is the part no header restates, so a driver update that keeps its
        /// UUID and changes its blob layout is caught by never opening the old file rather than by reading it and
        /// rejecting it.
        /// </summary>
        [Fact]
        public void TheKey_CoversTheUuidTheIdsAndTheDriverVersion()
        {
            string mine = new VulkanPipelineCacheFile("dir", Identity()).Path;

            Assert.NotEqual(mine, new VulkanPipelineCacheFile("dir", Identity(vendor: 0x8086)).Path);
            Assert.NotEqual(mine, new VulkanPipelineCacheFile("dir", Identity(device: 0x4321)).Path);
            Assert.NotEqual(mine, new VulkanPipelineCacheFile("dir", Identity(driver: 7)).Path);
            Assert.NotEqual(mine, new VulkanPipelineCacheFile("dir", Identity(uuidSeed: 42)).Path);

            Assert.Equal(mine, new VulkanPipelineCacheFile("dir", Identity()).Path);
        }

        /// <summary>
        /// THE ENGINE VERSION IS A PATH SEGMENT, so an upgrade leaves one obviously prunable folder rather than
        /// files nothing will ever open again. The same reasoning <c>D3D11DxbcCache</c> gives for the derived-data
        /// location it shares.
        /// </summary>
        [Fact]
        public void TheDefaultDirectory_CarriesTheEngineVersionAsASegment()
        {
            string directory = VulkanPipelineCacheFile.DefaultDirectory();

            // Empty is the honest answer on a platform that reports no local application data at all, and is the
            // signal to run without a cache rather than to invent a path in the current directory.
            if (directory.Length == 0) return;

            Assert.EndsWith(VulkanPipelineCacheFile.EngineVersion, directory, StringComparison.Ordinal);
            Assert.Contains("vulkan-pipeline-cache", directory, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ENVIRONMENT VARIABLE RELOCATES THE CACHE OR TURNS IT OFF, so a session chasing a pipeline
        /// miscompile can prove it is compiling fresh rather than believing it, and a CI leg can put the cache
        /// inside its workspace.
        /// </summary>
        [Theory]
        [InlineData("off")]
        [InlineData("0")]
        [InlineData("FALSE")]
        [InlineData("no")]
        [InlineData("none")]
        public void ADisableWord_TurnsTheCacheOff(string value)
            => Assert.Null(VulkanPipelineCacheFile.Resolve(value, Identity()));

        /// <summary>A directory value is taken VERBATIM, with no engine-version segment appended, because a caller
        /// who names a directory means that directory.</summary>
        [Fact]
        public void ADirectoryValue_IsTakenVerbatim()
        {
            VulkanPipelineCacheFile? file = VulkanPipelineCacheFile.Resolve("  /tmp/ke-vk  ", Identity());

            Assert.NotNull(file);
            Assert.StartsWith(Path.Combine("/tmp/ke-vk"), file!.Path, StringComparison.Ordinal);
        }

        // ---- the live cache ----

        /// <summary>
        /// A COLD START CREATES AN EMPTY CACHE AND A WARM ONE IS SEEDED FROM THE FILE, which is the pair MV8
        /// measures a startup time across.
        /// </summary>
        [Fact]
        public void AColdStartIsEmptyAndAWarmOneIsSeeded()
        {
            using var directory = new TempCacheDirectory();
            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(directory.Path, identity);

            var coldApi = new FakeVulkanPipelineApi();
            var cold = new VulkanPipelineCache(coldApi, file);

            Assert.False(cold.WarmStart);
            Assert.Empty(Assert.Single(coldApi.CacheSeeds));
            Assert.NotEqual(0UL, cold.Handle);

            byte[] blob = Blob(identity, bodyBytes: 128);
            coldApi.CacheData = blob;
            Assert.True(cold.Persist());
            cold.Destroy();

            var warmApi = new FakeVulkanPipelineApi();
            var warm = new VulkanPipelineCache(warmApi, file);

            Assert.True(warm.WarmStart);
            Assert.Equal(blob, Assert.Single(warmApi.CacheSeeds));
            Assert.Equal(blob.Length, warm.SeedBytes);
        }

        /// <summary>
        /// A DRIVER THAT REFUSES THE SEED STILL GETS A CACHE. Without the retry, one bad file would leave the
        /// process with no cache at all for its whole life, so a single unlucky blob would cost every launch after
        /// it as well. The retry costs one extra call at device creation.
        /// </summary>
        [Fact]
        public void ADriverThatRefusesTheSeed_StillGetsACache()
        {
            using var directory = new TempCacheDirectory();
            VulkanPipelineCacheIdentity identity = Identity();
            var file = new VulkanPipelineCacheFile(directory.Path, identity);
            Assert.True(file.TryWrite(Blob(identity, bodyBytes: 64)));

            var api = new FakeVulkanPipelineApi { FailCacheCreation = true };
            var cache = new VulkanPipelineCache(api, file);

            Assert.Equal(2, api.CacheSeeds.Count);
            Assert.NotEmpty(api.CacheSeeds[0]);
            Assert.Empty(api.CacheSeeds[1]);
            Assert.NotEqual(0UL, cache.Handle);
            Assert.False(cache.WarmStart);
        }

        /// <summary>
        /// NO DISK FILE STILL MAKES A LIVE CACHE, and that is worth having on its own: several shipped programs
        /// differ only in blend or depth state, so their pipelines share compiled stages within one run even when
        /// nothing is persisted between runs.
        /// </summary>
        [Fact]
        public void NoDiskFile_StillMakesALiveCache()
        {
            var api = new FakeVulkanPipelineApi { CacheData = [1, 2, 3, 4] };
            var cache = new VulkanPipelineCache(api, file: null);

            Assert.NotEqual(0UL, cache.Handle);
            Assert.False(cache.WarmStart);
            Assert.False(cache.Persist());

            cache.Destroy();
            Assert.Single(api.DestroyedCaches);
        }

        /// <summary>A DRIVER WITH NOTHING TO PERSIST WRITES NOTHING, so an empty answer from
        /// <c>vkGetPipelineCacheData</c> leaves no file rather than an unreadable one.</summary>
        [Fact]
        public void ADriverWithNothingToPersist_WritesNothing()
        {
            using var directory = new TempCacheDirectory();
            var file = new VulkanPipelineCacheFile(directory.Path, Identity());

            var cache = new VulkanPipelineCache(new FakeVulkanPipelineApi(), file);

            Assert.False(cache.Persist());
            Assert.False(File.Exists(file.Path));
            Assert.Equal(0, cache.PersistedBytes);
        }

        /// <summary>DESTROYING IS IDEMPOTENT, so a teardown that runs twice destroys one handle.</summary>
        [Fact]
        public void DestroyingTwice_EndsOneHandle()
        {
            var api = new FakeVulkanPipelineApi();
            var cache = new VulkanPipelineCache(api, file: null);

            cache.Destroy();
            cache.Destroy();

            Assert.Single(api.DestroyedCaches);
            Assert.Equal(0UL, cache.Handle);
        }

        // ---- fixtures ----

        static VulkanPipelineCacheIdentity Identity(uint vendor = 0x10DE, uint device = 0x1234,
            uint driver = 42, byte uuidSeed = 0)
        {
            byte[] bytes = uuid.ToArray();
            if (uuidSeed != 0) bytes[0] = uuidSeed;

            return new VulkanPipelineCacheIdentity(vendor, device, driver, bytes);
        }

        // A VkPipelineCacheHeaderVersionOne over a body of the requested length, in the fixed little-endian layout
        // the spec states: header size, header version, vendor id, device id, then the 16-byte UUID.
        static byte[] Blob(VulkanPipelineCacheIdentity identity, int bodyBytes)
        {
            var blob = new byte[VulkanPipelineCacheFile.HeaderLength + bodyBytes];

            BinaryPrimitives.WriteUInt32LittleEndian(blob, VulkanPipelineCacheFile.HeaderLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                blob.AsSpan(4), VulkanPipelineCacheFile.HeaderVersionOne);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), identity.VendorId);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), identity.DeviceId);
            identity.CacheUuid.CopyTo(blob.AsSpan(16));

            for (int i = 0; i < bodyBytes; i++)
            {
                blob[VulkanPipelineCacheFile.HeaderLength + i] = (byte)(i * 7 + 3);
            }

            return blob;
        }

        sealed class TempCacheDirectory : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ke-vk-pipeline-cache-" + Guid.NewGuid().ToString("N"));

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
