using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// What <c>KE_METAL_VALIDATION</c> asked for, what the process environment ACTUALLY armed, and the one
    /// disagreement between them that a reader could not otherwise diagnose.
    /// </summary>
    /// <param name="Requested">The tier <c>KE_METAL_VALIDATION</c> named.</param>
    /// <param name="UnrecognizedValue">The value of that variable when it was set and not understood, verbatim,
    /// or null.</param>
    /// <param name="Armed">The tier the Metal runtime really has, read from the LAUNCH environment. This is the
    /// one that is true.</param>
    /// <param name="DebugLayerArmed">Whether <c>MTL_DEBUG_LAYER</c> itself was in the launch environment. Kept
    /// beside <paramref name="Armed"/> rather than derived from it, because the tier is a MERGE and the merge
    /// loses the one distinction the device-class check needs: the shader variable alone reports the same
    /// <see cref="MetalValidationMode.Shaders"/> tier as both variables together and gets a different device
    /// class. Deriving the variable back out of the tier is what made #628's warning name a variable nobody had
    /// set.</param>
    /// <param name="ShaderValidationArmed">Whether <c>MTL_SHADER_VALIDATION</c> itself was in the launch
    /// environment.</param>
    /// <param name="DebugLayerSetInProcessOnly">Whether <c>MTL_DEBUG_LAYER</c> is set in the managed environment
    /// and was NOT in the launch environment, which is the "set after the runtime read it" case M-G3's log line
    /// asks for. Detected rather than guessed: on Unix the CLR keeps its own copy of the environment and
    /// <c>Environment.SetEnvironmentVariable</c> never writes through to the native one, so a variable present in
    /// the first and absent from the second was set in-process.</param>
    /// <param name="ShaderValidationSetInProcessOnly">The same, for <c>MTL_SHADER_VALIDATION</c>.</param>
    internal readonly record struct MetalValidationArming(
        MetalValidationMode Requested,
        string? UnrecognizedValue,
        MetalValidationMode Armed,
        bool DebugLayerArmed,
        bool ShaderValidationArmed,
        bool DebugLayerSetInProcessOnly,
        bool ShaderValidationSetInProcessOnly)
    {
        /// <summary>Whether the tier asked for is higher than the tier that is armed, which is the case worth a
        /// WARN: the tester believes they are validating and they are not.
        /// <para>
        /// IT COMPARES RUNGS, AND THE RUNGS ARE NOT NESTED, so it misses one combination: a process asking for
        /// <see cref="MetalValidationMode.On"/> with only <c>MTL_SHADER_VALIDATION</c> armed reads as
        /// <see cref="MetalValidationMode.Shaders"/>, which is the HIGHER rung, so nothing warns even though the
        /// API validation tier it asked for is absent. Recorded as
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/634 rather than fixed here, because the fix compares
        /// per-variable instead and that changes what this WARN means.
        /// </para>
        /// </summary>
        internal bool RequestedMoreThanArmed => Requested > Armed;
    }

    /// <summary>
    /// THE IMPURE HALF OF M-G3: read the two process-level variables the Metal runtime cares about, and the
    /// engine's own knob, and say what is really armed.
    ///
    /// <para><b>IT READS THE NATIVE ENVIRONMENT AS WELL AS THE MANAGED ONE, and that is the whole reason this
    /// type exists.</b> Section 14 asks for "a WARN when the variable was set after the runtime had already read
    /// it", and there is no API that answers that question directly. There is a measurable proxy: on Unix the
    /// CLR snapshots <c>environ</c> at startup and <c>Environment.SetEnvironmentVariable</c> writes only to that
    /// snapshot, so a variable the managed side reports and <c>getenv</c> does not was set in-process and the
    /// Metal runtime never saw it. Comparing the two turns an unanswerable question into a read.</para>
    ///
    /// <para><b>IT IS CAPTURED ONCE, AND THE ORDERING HAZARD ROW 1 RECORDED IS WHY.</b> The spike's control
    /// exposed that a probe run after a device already exists can only ever answer no, so it would be measuring
    /// the ordering rather than the mechanism. The same trap is available here in reverse: an arming answer taken
    /// twice at different moments could differ if anything set a variable in between, and the second reading
    /// would be the wrong one, because what the Metal runtime read is fixed at launch and never changes again.
    /// So the answer is taken once and memoized, and it is a statement about how the PROCESS started rather than
    /// about when it was asked.</para>
    ///
    /// <para><b>NOTHING HERE ARMS ANYTHING.</b> M-G3 measured that in-process mutation does not reach the
    /// framework, so this type deliberately has no <c>setenv</c> on any path. Row 1's spike has one, for the
    /// measurement, and the fact that it is confined there is the point.</para>
    /// </summary>
    internal static unsafe partial class MetalValidationReader
    {
        const string SystemLib = "/usr/lib/libSystem.B.dylib";

        static MetalValidationArming? _captured;
        static readonly object _gate = new();

        /// <summary>
        /// The arming for this process, read once. Safe on every operating system: off macOS the native read is
        /// skipped and the answer is whatever the managed environment says, which is what a device-free test
        /// exercises.
        /// </summary>
        internal static MetalValidationArming Current()
        {
            lock (_gate)
            {
                _captured ??= Capture();
                return _captured.Value;
            }
        }

        /// <summary>
        /// Take the reading fresh, ignoring the memo. For tests, which need to drive this against an environment
        /// they mutate, and never on a real path: a second reading on a live process would be measuring when it
        /// was asked rather than how the process started.
        /// </summary>
        internal static MetalValidationArming Capture()
        {
            MetalValidationMode requested = MetalValidation.Parse(
                Environment.GetEnvironmentVariable(MetalValidation.EnvVarName), out string? unrecognized);

            (bool debugArmed, bool debugInProcessOnly) = ReadProcessVariable(MetalValidation.DebugLayerVar);
            (bool shaderArmed, bool shaderInProcessOnly) =
                ReadProcessVariable(MetalValidation.ShaderValidationVar);

            // Shader validation is the higher RUNG on this ordering, so a process that armed only the shader
            // variable still reports the higher tier. Reporting Off there would be the reverse of the failure
            // this whole type exists to prevent.
            //
            // The rung does NOT imply the API layer, and reading it that way is what #628 was. MTL_SHADER_
            // VALIDATION alone gets in-shader bounds checking WITHOUT the API validation tier, which is what
            // MetalValidation.ActiveDescription says on that branch and what the device class confirms:
            // MTLGPUDebugDevice or MTLLegacySVDevice comes back rather than MTLDebugDevice, and the two are
            // different wrappers rather than one nested in the other.
            MetalValidationMode armed = shaderArmed
                ? MetalValidationMode.Shaders
                : debugArmed ? MetalValidationMode.On : MetalValidationMode.Off;

            return new MetalValidationArming(requested, unrecognized, armed, debugArmed, shaderArmed,
                debugInProcessOnly, shaderInProcessOnly);
        }

        // Whether a process-level variable is armed, and whether it is armed ONLY in the managed environment,
        // which means it was set after launch and the Metal runtime never read it.
        static (bool Armed, bool InProcessOnly) ReadProcessVariable(string name)
        {
            string? managed = Environment.GetEnvironmentVariable(name);
            if (!MetalValidation.IsArmed(managed)) return (false, false);

            // Off macOS there is no native environment worth reading and no Metal runtime to have read it, so the
            // managed answer is the whole answer. That keeps the type device-free-testable on the Linux and
            // Windows legs without a second code path for them to drift into.
            if (!KhaozEngineMetal.IsPlatformSupported) return (true, false);

            string? native = ReadNative(name);
            return MetalValidation.IsArmed(native) ? (true, false) : (false, true);
        }

        [SupportedOSPlatform("macos")]
        static string? ReadNative(string name)
        {
            var bytes = new byte[Encoding.ASCII.GetByteCount(name) + 1];
            Encoding.ASCII.GetBytes(name, bytes);

            fixed (byte* p = bytes)
            {
                byte* value = GetEnv(p);
                return value is null ? null : Marshal.PtrToStringUTF8((IntPtr)value);
            }
        }

        // The NATIVE environment, which is not the one System.Environment mutates on Unix. This is the only call
        // that can tell a variable set at launch from one set in-process, and it is a read: there is deliberately
        // no setenv anywhere in the shipped backend, because M-G3 measured that writing one changes nothing.
        [LibraryImport(SystemLib, EntryPoint = "getenv")]
        [SupportedOSPlatform("macos")]
        private static partial byte* GetEnv(byte* name);
    }
}
