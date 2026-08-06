using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <c>KE_VULKAN_FRAMES_IN_FLIGHT</c>, MV3'S KNOB: the ONE depth this backend pipelines at, governing BOTH the
    /// command-buffer pool slots every list owns (this row) and the per-frame segments every uniform ring is cut
    /// into (row 8, https://github.com/APKiwiOrg/KhaozEngine/issues/518).
    /// <para>
    /// ONE NUMBER, TWO INDEXES, and conflating them is the mistake available here. The POOL SLOT is per list and
    /// advances on every <c>Begin</c>. The RING SEGMENT is per frame and advances at the frame boundary. A list
    /// begun twice in one frame takes two different pool slots while both records write the SAME ring segment,
    /// which is correct in both directions: two records must not share a command buffer still in flight, and two
    /// records in one frame must see one frame's uniform values. What is shared is the DEPTH, because a deeper
    /// command-buffer ring behind a shallower uniform gate is dead capacity, so there is one number to move if MV3
    /// says 3 is wrong (section 6.1).
    /// </para>
    /// <para>
    /// THE BET THIS TURNS OFF. MV3 says 3 is enough that ring-segment backpressure and command-buffer slot waits
    /// never block the CPU, and its exit criterion is <c>BackpressureStallCount</c> zero across a full capture
    /// window, on ONE accumulator covering both (see <see cref="VulkanBackpressure"/>). A non-zero count means 3 is
    /// the wrong number and not that the design is wrong, so the lever raises it rather than disabling anything.
    /// </para>
    /// <para>
    /// THE TUNING-KNOB SURVIVAL RULE, quoted from section 2.7 because it is the whole condition on this variable
    /// outliving its deadline: "<i>A tuning knob or an observation flag keeps no second path alive, so it may
    /// survive its gate, and its Deadline cell says so and says on what condition.</i>
    /// <c>KE_VULKAN_FRAMES_IN_FLIGHT</c> is the second kind and may live on as a knob, <i>but only if MV3's exit
    /// criterion was met at its default, which is the condition that stops "it is only a knob" from becoming a way
    /// to keep a failed default.</i>" The deadline is rollout gate 4. A knob is not a way to ship a failed
    /// default.
    /// </para>
    /// <para>
    /// THE FLOOR IS 2 HERE AND 1 ON THE OTHER BACKEND, which is a real difference rather than a copied constant
    /// drifting. On Direct3D 11 the number sizes constant-buffer rings only, so 1 is the honest degenerate case:
    /// one frame of latency, one stall per frame, and a shape that proves the backpressure counter counts
    /// something real. Here the same 1 would give every list ONE pool, so every <c>Begin</c> would advance onto
    /// the slot it just used and wait for that record's own submission to complete before recording could start
    /// again. That is not a frame of latency, it is a synchronous round trip per RECORD, and a frame that records
    /// several lists would pay several full GPU drains. A capture taken there measures the drain rather than the
    /// pipeline, which is the one thing the MV3 lever must not be able to do quietly. 2 is the shallowest depth
    /// that still pipelines, and it is where a wrap arrives on the second re-record rather than the first.
    /// </para>
    /// <para>
    /// COST OF RAISING IT, so a field session knows what it is trading. Every command list allocates this many
    /// <c>VkCommandPool</c>s with a primary buffer each, and every uniform buffer in the process is allocated at
    /// its 256-aligned size times this number. Four is a third more of both than three.
    /// </para>
    /// <para>
    /// Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on any
    /// operating system, matching <see cref="VulkanValidation"/> and <see cref="VulkanPhysicalDeviceSelection"/>.
    /// </para>
    /// </summary>
    internal static class VulkanFramesInFlight
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. A whole number of frames,
        /// whitespace trimmed. Unset, empty, unparseable or out of range leaves <see cref="Default"/>.</summary>
        internal const string EnvVarName = "KE_VULKAN_FRAMES_IN_FLIGHT";

        /// <summary>Three, which is the number section 6.1 fixes and MV3 is the bet on.</summary>
        internal const int Default = 3;

        /// <summary>Two, the shallowest depth that pipelines at all on this backend. See the class note for why
        /// this is not the other backend's 1.</summary>
        internal const int Minimum = 2;

        /// <summary>The ceiling. Nothing about either ring breaks above it, and a value this large already means
        /// the caller mistyped something rather than chose it: sixteen frames of latency is far past anything a
        /// renderer can use, and sixteen command pools per list plus sixteen times the uniform memory is not
        /// free.</summary>
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
                + $"native Vulkan backend keeps {Default} frames in flight, which is the default. The floor is "
                + $"{Minimum} rather than 1 because at 1 every command list would own one pool and every Begin "
                + "would wait for its own previous record to finish on the GPU.";

        /// <summary>The INFO line naming how many frames this run got, so a capture proves the number its
        /// backpressure counter was measured against rather than resting on the tester believing they set the
        /// variable. MV3's exit criterion is a count taken at a specific depth, so the two belong in one session
        /// log.</summary>
        internal static string ActiveDescription(int framesInFlight)
            => framesInFlight == Default
                ? $"The native Vulkan backend runs {Default} frames in flight, the default: {Default} command "
                    + $"pools per list and {Default} segments per uniform ring. Set {EnvVarName}=<n> to change "
                    + "it, which is the MV3 lever."
                : $"The native Vulkan backend runs {framesInFlight} frames in flight (from "
                    + $"{EnvVarName}={framesInFlight}) rather than the default {Default}. Every command list "
                    + "allocates that many command pools and every uniform buffer that many segments, and the "
                    + "backpressure counter for this run describes that depth.";
    }
}
