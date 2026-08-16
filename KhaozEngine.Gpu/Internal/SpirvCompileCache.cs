using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// GLSL 450 TO SPIR-V, COMPILED ONCE PER PROCESS INSTEAD OF ONCE PER CALL
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/640">#640</see>). A memo in front of glslang,
    /// keyed on the source, the stage and the identity of the option set the caller compiles under.
    ///
    /// <para>
    /// <b>WHAT IT IS FOR, MEASURED RATHER THAN ASSUMED.</b> A <c>Scene3D</c> asks its device for 34 shader sets,
    /// which is 68 stage compiles, and on an M-series Mac those 68 calls are 2515 ms of the constructor's 2560 ms.
    /// Neither the cross-compile to MSL (21 ms) nor the driver's own shader-object creation (about 14 ms) is
    /// anywhere near it. The sources are <c>const string</c> fields, so the second scene in a process compiled the
    /// same characters to the same bytes again, and so did the tenth, and so did the first scene on a second
    /// device. That is the whole defect: it is CPU work with no device in it, repeated verbatim.
    /// </para>
    /// <para>
    /// <b>PROCESS-WIDE AND NOT PER DEVICE, WHICH IS THE POINT.</b> SPIR-V is device-free by construction: the same
    /// source under the same options is the same module whichever device is about to consume it, which is exactly
    /// why <c>VulkanShaderModuleCache</c> can key a device's modules on a hash of these bytes alone. A per-device
    /// memo would leave the shape that costs the most untouched, since a GPU test creates a device per capture and
    /// a game creates one per window, and both would recompile from scratch every time.
    /// </para>
    /// <para>
    /// <b>THE OPTIONS IDENTITY IS IN THE KEY BECAUSE TWO CALLERS OWN TWO SETS.</b> Every engine-owned compile runs
    /// through <see cref="SpirvFrontEnd"/> under <see cref="SpirvFrontEndPin"/>, and the incumbent
    /// <see cref="VeldridGpuDevice"/> deliberately keeps the library's own defaults on both of its paths. Those two
    /// sets are maintained independently, and their equality is asserted by
    /// <c>VulkanSpirvIncumbentParityTests</c> rather than held by construction, so a memo that keyed on the source
    /// alone would hand one caller the other's bytes the moment they diverged, and would do it silently. Keying on
    /// the identity string makes a divergence produce two entries instead of one wrong answer.
    /// </para>
    /// <para>
    /// <b>EVERY CALLER GETS ITS OWN ARRAY.</b> <see cref="GetOrCompile"/> hands back a copy, so the cached module
    /// cannot be mutated through a returned reference by a caller that has no idea it is holding shared state. The
    /// copy is tens of microseconds against a compile that is tens of milliseconds, and it keeps the callers'
    /// contract exactly what it was before this type existed: a fresh array they own.
    /// </para>
    /// <para>
    /// <b>BOUNDED, BECAUSE THE KEY HOLDS THE SOURCE ALIVE.</b> The engine ships 76 stage emissions across 59
    /// distinct modules, so <see cref="DefaultCapacity"/> is far above what any engine-owned run reaches. A
    /// consumer that GENERATES shader sources per frame would otherwise grow this without limit, so past the
    /// capacity the cache stops inserting and keeps compiling, which is the behaviour that existed before it. It
    /// never evicts, since an eviction policy on a set that is a compile-time constant in every shipped case is
    /// machinery with nothing to do.
    /// </para>
    /// <para>
    /// <b>THE KILL SWITCH IS <c>KE_SPIRV_CACHE</c></b>, read once, with the disable words <see cref="GpuDiskCache"/>
    /// already takes. It exists for the same reason that one's does: a session chasing a miscompile needs to be
    /// able to state that every module in the run came out of glslang rather than out of a dictionary.
    /// </para>
    /// </summary>
    internal sealed class SpirvCompileCache
    {
        /// <summary>How many distinct modules the cache will hold. Above every engine-owned run by a wide margin,
        /// and low enough that a consumer generating sources cannot grow the process without bound.</summary>
        internal const int DefaultCapacity = 512;

        /// <summary>The environment variable that switches the memo off for a whole process.</summary>
        internal const string DisableVariable = "KE_SPIRV_CACHE";

        /// <summary>
        /// The one every caller uses. Its enabled state is read from the environment at first touch, which is the
        /// only time reading it can be free of an ordering question.
        /// </summary>
        internal static SpirvCompileCache Shared { get; } =
            new(IsEnabled(Environment.GetEnvironmentVariable(DisableVariable)), DefaultCapacity);

        readonly ConcurrentDictionary<CacheKey, byte[]> _modules = new();
        readonly bool _enabled;
        readonly int _capacity;

        long _compiles;
        long _hits;

        /// <param name="enabled">False makes every call compile, which is what the kill switch buys.</param>
        /// <param name="capacity">How many distinct modules to hold before the cache stops inserting.</param>
        internal SpirvCompileCache(bool enabled, int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            _enabled = enabled;
            _capacity = capacity;
        }

        /// <summary>
        /// Whether a raw <see cref="DisableVariable"/> value asks for the memo. Blank or unset means yes, because
        /// the memo is the default, and the disable words are the same set <see cref="GpuDiskCache"/> takes so a
        /// reader who knows one knows the other.
        /// </summary>
        internal static bool IsEnabled(string? envValue)
        {
            if (string.IsNullOrWhiteSpace(envValue)) return true;
            return envValue.Trim().ToLowerInvariant() switch
            {
                "off" or "0" or "false" or "no" or "none" => false,
                _ => true,
            };
        }

        /// <summary>How many times a source actually reached the compiler through this cache. The number a test
        /// pins when it asserts that a second scene on a warm process compiles nothing.</summary>
        internal long CompileCount => Interlocked.Read(ref _compiles);

        /// <summary>How many calls were answered from the dictionary. With <see cref="CompileCount"/> this is the
        /// hit rate, and the pair is what makes the claim in this type's summary observable.</summary>
        internal long HitCount => Interlocked.Read(ref _hits);

        /// <summary>How many distinct modules are held right now, which is what <see cref="DefaultCapacity"/>
        /// bounds.</summary>
        internal int Count => _modules.Count;

        /// <summary>
        /// The SPIR-V for one source under one option set: the bytes this exact triple produced earlier in the
        /// process, or a fresh compile when it has not been seen.
        /// </summary>
        /// <param name="optionsIdentity">A stable one-line rendering of the compile options the
        /// <paramref name="compile"/> callback will use, so two callers with different options cannot collide.
        /// </param>
        /// <param name="stage">Which stage the source is compiled as. Part of the key, since one source string
        /// compiled as two stages is two modules.</param>
        /// <param name="glsl">The shader source.</param>
        /// <param name="compile">Produces the module. Called on a miss, and on every call when the cache is off or
        /// full. Its exception is never cached, so a source that failed to compile fails the same way next time.
        /// </param>
        internal byte[] GetOrCompile(
            string optionsIdentity, GpuShaderStages stage, string glsl, Func<byte[]> compile)
        {
            ArgumentNullException.ThrowIfNull(optionsIdentity);
            ArgumentNullException.ThrowIfNull(glsl);
            ArgumentNullException.ThrowIfNull(compile);

            if (!_enabled) return Compiled(compile);

            var key = new CacheKey(optionsIdentity, stage, glsl);
            if (_modules.TryGetValue(key, out byte[]? hit))
            {
                Interlocked.Increment(ref _hits);
                return (byte[])hit.Clone();
            }

            byte[] compiled = Compiled(compile);

            // NOT INSIDE A LOCK, unlike VulkanShaderModuleCache, and for the reason that one needs one: there a
            // duplicate creation LEAKS a native handle for the device's life, and here a duplicate compile
            // produces a managed array the loser drops on the floor. Two threads that both miss both compile, both
            // answer correctly, and one of the two entries is discarded by TryAdd.
            if (_modules.Count < _capacity) _modules.TryAdd(key, compiled);

            return (byte[])compiled.Clone();
        }

        byte[] Compiled(Func<byte[]> compile)
        {
            Interlocked.Increment(ref _compiles);
            return compile();
        }

        /// <summary>The triple that identifies a module. Ordinal string comparison, which is what the default
        /// <see cref="string"/> equality on a record struct already gives.</summary>
        readonly record struct CacheKey(string OptionsIdentity, GpuShaderStages Stage, string Glsl);
    }
}
