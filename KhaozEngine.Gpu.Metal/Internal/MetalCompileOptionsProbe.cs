using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>What a default-constructed <c>MTLCompileOptions</c> reports on this OS.</summary>
    internal sealed record MetalCompileOptionsReading
    {
        internal bool Read { get; init; }
        internal nuint LanguageVersionRaw { get; init; }
        internal bool FastMathEnabled { get; init; }
        internal bool PreserveInvariance { get; init; }
        internal bool RespondsToMathMode { get; init; }
        internal nint MathModeRaw { get; init; }

        /// <summary>The language version as Metal spells it, <c>major.minor</c>, decoded from the packed
        /// <c>(major &lt;&lt; 16) | minor</c> the enum uses.</summary>
        internal string LanguageVersion =>
            ((uint)LanguageVersionRaw >> 16).ToString(CultureInfo.InvariantCulture) + "."
            + ((uint)LanguageVersionRaw & 0xFFFF).ToString(CultureInfo.InvariantCulture);

        internal string Report() =>
            "read: " + Read + "\n"
            + "languageVersion: " + LanguageVersion + " (raw " + LanguageVersionRaw + ")\n"
            + "fastMathEnabled: " + FastMathEnabled + "\n"
            + "preserveInvariance: " + PreserveInvariance + "\n"
            + "responds to mathMode: " + RespondsToMathMode + "\n"
            + "mathMode: " + MathModeRaw + "\n";
    }

    /// <summary>
    /// VERIFICATION TASK THREE of work-breakdown row 1 in
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>: measure what a default-constructed
    /// <c>MTLCompileOptions</c> reports, so decision M-S6's pin is a no-op on the day it lands.
    /// <para>
    /// WHY THIS IS A MEASUREMENT AND NOT A CHOICE. The incumbent passes a default-constructed options object, so
    /// two facts about the runner are currently unstated rather than decided, and the committed metal goldens
    /// were baked under whichever defaults were in force. <c>fastMathEnabled</c> changes floating-point results,
    /// so flipping it moves every pixel with no other symptom. <c>languageVersion</c> defaults to the newest the
    /// OS supports, so it DRIFTS with the runner image, which is the same class of hazard the workflow already
    /// pins <c>macos-26</c> by number to avoid, one level up. Row 9 holds both as constants in
    /// <c>MslCompilePin</c> and derives the cache-key identity from them. This is what tells row 9 which numbers
    /// to write down.
    /// </para>
    /// <para>
    /// <c>preserveInvariance</c> is read too though the design leaves it at its default, because "left at the
    /// default" is only a decision if somebody checked what the default is. <c>mathMode</c> is read behind a
    /// <c>respondsToSelector:</c> because it is the newer spelling of the fast-math knob, and knowing whether
    /// this OS has it is what tells a later reader whether <c>fastMathEnabled</c> is still the property to pin
    /// or already a shim over something else.
    /// </para>
    /// <para>
    /// No device is involved. <c>MTLCompileOptions</c> is a plain object, so this measures the OS rather than
    /// the GPU, which is why the test that runs it is an ordinary fact rather than a <c>[GpuFact]</c>.
    /// </para>
    /// </summary>
    internal static class MetalCompileOptionsProbe
    {
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalCompileOptionsReading Read()
        {
            IntPtr pool = MetalInteropSpike.AutoreleasePoolPush();
            try
            {
                IntPtr cls = MetalInteropSpike.Cls("MTLCompileOptions");
                if (cls == IntPtr.Zero) return new MetalCompileOptionsReading();

                IntPtr options = MetalInteropSpike.MsgSend(
                    MetalInteropSpike.MsgSend(cls, MetalInteropSpike.Sel("alloc")), MetalInteropSpike.Sel("init"));
                if (options == IntPtr.Zero) return new MetalCompileOptionsReading();

                IntPtr mathModeSel = MetalInteropSpike.Sel("mathMode");
                bool respondsToMathMode =
                    MetalInteropSpike.MsgSendBoolPtr(options, MetalInteropSpike.Sel("respondsToSelector:"), mathModeSel) != 0;

                var reading = new MetalCompileOptionsReading
                {
                    Read = true,
                    LanguageVersionRaw = MetalInteropSpike.MsgSendNUInt(options, MetalInteropSpike.Sel("languageVersion")),
                    FastMathEnabled = MetalInteropSpike.MsgSendBool(options, MetalInteropSpike.Sel("fastMathEnabled")) != 0,
                    PreserveInvariance = MetalInteropSpike.MsgSendBool(options, MetalInteropSpike.Sel("preserveInvariance")) != 0,
                    RespondsToMathMode = respondsToMathMode,
                    MathModeRaw = respondsToMathMode ? MetalInteropSpike.MsgSendNInt(options, mathModeSel) : 0,
                };

                MetalInteropSpike.ObjcRelease(options);
                return reading;
            }
            finally
            {
                MetalInteropSpike.AutoreleasePoolPop(pool);
            }
        }
    }
}
