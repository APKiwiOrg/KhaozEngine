using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-F5's ACQUIRE RING: the binary semaphores <c>vkAcquireNextImageKHR</c> signals, handed out by a
    /// MONOTONIC ACQUIRE COUNTER and never by image index.
    ///
    /// <para><b>THE INDEXING IS THE WHOLE POINT AND IT IS THE MOST COMMON VULKAN SWAPCHAIN BUG.</b> The semaphore
    /// is handed to <c>vkAcquireNextImageKHR</c> BEFORE the image index is known, so a ring indexed by image index
    /// reuses a semaphore that may still be pending from an earlier acquire which returned a DIFFERENT image.
    /// Reusing a pending binary semaphore is undefined behaviour, and it manifests as a validation error and an
    /// INTERMITTENT HANG rather than as a clean failure, which is the class of defect a five-minute windowed pass
    /// does not find. Indexing by the acquire counter makes the reuse distance the ring's capacity instead of
    /// whatever the presentation engine happened to return.</para>
    ///
    /// <para><b>THE CAPACITY IS <c>max(framesInFlight, imageCount) + 1</c>, and it is the maximum rather than
    /// either alone because the two clocks are different.</b> Acquires are paced by the PRESENTATION ENGINE and
    /// recording is paced by the FRAME LOOP, so a ring sized on frames in flight can be exhausted by a swapchain
    /// with more images than that, and a ring sized on image count can be exhausted by a deeper pipeline. The
    /// plus one is the slack that keeps the semaphore handed to the oldest outstanding acquire out of reach for
    /// one more turn.</para>
    ///
    /// <para><b>RETIREMENT IS WHOLESALE AND HAPPENS UNDER THE RECREATE'S DRAIN.</b> A semaphore an acquire
    /// signalled that nothing ever waited on stays PENDING, and destroying a pending semaphore is undefined
    /// behaviour a validation layer catches and drivers mostly tolerate until they do not. There is no way to ask
    /// a binary semaphore whether it is pending, so the only safe retirement point is one where the queue is
    /// provably idle, which is what makes the recreate's drain UNCONDITIONAL rather than resize-only. This type
    /// therefore has no per-entry retire: <see cref="Rebuild"/> destroys every semaphore and makes a fresh set,
    /// and its caller has already drained.</para>
    ///
    /// <para>The counter, the capacity arithmetic and the handout order are all decided here over a seam of two
    /// driver calls, so the reuse-distance property is asserted by a plain <c>[Fact]</c> over a simulated acquire
    /// sequence that includes <c>OUT_OF_DATE</c> returns, on a machine with no Vulkan loader.</para>
    /// </summary>
    internal sealed class VulkanAcquireRing : IDisposable
    {
        readonly IVulkanSwapchainApi _api;
        readonly int _framesInFlight;

        ulong[] _semaphores = Array.Empty<ulong>();
        ulong _counter;
        bool _disposed;

        /// <param name="api">The swapchain seam, for <c>vkCreateSemaphore</c> and <c>vkDestroySemaphore</c>.</param>
        /// <param name="framesInFlight">The device's pipeline depth, one half of the capacity maximum.</param>
        internal VulkanAcquireRing(IVulkanSwapchainApi api, int framesInFlight)
        {
            ArgumentNullException.ThrowIfNull(api);
            if (framesInFlight < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    "A native Vulkan acquire ring is sized on the device's frames in flight, which is at least 1.");
            }

            _api = api;
            _framesInFlight = framesInFlight;
        }

        /// <summary>How many semaphores the ring currently holds. 0 before the first <see cref="Rebuild"/>.
        /// </summary>
        internal int Capacity => _semaphores.Length;

        /// <summary>
        /// How many semaphores have been handed out since the device was created. Monotonic and never reset by a
        /// rebuild, deliberately: it is the acquire counter the indexing is defined against, and resetting it at
        /// a recreate would make the first acquire after a resize reuse the index of the last one before it.
        /// </summary>
        internal ulong AcquireCount => _counter;

        /// <summary>The capacity a swapchain of <paramref name="imageCount"/> images needs at
        /// <paramref name="framesInFlight"/>. Pure, so the sizing rule is asserted directly.</summary>
        internal static int CapacityFor(int framesInFlight, int imageCount)
            => Math.Max(framesInFlight, imageCount) + 1;

        /// <summary>
        /// DESTROY EVERY SEMAPHORE AND MAKE A FRESH SET for a swapchain of <paramref name="imageCount"/> images.
        /// The caller has already drained the queue, which is what makes destroying the old set safe.
        /// </summary>
        internal void Rebuild(int imageCount)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (imageCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(imageCount), imageCount,
                    "A native Vulkan swapchain always has at least one image, so an acquire ring sized against "
                    + "zero of them is a swapchain that was never created.");
            }

            DestroyAll();

            int capacity = CapacityFor(_framesInFlight, imageCount);
            var made = new ulong[capacity];
            for (int i = 0; i < capacity; i++) made[i] = _api.CreateBinarySemaphore();
            _semaphores = made;
        }

        /// <summary>
        /// THE NEXT SEMAPHORE TO HAND TO <c>vkAcquireNextImageKHR</c>, taken at the acquire counter's current
        /// position and then advancing it. Called once per acquire ATTEMPT, including the attempts that come back
        /// <c>OUT_OF_DATE</c>: an attempt that failed still consumed its turn, and reusing its semaphore for the
        /// retry is precisely the reuse this ring exists to prevent.
        /// </summary>
        /// <exception cref="InvalidOperationException">The ring has no semaphores, which means no swapchain has
        /// been created yet.</exception>
        internal ulong Next()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_semaphores.Length == 0)
            {
                throw new InvalidOperationException(
                    "The native Vulkan acquire ring was asked for a semaphore before any swapchain existed. The "
                    + "ring is built from the swapchain's image count, so this is an acquire attempted before the "
                    + "first creation or after a failed one.");
            }

            ulong semaphore = _semaphores[(int)(_counter % (ulong)_semaphores.Length)];
            _counter++;
            return semaphore;
        }

        /// <summary>The semaphore at ring slot <paramref name="index"/>, for the tests that assert which slot an
        /// acquire took. Not part of the acquire path.</summary>
        internal ulong At(int index) => _semaphores[index];

        /// <summary>Destroy every semaphore. The caller has drained the queue.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DestroyAll();
        }

        void DestroyAll()
        {
            ulong[] dying = _semaphores;
            _semaphores = Array.Empty<ulong>();

            for (int i = 0; i < dying.Length; i++) _api.DestroySemaphore(dying[i]);
        }
    }
}
