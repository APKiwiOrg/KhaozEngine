using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHERE A COMMAND LIST'S BUFFER COMES FROM AND WHERE IT GOES (M-R2): a fresh <c>MTLCommandBuffer</c> per
    /// <c>Begin</c>, RETAINED, and released once it has been committed or the recording has been discarded.
    ///
    /// <para><b>THERE IS NO POOL BEHIND THIS AND THAT IS THE DECISION RATHER THAN AN OMISSION.</b> Vulkan needs
    /// <c>FramesInFlight</c> <c>VkCommandPool</c>s per list because a command buffer's memory is the pool's and a
    /// pool cannot be reset while its buffers are in flight. Metal's queue owns that allocation and hands out a
    /// fresh buffer each time, and an <c>MTLCommandBuffer</c> is single-use: there is no reset, no pool object
    /// and no allocator to choose between, so V-R2's ring has nothing to hold. What survives is the DEPTH, and it
    /// lives on the uniform ring's acquire alone (<see cref="MetalFramesInFlight"/>).</para>
    ///
    /// <para><b>THE RETAIN IS LOAD-BEARING.</b> <c>-commandBuffer</c> hands back an AUTORELEASED object, so it
    /// dies with whatever pool was in scope on the recording thread, and a list holds its buffer across every
    /// call between <c>Begin</c> and the submit. Retaining at acquisition and releasing at exactly one of the two
    /// exits is what makes that lifetime the LIST's rather than the caller's pool's.</para>
    ///
    /// <para><b>THE QUEUE'S OWN BOUND IS NAMED RATHER THAN ASSUMED AWAY.</b> <c>MTLCommandQueue</c> has a maximum
    /// number of UNCOMMITTED command buffers and <c>-commandBuffer</c> BLOCKS when it is reached, which would
    /// present as a frame-loop stall with no counter attached. Two things keep it out of reach rather than
    /// relying on it: <c>Begin</c> waits on the ring's frame slot first, which bounds how far ahead the frame
    /// loop can get, and <see cref="MetalUncommittedBuffers"/> counts what the backend holds so a device-free
    /// test can assert the bound.</para>
    ///
    /// <para><b>IT IS A SEAM SO THE LIST IS DEVICE-FREE.</b> Everything a command list decides (the second-Begin
    /// refusal, the seal, what a discarded recording releases, the encoder transitions) needs no Metal at all,
    /// and a fake handing out opaque numbers is what lets those run on the Linux and Windows legs.</para>
    /// </summary>
    internal interface IMetalCommandBufferSource
    {
        /// <summary>
        /// A fresh <c>MTLCommandBuffer</c> at +1, or <see cref="IntPtr.Zero"/> when the queue would not make one.
        /// <para>
        /// NIL IS THE CALLER'S TO REFUSE rather than this seam's, because what it means depends on where it
        /// happened: at <c>Begin</c> it is a device already in trouble and the list throws, and at the present
        /// boundary row 15 has its own answer. This member reports and does not interpret.
        /// </para>
        /// </summary>
        IntPtr Acquire();

        /// <summary>Release a buffer this source handed out. Called exactly once per successful
        /// <see cref="Acquire"/>: after the commit, or when a recording is discarded by a second
        /// <c>Begin</c> or by disposal. Safe for <see cref="IntPtr.Zero"/>.</summary>
        void Release(IntPtr commandBuffer);
    }
}
