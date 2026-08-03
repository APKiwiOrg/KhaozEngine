using System;
using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <c>KE_D3D11_FRAMES_IN_FLIGHT</c>, THE M3 KILL SWITCH (decision U5, section 13): how many per-frame segments
    /// every constant-buffer ring is cut into, and therefore how far ahead of the GPU the CPU may write uniforms
    /// before it has to wait for a segment to come free.
    /// <para>
    /// THE BET THIS TURNS OFF. M3 says three is enough that segment backpressure never blocks the CPU, and its
    /// exit criterion is a backpressure stall count of zero across a full soak capture window. A non-zero count
    /// means three is the wrong number and not that the ring is the wrong design, so the lever raises it rather
    /// than disabling anything. Lowering it is just as useful in the other direction: at one segment every frame
    /// waits for the previous frame to finish, which is the degenerate no-pipelining case a soak can use to prove
    /// the stall counter is measuring what it claims to.
    /// </para>
    /// <para>
    /// COST OF RAISING IT, so a field session knows what it is trading. Every uniform buffer in the process is
    /// allocated at its 256-aligned size times this number, so four segments is a third more constant-buffer
    /// memory than three. That is small in absolute terms (the engine's largest uniform buffer is under 10 KB)
    /// and it is not free.
    /// </para>
    /// <para>
    /// Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on any
    /// operating system, matching <see cref="D3D11RecordModes"/> and <see cref="D3D11RealDrain"/>.
    /// </para>
    /// </summary>
    internal static class D3D11FramesInFlight
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. A whole number of segments,
        /// whitespace trimmed. Unset, empty, unparseable or out of range leaves <see cref="Default"/>.</summary>
        internal const string EnvVarName = "KE_D3D11_FRAMES_IN_FLIGHT";

        /// <summary>Three, which is the number decision U1 fixes and milestone M3 is the bet on.</summary>
        internal const int Default = 3;

        /// <summary>One segment, meaning no pipelining at all: each frame waits for the previous frame's
        /// submission to complete before it may write a uniform. Legal, deliberately, because it is the shape
        /// that proves the backpressure counter counts something real.</summary>
        internal const int Minimum = 1;

        /// <summary>The ceiling. Nothing about the ring breaks above it, and a value this large already means the
        /// caller has mistyped something rather than chosen it, since sixteen frames of latency is far past
        /// anything a renderer can use and sixteen times the constant-buffer memory is not.</summary>
        internal const int Maximum = 16;

        /// <summary>
        /// How many segments <paramref name="envValue"/> asks for. A non-blank value that does not parse, or that
        /// parses outside <see cref="Minimum"/> to <see cref="Maximum"/>, comes back through
        /// <paramref name="unrecognizedValue"/> verbatim so the caller can WARN, and <see cref="Default"/> is
        /// used.
        /// <para>
        /// The unrecognized case is worth the branch for the same reason it is on the other two levers. This
        /// variable exists to settle a measurement, so a mistyped value that silently left three segments in
        /// place would produce a capture that reads as evidence about four and was taken on three.
        /// </para>
        /// </summary>
        internal static int Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return Default;

            if (!int.TryParse(envValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames)
                || frames < Minimum || frames > Maximum)
            {
                unrecognizedValue = envValue;
                return Default;
            }

            return frames;
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static int FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>The WARN body for a value that was set and understood as nothing.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a whole number of frames between {Minimum} and {Maximum}. The "
                + $"native Direct3D 11 constant-buffer rings keep {Default} segments, which is the default.";

        /// <summary>The INFO line naming how many segments this run got, so a capture proves the number its
        /// backpressure counter was measured against rather than resting on the tester believing they set the
        /// variable. The M3 exit criterion is a count taken at a specific segment count, so the two belong in one
        /// session log.</summary>
        internal static string ActiveDescription(int framesInFlight)
            => framesInFlight == Default
                ? $"The native Direct3D 11 constant-buffer rings run {Default} frame segments, the default. Set "
                    + $"{EnvVarName}=<n> to change it, which is the M3 lever."
                : $"The native Direct3D 11 constant-buffer rings run {framesInFlight} frame segments (from "
                    + $"{EnvVarName}={framesInFlight}) rather than the default {Default}. Every uniform buffer is "
                    + "allocated at that many times its own size, and the backpressure counter for this run "
                    + "describes that number of segments.";
    }
}
