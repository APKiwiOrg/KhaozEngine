using System;
using System.IO;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DISK DXBC CACHE, decision S4. One file per compiled stage, named for the
    /// <see cref="D3D11ShaderKey"/> that identifies it, under a per-user cache directory. A cold start
    /// cross-compiles and FXC-compiles roughly thirty graphics programs plus the compute kernels, and every
    /// subsequent start on the same engine version reads them back instead.
    ///
    /// <para>
    /// WINDOWS-ONLY BY NATURE, NOT BY GUARD, and the distinction is what lets this type be tested here. The bytes
    /// it stores are FXC output, so nothing else can ever produce them and no other platform will ever ask for
    /// one. But the type itself names no Direct3D type and does no interop: it is a keyed byte-array store, so it
    /// is device-free, loads nothing, and its whole contract (the key, the path, the atomic write, the corruption
    /// behaviour) is exercised in the headless suite on macOS and Linux like any other file-backed cache.
    /// </para>
    /// <para>
    /// EVERY FAILURE IS A MISS. A read that throws, a directory that cannot be created, a file that is truncated,
    /// a disk that is full or read-only: all of them fall back to compiling, and none of them propagate. That is
    /// the whole risk posture of a cache whose only job is to save time. The one thing NOT swallowed is a wrong
    /// answer, which is why the key covers everything that can change the bytes rather than being a file name
    /// derived from a shader's role.
    /// </para>
    /// <para>
    /// WHY NOT <c>AppDataPaths</c>. That type resolves the ROAMING per-app data directory for saves, settings and
    /// logs, and it lives in <c>KhaozEngine.App</c>, which this backend does not reference and should not: a GPU
    /// backend has no business depending on the application-lifecycle package. Compiled shader bytes are DERIVED
    /// data, reproducible from the sources at any time, so they belong under
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> (<c>%LOCALAPPDATA%</c> on Windows, the
    /// non-roaming half) where a user or a cleanup tool can delete the whole tree with no loss. The engine version
    /// is a path SEGMENT for the same reason: an upgrade leaves one obviously prunable folder behind rather than
    /// files that are unreachable and never expire.
    /// </para>
    /// <para>
    /// THE FILE PLUMBING ITSELF IS <see cref="GpuDiskCache"/> NOW, and what stays here is everything
    /// above it: the key this cache is keyed on, the extension, the subfolder and the empty-entry rule. That split
    /// is the one row 18 of the Metal design ruled on, refusing to share the KEY between backends that have
    /// nothing in common there and recording the plumbing as duplicated. The Metal MSL cache made it three copies,
    /// which is where the rule of three fires
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/606">#606</see>).
    /// </para>
    /// </summary>
    internal sealed class D3D11DxbcCache
    {
        /// <summary>
        /// Overrides where the cache lives, or turns it off. A directory path relocates it (useful for a CI leg
        /// that wants the cache inside the workspace, or a machine whose local app-data is not writable). The
        /// values <c>off</c>, <c>0</c>, <c>false</c>, <c>no</c> and <c>none</c> disable it, so a session chasing a
        /// shader miscompile can prove it is compiling fresh rather than believing it.
        /// </summary>
        internal const string EnvVarName = "KE_D3D11_SHADER_CACHE";

        /// <summary>The file extension. Named for what is inside rather than for the engine, so a directory
        /// listing is readable.</summary>
        internal const string FileExtension = ".dxbc";

        readonly string _directory;

        /// <summary>Creates a cache rooted at <paramref name="directory"/>, which is created on first write
        /// rather than here: a process that only ever reads (every entry already present, or the cache empty and
        /// every compile failing) should not leave a directory behind.</summary>
        internal D3D11DxbcCache(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException(
                    "A DXBC cache needs a directory. Pass null to Resolve to turn the cache off instead of "
                    + "pointing it at nothing.", nameof(directory));
            }
            _directory = directory;
        }

        /// <summary>Where entries are written. Exposed so a diagnostic line can name the real path rather than
        /// the rule that produced it.</summary>
        internal string Directory => _directory;

        /// <summary>The cache's own folder under the local app-data root.</summary>
        internal const string Subfolder = "d3d11-dxbc";

        /// <summary>
        /// The default location: <c>&lt;local-app-data&gt;/KhaozEngine/d3d11-dxbc/&lt;engine version&gt;</c>.
        /// Empty when the platform reports no local application data at all, which is the signal to run without a
        /// cache rather than to invent a path in the current directory.
        /// </summary>
        internal static string DefaultDirectory()
            => GpuDiskCache.DefaultDirectory(Subfolder, D3D11ShaderKey.EngineVersion);

        /// <summary>
        /// The cache <paramref name="envValue"/> asks for, or null for no cache. Blank means the default
        /// location, a disable word means null, and anything else is taken as a directory path VERBATIM (no
        /// engine-version segment appended: a caller who names a directory means that directory).
        /// <para>
        /// THE PURE DECISION, AND ONLY THE DECISION. Nothing in the shipped path calls it, which is deliberate
        /// rather than an oversight: <see cref="FromEnvironment"/> is the OPEN, and an open also prunes. Wiring
        /// this back into it would drop that sweep silently, and pruning here would make a member tests call
        /// freely delete folders as a side effect.
        /// </para>
        /// </summary>
        internal static D3D11DxbcCache? Resolve(string? envValue)
            => GpuDiskCache.ResolveDirectory(envValue, Subfolder, D3D11ShaderKey.EngineVersion) is { } directory
                ? new D3D11DxbcCache(directory)
                : null;

        /// <summary>
        /// The same decision read from the live environment, and the cache OPEN rather than only the decision.
        /// The one impure member here, in both senses: it reads the environment, and when the answer is the
        /// default location it sweeps the sibling engine-version folders left behind by earlier releases
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/611">#611</see>). Never an explicitly
        /// configured directory, which has no version segment and therefore no siblings to reason about.
        /// </summary>
        internal static D3D11DxbcCache? FromEnvironment()
            => GpuDiskCache.OpenDirectory(
                Environment.GetEnvironmentVariable(EnvVarName), Subfolder, D3D11ShaderKey.EngineVersion)
                    is { } directory
                ? new D3D11DxbcCache(directory)
                : null;

        /// <summary>The file an entry lives in. Throws on a key that is not one, because a caller asking for a
        /// path means it wants a path. The two best-effort members below guard first rather than call this with
        /// something it would refuse.</summary>
        internal string PathFor(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return Path.Combine(_directory, key + FileExtension);
        }

        /// <summary>
        /// The cached DXBC for <paramref name="key"/>, or null on any miss, a null or blank key included. An
        /// empty file is treated as a miss rather than as empty bytes: a zero-length DXBC cannot be a shader, so
        /// it is a half-written entry from a process that died, and handing it to <c>CreateVertexShader</c> would
        /// fail somewhere far less informative than here.
        /// <para>
        /// A KEY THAT IS NOT A KEY IS A MISS AND NOT A THROW. The path used to be computed INSIDE the try that
        /// made every failure a miss, so a null key answered "no entry" exactly as an unreadable directory does.
        /// Moving the file plumbing out to <see cref="GpuDiskCache"/> left the computation outside that
        /// protection, which turned one caller mistake into an exception out of a pair of members whose whole
        /// contract is that they never raise one.
        /// </para>
        /// </summary>
        internal byte[]? TryRead(string? key)
            => string.IsNullOrWhiteSpace(key) ? null : GpuDiskCache.TryReadAllBytes(PathFor(key));

        /// <summary>
        /// Store <paramref name="dxbc"/> under <paramref name="key"/>, best effort. Returns whether it landed,
        /// for a test and for a diagnostic line, and never throws: a cache that cannot be written is a slower
        /// start and nothing else.
        /// <para>
        /// Written to a unique temporary name in the same directory and MOVED into place, so a reader either sees
        /// the whole entry or no entry. A plain write leaves a truncated file behind when the process dies
        /// mid-write, and a truncated DXBC is exactly the shape that reaches the driver and fails obscurely. The
        /// move is per-process-unique rather than a fixed <c>.tmp</c>, so two processes compiling the same shader
        /// at once cannot overwrite each other's partial file.
        /// </para>
        /// </summary>
        internal bool TryWrite(string? key, ReadOnlySpan<byte> dxbc)
            => !string.IsNullOrWhiteSpace(key) && GpuDiskCache.TryWriteAtomic(PathFor(key), dxbc);
    }
}
