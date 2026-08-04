using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE WINDOWS <see cref="ID3D11InfoQueueSource"/>: <c>ID3D11InfoQueue</c> off a device created with the
    /// debug layer, as the three calls <see cref="D3D11InfoQueuePump"/> makes on it. Everything that decides what
    /// to do with a message (the rate limit, the severity promotion, the log line, the clear) is device-free on
    /// the far side of that interface, so what is left here is a read and a flatten.
    /// <para>
    /// THE TWO-PASS <c>GetMessageW</c> IS THE BINDING'S RATHER THAN OURS, and that is a correction to what
    /// <see cref="ID3D11InfoQueueSource"/> assumed rather than a shortcut. That note says Vortice 2.3.0 exposes
    /// only the raw <c>GetMessage(index, buffer, ref byteLength)</c> form, so a reader here would have to
    /// hand-marshal a <c>D3D11_MESSAGE</c> out of a caller-allocated buffer. Checked against the pinned package:
    /// it also exposes <c>Message GetMessage(ulong)</c>, whose body IS the two-pass call, sizing with a null
    /// buffer, stack-allocating, reading, and marshalling the description. Using it means the one piece of this
    /// row that could not be exercised on the machine it was written on is code the binding ships and tests
    /// rather than code invented here, which is the whole reason that note was cautious in the first place.
    /// </para>
    /// <para>
    /// THE <c>Message</c> IS A LOCAL AND NEVER A FIELD. It is a Vortice VALUE TYPE, so a field of one would make
    /// the CLR resolve the interop assembly merely to compute this type's layout, and the suite loads every type
    /// in this package by reflection on macOS. The queue itself is a COM interface, which is a pointer and costs
    /// nothing. That is the package's standing rule and this is one of the two places it would be easiest to
    /// break.
    /// </para>
    /// <para>
    /// THE SEVERITY IS CAST RATHER THAN SWITCHED, which <see cref="D3D11InfoSeverity"/> is written for: it
    /// carries the same ordinals the Direct3D header assigns, so a mismatch would show up as a wrong name in a
    /// log line rather than as a silent misclassification, and mapping five names by hand would be five more
    /// things to keep in step with a header that has not moved since Direct3D 11 shipped.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11InfoQueueMessages : ID3D11InfoQueueSource
    {
        readonly ID3D11InfoQueue _queue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        D3D11InfoQueueMessages(ID3D11InfoQueue queue) => _queue = queue;

        /// <summary>
        /// The message queue of <paramref name="device"/>, or NULL when it has none, which is not a fault: a
        /// device created without <c>D3D11_CREATE_DEVICE_DEBUG</c> exposes no <c>ID3D11InfoQueue</c> at all, and
        /// so does a device whose debug-layer request was retried away because the machine has no Graphics Tools
        /// installed. Both cases already warned at creation, so this answers null and the device builds no pump.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static ID3D11InfoQueueSource? TryCreateWindows(ID3D11Device device)
        {
            ArgumentNullException.ThrowIfNull(device);

            ID3D11InfoQueue? queue = device.QueryInterfaceOrNull<ID3D11InfoQueue>();
            return queue is null ? null : new D3D11InfoQueueMessages(queue);
        }

        /// <inheritdoc/>
        public ulong StoredMessageCount => StoredMessageCountWindows();

        /// <inheritdoc/>
        public D3D11InfoMessage Read(ulong index) => ReadWindows(index);

        /// <inheritdoc/>
        public void ClearStoredMessages() => _queue.ClearStoredMessages();

        /// <inheritdoc/>
        public void Dispose() => _queue.Dispose();

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        ulong StoredMessageCountWindows() => _queue.NumStoredMessages;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        D3D11InfoMessage ReadWindows(ulong index)
        {
            Message message = _queue.GetMessage(index);
            return new D3D11InfoMessage(
                (D3D11InfoSeverity)(int)message.Severity, (int)message.Category, (int)message.Id,
                message.Description);
        }
    }
}
