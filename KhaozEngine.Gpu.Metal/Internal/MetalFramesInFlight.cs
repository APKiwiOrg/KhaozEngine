using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// <c>KE_METAL_FRAMES_IN_FLIGHT</c>, MM4'S KNOB: the ONE depth this backend pipelines at. Row 7 owns the
    /// constant (https://github.com/APKiwiOrg/KhaozEngine/issues/573) and
    /// <see cref="MetalRingAllocator"/> and <see cref="MetalStagingArena"/> read it.
    ///
    /// <para><b>ONE NUMBER, ONE WAIT, AND THAT IS THE DIFFERENCE FROM THE VULKAN SIBLING (M-R2).</b> There the
    /// same number governs two things that each BLOCK, a per-list command-pool slot and a per-frame ring segment,
    /// and conflating them is the mistake available. Here there is no command-buffer pool at all. An
    /// <c>MTLCommandBuffer</c> is single-use: the queue owns its memory, hands out a fresh one per <c>Begin</c>,
    /// and there is no reset, no pool object and no allocator to choose between, so V-R2's command-buffer ring
    /// has nothing to hold. The uniform ring's segment acquire is the ONLY thing on this backend that ever waits
    /// on the GPU during a recording, so <c>BackpressureStallCount</c> means one thing here where it means two on
    /// Vulkan. The per-list staging arena is cut to the same depth and rotates on the same index, and it
    /// deliberately never waits: a slot whose blocks the GPU has not finished with keeps them until a later
    /// visit, which is what keeps that number single-sourced.</para>
    ///
    /// <para><b>THE FLOOR IS 1 HERE AND 2 ON VULKAN, and that is a derived difference rather than a copied
    /// constant drifting.</b> The Vulkan floor is 2 because at 1 every list would own ONE command pool, so every
    /// <c>Begin</c> would advance onto the slot it had just used and wait for its own previous record to finish
    /// on the GPU: a synchronous round trip per RECORD rather than a frame of latency. That argument needs a
    /// per-list pool ring to be true and this backend has none, so what is left at 1 is the shape the Direct3D 11
    /// backend already calls its honest degenerate case: one frame of latency, one stall per frame, and a
    /// configuration that proves the backpressure counter counts something real. It is a legal setting to
    /// MEASURE at and a terrible one to ship, which is what a floor is for.</para>
    ///
    /// <para><b>THE BET THIS TURNS OFF.</b> MM4 says 3 is enough that ring backpressure never blocks the CPU and
    /// that a drawable acquire does not become the frame's pacing, and its exit criterion is
    /// <c>BackpressureStallCount</c> zero across a full capture window. A non-zero count means 3 is the wrong
    /// number and not that the design is wrong, so the lever raises it rather than disabling anything. The
    /// deadline is rollout gate 4, and the tuning-knob survival rule applies: a knob may outlive its gate, but
    /// only if the exit criterion was met AT ITS DEFAULT, which is what stops "it is only a knob" from becoming a
    /// way to keep a failed default.</para>
    ///
    /// <para><b>COST OF RAISING IT</b>, so a field session knows what it is trading. Every uniform buffer in the
    /// process is allocated at its 256-aligned size times this number, each command list's staging arena keeps
    /// one more slot of pooled blocks, and the swapchain's <c>maximumDrawableCount</c> is set to it (M-W4, row
    /// 15), so four is a third more uniform memory and one more drawable held. No command buffer anywhere is
    /// allocated per frame in flight, which is the whole of M-R2.</para>
    ///
    /// <para>Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on
    /// any operating system, matching <see cref="MetalValidation"/> and <see cref="MetalDeviceSelection"/>.</para>
    /// </summary>
    internal static class MetalFramesInFlight
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. A whole number of frames,
        /// whitespace trimmed. Unset, empty, unparseable or out of range leaves <see cref="Default"/>.</summary>
        internal const string EnvVarName = "KE_METAL_FRAMES_IN_FLIGHT";

        /// <summary>Three, which is the number section 9.2 fixes and MM4 is the bet on.</summary>
        internal const int Default = 3;

        /// <summary>One, which pipelines nothing and is legal anyway. See the class note for why this is not the
        /// Vulkan sibling's 2.</summary>
        internal const int Minimum = 1;

        /// <summary>The ceiling. Nothing about the ring breaks above it, and a value this large already means the
        /// caller mistyped something rather than chose it: sixteen frames of latency is far past anything a
        /// renderer can use, and sixteen times the uniform memory is not free.</summary>
        internal const int Maximum = 16;

        /// <summary>
        /// How many frames <paramref name="envValue"/> asks for. A non-blank value that does not parse, or that
        /// parses outside <see cref="Minimum"/> to <see cref="Maximum"/>, comes back through
        /// <paramref name="unrecognizedValue"/> verbatim so the caller can WARN, and <see cref="Default"/> is
        /// used.
        /// <para>
        /// The unrecognized case is worth the branch because this variable exists to settle a MEASUREMENT: a
        /// mistyped value that silently left three frames in place would produce a capture that reads as evidence
        /// about four and was taken on three.
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
                + $"native Metal backend keeps {Default} frames in flight, which is the default. The floor is "
                + $"{Minimum} rather than the Vulkan backend's 2 because nothing here is allocated per frame in "
                + "flight except uniform ring segments: this backend has no command-buffer pool to advance onto.";

        /// <summary>
        /// The INFO line naming how many frames this run got, so a capture proves the number its backpressure
        /// counter was measured against rather than resting on the tester believing they set the variable. MM4's
        /// exit criterion is a count taken at a specific depth, so the two belong in one session log.
        /// <para>
        /// IT SAYS WHAT IS TRUE TODAY AND NAMES THE ROW THAT WILL MAKE THE REST TRUE. The number's consumers are
        /// the uniform ring's segments and each list's staging arena slots, both LIVE, and row 15's
        /// <c>maximumDrawableCount</c> (https://github.com/APKiwiOrg/KhaozEngine/issues/581), which is not. That
        /// split matters more here than anywhere else this row logs: this is the line MM4's measurement rests on,
        /// and a session log a measurement is read out of cannot be the place a claim runs ahead of the code.
        /// </para>
        /// </summary>
        internal static string ActiveDescription(int framesInFlight)
            => framesInFlight == Default
                ? $"The native Metal backend resolved {Default} frames in flight, the default. It sizes every "
                    + "uniform buffer's ring segments and each command list's staging arena slots, and row 15's "
                    + "maximumDrawableCount when the swapchain lands, and nothing else ever, because there is no "
                    + $"command-buffer pool on this backend. Set {EnvVarName}=<n> to change it, which is the MM4 "
                    + "lever."
                : $"The native Metal backend resolved {framesInFlight} frames in flight (from "
                    + $"{EnvVarName}={framesInFlight}) rather than the default {Default}. Every uniform buffer's "
                    + "ring and every command list's staging arena were built at that depth, so a backpressure "
                    + $"reading from this run describes {framesInFlight} frames rather than the default. The "
                    + "drawable queue is not sized yet, which is row 15's.";

        /// <summary>
        /// THE UNCOMMITTED-COMMAND-BUFFER BOUND (section 6.1), which is <see cref="Default"/> plus one and is the
        /// one place this number says anything about command buffers at all.
        /// <para>
        /// <c>MTLCommandQueue</c> has a maximum number of UNCOMMITTED command buffers and <c>-commandBuffer</c>
        /// BLOCKS when it is reached. That is a real bound with a real block and it is not the ring's, so the
        /// backend keeps it out of reach rather than relying on it: <c>Begin</c> waits on the ring's frame slot
        /// first, which bounds how far ahead the frame loop can get, and
        /// <see cref="MetalUncommittedBuffers"/> asserts the count device-free. The PLUS ONE is the present
        /// command buffer M-W6 keeps on its own (row 15,
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/581).
        /// </para>
        /// </summary>
        internal static int UncommittedBufferBound(int framesInFlight) => framesInFlight + 1;
    }
}
