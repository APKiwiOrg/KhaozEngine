using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE RESOLVED BINDING OF A SET, as row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) needs it at
    /// flush time: which argument table it goes in, the Objective-C object the array setter writes, and the three
    /// numbers a buffer bind composes its offset from.
    /// <para>
    /// THE OFFSET ROW 13 COMPOSES IS <c>frameBase + RangeOffset + callerDynamicOffset</c> (M-M4), where
    /// <c>frameBase</c> comes from <see cref="Ring"/> and the caller's per-draw offset is added only when
    /// <see cref="AppliesCallerOffset"/> is set. That last flag is the one thing
    /// <see cref="GpuResourceLayoutElement.Dynamic"/> decides on this backend.
    /// </para>
    /// <para>
    /// <see cref="Ring"/> IS NULL FOR EVERY NON-BUFFER BINDING AND FOR A BUFFER THAT IS NOT RING-BACKED, and a
    /// null ring simply contributes no frame base. Metal composes the base at bind for every buffer element
    /// whatever its kind, so unlike the Vulkan sibling there is no descriptor type deciding whether the base is
    /// applied, and a ring-backed buffer bound to a structured element is not a refusal here.
    /// </para>
    /// </summary>
    /// <param name="Space">Which of Metal's three argument tables this binding is written into.</param>
    /// <param name="Handle">The <c>MTLBuffer</c>, <c>MTLTexture</c> or <c>MTLSamplerState</c>, as the raw
    /// Objective-C object the array setters take a C array of.</param>
    /// <param name="Ring">The bound buffer's uniform ring, whose current segment base is the first term of the
    /// composed offset, or null.</param>
    /// <param name="RangeOffset">The set's own <see cref="GpuBufferRange.Offset"/>, 0 at every shipped site.</param>
    /// <param name="Range">The window this binding reads, which is the range's size or the buffer's own logical
    /// size. Carried for the M-M4 arithmetic and never handed to Metal, which takes no length.</param>
    /// <param name="AppliesCallerOffset">Whether the element was declared dynamic, so the caller's per-draw
    /// offset is added on top.</param>
    internal readonly record struct MetalBoundResource(
        MetalIndexSpace Space, IntPtr Handle, MetalUniformRing? Ring, uint RangeOffset, uint Range,
        bool AppliesCallerOffset);

    /// <summary>
    /// <see cref="IGpuResourceSet"/> ON THE NATIVE METAL BACKEND: the declared elements RESOLVED ONCE, at
    /// creation, into the handles and numbers a bind writes. Work-breakdown row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/576).
    ///
    /// <para><b>THERE IS NO NATIVE OBJECT HERE EITHER (M-B6).</b> Metal's answer to a descriptor set is an
    /// argument buffer, which section 8.4 declines by name: this engine's per-frame binding traffic is dominated
    /// by offsets-only rebinds of ONE set, which argument buffers do not improve, and every route to them changes
    /// the emission for every program at once. So a set here is a resolved table in managed memory, creation
    /// makes no native call, and <see cref="Dispose"/> releases nothing.</para>
    ///
    /// <para><b>EVERYTHING IS RESOLVED AT CREATION AND NOTHING AT A BIND.</b> A set is created once at load time
    /// across 68 shipped call sites and bound thousands of times a frame, so a type check, a range read or a ring
    /// lookup done at a bind is done for nothing. This is the same rule the Vulkan sibling states as V-M11 and
    /// reaches through a written descriptor set: the mechanism differs and the shape is identical.</para>
    ///
    /// <para><b>THE HANDLES ARE A SNAPSHOT, WHICH IS WHAT A DESCRIPTOR SET IS TOO.</b> Each binding records the
    /// Objective-C object its resource had at creation, so disposing a buffer or a texture while a set that names
    /// it is still bound is a caller error rather than something this type can absorb. The alternative considered
    /// was holding the WRAPPERS and re-reading <c>Handle</c> at every bind, which would answer nil instead of a
    /// released pointer after a disposal. It is declined because it puts a field read and a branch on the array
    /// setter's hot path to improve the failure mode of a caller error the sibling backends have identically, and
    /// because a nil texture in an argument table is not a better outcome than a loud one: what this does instead
    /// is refuse a resource that is ALREADY disposed at creation, so the snapshot can never start out nil.</para>
    ///
    /// <para><b>THE DECLARED <see cref="GpuResourceLayoutElement.Stages"/> IS NOT WHAT DECIDES WHICH STAGES GET A
    /// BIND, and reading it that way is the off-by-one this backend exists to close.</b> The authority is the
    /// per-program index table: an element with no entry for a stage is not referenced by that stage's emitted
    /// function and must NOT be bound for it (2.2b). The seam's visibility flags are what the ENGINE declared,
    /// the table is what the compiler DID, and row 13 binds through the second one.</para>
    ///
    /// <para><b>IT IS PLAIN DATA ON PURPOSE.</b> Nothing here reaches a factory, a device or a queue, so row 13's
    /// per-slot records can hold <see cref="Bindings"/> by reference without carrying resource creation into the
    /// recorder's field graph. That is the obligation the Vulkan sibling states as V-D2 and it costs nothing to
    /// honour here.</para>
    /// </summary>
    internal sealed class MetalResourceSet : IGpuResourceSet, IMetalOwnedResource
    {
        readonly IMetalDeviceLiveness _liveness;
        readonly MetalBoundResource[] _bindings;

        /// <param name="liveness">The creating device's token, which is its identity and which every bound
        /// resource is checked against.</param>
        /// <param name="description">The seam's description: a layout plus one resource per element, in
        /// order.</param>
        /// <exception cref="ArgumentException">The resource count disagrees with the layout, a resource is null,
        /// belongs to another backend or another device, is disposed, or is the wrong kind of thing for the
        /// element it lands on.</exception>
        internal MetalResourceSet(IMetalDeviceLiveness liveness, in GpuResourceSetDescription description)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;

            MetalResourceLayout layout = MetalResourceLayout.Require(
                description.Layout, liveness, "a native Metal resource set");
            IGpuBindableResource[] resources = description.Resources ?? [];

            if (resources.Length != layout.ElementCount)
            {
                throw new ArgumentException(
                    "A native Metal resource set was built with "
                    + resources.Length.ToString(CultureInfo.InvariantCulture)
                    + " resources against a layout declaring "
                    + layout.ElementCount.ToString(CultureInfo.InvariantCulture)
                    + " elements. Resources are matched to elements POSITIONALLY, because the element's position "
                    + "IS its binding number in the index table, so a count mismatch is not a shortfall the "
                    + "backend can work around.",
                    nameof(description));
            }

            Layout = layout;

            _bindings = new MetalBoundResource[layout.ElementCount];
            for (int i = 0; i < _bindings.Length; i++)
                _bindings[i] = Resolve(layout.ElementAt(i), resources[i], i);
        }

        /// <inheritdoc/>
        public IMetalDeviceLiveness Owner => _liveness;

        /// <summary>The layout this set satisfies, whose element count and declaration order this set's bindings
        /// are positional in.</summary>
        internal MetalResourceLayout Layout { get; }

        /// <summary>
        /// Every binding, in DECLARATION ORDER, which is binding order, which is the order the index table's
        /// <c>binding</c> key counts in. Row 13 walks this and looks each position up as
        /// <c>(setSlot, position, stage)</c>, where the set slot is the one the bind names rather than anything
        /// this object knows.
        /// </summary>
        internal ReadOnlySpan<MetalBoundResource> Bindings => _bindings;

        /// <summary>True once disposed. Nothing native is released. See the class note.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        /// <remarks>Idempotent, and releases nothing: a Metal resource set owns no Objective-C object at
        /// all.</remarks>
        public void Dispose() => IsDisposed = true;

        /// <summary>A set this backend created, on THIS device, refused by name for anything else. Row 13 binds
        /// through this.</summary>
        internal static MetalResourceSet Require(IGpuResourceSet? set, IMetalDeviceLiveness owner, string what)
        {
            if (set is null)
            {
                throw new ArgumentException(
                    $"{what} was given no resource set.", nameof(set));
            }

            return MetalResourceOwnership.Require<MetalResourceSet>(set, owner, nameof(set));
        }

        /// <summary>
        /// THE BIND WINDOW HAS TO BE A REAL WINDOW INSIDE THE BUFFER THE CALLER NAMED, whatever the element kind.
        /// Checked before the ring-specific M-M4 assertion, because a range that already leaves the LOGICAL
        /// buffer is a caller error with a better message than "leaves its segment".
        /// <para>
        /// STATIC AND OVER PLAIN INTEGERS so the arithmetic runs under an ordinary <c>[Fact]</c> on a machine
        /// with no Metal at all, which is the whole of what this refusal is: the rest of a resolution is type
        /// checks against wrappers only a device can make.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException">The window does not exist inside the buffer.</exception>
        internal static void RequireWindowInBuffer(uint rangeOffset, uint range, uint bufferSizeBytes,
            string where)
        {
            if (range != 0 && rangeOffset <= bufferSizeBytes && range <= bufferSizeBytes - rangeOffset) return;

            throw new ArgumentException(
                $"{where} binds {range.ToString(CultureInfo.InvariantCulture)} bytes at offset "
                + $"{rangeOffset.ToString(CultureInfo.InvariantCulture)} of a "
                + $"{bufferSizeBytes.ToString(CultureInfo.InvariantCulture)}-byte buffer. The window is resolved "
                + "once at creation and Metal's own setters carry no length at all, so nothing downstream would "
                + "report a window that does not exist: the shader would read whatever follows the buffer.",
                nameof(range));
        }

        // ONE BINDING, FULLY RESOLVED. Every refusal names the element by its declared name, because a message
        // about "element 4" is unactionable in a seven-element material layout, and by the KIND it declares,
        // because the mismatch is nearly always the resource array being one out of step with the layout.
        MetalBoundResource Resolve(in GpuResourceLayoutElement element, IGpuBindableResource? resource, int binding)
        {
            string where = $"'{Describe(element)}' at binding {binding.ToString(CultureInfo.InvariantCulture)} "
                + "of a native Metal resource set";

            if (resource is null)
            {
                throw new ArgumentException(
                    $"{where} is null. A set is resolved once at creation and never again, so there is no later "
                    + "point at which a missing resource could arrive.",
                    nameof(resource));
            }

            MetalIndexSpace space = MetalIndexSpaces.For(element.Kind);
            return space switch
            {
                MetalIndexSpace.Buffer => ResolveBuffer(element, resource, where),
                MetalIndexSpace.Texture => ResolveTexture(element, resource, where),
                _ => ResolveSampler(element, resource, where),
            };
        }

        MetalBoundResource ResolveBuffer(in GpuResourceLayoutElement element, IGpuBindableResource resource,
            string where)
        {
            uint rangeOffset;
            uint range;
            MetalBuffer buffer;

            if (resource is GpuBufferRange window)
            {
                buffer = Require<MetalBuffer>(window.Buffer, element, where, "a buffer");
                rangeOffset = window.Offset;
                range = window.Size;
            }
            else
            {
                buffer = Require<MetalBuffer>(resource, element, where, "a buffer");
                rangeOffset = 0;

                // THE LOGICAL SIZE, which on a ring-backed uniform buffer is emphatically not its allocation:
                // that is FramesInFlight segments and a window covering it would span every frame's copy at once.
                range = buffer.SizeInBytes;
            }

            RequireHandle(buffer.Handle.Handle, element, where, "MTLBuffer");
            RequireWindowInBuffer(rangeOffset, range, buffer.SizeInBytes, where);

            MetalUniformRing? ring = buffer.Ring;
            if (ring is not null)
            {
                // M-M4, AT CREATION, with the caller's per-draw offset taken as 0 because it is not knowable
                // here. IT CANNOT FIRE TODAY AND SAYING SO IS MORE USEFUL THAN IMPLYING IT IS LOAD-BEARING: the
                // window check above already bounds rangeOffset + range by the LOGICAL size, and the stride is
                // that size rounded UP, so a zero caller offset always fits. What it buys is that the invariant
                // is stated by one shared helper at the place the window is resolved and again at the place row
                // 13 composes the offset, so the two cannot drift into disagreeing about it. Row 13 is where it
                // can really fail, on the last frame slot only, for a caller offset five shipped renderers pass.
                MetalRingStride.RequireBindWindowFits(
                    rangeOffset, callerDynamicOffset: 0, range, ring.SegmentStrideBytes);
            }

            return new MetalBoundResource(
                MetalIndexSpace.Buffer, buffer.Handle.Handle, ring, rangeOffset, range, element.Dynamic);
        }

        MetalBoundResource ResolveTexture(in GpuResourceLayoutElement element, IGpuBindableResource resource,
            string where)
        {
            MetalTexture texture = Require<MetalTexture>(resource, element, where, "a texture");

            // A STAGING TEXTURE IS A Shared MTLBuffer AND NOT AN MTLTexture AT ALL (M-C5), so it has no handle
            // for the texture table. Refused by name here rather than binding nil, which would be a validation
            // error at the draw with nothing pointing back at the set that caused it.
            if (texture.IsStaging)
            {
                throw new ArgumentException(
                    $"{where} declares a {element.Kind} bound to a STAGING texture. A staging texture on this "
                    + "backend is a Shared MTLBuffer carrying a software subresource layout, not an MTLTexture, "
                    + "so there is nothing to write into the texture argument table. Copy it into a sampled "
                    + "texture and bind that.",
                    nameof(resource));
            }

            RequireHandle(texture.Handle.Handle, element, where, "MTLTexture");

            return new MetalBoundResource(
                MetalIndexSpace.Texture, texture.Handle.Handle, Ring: null, RangeOffset: 0, Range: 0,
                AppliesCallerOffset: false);
        }

        MetalBoundResource ResolveSampler(in GpuResourceLayoutElement element, IGpuBindableResource resource,
            string where)
        {
            MetalSampler sampler = Require<MetalSampler>(resource, element, where, "a sampler");
            RequireHandle(sampler.Handle.Handle, element, where, "MTLSamplerState");

            return new MetalBoundResource(
                MetalIndexSpace.Sampler, sampler.Handle.Handle, Ring: null, RangeOffset: 0, Range: 0,
                AppliesCallerOffset: false);
        }

        // THE WRONG-THING REFUSAL IS THIS TYPE'S AND THE WRONG-DEVICE ONE IS SHARED. The kind mismatch is what a
        // caller actually hits (a resource array one step out of line with its layout), so it names the element,
        // the kind it declares and what arrived. The identity half is MetalResourceOwnership's, unchanged, and
        // asking it a second time after the cast has already passed costs one type test and keeps that message in
        // exactly one place.
        T Require<T>(object? resource, in GpuResourceLayoutElement element, string where, string what)
            where T : class, IMetalOwnedResource
        {
            if (resource is not T typed)
            {
                throw new ArgumentException(
                    $"{where} declares {element.Kind}, which needs {what} created by the native Metal backend. It "
                    + $"was given a {resource?.GetType().Name ?? "null"}.",
                    nameof(resource));
            }

            return MetalResourceOwnership.Require<T>(typed, _liveness, nameof(resource));
        }

        // A DISPOSED RESOURCE ANSWERS A NIL HANDLE, and a set is a snapshot, so this is the one moment the
        // difference between "not created yet" and "already released" is still visible. Binding nil is legal
        // Objective-C and reads as an unbound slot at the draw, which is exactly the silent-wrong-pixel shape
        // this backend spends the whole of section 2.2b avoiding on the index side.
        static void RequireHandle(IntPtr handle, in GpuResourceLayoutElement element, string where,
            string objectType)
        {
            if (handle != IntPtr.Zero) return;

            throw new ArgumentException(
                $"{where} declares a {element.Kind} bound to a resource with no {objectType} handle, which means "
                + "it has already been disposed. A resource set records the Objective-C object each resource has "
                + "AT CREATION, so a disposed one would be bound as nil for the set's whole life.",
                nameof(element));
        }

        static string Describe(in GpuResourceLayoutElement element)
            => string.IsNullOrEmpty(element.Name) ? "<unnamed>" : element.Name;
    }
}
