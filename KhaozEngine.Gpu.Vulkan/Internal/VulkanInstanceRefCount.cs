using System;
using System.Threading;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// What an instance is created FOR, and therefore what makes two requests the same instance. A record struct,
    /// so equality is the field comparison it should be and a caller cannot forget to update it when a field is
    /// added.
    /// </summary>
    /// <param name="Windowed">Whether a platform surface extension is needed. The headless path enables none
    /// (V-N6), which is the difference that makes a headless instance unusable for presentation and is why the
    /// two are not interchangeable.</param>
    /// <param name="Window">Which platform surface extension, meaningful only when
    /// <paramref name="Windowed"/>.</param>
    /// <param name="Validation">The validation rung, which decides the layer, the debug-utils extension and the
    /// <c>VkValidationFeaturesEXT</c> chain. Part of the key because a session cannot turn validation on for its
    /// second device: the layer is an instance-creation argument.</param>
    internal readonly record struct VulkanInstanceKey(
        bool Windowed,
        GpuWindowKind Window,
        VulkanValidationMode Validation);

    /// <summary>One holder's claim on the shared instance. Releasing it is idempotent, and the instance goes when
    /// the last one does.</summary>
    /// <typeparam name="T">The instance payload. The real one on the shipped path, a fake in the tests, which is
    /// what makes the whole refcount lifecycle testable on a machine with no Vulkan loader.</typeparam>
    internal sealed class VulkanInstanceLease<T> : IDisposable where T : class
    {
        readonly VulkanInstanceRefCount<T> _owner;
        int _released;

        internal VulkanInstanceLease(VulkanInstanceRefCount<T> owner, T value)
        {
            _owner = owner;
            Value = value;
        }

        /// <summary>The shared instance. Valid until this lease is released, and shared with every other live
        /// lease, so nothing holding one may destroy it.</summary>
        internal T Value { get; }

        /// <summary>True once this lease has been released. The instance may still be alive: another holder may
        /// have one.</summary>
        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>
        /// Give up this claim. IDEMPOTENT, and that matters more here than the usual dispose hygiene: a device
        /// disposed twice would otherwise drop the refcount twice and destroy an instance another live device is
        /// still making calls through, which on the Vulkan loader aborts the process rather than failing quietly.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _owner.Release();
        }
    }

    /// <summary>
    /// DECISION V-N1's BOOKKEEPING: one shared instance for the process, refcounted, created on the first claim
    /// and destroyed when the last one goes.
    /// <para>
    /// <b>THE POLICY IS SEPARATE FROM THE NATIVE CALLS, and that is what makes the lifecycle testable.</b>
    /// Creating a real <c>VkInstance</c> needs a loader, an ICD and a driver, which the developer machines this is
    /// written on have none of. The refcount, the destroy-at-zero rule, the idempotent release and the
    /// configuration check are all decidable from nothing at all, so they are here over an injected factory and
    /// the golden lifecycle assertion ("create and destroy many devices, and the instance is gone") runs under
    /// <c>dotnet test</c> everywhere.
    /// </para>
    /// <para>
    /// <b>WHY ONE INSTANCE IS MORE THAN TIDINESS, stated as the hypothesis it is.</b> The workflow header records
    /// that concurrent device creation raced the Vulkan loader on lavapipe and was fixed by serialising creation
    /// process-wide. The racing operation was <c>vkCreateInstance</c> and the loader's ICD enumeration underneath
    /// it, and with one refcounted instance the golden suite's repeated device creation stops touching that path
    /// after the first device. That is a hypothesis about a fixed defect and NOT a claim to have fixed anything.
    /// Bet MV7 tests it on the CI leg (https://github.com/APKiwiOrg/KhaozEngine/issues/529), and the lifecycle
    /// gate stays regardless: it also covers disposal and it is not this backend's to remove.
    /// </para>
    /// <para>
    /// <b>THE SUPPORT PROBE IS DELIBERATELY NOT A HOLDER.</b> It creates and destroys its own throwaway instance
    /// (<see cref="VulkanSupportProbe"/>), because it has to answer BEFORE any device exists, which is before this
    /// one is allowed to. A probe that took a lease would leave the process instance alive from the first
    /// settings-screen query until exit, on a machine that may never create a device at all.
    /// </para>
    /// <para>
    /// <b>ITS OWN LOCK, even though <c>GpuDeviceContext</c>'s lifecycle gate already serialises creation.</b> That
    /// gate covers the paths the context owns, and a consumer holding an <c>IGpuDevice</c> may dispose it from any
    /// thread it likes. The lock is uncontended on every path either way, and correctness that depends on a
    /// caller's discipline is not correctness.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The instance payload.</typeparam>
    internal sealed class VulkanInstanceRefCount<T> where T : class
    {
        readonly Func<VulkanInstanceKey, T> _create;
        readonly Action<T> _destroy;
        readonly object _gate = new();

        T? _live;
        VulkanInstanceKey _liveKey;
        int _count;

        /// <param name="create">Builds the real instance for a key. Called under the lock, exactly once per
        /// lifetime, and never for a second live instance.</param>
        /// <param name="destroy">Tears it down. Called under the lock when the count reaches zero.</param>
        internal VulkanInstanceRefCount(Func<VulkanInstanceKey, T> create, Action<T> destroy)
        {
            ArgumentNullException.ThrowIfNull(create);
            ArgumentNullException.ThrowIfNull(destroy);

            _create = create;
            _destroy = destroy;
        }

        /// <summary>How many live leases there are. THE number the lifecycle test asserts reaches zero.</summary>
        internal int Count { get { lock (_gate) return _count; } }

        /// <summary>Whether an instance currently exists. Separate from <see cref="Count"/> being zero on
        /// purpose: the test that "the instance is GONE at zero" has to be able to see the difference between a
        /// count of zero and a destroy that did not happen.</summary>
        internal bool IsLive { get { lock (_gate) return _live != null; } }

        /// <summary>
        /// Claim the shared instance for <paramref name="key"/>, creating it when there is none.
        /// </summary>
        /// <exception cref="NotSupportedException">The live instance does not carry what
        /// <paramref name="key"/> needs. See the remarks: that is the one case the single-instance model cannot
        /// serve, and refusing loudly is the only honest answer available.</exception>
        /// <remarks>
        /// A live instance carries a fixed extension and layer list, decided when it was created, and Vulkan
        /// offers no way to add one afterwards. So a process that holds a HEADLESS device open (no surface
        /// extension) and then asks for a WINDOWED one has asked for something the single-instance model cannot
        /// give it. Refusing names what happened. The alternatives are worse in both directions: creating a second
        /// instance abandons V-N1 silently and reopens the loader race MV7 is measuring, and creating every
        /// instance with the surface extensions "just in case" takes down the golden leg, which runs on a machine
        /// with no display server.
        /// <para>
        /// THE TEST IS SATISFACTION, NOT EQUALITY, and 17.40.0 is where that stopped being a distinction without
        /// a difference. It was written as equality, which refused the ordering the refusal message itself
        /// prescribes: a windowed instance's extension list is the headless one PLUS <c>VK_KHR_surface</c> and
        /// one platform surface extension, so a headless device asked for while a windowed one is live already
        /// has everything it needs and asks for nothing the live instance lacks. Equality refused it anyway, so
        /// "create the windowed device first" resolved nothing and a Linux client that opened a window and then
        /// took a headless capture was refused whichever order it used. The reverse direction is a real
        /// shortfall and still refuses, as does a windowed request for a DIFFERENT platform surface, and so does
        /// any change of validation rung, because the layer is a <c>vkCreateInstance</c> argument.
        /// </para>
        /// <para>
        /// THE CASE BECAME REACHABLE WHEN THE WINDOWED PATH LANDED
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), AND THE DECISION NOT TO CREATE A SECOND
        /// INSTANCE STANDS (https://github.com/APKiwiOrg/KhaozEngine/issues/543). What changed is only which
        /// requests count as a shortfall. A process that opens a HEADLESS device for a bake and then asks for a
        /// WINDOWED one is an editor or a tool rather than a game, nothing in this fleet does it, and both
        /// alternatives still cost more than that case is worth. It gets the message stating the ORDERING RULE
        /// that resolves it, which now actually resolves it: create the windowed device first, or run them in
        /// separate processes.
        /// </para>
        /// </remarks>
        internal VulkanInstanceLease<T> Acquire(in VulkanInstanceKey key)
        {
            lock (_gate)
            {
                if (_live is null)
                {
                    // The count is raised only AFTER creation succeeds. A throw here must leave the refcount at
                    // zero with no instance, or the next acquire would hand out a lease on nothing.
                    T created = _create(key);
                    _live = created ?? throw new InvalidOperationException(
                        "The native Vulkan instance factory returned nothing. A factory that cannot create an "
                        + "instance must throw, so the failure carries a reason the fallback can log.");
                    _liveKey = key;
                    _count = 1;
                    return new VulkanInstanceLease<T>(this, _live);
                }

                if (!Satisfies(_liveKey, key)) throw MismatchedConfiguration(_liveKey, key);

                _count++;
                return new VulkanInstanceLease<T>(this, _live);
            }
        }

        // Called only by a lease, and only once per lease, which is what makes an unbalanced release impossible
        // rather than merely unlikely.
        internal void Release()
        {
            T? dying;
            lock (_gate)
            {
                if (_count == 0) return;
                if (--_count > 0) return;

                dying = _live;
                _live = null;
                _liveKey = default;
                if (dying is null) return;

                // Destroyed UNDER the lock, so an acquire racing the last release cannot see a null instance and
                // start creating a second one while this one is still being torn down. The Vulkan loader is the
                // shared thing underneath both, and that overlap is exactly what the process-wide serialisation
                // exists to prevent.
                _destroy(dying);
            }
        }

        /// <summary>
        /// Whether the LIVE instance already carries everything <paramref name="asked"/> needs. Wider than
        /// equality in exactly one direction, and the direction is decided by the extension lists rather than by
        /// which entry point asked: windowed is headless plus the two surface extensions, so a headless request
        /// is served by either kind of live instance, and a windowed request is served only by a windowed
        /// instance on the SAME platform surface. Validation is a hard match either way, since the layer and the
        /// <c>VkValidationFeaturesEXT</c> chain are creation arguments and a live instance cannot gain them.
        /// <para>
        /// Pure and internal, so the whole rule is decidable with no loader, which is the same reason the
        /// refcount itself keeps its native calls behind an injected factory.
        /// </para>
        /// </summary>
        internal static bool Satisfies(in VulkanInstanceKey live, in VulkanInstanceKey asked)
        {
            if (live.Validation != asked.Validation) return false;
            if (!asked.Windowed) return true;
            return live.Windowed && live.Window == asked.Window;
        }

        static NotSupportedException MismatchedConfiguration(in VulkanInstanceKey live, in VulkanInstanceKey asked)
            => new($"The native Vulkan backend has one process-wide VkInstance (decision V-N1) and it is already "
                + $"live for a different configuration. It was created as {Describe(live)} and this device asked "
                + $"for {Describe(asked)}. A live instance's extension and layer lists are fixed at creation and "
                + "Vulkan offers no way to add one afterwards, so this needs the other device released first. "
                + "Create the windowed device before any headless one, or run them in separate processes.");

        static string Describe(in VulkanInstanceKey key)
            => (key.Windowed ? $"windowed on {key.Window}" : "headless")
                + $" with validation {key.Validation}";
    }
}
