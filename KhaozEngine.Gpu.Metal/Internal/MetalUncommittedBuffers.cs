using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// HOW MANY <c>MTLCommandBuffer</c>s THIS DEVICE HOLDS UNCOMMITTED, and the bound section 6.1 asserts:
    /// <see cref="MetalFramesInFlight.UncommittedBufferBound"/>, which is the frame depth plus one.
    ///
    /// <para><b>WHY THIS IS COUNTED AT ALL.</b> <c>MTLCommandQueue</c> has a maximum number of uncommitted
    /// command buffers and <c>-commandBuffer</c> BLOCKS when it is reached. That is a real bound with a real
    /// block, it is NOT the uniform ring's, and a blocked <c>-commandBuffer</c> would present as a frame-loop
    /// stall with no counter attached, which is exactly the shape section 16 exists to keep off the list. The
    /// backend keeps the queue's bound out of reach rather than relying on it, and this is the instrument that
    /// says whether it did.</para>
    ///
    /// <para><b>THE PLUS ONE IS THE PRESENT BUFFER.</b> M-W6 keeps <c>presentDrawable:</c> on its own command
    /// buffer, exactly as the incumbent does, so a frame at full depth holds one buffer per in-flight recording
    /// plus that one. Row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581) is what OCCUPIED the plus
    /// one, and before it landed the peak observed was one lower, which was a fact about coverage rather than
    /// about the bound.</para>
    ///
    /// <para><b>IT REPORTS AND DOES NOT THROW.</b> Exceeding the bound is a pacing defect rather than a
    /// corruption: the work is still correct, the frame loop is simply able to run further ahead than the design
    /// says it should, and the queue's own block is what would eventually stop it. Throwing would turn a
    /// measurable pacing problem into a crash in a consumer's frame loop, so the first exceedance logs once with
    /// the numbers and the device-free test asserts <see cref="Peak"/> against <see cref="Bound"/> over the
    /// recording shapes the backend can produce.</para>
    ///
    /// <para><b>ONE PER DEVICE, and interlocked</b>, because N command lists record concurrently on this backend
    /// (M-R3) and each one acquires and releases on its own thread.</para>
    /// </summary>
    internal sealed class MetalUncommittedBuffers
    {
        static readonly ILogger log = Log.For<MetalUncommittedBuffers>();

        readonly ILogger _log;

        int _outstanding;
        int _peak;
        int _reported;

        /// <param name="framesInFlight">The device's resolved <see cref="MetalFramesInFlight"/> depth.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal MetalUncommittedBuffers(int framesInFlight, ILogger? logger = null)
        {
            Bound = MetalFramesInFlight.UncommittedBufferBound(framesInFlight);
            _log = logger ?? log;
        }

        /// <summary>The bound, which is the frame depth plus one.</summary>
        internal int Bound { get; }

        /// <summary>How many buffers are held right now.</summary>
        internal int Outstanding => Volatile.Read(ref _outstanding);

        /// <summary>The highest <see cref="Outstanding"/> ever reached, which is what the device-free assertion
        /// reads.</summary>
        internal int Peak => Volatile.Read(ref _peak);

        /// <summary>True once <see cref="Peak"/> has passed <see cref="Bound"/>.</summary>
        internal bool ExceededBound => Peak > Bound;

        /// <summary>A buffer was acquired and is now held uncommitted.</summary>
        internal void Acquired()
        {
            int outstanding = Interlocked.Increment(ref _outstanding);
            RaisePeak(outstanding);
        }

        /// <summary>A held buffer was committed, or its recording was discarded and the buffer released. Both
        /// exits are the same event here: the backend no longer holds it.</summary>
        internal void Released() => Interlocked.Decrement(ref _outstanding);

        // The peak is a compare-and-swap loop rather than a read-then-write, because two lists beginning at once
        // would otherwise both read the old peak and both store their own, losing the higher of the two. That
        // matters here more than the usual: the peak IS the measurement, so a lost update reads as the bound
        // having been respected.
        void RaisePeak(int outstanding)
        {
            int peak = Volatile.Read(ref _peak);
            while (outstanding > peak)
            {
                int seen = Interlocked.CompareExchange(ref _peak, outstanding, peak);
                if (seen == peak) break;
                peak = seen;
            }

            if (outstanding <= Bound) return;
            if (Interlocked.Exchange(ref _reported, 1) == 1) return;

            _log.Warn($"The native Metal backend is holding {outstanding} uncommitted MTLCommandBuffers, which is "
                + $"past the bound of {Bound} ({MetalFramesInFlight.EnvVarName} frames in flight plus the one "
                + "present buffer). MTLCommandQueue blocks in -commandBuffer once its own maximum is reached, so "
                + "this is the warning that arrives before a frame-loop stall with nothing attached to it. It is "
                + "reported once per device.");
        }
    }
}
