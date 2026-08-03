using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// One <c>D3D11_MESSAGE_SEVERITY</c>, with the same ordinals the Direct3D header assigns, so a Windows reader
    /// can cast rather than switch and a mismatch would show up as a wrong name in a log rather than as a silent
    /// misclassification.
    /// </summary>
    internal enum D3D11InfoSeverity
    {
        /// <summary>Memory or object state the runtime believes is already corrupt. Raised to WARN.</summary>
        Corruption = 0,

        /// <summary>An API misuse the runtime refused. Raised to WARN.</summary>
        Error = 1,

        /// <summary>Something legal that the runtime believes is a mistake.</summary>
        Warning = 2,

        /// <summary>Informational chatter, which is most of the volume.</summary>
        Info = 3,

        /// <summary>An application message pushed into the queue rather than one the runtime raised.</summary>
        Message = 4,
    }

    /// <summary>One message out of the debug layer, flattened to plain data so the pump and its rate limit are
    /// device-free.</summary>
    internal readonly struct D3D11InfoMessage
    {
        internal D3D11InfoMessage(D3D11InfoSeverity severity, int category, int id, string text)
        {
            Severity = severity;
            Category = category;
            Id = id;
            Text = text ?? string.Empty;
        }

        internal D3D11InfoSeverity Severity { get; }

        /// <summary><c>D3D11_MESSAGE_CATEGORY</c> as its raw ordinal. Carried rather than named, because the
        /// category adds nothing a reader cannot get from the text and mapping 12 names would be 12 more things
        /// to keep in step with the Windows header.</summary>
        internal int Category { get; }

        /// <summary><c>D3D11_MESSAGE_ID</c>. The stable identity of a message across runs, and half of the key the
        /// rate limit dedups on.</summary>
        internal int Id { get; }

        /// <summary>The message body, already decoded from the queue's byte buffer.</summary>
        internal string Text { get; }
    }

    /// <summary>
    /// THE FOUR CALLS A DEBUG-LAYER PUMP MAKES, behind an interface for the same reason
    /// <see cref="ID3D11SwapchainSurface"/> and <see cref="ID3D11FenceTimeline"/> exist: everything above it is
    /// engine logic that runs under <c>dotnet test</c> on macOS, and the Windows side is a thin reader with
    /// nothing to decide.
    /// <para>
    /// NOTHING IMPLEMENTS THIS ON WINDOWS YET, and that is stated rather than left to be discovered.
    /// <c>ID3D11InfoQueue::GetMessageW</c> is a two-pass call into a caller-allocated <c>D3D11_MESSAGE</c> buffer,
    /// and Vortice 2.3.0 exposes only that raw form, so the reader is real interop that a machine with the
    /// Windows Graphics Tools installed has to exercise before anyone should believe it. Writing it here, on a
    /// Mac, would mean shipping unverified marshalling behind a lever whose entire purpose is being trusted
    /// during a crash investigation. It lands with the device row, which runs on Windows and creates the device
    /// this queue comes off.
    /// </para>
    /// </summary>
    internal interface ID3D11InfoQueueSource : IDisposable
    {
        /// <summary><c>GetNumStoredMessages</c>. The count of messages waiting to be read.</summary>
        ulong StoredMessageCount { get; }

        /// <summary>Read the message at <paramref name="index"/>, which is valid while
        /// <see cref="StoredMessageCount"/> has not been reset under the reader.</summary>
        D3D11InfoMessage Read(ulong index);

        /// <summary><c>ClearStoredMessages</c>. Called at the end of every pump, so the queue does not grow without
        /// bound on a session whose messages the rate limit is suppressing.</summary>
        void ClearStoredMessages();
    }
}
