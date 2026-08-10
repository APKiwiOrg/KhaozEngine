using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHAT A BIND READS OFF A RESOURCE, AND IT READS IT THROUGH THAT RESOURCE'S OWN DISPOSAL GUARD.
    /// <see cref="MetalBuffer"/>, <see cref="MetalTexture"/> and <see cref="MetalSampler"/> implement it, and
    /// <see cref="MetalBoundResource"/> holds one so row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579) reaches the guard rather than a copy of what the
    /// guard answered once.
    ///
    /// <para><b>IT EXISTS BECAUSE A SNAPSHOT DEFEATS THE GUARD, WHICH IS A CORRUPTION AND NOT A LOUD FAILURE.</b>
    /// Every wrapper answers a nil handle after disposal, and <see cref="MetalBuffer.Ring"/> additionally answers
    /// null, because the ring holds the <c>contents()</c> pointer of an <c>MTLBuffer</c> that
    /// <see cref="MetalBuffer.Dispose"/> has released: a write reaching it afterwards is a <c>memcpy</c> into
    /// memory the driver has taken back. A resource set that copied the handle and the ring at creation would
    /// hand row 13 both of those released values for the set's whole life, with the guard sitting one field read
    /// away and unreachable. Holding the wrapper is what makes the guard the bind's predicate again.</para>
    ///
    /// <para><b>TWO PROPERTY READS, WHICH IS WHAT RESOLVE-ONCE CAN AFFORD TO GIVE BACK.</b> Everything genuinely
    /// expensive about a resolution still happens at creation: the kind resolution, the type and device checks,
    /// the argument-table position, the window arithmetic and whether the caller's per-draw offset applies. What
    /// moves to bind time is one null check inside each of these two members, which is the price of a disposed
    /// resource degrading to the nil-handle and unringed behaviour BY CONSTRUCTION instead of by a rule nobody
    /// can enforce.</para>
    ///
    /// <para><b>ONLY A BUFFER CAN CARRY A RING</b>, so the other two answer null unconditionally. That keeps the
    /// ring question off a type test on the bind path: the record asks the resource, and a texture or a sampler
    /// simply has no ring to offer.</para>
    /// </summary>
    internal interface IMetalBindable
    {
        /// <summary>The <c>MTLBuffer</c>, <c>MTLTexture</c> or <c>MTLSamplerState</c> as the raw Objective-C
        /// object an array setter writes. <see cref="IntPtr.Zero"/> once the resource is disposed, which Metal
        /// reads as an unbound slot rather than dereferencing a released pointer.</summary>
        IntPtr BindHandle { get; }

        /// <summary>The uniform ring whose captured segment base is the first term of a buffer bind's composed
        /// offset. Null for every non-buffer resource, for a buffer that is not ring-backed, and for a
        /// ring-backed buffer that has been disposed.</summary>
        MetalUniformRing? BindRing { get; }
    }
}
