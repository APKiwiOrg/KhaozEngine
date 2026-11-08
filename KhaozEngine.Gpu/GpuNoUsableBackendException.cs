using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Every backend the engine was willing to try failed: the requested one, and then the platform default it
    /// fell back to. There is no device, so the app cannot render.
    /// <para>
    /// IT EXISTS BECAUSE THE FIRST FAILURE USED TO VANISH. The fallback's own exception propagated raw out of
    /// device creation, so a crash report named the fallback, carried the fallback's reason, and said nothing
    /// about the backend that was actually asked for or why it did not work. Where the requested backend and
    /// the fallback are two implementations of the same API, those two failures usually have the SAME
    /// underlying cause (a missing loader, a driver that will not initialize), and the one worth reading is the
    /// first. A reader who only ever sees the second re-derives the whole sequence from a log line.
    /// </para>
    /// <para>
    /// SO BOTH ARE KEPT. <see cref="Reason"/> identifies the requested refusal, with the requested diagnostic on
    /// <see cref="RequestedFailure"/> and both backend identifiers on <see cref="RequestedBackend"/> and
    /// <see cref="FallbackBackend"/>. The first failure is
    /// <see cref="Exception.InnerException"/>, so a debugger stops on the attempt that started this rather than
    /// on the recovery from it, and the second is <see cref="FallbackFailure"/>. Nothing is rendered into the
    /// message in place of an exception object: both stacks stay reachable.
    /// </para>
    /// <para>
    /// A backend that was NAMED outright never reaches here. Naming one turns fallback off, so its failure
    /// propagates alone and is already the only thing that happened.
    /// </para>
    /// </summary>
    public sealed class GpuNoUsableBackendException : InvalidOperationException
    {
        /// <summary>The refusal reason. Null only for a consumer-created instance built through a
        /// message-only constructor outside the engine.</summary>
        public GpuNoUsableBackendRefusal? Reason { get; }

        /// <summary>Build a refusal with its structured reason and developer message.</summary>
        public GpuNoUsableBackendException(GpuNoUsableBackendRefusal reason, string message)
            : base(message) => Reason = reason;

        /// <summary>Build a refusal with its structured reason, developer message and original failure.</summary>
        public GpuNoUsableBackendException(GpuNoUsableBackendRefusal reason, string message, Exception inner)
            : base(message, inner) => Reason = reason;

        /// <summary>The requested backend's failure diagnostic, including a probe or missing-provider refusal
        /// when creation never threw. Null for a consumer-created message-only instance.</summary>
        public string? RequestedFailure { get; init; }

        /// <summary>The backend that was asked for and tried first. Default when this was built by one of the
        /// standard constructors.</summary>
        public GpuBackendKind RequestedBackend { get; init; }

        /// <summary>The backend the engine fell back to, which then failed as well. Default when this was built
        /// by one of the standard constructors.</summary>
        public GpuBackendKind FallbackBackend { get; init; }

        /// <summary>
        /// The FALLBACK's exception, whole. <see cref="Exception.InnerException"/> is the requested backend's
        /// failure instead, because that is the attempt a reader has to see and the one that used to be lost. On
        /// a requested backend that never threw (the machine simply reported no support for it, which produces a
        /// reason and no exception) the two are the same object, since there is only one exception to carry.
        /// Null when this was built by one of the standard constructors.
        /// </summary>
        public Exception? FallbackFailure { get; init; }

        /// <summary>
        /// The exception as the creation paths throw it. <paramref name="requestedFailure"/> is the rendered
        /// reason the requested backend did not work, and <paramref name="requestedCause"/> is the exception
        /// behind it, null when the machine reported no support or a stored preference had no provider.
        /// </summary>
        internal static GpuNoUsableBackendException Build(
            GpuNoUsableBackendRefusal reason,
            GpuBackendKind requested,
            string requestedFailure,
            GpuBackendKind fallback,
            Exception fallbackFailure,
            Exception? requestedCause)
            => new(reason, BuildMessage(requested, requestedFailure, fallback, fallbackFailure),
                requestedCause ?? fallbackFailure)
            {
                RequestedBackend = requested,
                FallbackBackend = fallback,
                RequestedFailure = requestedFailure,
                FallbackFailure = fallbackFailure,
            };

        /// <summary>Standard parameterless constructor. Every property is left at its default.</summary>
        public GpuNoUsableBackendException()
            : base("No graphics backend could create a device.")
        {
        }

        /// <summary>Standard message constructor. Every property is left at its default.</summary>
        public GpuNoUsableBackendException(string message) : base(message)
        {
        }

        /// <summary>Standard message plus inner-exception constructor. Every property is left at its
        /// default.</summary>
        public GpuNoUsableBackendException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        // The two attempts in the order they happened, each with the backend and the reason side by side. The
        // fallback's exception is rendered as type plus message rather than left implicit: a reader pasting one
        // line into a support thread should not have to also paste the inner exception to say what went wrong
        // second.
        static string BuildMessage(GpuBackendKind requested, string requestedFailure, GpuBackendKind fallback,
            Exception fallbackFailure)
            => $"No graphics device could be created. {requested} was requested and failed "
                + $"({requestedFailure}), so the engine fell back to {fallback}, which failed too "
                + $"({fallbackFailure.GetType().Name}: {fallbackFailure.Message}). The first failure is this "
                + "exception's InnerException and the second is on FallbackFailure, so neither attempt has to "
                + "be reconstructed from a log.";
    }
}
