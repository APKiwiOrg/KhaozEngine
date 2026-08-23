using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The refusal a nested recording gets instead of a corrupted device. Thrown by
    /// <see cref="GpuRecording.Open"/> when something opens a second command list on a device that already has one
    /// recording, which is the violation of the portable one-open-recording-per-device contract on
    /// <see cref="IGpuCommandList.Begin"/>.
    /// <para>
    /// It carries both halves of the diagnosis, because the useful sentence needs both: <see cref="Owner"/> is who
    /// is already recording (usually the frame's own list) and <see cref="Attempted"/> is who tried to open a
    /// second one. A stack trace alone names only the second, and the whole difficulty of
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see> was that the damage showed up
    /// nowhere near either of them.
    /// </para>
    /// </summary>
    public sealed class GpuNestedRecordingException : InvalidOperationException
    {
        /// <summary>Build the refusal. <paramref name="owner"/> is the recording that was already open,
        /// <paramref name="attempted"/> the one that was refused.</summary>
        public GpuNestedRecordingException(string owner, string attempted)
            : base(BuildMessage(owner, attempted))
        {
            Owner = owner;
            Attempted = attempted;
        }

        /// <summary>Who was already recording on the device.</summary>
        public string Owner { get; }

        /// <summary>Who tried to open a second recording and was refused.</summary>
        public string Attempted { get; }

        /// <summary>The message text, built here so a test can assert the wording without catching anything.
        /// <para>
        /// It explains itself with the PORTABLE RULE and never with one backend's mechanism, which is a
        /// deliberate change from the wording that shipped through 17.x. That named the incumbent's
        /// immediate-context mode as the reason, and a reader who meets a backend name in a portable refusal
        /// reasonably concludes the rule belongs to that backend and stops applying it once that backend is
        /// gone. The rule outlived the leg (#690), so the message says the rule, the damage and the fix.
        /// </para></summary>
        public static string BuildMessage(string owner, string attempted) =>
            $"{attempted} tried to open a GPU recording while {owner} is already recording on this device. "
            + "The portable contract on IGpuCommandList.Begin is ONE OPEN RECORDING PER DEVICE. Backends "
            + "disagree about what a second concurrent recording means, and on the ones that do not tolerate "
            + "it every binding the first recording believes is live goes away and the device "
            + "faults several draws later, nowhere near this call. Do the work in the frame's pre-record phase "
            + "instead: AppWindow.Run takes an onPrepare callback that runs before the frame's list is opened, "
            + "and Scene3D.PrepareFrame is where a 3D producer's own GPU work belongs. Work that is not "
            + "per-frame at all (a texture load, a readback, an offscreen capture) belongs outside the frame.";
    }

    /// <summary>
    /// THE SEAM'S OPEN-RECORDING REGISTER, and the one place the one-open-recording-per-device contract is
    /// enforced rather than described. Every command list the engine opens is opened through
    /// <see cref="Open"/>, so a second one on the same device is refused BY NAME, on every backend, before it
    /// can touch anything.
    /// <para>
    /// WHY THIS IS NOT LEFT TO THE BACKENDS. They disagree, and each disagreement is load-bearing for that
    /// backend rather than negotiable: the Veldrid Direct3D11 leg rejected a second recording outright, the
    /// engine's own native Direct3D11, Vulkan and Metal backends each tolerate N concurrent recordings for
    /// three different structural reasons, and the Veldrid Metal and Vulkan legs silently produced a
    /// half-recorded frame. Code written against any one of those does not port, and the machine a fallback
    /// lands on swaps the backend without telling the code. So the refusal lives above them all, where it reads
    /// the same everywhere and is provable with no GPU at all
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
    /// </para>
    /// <para>
    /// WHAT IT DOES NOT SEE, stated plainly because the limit matters. This register knows about the recordings
    /// that are OPENED THROUGH IT, which is every one the engine opens: the windowed frame loop, both snapshot
    /// hosts, the preview, the offscreen 2D captures, the ocean's priming pass, the retire barrier, the mipmap
    /// generates and every readback. A consumer that calls <see cref="IGpuCommandList.Begin"/> directly on a
    /// list of its own is invisible to it, and gets whatever its backend does. That still catches the case that
    /// matters, because the OUTER list in a nested pair is almost always the engine's own frame list, and it is
    /// the outer one whose bindings the inner recording destroys.
    /// </para>
    /// <para>
    /// The register is keyed by device instance and holds no strong reference, so a disposed device's entry dies
    /// with it. TWO DEVICES SHARE NOTHING, including their locks: each entry carries its own, and no lock at all
    /// is held while a backend is inside <see cref="IGpuCommandList.Begin"/>. That is deliberate rather than
    /// incidental, because Begin BLOCKS by design on the engine's own backends (the Metal and Vulkan rings both
    /// wait there for a free slot when the GPU is behind), so a process-wide gate around it would have made one
    /// device's backpressure into every other device's stall. It is still not a thread-safety mechanism: the
    /// contract it enforces is per DEVICE and says nothing about threads, so recording on two threads at once is
    /// refused here exactly as it is on one.
    /// </para>
    /// </summary>
    public static class GpuRecording
    {
        // One per device, and its own lock. Everything the register does to a device is short: read the owner,
        // claim it, release it. The expensive part (the backend's Begin) happens outside.
        // Commands is the identity half of the claim: it is what lets a release ask "am I still the open one"
        // rather than trusting the caller, which is what makes a copied scope harmless.
        sealed class Slot
        {
            internal string? Owner;
            internal IGpuCommandList? Commands;
        }

        static readonly ConditionalWeakTable<IGpuDevice, Slot> Slots = new();

        /// <summary>Who is recording on <paramref name="device"/> right now, or null when nothing is. The
        /// question a caller asks when it wants to branch rather than be refused.</summary>
        public static string? OpenOwner(IGpuDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (!Slots.TryGetValue(device, out Slot? slot)) return null;
            lock (slot) return slot.Owner;
        }

        /// <summary>True when nothing is recording on <paramref name="device"/>, so a caller may open a list of
        /// its own. The inverse of <see cref="OpenOwner"/> being set, named for the question a producer asks.
        /// <para>ADVISORY, not a reservation. It answers for the instant it was called, so a concurrent
        /// <see cref="Open"/> on the same device can win the race between the true it returned and the open it
        /// encouraged. Branch on it to avoid an expected refusal, never to make one impossible: the only thing
        /// that actually claims the device is <see cref="Open"/>.</para>
        /// </summary>
        public static bool CanOpen(IGpuDevice device) => OpenOwner(device) is null;

        /// <summary>
        /// Open <paramref name="commands"/> for recording on <paramref name="device"/>, registering
        /// <paramref name="owner"/> as the recording's name until the returned scope is disposed. Calls
        /// <see cref="IGpuCommandList.Begin"/> on the way in and <see cref="IGpuCommandList.End"/> on the way
        /// out, so the pair cannot be split by an early return or an exception.
        /// </summary>
        /// <param name="device">The device the list belongs to. Two lists on two devices never collide.</param>
        /// <param name="commands">The list to begin.</param>
        /// <param name="owner">What to call this recording in a refusal message. Say it the way it would read in
        /// a sentence, e.g. "the window's frame list" or "Scene3D.LoadTexture".</param>
        /// <exception cref="GpuNestedRecordingException">Another recording is already open on
        /// <paramref name="device"/>. Nothing was begun and the open recording is untouched.</exception>
        public static GpuRecordingScope Open(IGpuDevice device, IGpuCommandList commands, string owner)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentException.ThrowIfNullOrEmpty(owner);

            Slot slot = Slots.GetValue(device, static _ => new Slot());

            // CLAIM FIRST, BEGIN OUTSIDE THE LOCK. The claim is what makes the refusal correct, and it is three
            // instructions, so it is the only thing serialized. The Begin is not: it blocks by design on the
            // engine's own backends while the GPU catches up, and running it under the register's lock made one
            // device's backpressure into another device's stall.
            lock (slot)
            {
                if (slot.Owner is { } open) throw new GpuNestedRecordingException(open, owner);
                slot.Owner = owner;
                slot.Commands = commands;
            }

            try { commands.Begin(); }
            catch
            {
                // A backend may refuse the Begin for its own reasons (the native Metal list refuses a second
                // Begin on ITSELF, since it takes a fresh command buffer per recording). Nothing was begun, so
                // nothing may stay claimed: a device left marked as recording would refuse every later frame.
                Release(device, commands, end: false);
                throw;
            }
            return new GpuRecordingScope(device, commands);
        }

        /// <summary>End <paramref name="commands"/> and release the device's claim, if that list is still the one
        /// holding it. What <see cref="GpuRecordingScope.Dispose"/> calls.</summary>
        internal static void EndAndRelease(IGpuDevice device, IGpuCommandList commands)
            => Release(device, commands, end: true);

        // THE OWNER-MATCHED RELEASE, and the match is the point. A GpuRecordingScope is a struct, so it copies
        // silently, and before this it ended its list and cleared the device unconditionally: a second Dispose
        // ended a list that was no longer recording (which the native backends throw on) and a copy left over
        // from an earlier scope released whatever recording happened to be open by then. Matching the list makes
        // both of those nothing at all.
        //
        // The End runs while the claim is still held, so the device is never briefly free with a list still open,
        // and it runs inside the finally so an End that faults cannot leave the device marked as recording for
        // ever. Holding the lock across it is safe in a way holding it across Begin was not: End seals a list
        // rather than waiting on the GPU, and the lock is this device's own.
        //
        // `end` is false only on the Begin-failure path above, where there is no recording to seal.
        static void Release(IGpuDevice device, IGpuCommandList commands, bool end)
        {
            if (!Slots.TryGetValue(device, out Slot? slot)) return;
            lock (slot)
            {
                if (!ReferenceEquals(slot.Commands, commands)) return;
                try { if (end) commands.End(); }
                finally
                {
                    slot.Owner = null;
                    slot.Commands = null;
                }
            }
        }
    }

    /// <summary>
    /// One open recording, returned by <see cref="GpuRecording.Open"/>. Disposing it ends the command list and
    /// releases the device's claim, in that order, and the release happens even when the end throws: a device
    /// left permanently marked as recording would refuse every later frame for a fault that already happened.
    /// <para>
    /// IDEMPOTENT, AND SO IS EVERY COPY OF IT. This is a struct, so it copies whenever it is assigned, passed or
    /// captured, and a copy is indistinguishable from the original. Disposal is therefore matched against the
    /// list the device's claim actually names: the FIRST dispose of a scope ends its list and releases, and a
    /// second dispose, or a copy left over from a recording that has already finished, does nothing at all.
    /// Without that match a stale copy would end a list that is not recording, which the native backends throw
    /// on, and would release a claim belonging to whatever recording had opened since.
    /// </para>
    /// <para>The default value is the NOT-RECORDING scope, which disposes to nothing. That is what a frame loop
    /// holds on a frame it decided not to render.</para>
    /// </summary>
    public readonly struct GpuRecordingScope : IDisposable
    {
        readonly IGpuDevice? _device;
        readonly IGpuCommandList? _commands;

        internal GpuRecordingScope(IGpuDevice device, IGpuCommandList commands)
        {
            _device = device;
            _commands = commands;
        }

        /// <summary>The list this scope opened, or null for the default not-recording scope.</summary>
        public IGpuCommandList? Commands => _commands;

        /// <summary>End the recording and release the device's claim, if this scope's list is still the one
        /// holding it. See the type's note: a second call and a stale copy both do nothing.</summary>
        public void Dispose()
        {
            if (_device is null || _commands is null) return;
            GpuRecording.EndAndRelease(_device, _commands);
        }
    }
}
