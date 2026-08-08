using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE VALIDITY KEY OF A PERSISTED <c>VkPipelineCache</c>, WHICH THE API SUPPLIES RATHER THAN THE ENGINE
    /// INVENTING (V-S7). One draft wanted the disk cache deferred because "it needs a validity key", and the
    /// answer is that <c>VkPhysicalDeviceProperties.pipelineCacheUUID</c> IS one: the driver changes it whenever
    /// its own blobs stop being readable.
    /// <para>
    /// THE DRIVER VERSION IS CARRIED ALONGSIDE IT even though the header does not restate it, because a driver
    /// update that keeps the UUID and changes the blob layout is the case the UUID alone would miss. It rides the
    /// FILE NAME rather than the contents, so a stale entry is not read and rejected, it is simply never opened.
    /// </para>
    /// </summary>
    internal sealed class VulkanPipelineCacheIdentity
    {
        /// <summary><c>VK_UUID_SIZE</c>. A pipeline cache UUID is exactly this many bytes.</summary>
        internal const int UuidLength = 16;

        readonly byte[] _cacheUuid;

        /// <param name="vendorId">The device's <c>vendorID</c>, restated inside every cache header.</param>
        /// <param name="deviceId">The device's <c>deviceID</c>, restated inside every cache header.</param>
        /// <param name="driverVersion">The driver's own version, which no header carries.</param>
        /// <param name="cacheUuid">The device's <c>pipelineCacheUUID</c>, exactly
        /// <see cref="UuidLength"/> bytes. COPIED, because it arrives out of a fixed buffer inside a structure the
        /// physical-device read does not keep.</param>
        /// <exception cref="ArgumentException"><paramref name="cacheUuid"/> is the wrong length.</exception>
        internal VulkanPipelineCacheIdentity(uint vendorId, uint deviceId, uint driverVersion,
            ReadOnlySpan<byte> cacheUuid)
        {
            if (cacheUuid.Length != UuidLength)
            {
                throw new ArgumentException(
                    "A Vulkan pipeline cache UUID is exactly " + UuidLength.ToString(CultureInfo.InvariantCulture)
                    + " bytes and this one is " + cacheUuid.Length.ToString(CultureInfo.InvariantCulture)
                    + ", which means it did not come from VkPhysicalDeviceProperties.",
                    nameof(cacheUuid));
            }

            VendorId = vendorId;
            DeviceId = deviceId;
            DriverVersion = driverVersion;
            _cacheUuid = cacheUuid.ToArray();
        }

        /// <summary>The device's <c>vendorID</c>.</summary>
        internal uint VendorId { get; }

        /// <summary>The device's <c>deviceID</c>.</summary>
        internal uint DeviceId { get; }

        /// <summary>The driver's own version.</summary>
        internal uint DriverVersion { get; }

        /// <summary>The device's <c>pipelineCacheUUID</c>.</summary>
        internal ReadOnlySpan<byte> CacheUuid => _cacheUuid;

        /// <summary>
        /// The file-name stem this identity's cache lives under: the UUID in hex, then the vendor and device ids,
        /// then the driver version. Lowercase hex only, so it is a legal file name on every platform, and long
        /// enough that two devices in one machine never collide.
        /// </summary>
        internal string Key
            => Convert.ToHexStringLower(_cacheUuid)
                + "-" + VendorId.ToString("x8", CultureInfo.InvariantCulture)
                + DeviceId.ToString("x8", CultureInfo.InvariantCulture)
                + "-" + DriverVersion.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// THE ON-DISK HALF OF THE <c>VkPipelineCache</c> (V-S7, 12.4), and the whole of it is device-free: this type
    /// names no Vulkan handle, makes no driver call, and is a keyed byte-array store whose contract (the key, the
    /// path, the header validation, the atomic write, the corruption behaviour) runs in the headless suite like
    /// any other file-backed cache. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>WHY IT EXISTS AT ALL.</b> The incumbent passes <c>VkPipelineCache.Null</c> at BOTH pipeline
    /// creation sites, so every launch recompiles every pipeline from SPIR-V, across the shipped graphics
    /// programs and compute kernels and considerably more pipeline permutations than programs, because everything
    /// except viewport and scissor is baked into the pipeline object (see
    /// <see cref="VulkanPipelineDynamicState"/>).</para>
    ///
    /// <para><b>A CORRUPT CACHE IS A CRASH CLASS, SO THE HEADER IS VALIDATED BEFORE <c>pCacheData</c> IS EVER
    /// PASSED.</b> That is the caution one design draft raised, adopted as a REQUIREMENT rather than as a reason
    /// to defer the whole feature. <see cref="Validate"/> checks the header size, the header version, the vendor
    /// and device ids and the <c>pipelineCacheUUID</c>, and any mismatch is a silent discard. The driver is
    /// required to do the same check itself, but "required to" is not the same as "every driver on every machine
    /// does", and the file this reads is one a user, a sync tool or a half-finished write can have mangled.</para>
    ///
    /// <para><b>AND EVERY FAILURE IS A MISS.</b> A read that throws, a directory that cannot be created, a
    /// truncated file, a full or read-only disk: all of them fall back to a cold cache, and none of them
    /// propagate. That is the whole risk posture of a cache whose only job is to save time, and it is also
    /// MV8's own kill switch, so there is nothing to switch off.</para>
    ///
    /// <para><b>WHY <c>LocalApplicationData</c> AND NOT <c>AppDataPaths</c>.</b> The same reason
    /// <c>D3D11DxbcCache</c> gives: that type lives in <c>KhaozEngine.App</c>, which a GPU backend must not
    /// depend on, and a compiled pipeline blob is DERIVED data that a cleanup tool should be free to delete. The
    /// engine version is a path SEGMENT so an upgrade leaves one obviously prunable folder rather than files
    /// nothing will ever open again.</para>
    /// </summary>
    internal sealed class VulkanPipelineCacheFile
    {
        /// <summary>
        /// Relocates the cache, or turns it off. A directory path moves it (a CI leg that wants it inside the
        /// workspace, a machine whose local app-data is not writable). The values <c>off</c>, <c>0</c>,
        /// <c>false</c>, <c>no</c> and <c>none</c> disable it, so a session chasing a pipeline miscompile can
        /// prove it is compiling fresh rather than believing it.
        /// </summary>
        internal const string EnvVarName = "KE_VULKAN_PIPELINE_CACHE";

        /// <summary>The file extension. Named for what is inside rather than for the engine.</summary>
        internal const string FileExtension = ".vkpipelinecache";

        /// <summary><c>VkPipelineCacheHeaderVersionOne</c> is exactly this many bytes.</summary>
        internal const int HeaderLength = 32;

        /// <summary><c>VK_PIPELINE_CACHE_HEADER_VERSION_ONE</c>.</summary>
        internal const uint HeaderVersionOne = 1;

        readonly string _directory;
        readonly VulkanPipelineCacheIdentity _identity;

        /// <summary>Creates a cache rooted at <paramref name="directory"/>, which is created on first write rather
        /// than here, so a process that only reads leaves no directory behind.</summary>
        internal VulkanPipelineCacheFile(string directory, VulkanPipelineCacheIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException(
                    "A Vulkan pipeline cache needs a directory. Pass null to Resolve to turn the cache off "
                    + "instead of pointing it at nothing.", nameof(directory));
            }

            _directory = directory;
            _identity = identity;
        }

        /// <summary>Where the entry is written. Exposed so a diagnostic line can name the real path.</summary>
        internal string Path => System.IO.Path.Combine(_directory, _identity.Key + FileExtension);

        /// <summary>
        /// The engine version, read off this assembly, which the shared <c>&lt;KhaozEngineVersion&gt;</c> line
        /// versions, so nothing is kept in sync by hand.
        /// </summary>
        internal static string EngineVersion { get; } =
            typeof(KhaozEngineVulkan).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        /// <summary>
        /// The default location: <c>&lt;local-app-data&gt;/KhaozEngine/vulkan-pipeline-cache/&lt;engine
        /// version&gt;</c>. Empty when the platform reports no local application data, which is the signal to run
        /// without a cache rather than to invent a path in the current directory.
        /// </summary>
        internal static string DefaultDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(root)
                ? string.Empty
                : System.IO.Path.Combine(root, "KhaozEngine", "vulkan-pipeline-cache", EngineVersion);
        }

        /// <summary>
        /// The cache <paramref name="envValue"/> asks for, or null for no cache at all. Blank means the default
        /// location, a disable word means null, and anything else is taken as a directory path VERBATIM, with no
        /// engine-version segment appended, because a caller who names a directory means that directory.
        /// </summary>
        internal static VulkanPipelineCacheFile? Resolve(string? envValue, VulkanPipelineCacheIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            if (string.IsNullOrWhiteSpace(envValue))
            {
                string fallback = DefaultDirectory();
                return string.IsNullOrEmpty(fallback) ? null : new VulkanPipelineCacheFile(fallback, identity);
            }

            string value = envValue.Trim();
            return value.ToLowerInvariant() switch
            {
                "off" or "0" or "false" or "no" or "none" => null,
                _ => new VulkanPipelineCacheFile(value, identity),
            };
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static VulkanPipelineCacheFile? FromEnvironment(VulkanPipelineCacheIdentity identity)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), identity);

        /// <summary>
        /// The seed blob for <c>vkCreatePipelineCache</c>, or null on ANY miss: no file, an unreadable one, one
        /// too short to hold a header, or one whose header does not match this device. Nothing here throws.
        /// </summary>
        internal byte[]? TryRead()
        {
            try
            {
                string path = Path;
                if (!File.Exists(path)) return null;

                byte[] blob = File.ReadAllBytes(path);
                return Validate(blob, _identity) ? blob : null;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                return null;
            }
        }

        /// <summary>
        /// Store <paramref name="blob"/>, best effort. Returns whether it landed, for a test and for a diagnostic
        /// line, and never throws.
        /// <para>
        /// WRITTEN TO A PROCESS-UNIQUE TEMPORARY NAME IN THE SAME DIRECTORY AND MOVED INTO PLACE, so a reader
        /// either sees the whole entry or no entry. A plain write leaves a truncated file behind when the process
        /// dies mid-write, and a truncated pipeline cache is precisely the shape this type exists to keep away
        /// from a driver. The move is per-process-unique rather than a fixed <c>.tmp</c>, so two processes writing
        /// the same device's cache cannot overwrite each other's partial file.
        /// </para>
        /// <para>
        /// A BLOB THAT DOES NOT VALIDATE IS NOT WRITTEN. That never happens with a blob the driver just handed
        /// back, and refusing it here means the corruption test cannot pass by writing a file the read side would
        /// then reject.
        /// </para>
        /// </summary>
        internal bool TryWrite(ReadOnlySpan<byte> blob)
        {
            if (!Validate(blob, _identity)) return false;

            string temp = string.Empty;
            try
            {
                Directory.CreateDirectory(_directory);
                temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, blob);
                File.Move(temp, Path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                if (temp.Length != 0) TryDelete(temp);
                return false;
            }
        }

        /// <summary>
        /// Delete the entry, best effort, and never throw. A file that was never there is not a failure.
        /// <para>
        /// THE ONE CALLER IS THE DRIVER-REFUSED-THE-SEED PATH. A blob this type validated and the driver then
        /// rejected is otherwise only replaced at a clean teardown, so a process that dies before one leaves the
        /// same rejected file for the next launch to read, seed and be refused again, once per launch for as long
        /// as the file survives. The retry with no seed rescues the RUN, and only a delete rescues the ones after
        /// it.
        /// </para>
        /// </summary>
        internal void TryDiscard() => TryDelete(Path);

        /// <summary>
        /// THE HEADER CHECK, AS A PURE FUNCTION. True when <paramref name="blob"/> begins with a
        /// <c>VkPipelineCacheHeaderVersionOne</c> that names <paramref name="identity"/>'s device.
        /// <para>
        /// The header is five fields in a fixed little-endian layout: the header size (32), the header version
        /// (1), the vendor id, the device id, and the 16-byte <c>pipelineCacheUUID</c>. All five are checked. A
        /// blob of exactly <see cref="HeaderLength"/> bytes is VALID and is what a driver hands back for a cache
        /// nothing was ever compiled into.
        /// </para>
        /// </summary>
        internal static bool Validate(ReadOnlySpan<byte> blob, VulkanPipelineCacheIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            if (blob.Length < HeaderLength) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob) != HeaderLength) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]) != HeaderVersionOne) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]) != identity.VendorId) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob[12..]) != identity.DeviceId) return false;

            return blob.Slice(16, VulkanPipelineCacheIdentity.UuidLength).SequenceEqual(identity.CacheUuid);
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                // A file that will not delete is litter rather than a failure. A leftover temporary is replaced
                // by the next write, which takes a fresh name, and a rejected seed nothing could remove costs one
                // more refused create on the next launch.
            }
        }

        // Everything a file system can reasonably say no with. Deliberately not a bare catch: an
        // OutOfMemoryException or a cancellation is not a cache miss and must not be swallowed as one.
        static bool IsRecoverable(Exception ex)
            => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }
}
