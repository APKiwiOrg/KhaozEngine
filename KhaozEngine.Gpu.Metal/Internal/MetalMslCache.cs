using System;
using System.IO;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DISK CACHE OF THE EMISSION, NOT OF A LIBRARY (#592, section 12.5's row-9 addendum). One file per
    /// program, named for the <see cref="MetalShaderKey"/> that identifies it, holding the MSL of every stage, the
    /// entry-point name of every stage and the binding table read off that emission.
    ///
    /// <para>
    /// WHY THIS AND NOT A <c>.metallib</c>, WHICH IS WHAT M-S7 SPECIFIED. Two measurements, both taken on an
    /// Apple M2 Max under macOS 26 before any of this was written. There is no public API that serializes an
    /// executable-type <c>MTLLibrary</c> compiled from source, and the one route that does serialize
    /// (<c>MTLLibraryTypeDynamic</c>) produces a library whose functions abort the process when asked for. And it
    /// would have bought nothing anyway: macOS already caches the MSL-to-library compile ACROSS PROCESSES, keyed
    /// on the source, at 0.02 ms warm against 68 to 98 ms for a novel source, with the compiler service warmed
    /// first so neither number is startup cost. What the OS does not touch is the ENGINE's half, GLSL to SPIR-V
    /// through glslang and then SPIR-V to MSL through SPIRV-Cross, measured at 4,168 ms for the whole shipped
    /// corpus. That is the cost this skips, and it is the same cost <c>D3D11DxbcCache</c> exists to skip on the
    /// other backend.
    /// </para>
    /// <para>
    /// EVERY FAILURE IS A MISS, and a CORRUPT ENTRY IS A MISS AND A DELETE. A read that throws, an absent
    /// directory, a truncated file, a payload from another engine version, a hash that does not match, a table
    /// that fails its structural checks: all of them fall back to emitting, none of them propagate, and the ones
    /// that read a real file remove it so the next launch does not pay for it again. The one thing never
    /// swallowed is a WRONG answer, which is why the payload is authenticated rather than trusted: a mangled
    /// binding table would bind the wrong resource and render a wrong pixel with no error anywhere, which is the
    /// class section 2.2b exists to close.
    /// </para>
    /// <para>
    /// FREE-THREADED, LIKE THE CREATION PATH IT SITS ON (M-W8). Two threads that miss on one key both emit and
    /// both write, and that is benign rather than raced: the key is a content hash and the emission under the
    /// pinned options is deterministic, so both write the SAME bytes, and the write is a rename over a
    /// process-unique temporary file, so a reader sees one whole entry or none. The counters below are the only
    /// mutable state and they are interlocked.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND CROSS-PLATFORM, exactly as the two sibling caches are. This type names no Metal type and
    /// makes no interop call, so its whole contract (the key, the path, the atomic write, the corruption
    /// behaviour, the round trip) is exercised in the headless suite on Linux and Windows too. Only macOS can
    /// turn what it stores into a library.
    /// </para>
    /// </summary>
    internal sealed class MetalMslCache
    {
        /// <summary>
        /// Overrides where the cache lives, or turns it off. A directory path relocates it (a CI leg that wants
        /// the cache inside the workspace, or a machine whose local app-data is not writable). The values
        /// <c>off</c>, <c>0</c>, <c>false</c>, <c>no</c> and <c>none</c> disable it, so a session chasing a
        /// binding or a shader problem can prove it is emitting fresh rather than believing it.
        /// </summary>
        internal const string EnvVarName = "KE_METAL_MSL_CACHE";

        /// <summary>The file extension. Named for what is inside rather than for the engine, so a directory
        /// listing is readable.</summary>
        internal const string FileExtension = ".kemsl";

        /// <summary>The cache's own folder under the local app-data root.</summary>
        internal const string Subfolder = "metal-msl";

        readonly string _directory;
        int _hits;
        int _misses;
        int _writes;
        int _discards;

        /// <summary>Creates a cache rooted at <paramref name="directory"/>, which is created on first write
        /// rather than here: a process that only ever reads should not leave a directory behind.</summary>
        internal MetalMslCache(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException(
                    "An MSL cache needs a directory. Pass null to Resolve to turn the cache off instead of "
                    + "pointing it at nothing.", nameof(directory));
            }
            _directory = directory;
        }

        /// <summary>Where entries are written. Exposed so a diagnostic line can name the real path rather than
        /// the rule that produced it.</summary>
        internal string Directory => _directory;

        /// <summary>How many programs this cache answered from disk. Read by the corpus measurement, which is
        /// what turns "the warm pass skipped the emission" into a number rather than a claim.</summary>
        internal int Hits => Volatile.Read(ref _hits);

        /// <summary>How many programs this cache could not answer, corrupt entries included.</summary>
        internal int Misses => Volatile.Read(ref _misses);

        /// <summary>How many entries this cache wrote.</summary>
        internal int Writes => Volatile.Read(ref _writes);

        /// <summary>How many entries this cache DELETED because they did not parse or did not validate. Non-zero
        /// here is the signal that something is mangling files, which is otherwise invisible: a corrupt entry and
        /// an absent one are the same slower start.</summary>
        internal int Discards => Volatile.Read(ref _discards);

        /// <summary>
        /// The default location: <c>&lt;local-app-data&gt;/KhaozEngine/metal-msl/&lt;engine version&gt;</c>.
        /// Empty when the platform reports no local application data at all, which is the signal to run without a
        /// cache rather than to invent a path in the current directory.
        /// </summary>
        internal static string DefaultDirectory()
            => GpuDiskCache.DefaultDirectory(Subfolder, MetalShaderKey.EngineVersion);

        /// <summary>
        /// The cache <paramref name="envValue"/> asks for, or null for no cache. Blank means the default
        /// location, a disable word means null, and anything else is taken as a directory path VERBATIM (no
        /// engine-version segment appended: a caller who names a directory means that directory).
        /// </summary>
        internal static MetalMslCache? Resolve(string? envValue)
            => GpuDiskCache.ResolveDirectory(envValue, Subfolder, MetalShaderKey.EngineVersion) is { } directory
                ? new MetalMslCache(directory)
                : null;

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static MetalMslCache? FromEnvironment()
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName));

        /// <summary>The file an entry lives in. Throws on a key that is not one, because a caller asking for a
        /// path means it wants a path. The two best-effort members below guard first rather than call this with
        /// something it would refuse.</summary>
        internal string PathFor(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return Path.Combine(_directory, key + FileExtension);
        }

        /// <summary>
        /// The emission stored under <paramref name="key"/>, or null on any miss. A file that is present but does
        /// not parse, does not authenticate or does not survive the table's structural checks is DELETED on the
        /// way out, because it can only fail the same way on every future launch.
        /// </summary>
        /// <param name="key">The program's content key. A null or blank one is a miss, not a throw.</param>
        /// <param name="label">A name for the program, for the message a refused table would carry.</param>
        internal MetalMslCacheEntry? TryLoad(string? key, string label)
        {
            // A KEY THAT IS NOT A KEY IS A MISS AND NOT A THROW, which is where the file plumbing's extraction
            // moved the line and this moves it back. The path used to be computed INSIDE the try that made every
            // failure a miss, so a null key answered "no entry" exactly as an unreadable directory does.
            // Computing it outside that protection turned one caller mistake into an exception out of a pair of
            // members whose whole contract is that they never raise one.
            if (string.IsNullOrWhiteSpace(key))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            string path = PathFor(key);
            byte[]? file = GpuDiskCache.TryReadAllBytes(path);
            if (file is null)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            MetalMslCacheEntry? entry = MetalMslCacheEntry.TryParse(file, key, label);
            if (entry is null)
            {
                GpuDiskCache.TryDelete(path);
                Interlocked.Increment(ref _discards);
                Interlocked.Increment(ref _misses);
                return null;
            }

            Interlocked.Increment(ref _hits);
            return entry;
        }

        /// <summary>
        /// Store <paramref name="entry"/> under <paramref name="key"/>, best effort. Returns whether it landed,
        /// for a test and for a diagnostic line, and never throws: a cache that cannot be written is a slower
        /// start and nothing else. A null or blank key is a "no" like a full disk is, rather than an exception
        /// out of a member that promises not to raise one.
        /// </summary>
        internal bool TryStore(string? key, MetalMslCacheEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrWhiteSpace(key)) return false;   // a miss, for the reason TryLoad states

            if (!GpuDiskCache.TryWriteAtomic(PathFor(key), entry.Serialize(key))) return false;

            Interlocked.Increment(ref _writes);
            return true;
        }
    }
}
