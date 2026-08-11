using System;
using System.Globalization;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE RESOLVED BINDING OF A SET, as row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) needs it at
    /// flush time: which argument table it goes in, the resource whose Objective-C object the array setter writes,
    /// and the numbers a buffer bind composes its offset from.
    /// <para>
    /// THE OFFSET ROW 13 COMPOSES IS <c>frameBase + RangeOffset + callerDynamicOffset</c> (M-M4), where
    /// <c>frameBase</c> is <see cref="Ring"/>'s <see cref="MetalUniformRing.SegmentBaseBytes"/> FOR THE SEGMENT
    /// THAT RECORDING CAPTURED AT ITS <c>Begin</c>, and never
    /// <see cref="MetalUniformRing.CurrentSegmentBaseBytes"/>, which is the device's live segment and is
    /// documented for the two device-level callers only: a second list beginning meanwhile moves it, so a
    /// recording that read it could bind a base no write in that recording used. The caller's per-draw offset is
    /// added only when <see cref="AppliesCallerOffset"/> is set, which is the one thing
    /// <see cref="GpuResourceLayoutElement.Dynamic"/> decides on this backend.
    /// </para>
    /// <para>
    /// <see cref="Ring"/> IS NULL FOR EVERY NON-BUFFER BINDING AND FOR A BUFFER THAT IS NOT RING-BACKED, and a
    /// null ring simply contributes no frame base. Metal composes the base at bind for every buffer element
    /// whatever its kind, so unlike the Vulkan sibling there is no descriptor type deciding whether the base is
    /// applied, and a ring-backed buffer bound to a structured element is not a refusal here.
    /// </para>
    /// <para>
    /// <b>THE TWO GUARDED VALUES ARE READ THROUGH <see cref="Resource"/> AND NOT STORED</b>, which is what keeps
    /// <see cref="MetalBuffer.Ring"/>'s null-after-dispose the bind's predicate rather than a guard the set has
    /// already stepped past. See <see cref="IMetalBindable"/> and the class note on
    /// <see cref="MetalResourceSet"/>.
    /// </para>
    /// </summary>
    /// <param name="Space">Which of Metal's three argument tables this binding is written into.</param>
    /// <param name="Resource">The resolved wrapper: a <see cref="MetalBuffer"/>, a <see cref="MetalTexture"/> or
    /// a <see cref="MetalSampler"/>, held so the bind reads its handle and its ring through the wrapper's own
    /// disposal guard.</param>
    /// <param name="RangeOffset">The set's own <see cref="GpuBufferRange.Offset"/>, 0 at every shipped site.</param>
    /// <param name="Range">The window this binding reads, which is the range's size or the buffer's own logical
    /// size. Carried for the M-M4 arithmetic and never handed to Metal, which takes no length.</param>
    /// <param name="AppliesCallerOffset">Whether the element was declared dynamic, so the caller's per-draw
    /// offset is added on top.</param>
    internal readonly record struct MetalBoundResource(
        MetalIndexSpace Space, IMetalBindable Resource, uint RangeOffset, uint Range, bool AppliesCallerOffset)
    {
        /// <summary>The <c>MTLBuffer</c>, <c>MTLTexture</c> or <c>MTLSamplerState</c> the array setters take a C
        /// array of, as it stands NOW: <see cref="IntPtr.Zero"/> once the resource is disposed.</summary>
        internal IntPtr Handle => Resource.BindHandle;

        /// <summary>The bound buffer's uniform ring as it stands NOW, or null for a non-buffer binding, a buffer
        /// that is not ring-backed, and a ring-backed buffer that has been disposed.</summary>
        internal MetalUniformRing? Ring => Resource.BindRing;
    }

    /// <summary>
    /// EVERYTHING A BIND NEEDS FROM A SET, AS PLAIN DATA, which is what a per-slot record in
    /// <see cref="MetalBindRecords"/> holds instead of the set itself.
    ///
    /// <para><b>IDENTITY IS THE BINDINGS ARRAY, BY REFERENCE.</b> One <see cref="MetalResourceSet"/> builds
    /// exactly one array at creation and never replaces it, so comparing arrays compares sets, exactly as the
    /// Vulkan sibling compares <c>VkDescriptorSet</c> handles for the same reason. That is also what makes the
    /// offsets-only arm safe: a slot whose recorded array IS the one already written into this encoder's
    /// argument table needs its offsets moved and nothing else.</para>
    ///
    /// <para><b>IT IS A PROJECTION RATHER THAN THE OBJECT SO THE FLUSH IS DEVICE-FREE TESTABLE.</b> A
    /// <see cref="MetalResourceSet"/> can only be built by resolving real <see cref="MetalBuffer"/>,
    /// <see cref="MetalTexture"/> and <see cref="MetalSampler"/> wrappers, which need an <c>MTLDevice</c>. A
    /// record holding this instead is driven by an ordinary <c>[Fact]</c> over
    /// <see cref="MetalBoundResource"/>s built on any <see cref="IMetalBindable"/>, which is where the whole
    /// schedule, the run cutting and the offset composition are actually checked.</para>
    ///
    /// <para><b>THE ARRAY IS HELD AND NEVER COPIED</b>, so a bind allocates nothing. Neither side mutates it: the
    /// set writes it once at creation and a bind reads it.</para>
    /// </summary>
    /// <param name="Bindings">Every binding in DECLARATION order, which is binding order. Null means the slot
    /// holds no set, which is the state clause 6's skip is about.</param>
    /// <param name="HasDynamicElement">Whether any binding applies the caller's per-draw offset, computed once at
    /// creation. What a non-zero dynamic offset is refused against: with no element to attach to, the offset
    /// would be silently dropped and the draw would read the buffer's first slot.</param>
    internal readonly record struct MetalBoundSet(MetalBoundResource[]? Bindings, bool HasDynamicElement)
    {
        /// <summary>Whether this record names a set at all. False for a slot never bound and for one bound to
        /// null, which the flush treats identically because they are.</summary>
        internal bool IsBound => Bindings is not null;

        /// <summary>The bindings, empty rather than null when the slot holds no set, so a walk has one
        /// shape.</summary>
        internal ReadOnlySpan<MetalBoundResource> Resources => Bindings;

        /// <summary>Whether two records name the same set, which is an array reference compare. See the class
        /// note for why that is the right question.</summary>
        internal bool SameSetAs(in MetalBoundSet other) => ReferenceEquals(Bindings, other.Bindings);
    }

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
    /// <para><b>EACH BINDING HOLDS THE WRAPPER, AND THE HANDLE AND THE RING ARE READ THROUGH IT AT THE BIND.</b>
    /// An earlier shape of this type snapshotted both values at creation, which is what a descriptor set does and
    /// which reads as resolve-once taken one step further. It is wrong here, because those two are precisely the
    /// values whose job is to CHANGE on disposal. <see cref="MetalBuffer.Ring"/> answers null once the buffer is
    /// disposed, and the whole reason it does is that the ring holds the <c>contents()</c> pointer of an
    /// <c>MTLBuffer</c> that has since been released, so a write reaching it lands in memory the driver has taken
    /// back. A snapshot puts that guard behind the set: the ringed arm would compose a base off a forgotten ring
    /// and write the released pointer into the argument table, silently. Holding
    /// <see cref="IMetalBindable"/> instead means a resource disposed after creation degrades to the nil-handle
    /// and unringed behaviour BY CONSTRUCTION, which is the posture every other row here converged on. What it
    /// costs is one null check inside each of two property reads at bind time. Everything genuinely expensive
    /// stays at creation: the kind resolution, the type and device checks, the argument-table position, the
    /// window arithmetic and whether the per-draw offset applies. Creation still refuses a resource that is
    /// ALREADY disposed, because a set that starts out nil is a caller error with no later point at which it
    /// could come right.</para>
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
        readonly IDeviceLiveness _liveness;
        readonly MetalBoundResource[] _bindings;

        /// <param name="liveness">The creating device's token, which is its identity and which every bound
        /// resource is checked against.</param>
        /// <param name="description">The seam's description: a layout plus one resource per element, in
        /// order.</param>
        /// <exception cref="ArgumentException">The resource count disagrees with the layout, a resource is null,
        /// belongs to another backend or another device, is already disposed, or is the wrong kind of thing for
        /// the element it lands on.</exception>
        /// <exception cref="ObjectDisposedException">The layout is already disposed, refused through
        /// <see cref="MetalResourceLayout.Require"/>.</exception>
        internal MetalResourceSet(IDeviceLiveness liveness, in GpuResourceSetDescription description)
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
            bool dynamic = false;
            for (int i = 0; i < _bindings.Length; i++)
            {
                _bindings[i] = Resolve(layout.ElementAt(i), resources[i], i);
                dynamic |= _bindings[i].AppliesCallerOffset;
            }

            // COMPUTED ONCE, because a bind asks it and a bind happens thousands of times a frame against a set
            // built once at load time across 68 shipped call sites. It is the only thing about this set a record
            // needs that is not in the bindings array itself.
            AsBound = new MetalBoundSet(_bindings, dynamic);
        }

        /// <inheritdoc/>
        public IDeviceLiveness Owner => _liveness;

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

        /// <summary>
        /// THIS SET AS THE PLAIN DATA A PER-SLOT RECORD HOLDS (<see cref="MetalBoundSet"/>). What
        /// <c>MetalCommandList.SetGraphicsResourceSet</c> hands <see cref="MetalBindRecords"/>, so nothing in the
        /// recorder's field graph reaches a layout, a liveness token or this object at all.
        /// </summary>
        internal MetalBoundSet AsBound { get; }

        /// <summary>True once disposed. Nothing native is released, and the flag is what <see cref="Require"/>
        /// refuses on. See the class note.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        /// <remarks>Idempotent, and releases nothing: a Metal resource set owns no Objective-C object at
        /// all.</remarks>
        public void Dispose() => IsDisposed = true;

        /// <summary>
        /// A set this backend created, on THIS device, NOT DISPOSED, refused by name for anything else. Row 13
        /// binds through this, which is the reason the disposed check is here rather than nowhere: a set owns no
        /// Objective-C object, so binding a disposed one would simply work, and the caller who disposed it
        /// believes its bindings are gone.
        /// </summary>
        /// <exception cref="ArgumentException">No set, another backend's, or another device's.</exception>
        /// <exception cref="ObjectDisposedException">This backend's set, on this device, already
        /// disposed.</exception>
        internal static MetalResourceSet Require(IGpuResourceSet? set, IDeviceLiveness owner, string what)
        {
            if (set is null)
            {
                throw new ArgumentException(
                    $"{what} was given no resource set.", nameof(set));
            }

            MetalResourceSet typed = MetalResourceOwnership.Require<MetalResourceSet>(set, owner, nameof(set));

            if (typed.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(MetalResourceSet),
                    $"{what} was given a native Metal resource set that is already disposed. A set releases "
                    + "nothing on this backend, so the bind would work and would name resources the caller "
                    + "considers unbound.");
            }

            return typed;
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

        /// <summary>
        /// A TEXTURE HAS TO HAVE BEEN CREATED FOR THE DIRECTION THE ELEMENT DECLARES, which is the Vulkan
        /// sibling's refusal in this backend's own terms. <c>VulkanResourceSet.ResolveImage</c> refuses a texture
        /// with no view for the descriptor it is landing on, and every view there is created from the declared
        /// usage bits at RESOURCE creation. Metal creates no views at all (M-M10), so what stands in for the
        /// missing view is the <c>MTLTextureUsage</c> the texture was created with, which
        /// <see cref="MetalFormats.ToTextureUsage"/> derives from the same seam bits: no
        /// <see cref="GpuTextureUsage.Sampled"/> means no <c>ShaderRead</c>, and no
        /// <see cref="GpuTextureUsage.Storage"/> means no <c>ShaderWrite</c>. Binding a texture into a table
        /// without the bit it is read or written through is a validation abort under the debug layer M-T7 arms on
        /// every run, and undefined behaviour without it.
        /// <para>
        /// THE MATRIX IS THE SIBLING'S, INCLUDING THE ONE ARM THAT LOOKS ODD HERE.
        /// <see cref="GpuTextureUsage.GenerateMipmaps"/> admits a read-only binding, because Vulkan creates the
        /// sampled view for it. On Metal it maps to no usage bit at all, so such a texture is created
        /// <c>MTLTextureUsage.Unknown</c>, which Metal reads as "any usage" rather than "none": the binding is
        /// legal for the same reason by a different mechanism, and diverging from the sibling here would refuse
        /// something that works.
        /// </para>
        /// <para>
        /// STATIC AND OVER THE SEAM'S OWN ENUMS, like <see cref="RequireWindowInBuffer"/>, so both directions run
        /// under an ordinary <c>[Fact]</c> on a machine with no Metal at all.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException">The texture cannot be read, or cannot be written, the way the
        /// element declares.</exception>
        internal static void RequireTextureUsage(GpuResourceKind kind, GpuTextureUsage usage, string where)
        {
            bool write = kind == GpuResourceKind.TextureReadWrite;

            GpuTextureUsage needed = write
                ? GpuTextureUsage.Storage
                : GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps;

            if ((usage & needed) != 0) return;

            throw new ArgumentException(
                $"{where} declares a {kind} bound to a texture created with usage {usage}, which cannot be "
                + (write ? "written" : "read") + " by a shader. A Metal texture's usage bits are fixed at "
                + "creation from the declared seam usage and no view narrows or widens one here (M-M10), so this "
                + "is a texture that was not created for the job rather than something the bind could arrange. "
                + "Add GpuTextureUsage." + (write ? "Storage" : "Sampled") + " to its description.",
                nameof(usage));
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
                MetalIndexSpace.Buffer, buffer, rangeOffset, range, element.Dynamic);
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

            RequireTextureUsage(element.Kind, texture.Usage, where);
            RequireHandle(texture.Handle.Handle, element, where, "MTLTexture");

            return new MetalBoundResource(
                MetalIndexSpace.Texture, texture, RangeOffset: 0, Range: 0, AppliesCallerOffset: false);
        }

        MetalBoundResource ResolveSampler(in GpuResourceLayoutElement element, IGpuBindableResource resource,
            string where)
        {
            MetalSampler sampler = Require<MetalSampler>(resource, element, where, "a sampler");
            RequireHandle(sampler.Handle.Handle, element, where, "MTLSamplerState");

            return new MetalBoundResource(
                MetalIndexSpace.Sampler, sampler, RangeOffset: 0, Range: 0, AppliesCallerOffset: false);
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

        // A DISPOSED RESOURCE ANSWERS A NIL HANDLE, and this is the moment a set that would be born useless is
        // still in front of the caller. A resource disposed LATER degrades to the same nil at the bind on its
        // own, because the binding holds the wrapper rather than a copy of what it answered here, so what this
        // refusal adds is the case where there was never anything to bind at all. Binding nil is legal
        // Objective-C and reads as an unbound slot at the draw, which is exactly the silent-wrong-pixel shape
        // this backend spends the whole of section 2.2b avoiding on the index side.
        static void RequireHandle(IntPtr handle, in GpuResourceLayoutElement element, string where,
            string objectType)
        {
            if (handle != IntPtr.Zero) return;

            throw new ArgumentException(
                $"{where} declares a {element.Kind} bound to a resource with no {objectType} handle, which means "
                + "it has already been disposed. A set resolves its resources once at creation, so a resource "
                + "that is nil here is nil for the set's whole life with nothing later to fix it.",
                nameof(element));
        }

        static string Describe(in GpuResourceLayoutElement element)
            => string.IsNullOrEmpty(element.Name) ? "<unnamed>" : element.Name;
    }
}
