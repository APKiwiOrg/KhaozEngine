using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// EVERYTHING M-G4 READS OFF A FINISHED COMMAND BUFFER, as plain data with no Objective-C handle in it: the
    /// status, the <c>MTLCommandBufferError</c> code, and the driver's own localized description.
    /// <para>
    /// THE SPLIT IS WHAT MAKES THE LATCH TESTABLE. Reading these needs macOS, a device and a failure nobody can
    /// provoke on demand. DECIDING on them needs nothing, so <see cref="MetalDeviceLossLatch"/> takes this
    /// snapshot and every one of its behaviours (the once-only claim, the liveness flip, the header string, the
    /// publish race) runs under <c>dotnet test</c> on a machine with no Metal at all. That is the same split
    /// <see cref="MetalDeviceFacts"/> takes for the probe, for the same reason.
    /// </para>
    /// <para>
    /// THE INCUMBENT PRODUCES NONE OF THIS. It reads <c>status</c> in exactly one place, to decide whether
    /// waiting is worth it, and never reads <c>error</c> at all, so a Metal device loss today is invisible to the
    /// engine and to telemetry. That is what #427 asks for, and the correct time to close it is the day the
    /// backend lands: retrofitting the reporting after the first field crash wastes the crash.
    /// </para>
    /// </summary>
    /// <param name="Status">The buffer's final <c>-status</c>.</param>
    /// <param name="Code">The <c>-code</c> of its <c>-error</c>, or <see cref="MTLCommandBufferError.None"/> when
    /// there was no error. STABLE, unlike the description, which is why the header field is built on it.</param>
    /// <param name="Description">The driver's <c>-localizedDescription</c>, empty when there is no error or it
    /// had nothing to say. Localized by name and by nature, so it is a sentence for a human rather than a token
    /// to group on.</param>
    internal readonly record struct MetalCommandBufferFault(
        MTLCommandBufferStatus Status,
        MTLCommandBufferError Code,
        string Description)
    {
        /// <summary>The healthy answer: a buffer that completed with a nil error.</summary>
        internal static MetalCommandBufferFault Completed
            => new(MTLCommandBufferStatus.Completed, MTLCommandBufferError.None, "");

        /// <summary>
        /// Whether this reading is a FAILURE, which is the whole of what the latch acts on.
        /// <para>
        /// EITHER SIGNAL COUNTS, deliberately. Metal sets <c>status</c> to <c>Error</c> and hands back a non-nil
        /// <c>error</c> together, so in practice these agree, and requiring both would let a driver that reported
        /// only one of them slip a failure past the latch silently. Requiring either costs nothing on the healthy
        /// path, where both are absent.
        /// </para>
        /// </summary>
        internal bool IsFailure
            => Status == MTLCommandBufferStatus.Error || Code != MTLCommandBufferError.None;

        /// <summary>
        /// The STABLE token for this failure, which is what the telemetry session header carries so a capture can
        /// group across sessions. A code this build does not name still gets a token, as its number, because an
        /// unrecognised code is exactly the case where a reader most needs something to search for.
        /// </summary>
        internal string Token()
        {
            if (Code != MTLCommandBufferError.None)
            {
                return Enum.IsDefined(Code)
                    ? "MTLCommandBufferError" + Code
                    : "MTLCommandBufferError(" + ((long)Code).ToString(CultureInfo.InvariantCulture) + ")";
            }

            // Status said Error and the driver gave no code. Rare, and it still needs a token rather than
            // reporting as a healthy buffer.
            return "MTLCommandBufferStatusError";
        }

        /// <summary>
        /// Read <paramref name="buffer"/>'s status and error. Called AFTER the buffer has finished, which for
        /// this row means after a blocking wait and for the timeline row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571) means inside the completion handler M-F2 keeps
        /// for exactly this job.
        /// <para>
        /// NOT <c>[Conditional("DEBUG")]</c> AND NOT BEHIND A KNOB, which is phase 3's <c>CheckResult</c> lesson
        /// arriving here: a latch built on checks that compile away in Release never fires in the only
        /// configuration anybody ships. M-G4 says every command buffer, in EVERY configuration, and the cost is
        /// two message sends per completed buffer.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalCommandBufferFault Read(MTLCommandBuffer buffer)
        {
            if (buffer.IsNull) return Completed;

            MTLCommandBufferStatus status = buffer.Status();
            NSError error = buffer.Error();
            if (error.IsNull) return new MetalCommandBufferFault(status, MTLCommandBufferError.None, "");

            return new MetalCommandBufferFault(status, (MTLCommandBufferError)error.Code(),
                error.LocalizedDescription());
        }
    }
}
