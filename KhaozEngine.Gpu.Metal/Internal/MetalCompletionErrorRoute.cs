using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE ONE LINE BETWEEN THE COMPLETION HANDLER AND THE ERROR LATCH, and the consequence of M-F2's
    /// against-both ruling for the submit path: every buffer this backend commits carries row 5's handler, and
    /// the handler's only job is to deliver what it read to row 4's latch.
    ///
    /// <para><b>WHY AN ADAPTER AT ALL, rather than making the latch implement the sink.</b> The two speak
    /// different snapshots on purpose. <see cref="MetalCommandBufferOutcome"/> is what the completion path can
    /// read on a driver thread with no allocation and no Objective-C object left alive, and
    /// <see cref="MetalCommandBufferFault"/> is what every SITE that can see a failure reports, including the
    /// teardown drain, which reads its own status and error synchronously and never goes near a completion
    /// handler. Fusing them would put the completion path's shape on the drain's call site or the drain's on the
    /// completion path, and the latch is deliberately the one place that decides, from either.</para>
    ///
    /// <para><b>THE SITE NAME TRAVELS IN, which is what "latched at the fault site" means.</b> A device that has
    /// gone reports failures from every later call too, so saying which one saw it FIRST is the only ordering
    /// information a post-mortem gets out of an unordered completion stream. Every buffer this route sees came
    /// from a submit, so the site says so.</para>
    ///
    /// <para><b>IT CARRIES NO ORDERING RESPONSIBILITY AT ALL.</b> Metal delivers completion handlers on an
    /// arbitrary internal thread in no guaranteed order, and this takes no lock, sets no event and advances no
    /// counter: it reads a snapshot and hands it to a latch whose own claim is a compare-and-swap. Every ordering
    /// question on this backend is answered by <see cref="MetalTimeline"/>'s shared event instead.</para>
    ///
    /// <para><b>IT MUST NOT THROW</b>, which the latch already satisfies:
    /// <see cref="MetalDeviceLossLatch.Check"/> is a compare-and-swap, four volatile writes and a log line.
    /// <see cref="MetalCompletionHandler"/> catches anyway, because a rule enforced only by convention is the one
    /// that fails in the field, but a sink that relies on that catch is reporting nothing.</para>
    /// </summary>
    internal sealed class MetalCompletionErrorRoute : IMetalCommandBufferErrorSink
    {
        readonly MetalDeviceLossLatch _latch;

        /// <param name="latch">The device's one error latch (M-G4).</param>
        internal MetalCompletionErrorRoute(MetalDeviceLossLatch latch)
        {
            ArgumentNullException.ThrowIfNull(latch);

            _latch = latch;
        }

        /// <summary>The site name every completion-delivered failure is latched under.</summary>
        internal const string Site = "addCompletedHandler (a submitted command buffer)";

        /// <inheritdoc/>
        public void CommandBufferCompleted(in MetalCommandBufferOutcome outcome)
            => _latch.Check(
                new MetalCommandBufferFault(
                    (MTLCommandBufferStatus)outcome.Status,
                    (MTLCommandBufferError)outcome.ErrorCode,
                    outcome.ErrorDescription),
                Site);
    }
}
