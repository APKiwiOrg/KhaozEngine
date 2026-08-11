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
        /// </summary>
        public static string BuildMessage(string owner, string attempted) =>
            $"{attempted} tried to open a GPU recording while {owner} is already recording on this device. "
            + "The portable contract on IGpuCommandList.Begin is ONE OPEN RECORDING PER DEVICE. With Direct3D11 "
            + "in immediate-context mode a command list IS the device's immediate context and opening a second "
            + "one resets it, so every binding the first recording believes is live goes away and the device "
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
    /// backend rather than negotiable: the Veldrid Direct3D11 leg rejects a second recording outright, the
    /// engine's own native Direct3D11, Vulkan and Metal backends each tolerate N concurrent recordings for
    /// three different structural reasons, and the Veldrid Metal and Vulkan legs silently produce a
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
    /// with it and two devices never see each other. It is not a thread-safety mechanism: the contract it
    /// enforces is per DEVICE and says nothing about threads, so recording on two threads at once is refused
    /// here exactly as it is on one.
    /// </para>
    /// </summary>
    public static class GpuRecording
    {
        sealed class Slot { internal string? Owner; }

        static readonly ConditionalWeakTable<IGpuDevice, Slot> Slots = new();
        static readonly object Gate = new();

        /// <summary>Who is recording on <paramref name="device"/> right now, or null when nothing is. The
        /// question a caller asks when it wants to branch rather than be refused.</summary>
        public static string? OpenOwner(IGpuDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            lock (Gate) return Slots.TryGetValue(device, out Slot? slot) ? slot.Owner : null;
        }

        /// <summary>True when nothing is recording on <paramref name="device"/>, so a caller may open a list of
        /// its own. The inverse of <see cref="OpenOwner"/> being set, named for the question a producer asks.
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

            lock (Gate)
            {
                Slot slot = Slots.GetValue(device, static _ => new Slot());
                if (slot.Owner is { } open) throw new GpuNestedRecordingException(open, owner);
                // Begin BEFORE claiming the slot, so a backend that refuses the Begin for its own reasons (the
                // native Metal list refuses a second Begin on ITSELF) leaves nothing registered behind.
                commands.Begin();
                slot.Owner = owner;
            }
            return new GpuRecordingScope(device, commands);
        }

        /// <summary>Release the device's claim. Idempotent: a slot that is already clear stays clear.</summary>
        internal static void Close(IGpuDevice device)
        {
            lock (Gate)
            {
                if (Slots.TryGetValue(device, out Slot? slot)) slot.Owner = null;
            }
        }
    }

    /// <summary>
    /// One open recording, returned by <see cref="GpuRecording.Open"/>. Disposing it ends the command list and
    /// releases the device's claim, in that order, and the release happens even when the end throws: a device
    /// left permanently marked as recording would refuse every later frame for a fault that already happened.
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

        /// <summary>End the recording and release the device's claim.</summary>
        public void Dispose()
        {
            if (_device is null || _commands is null) return;
            try { _commands.End(); }
            finally { GpuRecording.Close(_device); }
        }
    }
}
