using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// Disk cache for the Vulkan front end's SPIR-V bytes. Entries are keyed by source, stage, engine version,
    /// compiler package and pinned options. Each file authenticates its payload before any byte can reach Vulkan.
    /// Every read or write failure is a cache miss.
    /// </summary>
    internal sealed class SpirvBytesCache
    {
        internal const string EnvVarName = "KE_VULKAN_SPIRV_CACHE";
        internal const string Subfolder = "vulkan-spirv";
        internal const string FileExtension = ".kespv";
        internal const string Schema = "khaozengine-vulkan-spirv-v1";

        static readonly byte[] Magic = Encoding.ASCII.GetBytes("KESPIRV1");
        const int HashLength = 32;
        const int HeaderLength = 8 + sizeof(int) + HashLength;
        const uint SpirvMagic = 0x07230203;

        internal static string EngineVersion { get; } =
            typeof(KhaozEngineVulkan).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        readonly string _directory;
        int _hits;
        int _misses;
        int _writes;
        int _discards;

        internal SpirvBytesCache(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A SPIR-V cache needs a directory.", nameof(directory));
            _directory = directory;
        }

        internal int Hits => Volatile.Read(ref _hits);
        internal int Misses => Volatile.Read(ref _misses);
        internal int Writes => Volatile.Read(ref _writes);
        internal int Discards => Volatile.Read(ref _discards);

        internal static SpirvBytesCache? Resolve(string? envValue)
            => GpuDiskCache.ResolveDirectory(envValue, Subfolder, EngineVersion) is { } directory
                ? new SpirvBytesCache(directory)
                : null;

        internal static SpirvBytesCache? FromEnvironment()
            => GpuDiskCache.OpenDirectory(
                Environment.GetEnvironmentVariable(EnvVarName), Subfolder, EngineVersion) is { } directory
                    ? new SpirvBytesCache(directory)
                    : null;

        internal string PathFor(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return Path.Combine(_directory, key + FileExtension);
        }

        internal static string KeyFor(string optionsIdentity, GpuShaderStages stage, string glsl)
            => KeyFor(EngineVersion, SpirvToolchainVersion.Identity, optionsIdentity, stage, glsl);

        internal static string KeyFor(string engineVersion, string toolchainIdentity, string optionsIdentity,
            GpuShaderStages stage, string glsl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(toolchainIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(optionsIdentity);
            ArgumentNullException.ThrowIfNull(glsl);

            string text = Schema + "\n"
                + engineVersion + "\n"
                + toolchainIdentity + "\n"
                + optionsIdentity.Length.ToString(CultureInfo.InvariantCulture) + "\n" + optionsIdentity
                + ((uint)stage).ToString(CultureInfo.InvariantCulture) + "\n"
                + glsl.Length.ToString(CultureInfo.InvariantCulture) + "\n" + glsl;
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        internal byte[] GetOrCompile(
            string optionsIdentity, GpuShaderStages stage, string glsl, Func<byte[]> compile)
        {
            ArgumentNullException.ThrowIfNull(compile);
            string key = KeyFor(optionsIdentity, stage, glsl);
            string path = PathFor(key);
            byte[]? file = GpuDiskCache.TryReadAllBytes(path);
            if (file is not null && TryParse(file) is { } hit)
            {
                Interlocked.Increment(ref _hits);
                return hit;
            }

            if (file is not null)
            {
                GpuDiskCache.TryDelete(path);
                Interlocked.Increment(ref _discards);
            }
            Interlocked.Increment(ref _misses);

            byte[] compiled = compile();
            if (IsSpirv(compiled) && GpuDiskCache.TryWriteAtomic(path, Serialize(compiled)))
                Interlocked.Increment(ref _writes);
            return compiled;
        }

        static byte[] Serialize(ReadOnlySpan<byte> spirv)
        {
            var file = new byte[HeaderLength + spirv.Length];
            Magic.CopyTo(file, 0);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(Magic.Length), spirv.Length);
            SHA256.HashData(spirv, file.AsSpan(Magic.Length + sizeof(int), HashLength));
            spirv.CopyTo(file.AsSpan(HeaderLength));
            return file;
        }

        static byte[]? TryParse(ReadOnlySpan<byte> file)
        {
            if (file.Length < HeaderLength || !file.Slice(0, Magic.Length).SequenceEqual(Magic)) return null;
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(Magic.Length, sizeof(int)));
            if (length < 0 || file.Length != HeaderLength + length) return null;
            ReadOnlySpan<byte> payload = file.Slice(HeaderLength, length);
            if (!IsSpirv(payload)) return null;

            Span<byte> actual = stackalloc byte[HashLength];
            SHA256.HashData(payload, actual);
            if (!CryptographicOperations.FixedTimeEquals(
                    actual, file.Slice(Magic.Length + sizeof(int), HashLength))) return null;
            return payload.ToArray();
        }

        static bool IsSpirv(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 5 * sizeof(uint)
                && bytes.Length % sizeof(uint) == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == SpirvMagic
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(3 * sizeof(uint))) > 0
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4 * sizeof(uint))) == 0;
    }
}
