using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHICH ATTACHMENT A COLOUR CLEAR LANDS ON, which is decision M-A2 expressed as two branches so the first
    /// golden run can A/B them.
    /// </summary>
    internal enum MetalClearMode
    {
        /// <summary>
        /// THE DEFAULT AND THE FIX: a clear lands on the attachment the caller NAMED. One index, and it is the
        /// whole of M-A2.
        /// </summary>
        PerAttachment = 0,

        /// <summary>
        /// THE INCUMBENT, REPRODUCED EXACTLY: every clear lands on attachment 0 whatever index the caller passed,
        /// so a framebuffer with more than one colour target never clears the rest at all. What
        /// <c>KE_METAL_CLEAR=attachment0</c> selects, for the A/B only.
        /// </summary>
        Attachment0 = 1,
    }

    /// <summary>
    /// DECISION M-A2's KILL SWITCH, AND ITS DEADLINE IS WRITTEN INTO THIS FILE: <c>KE_METAL_CLEAR=attachment0</c>
    /// reproduces the incumbent's collapse for the first golden run, and at GATE 1 the switch is removed and the
    /// losing branch deleted whichever way the goldens go.
    ///
    /// <para><b>WHAT THE INCUMBENT DOES, AND WHY IT IS NOT AN INVISIBLE CORRECTION.</b> The Veldrid Metal backend
    /// writes every clear into <c>colorAttachments[0]</c>, so a framebuffer with more than one colour target
    /// clears only its first.
    /// <c>KhaozEngine.Render3D/Rendering/ModelRenderer.BeginModelPass</c> clears attachments 0, 1 and 2 of
    /// <c>ModelFB</c> and ships a comment describing the collapse, whose workaround (make the three clear values
    /// equal) does nothing about two attachments that are never cleared at all. So this IS a deliberate rendering
    /// change on the fleet's reference golden family, and 2.4 is the argument for making it: what those two
    /// attachments load today is a freshly created <c>StorageModePrivate</c> texture nothing has written, which
    /// means the CURRENT behaviour is the unstable one and the committed goldens were baked reading it.</para>
    ///
    /// <para><b>THE EXIT CRITERION IS NOT "SOME GOLDENS MOVED" (MM2).</b> Either both positions are green, or
    /// exactly the scenes whose framebuffer has more than one colour target differ and the difference is
    /// explained by two attachments going from Load to Clear. A difference anywhere else means something other
    /// than this clause moved, and the switch exists so that question can be asked by flipping one variable
    /// instead of by bisecting a backend.</para>
    ///
    /// <para><b>THE PARSE IS PURE AND THE READ IS MEMOIZED, which is the split
    /// <see cref="MetalValidation"/> already uses.</b> <see cref="Parse"/> takes the string, so every branch of it
    /// runs on the Linux and Windows legs with no environment to mutate, and <see cref="Current"/> reads the
    /// process environment ONCE. Reading it per pass would let a mid-run change split one frame's clears between
    /// two policies, which is a shape no A/B could interpret.</para>
    ///
    /// <para><b>AN UNRECOGNIZED VALUE IS THE DEFAULT PLUS A NAME, not a throw and not a silent nothing.</b> The
    /// knob has one non-default value, so a fourth spelling is a typo, and a typo that silently selected the fix
    /// while the tester believed they had selected the incumbent would make the A/B report the same position
    /// twice. THE NAME IS EMITTED RATHER THAN RETURNED AND DROPPED: <see cref="Report(Action{string})"/> is the
    /// production entry point and <c>MetalGpuDevice</c> calls it at creation, which is where
    /// <see cref="MetalValidation"/>'s own unrecognized-value WARN goes out. A parse that names the typo to a
    /// caller nothing logs is the same silence with more code.</para>
    /// </summary>
    internal static class MetalClearPolicy
    {
        static MetalClearMode? _current;
        static string? _unrecognized;
        static readonly object _gate = new();

        /// <summary>The engine knob. Removed at gate 1 along with the losing branch.</summary>
        internal const string EnvVarName = "KE_METAL_CLEAR";

        /// <summary>The value that selects the incumbent's collapse.</summary>
        internal const string Attachment0Value = "attachment0";

        /// <summary>
        /// The mode for this process, read once. Every command list takes its copy from here at construction, so
        /// a recording cannot straddle two policies.
        /// </summary>
        internal static MetalClearMode Current => Resolve(out _);

        /// <summary>
        /// The mode for this process AND the value nothing understood, out of the SAME reading. One memo holds
        /// both, so the WARN a device emits describes the value the recordings under it actually ran on. Two
        /// readings could not promise that: a variable changed in between would report one position and clear
        /// under the other, which is the one shape the gate-1 A/B cannot interpret.
        /// </summary>
        internal static MetalClearMode Resolve(out string? unrecognizedValue)
        {
            lock (_gate)
            {
                _current ??= Parse(Environment.GetEnvironmentVariable(EnvVarName), out _unrecognized);

                unrecognizedValue = _unrecognized;
                return _current.Value;
            }
        }

        /// <summary>
        /// SAY SO WHEN THIS PROCESS'S VALUE WAS A TYPO, and say nothing otherwise. The production entry point,
        /// called once at device creation, and the reason the parse's second output is not dropped on the floor.
        /// </summary>
        /// <param name="warn">The WARN sink, <c>log.Warn</c> on the real path.</param>
        internal static void Report(Action<string> warn)
        {
            ArgumentNullException.ThrowIfNull(warn);

            _ = Resolve(out string? unrecognizedValue);
            Emit(unrecognizedValue, warn);
        }

        /// <summary>
        /// The same decision over an EXPLICIT value rather than the memo, which is what makes the wiring above
        /// assertable on a machine with no Metal and no environment to mutate. Both overloads share one body, so
        /// a device-free row about this one is a row about the production one.
        /// </summary>
        /// <param name="envValue">What <c>KE_METAL_CLEAR</c> was set to.</param>
        /// <param name="warn">The WARN sink.</param>
        internal static void Report(string? envValue, Action<string> warn)
        {
            ArgumentNullException.ThrowIfNull(warn);

            _ = Parse(envValue, out string? unrecognizedValue);
            Emit(unrecognizedValue, warn);
        }

        /// <summary>
        /// What <paramref name="envValue"/> asks for, with <paramref name="unrecognizedValue"/> set verbatim
        /// (quotes, stray spaces and all) when the value was neither blank nor understood.
        /// </summary>
        internal static MetalClearMode Parse(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return MetalClearMode.PerAttachment;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "attachment0": case "attachment-0": case "incumbent":
                    return MetalClearMode.Attachment0;
                case "perattachment": case "per-attachment": case "attachment": case "default":
                    return MetalClearMode.PerAttachment;
                default:
                    unrecognizedValue = envValue;
                    return MetalClearMode.PerAttachment;
            }
        }

        /// <summary>
        /// WHERE A CLEAR OF <paramref name="requestedIndex"/> ACTUALLY LANDS under <paramref name="mode"/>, and
        /// the single expression the whole kill switch is.
        /// <para>
        /// THE FOLD IS AT THE RECORD, NOT AT THE BEGIN, which is what makes the two positions genuinely
        /// comparable. Under <see cref="MetalClearMode.Attachment0"/> a clear of attachment 2 overwrites
        /// attachment 0's pending value exactly as the incumbent does, so a pass clearing three attachments to
        /// three colours ends up with the LAST of them on slot 0 and nothing on the rest. Folding at the begin
        /// instead would have to invent a rule for which of the three won.
        /// </para>
        /// </summary>
        /// <param name="mode">The policy this recording was built with.</param>
        /// <param name="requestedIndex">The colour attachment index the caller named, already range-checked
        /// against the bound framebuffer.</param>
        internal static uint TargetIndex(MetalClearMode mode, uint requestedIndex)
            => mode == MetalClearMode.Attachment0 ? 0u : requestedIndex;

        // The one body both Report overloads run, so the emit-or-stay-quiet rule lives in one place and neither
        // entry point can drift into naming a typo the other swallows.
        static void Emit(string? unrecognizedValue, Action<string> warn)
        {
            if (unrecognizedValue is not null) warn(UnrecognizedDescription(unrecognizedValue));
        }

        /// <summary>The WARN line for a value nothing understood, naming both spellings so the reader does not
        /// have to find this file.</summary>
        internal static string UnrecognizedDescription(string value)
            => $"{EnvVarName}='{value}' is not a recognized value. Use {Attachment0Value} to reproduce the "
                + "Veldrid Metal backend's colorAttachments[0] clear collapse for an A/B, or leave it unset for "
                + "the per-attachment clear this backend ships (decision M-A2). This run uses the per-attachment "
                + "clear.";
    }
}
