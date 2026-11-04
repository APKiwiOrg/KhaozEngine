using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Thrown when a device is asked for, BY NAME, on one of the four <see cref="GpuBackendKind"/> members
    /// retired in 18.0.0 with the Veldrid backend that implemented them.
    /// <para>
    /// The members are kept forever, because the enum is append-only and a consuming game persists the player's
    /// chosen backend into a settings file. What they lost is an implementation, so a caller that NAMES one gets
    /// this instead of a device. The tidy-looking alternative, repointing the member at the API's native
    /// backend, is ruled out by section 5.2 of <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c>: it would
    /// move every Windows tester's stored <c>Direct3D11</c> onto a different implementation with no rebuild
    /// signal and no player notice, and a compile break or a loud throw is the safe failure.
    /// </para>
    /// <para>
    /// A PLAYER NEVER SEES THIS, and that is the point of where it is thrown. A retired member arriving as a
    /// stored preference, or as a <c>KE_GRAPHICS_BACKEND</c> token, is redirected by
    /// <see cref="GpuBackendSelector.Resolve(string?, OSPlatformKind, GpuBackendKind?)"/> onto the API's native
    /// backend before the provider registry is consulted at all. This is reachable only by naming a retired
    /// member outright, through <c>GpuDeviceContext.CreateHeadless(GpuBackendKind)</c> or the explicit-backend
    /// windowed overload, which is code rather than configuration.
    /// </para>
    /// </summary>
    public sealed class GpuBackendRetiredException : InvalidOperationException
    {
        /// <summary>The refusal reason. Null only for a consumer-created instance built through a
        /// message-only constructor outside the engine.</summary>
        public GpuBackendRetiredRefusal? Reason { get; }

        /// <summary>Build a refusal with its structured reason and developer message.</summary>
        public GpuBackendRetiredException(GpuBackendRetiredRefusal reason, string message)
            : base(message) => Reason = reason;

        /// <summary>Build a refusal with its structured reason, developer message and original failure.</summary>
        public GpuBackendRetiredException(GpuBackendRetiredRefusal reason, string message, Exception inner)
            : base(message, inner) => Reason = reason;

        /// <summary>The release that retired the implementation. Null for a consumer-created message-only instance.</summary>
        public Version? RetirementVersion { get; init; }

        /// <summary>The retired backend that was named.</summary>
        public GpuBackendKind Backend { get; init; }

        /// <summary>The live backend serving that API now, which is what the caller should name instead.</summary>
        public GpuBackendKind Replacement { get; init; }

        /// <summary>The exception as the creation path throws it, with the message built from the pair.</summary>
        public GpuBackendRetiredException(GpuBackendKind backend, GpuBackendKind replacement)
            : this(GpuBackendRetiredRefusal.BackendRetired, BuildMessage(backend, replacement))
        {
            Backend = backend;
            Replacement = replacement;
            RetirementVersion = new Version(18, 0, 0);
        }

        /// <summary>Standard message constructor. Both properties are left at their defaults.</summary>
        public GpuBackendRetiredException(string message) : base(message)
        {
        }

        /// <summary>Standard message plus inner-exception constructor. Both properties are left at their
        /// defaults.</summary>
        public GpuBackendRetiredException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        // Names the retirement, the release, and the member to name instead. The replacement is spelled out
        // rather than left to the reader because the four retirements are not symmetric: three of them have a
        // native implementation of the same API, and OpenGL has no replacement at all and answers the platform
        // default.
        internal static string BuildMessage(GpuBackendKind backend, GpuBackendKind replacement)
            => $"GpuBackendKind.{backend} was retired in 18.0.0, together with the Veldrid backend that "
                + "implemented it. The member is kept so a stored graphics preference written by an older build "
                + $"still loads, and it has no implementation behind it: name GpuBackendKind.{replacement} "
                + "instead. A retired backend arriving as a STORED PREFERENCE or as a KE_GRAPHICS_BACKEND token "
                + "is redirected onto that replacement automatically and reported through the ordinary "
                + "GpuBackendSource.FallbackAfterFailure path, so this exception means a caller named the "
                + "retired member in code.";
    }
}
