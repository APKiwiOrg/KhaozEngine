using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// A resource that knows which native Metal device created it. Implemented by every wrapper the factory
    /// hands out, so a device entry point can refuse one that belongs to a different device.
    /// </summary>
    internal interface IMetalOwnedResource
    {
        /// <summary>The creating device's liveness token, which every wrapper already holds. See
        /// <see cref="MetalResourceOwnership"/> for why that token IS the device's identity here.</summary>
        IDeviceLiveness Owner { get; }
    }

    /// <summary>
    /// THE CAST EVERY DEVICE ENTRY POINT MAKES, WITH THE TWO QUESTIONS IT HAS TO ASK. A seam resource arrives as
    /// an interface, and a plain <c>(MetalTexture)texture</c> answers only the first: is this the right backend.
    /// The second is whether it is the right DEVICE, and a type-only cast lets device A's texture blit on device
    /// B's queue with nothing said.
    ///
    /// <para><b>THE IDENTITY IS THE LIVENESS TOKEN AND NOT THE <c>MTLDevice</c> HANDLE, which is the one thing a
    /// reader will want to argue with.</b> Apple silicon reports a SINGLE <c>MTLDevice</c> for the process, which
    /// is why <c>MetalCompletionHandler</c> keys its registry on the queue rather than on the device, so two
    /// <c>MetalGpuDevice</c> instances routinely carry the same handle and comparing handles would answer "same
    /// device" for two devices that share nothing else. A <see cref="DeviceLiveness"/> is created once per
    /// device in <c>MetalGpuDevice.Create</c> and handed to every wrapper that device makes, so reference
    /// identity on it is exactly "created by this device" and costs no new field anywhere.</para>
    ///
    /// <para><b>WHAT THE MISTAKE COSTS, on the two machine shapes.</b> Where the devices are genuinely different
    /// (a Mac with more than one Metal device, which is the whole reason <c>KE_METAL_DEVICE</c> exists) a copy
    /// between them is illegal and the driver is entitled to anything. Where they share one <c>MTLDevice</c> the
    /// copy SUCCEEDS, and the failure moves to teardown: the resource is registered against one device's liveness
    /// while another device is using it, so whichever tears down first decides whether the other's release is a
    /// no-op or a call against a released object. That second shape is the dangerous one, because nothing about
    /// it is visible until a teardown order changes.</para>
    ///
    /// <para>The command-list row (https://github.com/APKiwiOrg/KhaozEngine/issues/573) applies the same
    /// invariant to <c>Submit</c> and to the fence it takes, so the rule is one rule rather than two.</para>
    /// </summary>
    internal static class MetalResourceOwnership
    {
        /// <summary>
        /// Cast <paramref name="resource"/> to the backend's own wrapper and confirm it was created by the device
        /// whose token is <paramref name="owner"/>. Throws by name for either failure.
        /// </summary>
        /// <typeparam name="T">The wrapper type the entry point needs.</typeparam>
        /// <param name="resource">The seam resource the caller passed.</param>
        /// <param name="owner">The calling device's liveness token, which is its identity.</param>
        /// <param name="parameterName">The entry point's own parameter name, for the exception.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
        internal static T Require<T>(object resource, IDeviceLiveness owner, string parameterName)
            where T : class, IMetalOwnedResource
        {
            // NULL IS ASKED FIRST, because the wrong-backend arm below cannot answer it: `resource is not T`
            // passes for null and the message it builds reads resource.GetType(), so a null argument came back as
            // a NullReferenceException from inside the refusal that exists to name the problem. An entry point
            // taking a params array of seam resources makes that trivially reachable from a caller, and the
            // caller's own parameter name is what the exception carries, because "resource" is this helper's word
            // and not theirs.
            ArgumentNullException.ThrowIfNull(resource, parameterName);

            if (resource is not T typed)
            {
                throw new ArgumentException(
                    "That resource was not created by the native Metal backend: it is a "
                    + resource.GetType().Name + " where a " + typeof(T).Name + " was needed. A resource created "
                    + "on one backend cannot be used on another's device, and the backend a device uses is fixed "
                    + "at creation.", parameterName);
            }

            if (!ReferenceEquals(typed.Owner, owner))
            {
                throw new ArgumentException(
                    "That " + typeof(T).Name + " was created by a DIFFERENT native Metal device. Every resource "
                    + "records the device that made it, because a type-only cast cannot tell two of them apart: "
                    + "on a machine with more than one Metal device the copy would be illegal, and on Apple "
                    + "silicon, where the whole process shares one MTLDevice, it would succeed and leave the two "
                    + "devices' teardowns disagreeing about who releases it. Create the resource on the device "
                    + "that uses it.", parameterName);
            }

            return typed;
        }
    }
}
