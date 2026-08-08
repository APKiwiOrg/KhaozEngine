using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <c>VkShaderModule</c>s DEDUPLICATED BY SPIR-V HASH within a device (V-S7), because several shipped programs
    /// share a stage. Work-breakdown row 16 (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>THE SHARING IS REAL RATHER THAN THEORETICAL.</b> The engine's 34 shipped graphics programs and 8
    /// compute kernels are 76 stage emissions and 59 DISTINCT modules, measured on 2026-08-08. One fullscreen
    /// vertex source backs eleven post-processing programs on its own, and the model, skinned-model, shadow-depth,
    /// water and billboard families each share a stage across two. Without dedup a load creates 76 modules and
    /// destroys 76, and every duplicate is a driver-side parse of bytes the driver has already parsed.</para>
    ///
    /// <para><b>THE KEY IS A HASH OF THE BYTES AND NOTHING ELSE, WHICH IS WHY IT NEEDS NO OPTIONS TOKEN.</b> The
    /// Direct3D 11 disk cache keys on the sources plus both pinned option sets plus the engine version, because it
    /// caches an artefact ACROSS PROCESSES and has to prove the inputs still match. This cache lives and dies with
    /// one <c>VkDevice</c> inside one process, and the bytes it keys on ARE the emission: two equal SPIR-V modules
    /// are the same module to the driver whatever produced them. So <c>SpirvFrontEndPin.Identity</c> is
    /// deliberately not in the key, and a pin change moves the bytes and therefore the key by construction.</para>
    ///
    /// <para><b>SHA-256 RATHER THAN A CHEAP HASH.</b> Two distinct modules colliding would hand a pipeline the
    /// WRONG shader, which compiles, binds and renders something plausible. The whole cost is one hash per stage
    /// per load over a few tens of kilobytes, and the alternative that avoids the question entirely (comparing the
    /// full byte arrays on every lookup) is what the hash is standing in for.</para>
    ///
    /// <para><b>CREATION IS FREE-THREADED, SO THIS TAKES ITS OWN SHORT LOCK</b>, held across the
    /// <c>vkCreateShaderModule</c> itself and not only around the dictionary, for the reason
    /// <see cref="VulkanDescriptorSetLayoutCache"/> holds its own: two threads that both missed would both create,
    /// and the loser's handle would leak for the device's life. A module creation is a load-time call and never on
    /// a frame path.</para>
    ///
    /// <para><b>NOTHING BUT DEVICE TEARDOWN DESTROYS A HANDLE.</b> A handle is shared by every program with the
    /// same stage bytes, so <c>IGpuShaderSet.Dispose</c> cannot end one: the second program would be left naming a
    /// destroyed object. <see cref="DestroyAll"/> runs once, in the device's teardown window, after the wait that
    /// made the GPU idle and before the liveness flip. Holding every module for the device's life is also what the
    /// API asks for in the other direction anyway, since a <c>VkShaderModule</c> may legally be destroyed as soon
    /// as every pipeline built from it exists, and this backend has no point at which it knows that.</para>
    /// </summary>
    internal sealed class VulkanShaderModuleCache
    {
        readonly IVulkanShaderApi _api;
        readonly object _gate = new();
        readonly Dictionary<string, ulong> _byHash = new(StringComparer.Ordinal);

        int _requests;

        /// <param name="api">The native shader seam. Held here and NOT handed to a shader set, so a
        /// <see cref="VulkanShaderSet"/> carries handles and no way to create another module.</param>
        internal VulkanShaderModuleCache(IVulkanShaderApi api)
        {
            ArgumentNullException.ThrowIfNull(api);
            _api = api;
        }

        /// <summary>How many distinct SPIR-V modules have a handle. The observable half of the dedup claim: a run
        /// that creates the eleven fullscreen post programs leaves this counting ONE vertex module.</summary>
        internal int DistinctModuleCount
        {
            get { lock (_gate) return _byHash.Count; }
        }

        /// <summary>How many stages have asked, distinct or not. With <see cref="DistinctModuleCount"/> this is
        /// the hit rate, which is what the teardown diagnostic reports and what the dedup test asserts on.
        /// </summary>
        internal int RequestCount
        {
            get { lock (_gate) return _requests; }
        }

        /// <summary>
        /// The shared <c>VkShaderModule</c> for one SPIR-V module: the existing handle when these exact bytes have
        /// been seen on this device, and a freshly created one otherwise.
        /// </summary>
        /// <param name="spirv">The module's SPIR-V, as the front end emitted it. Not retained.</param>
        /// <exception cref="ArgumentException">The blob is empty or is not a whole number of 32-bit words.</exception>
        internal ulong GetOrCreate(ReadOnlySpan<byte> spirv)
        {
            // THE WORD-LENGTH GUARD LIVES HERE RATHER THAN ON THE SEAM, so it runs under dotnet test with no
            // loader. vkCreateShaderModule takes codeSize in BYTES and pCode as a uint*, so a length that is not a
            // multiple of four makes the driver read past the end of the buffer, which is a crash inside the ICD
            // with no useful stack rather than an error. Unreachable through the engine's own front end, since a
            // SPIR-V module is a word stream by definition, and cheap enough to state anyway.
            if (spirv.Length == 0 || spirv.Length % 4 != 0)
            {
                throw new ArgumentException(
                    "A SPIR-V module is a stream of 32-bit words, so its byte length is non-zero and a multiple of "
                    + "four. This one is " + spirv.Length.ToString(CultureInfo.InvariantCulture)
                    + " bytes, which means it did not come from the engine's front end.",
                    nameof(spirv));
            }

            string key = Convert.ToHexStringLower(SHA256.HashData(spirv));

            lock (_gate)
            {
                _requests++;

                if (_byHash.TryGetValue(key, out ulong existing)) return existing;

                // INSIDE THE LOCK. Two threads that both missed would both create, and the loser's handle would
                // leak until vkDestroyDevice collected it.
                ulong created = _api.CreateShaderModule(spirv);
                _byHash[key] = created;
                return created;
            }
        }

        /// <summary>
        /// Destroy every shared module. Called ONCE, from the device's teardown window. Returns how many were
        /// destroyed, which is <see cref="DistinctModuleCount"/> before the call.
        /// </summary>
        internal int DestroyAll()
        {
            lock (_gate)
            {
                int destroyed = _byHash.Count;
                foreach (ulong handle in _byHash.Values) _api.DestroyShaderModule(handle);
                _byHash.Clear();
                return destroyed;
            }
        }

        /// <summary>The line a teardown diagnostic quotes, with the hit rate that makes the dedup observable
        /// rather than asserted.</summary>
        internal string Describe()
        {
            lock (_gate)
            {
                return _requests.ToString(CultureInfo.InvariantCulture)
                    + " shader stages shared "
                    + _byHash.Count.ToString(CultureInfo.InvariantCulture)
                    + " VkShaderModules";
            }
        }
    }
}
