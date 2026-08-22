using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Every backend the engine was willing to try failed: the requested one, and then the platform's Veldrid
    /// incumbent it fell back to. There is no device, so the app cannot render.
    /// <para>
    /// IT EXISTS BECAUSE THE FIRST FAILURE USED TO VANISH. The fallback's own exception propagated raw out of
    /// device creation, so a crash report named the incumbent, carried the incumbent's reason, and said nothing
    /// about the backend that was actually asked for or why it did not work. On a machine where the requested
    /// backend is a native one and the incumbent is its Veldrid twin, those two failures usually have the SAME
    /// underlying cause (a missing loader, a driver that will not initialize), and the one worth reading is the
    /// first. A reader who only ever sees the second re-derives the whole sequence from a log line.
    /// </para>
    /// <para>
    /// SO BOTH ARE KEPT, in the two places a reader looks. The message names both backends and both reasons, in
    /// the order they were tried, which is what a session log and a support paste show. The first failure is
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
        /// <summary>The backend that was asked for and tried first. Default when this was built by one of the
        /// standard constructors.</summary>
        public GpuBackendKind RequestedBackend { get; }

        /// <summary>The backend the engine fell back to, which then failed as well. Default when this was built
        /// by one of the standard constructors.</summary>
        public GpuBackendKind FallbackBackend { get; }

        /// <summary>
        /// The FALLBACK's exception, whole. <see cref="Exception.InnerException"/> is the requested backend's
        /// failure instead, because that is the attempt a reader has to see and the one that used to be lost. On
        /// a requested backend that never threw (the machine simply reported no support for it, which produces a
        /// reason and no exception) the two are the same object, since there is only one exception to carry.
        /// Null when this was built by one of the standard constructors.
        /// </summary>
        public Exception? FallbackFailure { get; }

        /// <summary>
        /// The exception as the creation paths throw it. <paramref name="requestedFailure"/> is the rendered
        /// reason the requested backend did not work, and <paramref name="requestedCause"/> is the exception
        /// behind it, null when the machine reported no support and nothing threw.
        /// </summary>
        internal static GpuNoUsableBackendException Build(
            GpuBackendKind requested,
            string requestedFailure,
            GpuBackendKind fallback,
            Exception fallbackFailure,
            Exception? requestedCause)
            => new(
                BuildMessage(requested, requestedFailure, fallback, fallbackFailure),
                requested,
                fallback,
                fallbackFailure,
                requestedCause ?? fallbackFailure);

        GpuNoUsableBackendException(string message, GpuBackendKind requested, GpuBackendKind fallback,
            Exception fallbackFailure, Exception inner)
            : base(message, inner)
        {
            RequestedBackend = requested;
            FallbackBackend = fallback;
            FallbackFailure = fallbackFailure;
        }

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
