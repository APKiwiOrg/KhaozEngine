using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE LIVE <c>VkPipelineCache</c>: created WITH the device, seeded from
    /// <see cref="VulkanPipelineCacheFile"/> and written back in the device's teardown window (V-S7, 12.4).
    /// Work-breakdown row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>THE WHOLE PATH IS BEST-EFFORT, WHICH IS MV8's KILL SWITCH BY CONSTRUCTION.</b> There is no
    /// arrangement of this type that can fail a launch. No disk file is a cold start. A file whose header names
    /// another device is discarded before <c>pCacheData</c> is passed. A driver that refuses the blob ANYWAY gets
    /// one more chance with an empty cache, so a rejected seed costs a cold compile rather than the cache. And a
    /// create that fails outright leaves handle 0, which every pipeline creation passes through as "no cache".
    /// </para>
    ///
    /// <para><b>THE RETRY WITH NO SEED IS THE ONE PIECE OF BEHAVIOUR THAT IS NOT OBVIOUS.</b> Without it, a driver
    /// that dislikes a blob this backend's own header check accepted (a legal header over a body the driver has
    /// since stopped understanding, which is what a driver is entitled to do while keeping its
    /// <c>pipelineCacheUUID</c>) would leave the process with no cache at all for its whole life, so ONE bad file
    /// would cost every subsequent launch as well. The retry costs one extra call at device creation.</para>
    ///
    /// <para><b>IT IS WRITTEN BACK ONCE, AT TEARDOWN, RATHER THAN AS PIPELINES ARE CREATED.</b> A blob is the
    /// whole cache and there is no incremental form of <c>vkGetPipelineCacheData</c>, so writing per pipeline
    /// would rewrite the file once per program at load. Teardown is also the one moment every pipeline the run was
    /// going to make has been made.</para>
    /// </summary>
    internal sealed class VulkanPipelineCache
    {
        readonly IVulkanPipelineApi _api;
        readonly VulkanPipelineCacheFile? _file;

        ulong _handle;

        /// <param name="api">The device's pipeline seam.</param>
        /// <param name="file">Where the blob lives, or null when the cache is turned off
        /// (<see cref="VulkanPipelineCacheFile.EnvVarName"/>) or the platform reports no place to put it. A null
        /// file still creates a live in-process cache, which is worth having on its own: several shipped programs
        /// differ only in blend or depth state, so their pipelines share compiled stages within one run.</param>
        internal VulkanPipelineCache(IVulkanPipelineApi api, VulkanPipelineCacheFile? file)
        {
            ArgumentNullException.ThrowIfNull(api);

            _api = api;
            _file = file;

            byte[]? seed = file?.TryRead();
            SeedBytes = seed?.Length ?? 0;

            _handle = _api.CreateCache(seed ?? []);
            WarmStart = seed is not null && _handle != 0;

            if (_handle == 0 && seed is not null)
            {
                // See the type remarks: the seed is what the driver refused, so try again without it.
                _handle = _api.CreateCache([]);
            }
        }

        /// <summary>The <c>VkPipelineCache</c> every pipeline creation is compiled through, or 0 when there is
        /// none, which every creation passes straight to the driver as the null handle.</summary>
        internal ulong Handle => _handle;

        /// <summary>True when a disk blob was read, validated and accepted by the driver. The observable half of
        /// the warm-start claim MV8 measures.</summary>
        internal bool WarmStart { get; }

        /// <summary>How many bytes the disk blob carried, or 0 on a cold start.</summary>
        internal int SeedBytes { get; }

        /// <summary>How many bytes were written back at teardown, or 0 when nothing was.</summary>
        internal int PersistedBytes { get; private set; }

        /// <summary>
        /// Read the cache back off the driver and write it to disk, best effort. Called ONCE, from the device's
        /// teardown window, before <see cref="Destroy"/> and while the device is still alive.
        /// </summary>
        /// <returns>Whether a blob landed on disk.</returns>
        internal bool Persist()
        {
            if (_handle == 0 || _file is null) return false;

            byte[] blob = _api.ReadCacheData(_handle);
            if (blob.Length == 0) return false;

            if (!_file.TryWrite(blob)) return false;

            PersistedBytes = blob.Length;
            return true;
        }

        /// <summary>Destroy the handle. Called ONCE, after <see cref="Persist"/>, and idempotent.</summary>
        internal void Destroy()
        {
            if (_handle == 0) return;

            _api.DestroyCache(_handle);
            _handle = 0;
        }

        /// <summary>The line a teardown diagnostic quotes.</summary>
        internal string Describe()
        {
            if (_handle == 0 && SeedBytes == 0 && PersistedBytes == 0) return "no VkPipelineCache";

            string start = WarmStart
                ? "warm from " + SeedBytes.ToString(CultureInfo.InvariantCulture) + " cached bytes"
                : "cold";
            string written = PersistedBytes == 0
                ? "nothing written back"
                : PersistedBytes.ToString(CultureInfo.InvariantCulture) + " bytes written back";

            return "VkPipelineCache started " + start + ", " + written;
        }
    }
}
