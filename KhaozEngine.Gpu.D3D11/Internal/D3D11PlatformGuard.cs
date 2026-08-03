using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The one wording for "this object exists only on Windows and something reached it elsewhere", shared by
    /// every type in this package whose members implement a cross-platform interface over Direct3D objects.
    /// <para>
    /// WHY THE BRANCH EXISTS AT ALL, since it is unreachable. A type like
    /// <see cref="D3D11MonotonicFenceTimeline"/> holds Direct3D objects, so its constructor is
    /// <c>[SupportedOSPlatform("windows")]</c> and it cannot be built anywhere else. But its members implement
    /// <see cref="ID3D11FenceTimeline"/>, which is called from code that runs everywhere, so those members cannot
    /// carry the platform attribute themselves (a platform-specific implementation of an all-platform interface
    /// member is its own compatibility warning, and with warnings as errors that fails the build). The
    /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/> check in each of them is what lets the analyzer see the
    /// Direct3D call below it as guarded, and this is the else branch that check needs.
    /// </para>
    /// <para>
    /// It is also the honest failure if the shape is ever broken. Reaching it means a Windows-only object was
    /// built somewhere it should not have been, and saying so beats an interop crash with a Direct3D type name in
    /// it that a macOS reader has no way to interpret.
    /// </para>
    /// </summary>
    internal static class D3D11PlatformGuard
    {
        /// <summary>The exception for a Windows-only <paramref name="what"/> reached off Windows.</summary>
        internal static PlatformNotSupportedException NotOnThisPlatform(string what)
            => new($"The native Direct3D 11 backend's {what} was used on an operating system that has no Direct3D "
                + "11. It holds Direct3D objects, so it can only be created on Windows, and reaching this means "
                + "one was created anyway. Read KhaozEngineD3D11.IsPlatformSupported before naming this backend.");
    }
}
