using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-S6, AND THE ONE PLACE THE <c>MTLCompileOptions</c> ARE WRITTEN DOWN (section 12.4 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). The incumbent passes a default-constructed
    /// options object, so two facts about the runner are currently unstated rather than decided, and the committed
    /// <c>metal</c> goldens were baked under whichever defaults were in force.
    ///
    /// <para>
    /// <b><c>fastMathEnabled</c> is pinned ON.</b> Fast math changes floating-point results, so flipping it moves
    /// every pixel with no other symptom, which is exactly the kind of change that arrives as an unexplainable
    /// golden diff months later. The default is on and the goldens were baked with it on, so pinning it is a no-op
    /// today and a guard forever.
    /// </para>
    /// <para>
    /// <b><c>languageVersion</c> is pinned to 3.2.</b> The default is the newest the OS supports, which DRIFTS
    /// with the runner image. The workflow already pins <c>macos-26</c> by number rather than to
    /// <c>macos-latest</c> so an image promotion cannot move the GPU under a golden gate, and the language version
    /// is the same class of hazard one level up. Row 1 measured what <c>macos-26</c> reports before this was
    /// written: 3.2, raw <c>196610</c>, the packed <c>(3 &lt;&lt; 16) | 2</c> the enum uses. So the pin is a no-op
    /// on the day it lands, which is what measuring first was for.
    /// </para>
    /// <para>
    /// <b><c>preserveInvariance</c> is left OFF</b>, matching the incumbent, and it is written here rather than
    /// omitted because "left at the default" is only a decision if somebody checked what the default is. Row 1
    /// checked: off. It forces position computation to be invariant across pipelines, which matters for
    /// multi-pass depth equality, and turning it on is a follow-up with a TRIGGER (Z-fighting between the depth
    /// prepass and a later pass) rather than a speculative change to a golden-bearing knob.
    /// </para>
    /// <para>
    /// AND THE NEWER <c>mathMode</c> PROPERTY EXISTS ON THIS OS AND AGREES WITH THE OLDER ONE, reading 2
    /// (<c>MTLMathModeFast</c>) while <c>fastMathEnabled</c> reads true. That agreement is what makes pinning
    /// <c>fastMathEnabled</c> a pin on the real setting rather than on a shim nothing reads, and the day the two
    /// disagree is the day this pin moves to <c>mathMode</c>. <c>MetalCompileOptionsPinTests</c> re-reads all four
    /// on every <c>dotnet test</c> and goes red when the OS moves them, which is the drift this decision exists
    /// for.
    /// </para>
    /// <para>
    /// <b>WHAT THIS PIN DOES NOT FREEZE, which its name invites the opposite belief about (2.2b, pin 7).</b> It
    /// freezes the options Metal COMPILES the MSL under. It does not reach the MSL at all, so it freezes neither
    /// SPIRV-Cross's resource naming nor its index numbering, and the binding table read off the emitted argument
    /// names is not protected by anything in this file. <c>MslCrossCompilePin</c> does not freeze them either.
    /// What actually freezes the emission is the exact <c>Veldrid.SPIRV</c> version pinned in
    /// <c>Directory.Packages.props</c>, whose bundled <c>libveldrid-spirv</c> carries the SPIRV-Cross the engine
    /// emits through, so that drift arrives on a deliberate package bump rather than on an OS update.
    /// </para>
    /// </summary>
    internal static class MslCompilePin
    {
        /// <summary>Metal Shading Language 3.2, as <c>macos-26</c> reports it. Packed
        /// <c>(major &lt;&lt; 16) | minor</c>, which is how <c>MTLLanguageVersion</c> is defined.</summary>
        internal const uint LanguageVersion = (3u << 16) | 2u;

        /// <summary>The major half of <see cref="LanguageVersion"/>, for a message and for the identity.</summary>
        internal const uint LanguageVersionMajor = LanguageVersion >> 16;

        /// <summary>The minor half of <see cref="LanguageVersion"/>.</summary>
        internal const uint LanguageVersionMinor = LanguageVersion & 0xFFFFu;

        /// <summary>Fast math ON, which is the default the goldens were baked under.</summary>
        internal const bool FastMathEnabled = true;

        /// <summary>Invariant position computation OFF, matching the incumbent.</summary>
        internal const bool PreserveInvariance = false;

        /// <summary>
        /// A stable one-line rendering of the pinned set, for a cache key, in the exact shape
        /// <c>HlslCrossCompilePin.Identity</c> and <c>SpirvFrontEndPin.Identity</c> already use. BUILT FROM THE
        /// VALUES rather than typed out beside them, so a pin change moves every derived cache key by
        /// construction instead of by remembering.
        /// </summary>
        internal static readonly string Identity =
            "metal/compile"
            + ";languageVersion=" + LanguageVersionMajor.ToString(CultureInfo.InvariantCulture)
            + "." + LanguageVersionMinor.ToString(CultureInfo.InvariantCulture)
            + ";fastMath=" + Bit(FastMathEnabled)
            + ";preserveInvariance=" + Bit(PreserveInvariance);

        // 1 / 0 rather than true / false, matching the sibling pins: nothing but a hash reads this token.
        static string Bit(bool value) => value ? "1" : "0";
    }
}
