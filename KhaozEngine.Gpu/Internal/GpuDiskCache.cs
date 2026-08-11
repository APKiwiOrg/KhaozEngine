using System;
using System.IO;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE FILE PLUMBING UNDER EVERY BACKEND'S DISK CACHE, AND DELIBERATELY NOTHING ABOVE IT. Where the cache
    /// lives, how the environment relocates or disables it, how an entry is written so a reader never sees half
    /// of one, and which exceptions count as "no cache today". The KEY, the payload and any header validation
    /// stay with the backend that owns them.
    ///
    /// <para>
    /// WHY THE LINE IS DRAWN THERE, AND WHY IT MOVED. Row 18 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> refused #531's "shader-cache KEY and file
    /// discipline" candidate outright, because the key it named exists at no backend: <c>D3D11ShaderKey</c> is
    /// pure CONTENT with no device in it, <c>VulkanPipelineCacheIdentity</c> is pure DEVICE with no pin and no
    /// engine version, and the intersection of the two is empty. That refusal stands and this type does not
    /// reopen it. What the refusal ALSO recorded is that the plumbing below the key is duplicated, and that it
    /// was two copies at the time, which is the count V-P4 declines. The Metal MSL cache
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/592">#592</see>) is the third client, which is
    /// the trigger <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/606">#606</see> named in advance.
    /// </para>
    /// <para>
    /// EVERY MEMBER IS BEST EFFORT AND NOTHING PROPAGATES. A read that throws, a directory that cannot be
    /// created, a truncated file, a disk that is full or read-only: all of them answer "no" and the caller
    /// compiles instead. That is the whole risk posture of a cache whose only job is to save time, and it is why
    /// the exception filter is a NAMED set rather than a bare catch: an <c>OutOfMemoryException</c> or a
    /// cancellation is not a cache miss and must not be swallowed as one.
    /// </para>
    /// <para>
    /// <c>LocalApplicationData</c> AND NOT <c>AppDataPaths</c>, for the reason <c>D3D11DxbcCache</c> wrote first.
    /// That type lives in <c>KhaozEngine.App</c>, which a GPU backend must not depend on, and everything cached
    /// here is DERIVED data reproducible from the sources at any time, so a user or a cleanup tool should be free
    /// to delete the whole tree. The engine version is a path SEGMENT for the same reason: an upgrade leaves one
    /// obviously prunable folder rather than files nothing will ever open again.
    /// </para>
    /// <para>
    /// AND SOMETHING ACTUALLY PRUNES IT NOW (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/611">
    /// #611</see>). That prunability was real and no code exercised it, so the tree accumulated one folder per
    /// engine version a machine had ever run, forever, and only a user or a cleanup tool ever removed one.
    /// <see cref="OpenDirectory"/> sweeps the siblings at cache open, once per process per cache, which is what
    /// makes the rule live in ONE place for all three backends rather than three times.
    /// </para>
    /// </summary>
    internal static class GpuDiskCache
    {
        /// <summary>
        /// The default location for a cache: <c>&lt;local-app-data&gt;/KhaozEngine/&lt;subfolder&gt;/&lt;engine
        /// version&gt;</c>. Empty when the platform reports no local application data at all, which is the signal
        /// to run without a cache rather than to invent a path in the current directory.
        /// </summary>
        /// <param name="subfolder">The cache's own folder name, one per backend and payload
        /// (<c>d3d11-dxbc</c>, <c>vulkan-pipeline-cache</c>, <c>metal-msl</c>).</param>
        /// <param name="engineVersion">The engine version, read off the calling backend's own assembly so
        /// nothing is kept in sync by hand.</param>
        internal static string DefaultDirectory(string subfolder, string engineVersion)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subfolder);
            ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);

            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(root)
                ? string.Empty
                : Path.Combine(root, "KhaozEngine", subfolder, engineVersion);
        }

        /// <summary>
        /// The directory <paramref name="envValue"/> asks for, or null for no cache at all. Blank means the
        /// default location, a disable word means null, and anything else is taken as a directory path VERBATIM,
        /// with no engine-version segment appended, because a caller who names a directory means that directory.
        /// <para>
        /// THE DISABLE WORDS ARE A SET RATHER THAN <c>off</c> ALONE because any other value is a path, so a
        /// session that meant to switch the cache off and typed <c>no</c> would otherwise get a cache in a
        /// directory called <c>no</c> and believe it was compiling fresh. That belief is the exact thing the
        /// switch exists to give a session chasing a miscompile.
        /// </para>
        /// </summary>
        internal static string? ResolveDirectory(string? envValue, string subfolder, string engineVersion)
        {
            if (string.IsNullOrWhiteSpace(envValue))
            {
                string fallback = DefaultDirectory(subfolder, engineVersion);
                return string.IsNullOrEmpty(fallback) ? null : fallback;
            }

            string value = envValue.Trim();
            return value.ToLowerInvariant() switch
            {
                "off" or "0" or "false" or "no" or "none" => null,
                _ => value,
            };
        }

        /// <summary>
        /// THE ONE MEMBER A CACHE CALLS AT OPEN: <see cref="ResolveDirectory"/>'s answer, with the stale sibling
        /// version folders swept when that answer is the DEFAULT location.
        /// <para>
        /// THE SWEEP IS GATED ON BEING THE DEFAULT DIRECTORY, never on an explicitly configured one. The three
        /// environment variables take a directory path VERBATIM with no engine-version segment appended, so a
        /// caller-named directory has no sibling version folders to reason about and deleting anything beside it
        /// would be deleting whatever else the caller keeps there. The gate is a comparison against
        /// <see cref="DefaultDirectory"/> rather than a test for a blank environment value, so pointing the
        /// variable AT the default location still prunes, and any difference at all (a trailing separator, a
        /// different spelling of the same path) fails safe by not pruning.
        /// </para>
        /// <para>
        /// SEPARATE FROM <see cref="ResolveDirectory"/> SO THAT ONE STAYS PURE. Resolution is a decision about a
        /// string and is called freely by tests and by callers who name their own directory. Deleting folders is
        /// not something either should do as a side effect, and a cache is OPENED once per process, which is
        /// exactly the frequency this sweep wants.
        /// </para>
        /// </summary>
        /// <param name="envValue">The raw environment value, as <see cref="ResolveDirectory"/> reads it.</param>
        /// <param name="subfolder">The cache's own folder name.</param>
        /// <param name="engineVersion">The running engine version, which is the folder that survives.</param>
        internal static string? OpenDirectory(string? envValue, string subfolder, string engineVersion)
        {
            string? directory = ResolveDirectory(envValue, subfolder, engineVersion);
            if (directory is null) return null;

            if (string.Equals(directory, DefaultDirectory(subfolder, engineVersion), StringComparison.Ordinal))
            {
                PruneOtherVersions(directory);
            }

            return directory;
        }

        /// <summary>
        /// Delete every sibling of <paramref name="versionDirectory"/> under its parent, best effort, and never
        /// throw. Returns how many folders went, for a test and for a diagnostic line.
        /// <para>
        /// EVERY OTHER VERSION GOES, INCLUDING A NEWER ONE, and both alternatives were weighed rather than
        /// skipped. Keeping the previous version back would make a downgrade or a bisect cheap and would roughly
        /// double the floor, which trades a permanent cost every player pays against a convenience only a
        /// developer uses, and a developer already has the environment variable. A folder from a NEWER version is
        /// a downgrade in progress rather than garbage, and deleting it does cost the upgrade back one cold
        /// start, which is seconds of re-emission and never a wrong answer. Neither is worth a version
        /// COMPARISON here: parsing a folder name to rank it invents a way to be wrong about a directory whose
        /// only real property is that the running engine will never open it.
        /// </para>
        /// <para>
        /// A FOLDER THAT WILL NOT DELETE IS LITTER RATHER THAN A FAILURE, matching what <see cref="TryDelete"/>
        /// does with a file, and it is skipped ON ITS OWN so one locked folder cannot stop the others. Nothing
        /// here is load bearing: a stale folder is never READ, because the engine version is a path segment as
        /// well as a key component, so this is disk hygiene and the correct answer to any refusal is to leave it
        /// and carry on.
        /// </para>
        /// </summary>
        /// <param name="versionDirectory">The running version's own directory, whose siblings are swept. Its own
        /// folder survives, and so does everything that is not a directory.</param>
        internal static int PruneOtherVersions(string versionDirectory)
        {
            if (string.IsNullOrWhiteSpace(versionDirectory)) return 0;

            string? parent;
            string keep;
            try
            {
                string trimmed = Path.TrimEndingDirectorySeparator(versionDirectory);
                parent = Path.GetDirectoryName(trimmed);
                keep = Path.GetFileName(trimmed);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                return 0;
            }

            if (string.IsNullOrEmpty(parent) || keep.Length == 0) return 0;

            string[] siblings;
            try
            {
                if (!Directory.Exists(parent)) return 0;
                siblings = Directory.GetDirectories(parent);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                return 0;
            }

            int removed = 0;
            foreach (string sibling in siblings)
            {
                if (string.Equals(Path.GetFileName(sibling), keep, StringComparison.Ordinal)) continue;

                try
                {
                    Directory.Delete(sibling, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    // Skipped on its own, so the next sibling still goes.
                }
            }

            return removed;
        }

        /// <summary>
        /// The bytes at <paramref name="path"/>, or null on ANY miss: no file, an unreadable one, or an EMPTY
        /// one. Nothing here throws.
        /// <para>
        /// A ZERO-LENGTH FILE IS A MISS RATHER THAN EMPTY BYTES. It is what a process that died mid-write leaves
        /// behind on a platform where the rename below is not atomic, and no backend's payload can legitimately
        /// be empty: an empty DXBC is not a shader, an empty pipeline blob fails its header check, and an empty
        /// MSL payload carries no stage.
        /// </para>
        /// </summary>
        internal static byte[]? TryReadAllBytes(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                return bytes.Length == 0 ? null : bytes;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                return null;
            }
        }

        /// <summary>
        /// Write <paramref name="bytes"/> to <paramref name="path"/>, best effort. Returns whether it landed, for
        /// a test and for a diagnostic line, and never throws.
        /// <para>
        /// WRITTEN TO A PROCESS-UNIQUE TEMPORARY NAME IN THE SAME DIRECTORY AND MOVED INTO PLACE, so a reader
        /// either sees the whole entry or no entry. A plain write leaves a truncated file behind when the process
        /// dies mid-write, and a truncated entry is exactly the shape that reaches a driver and fails obscurely.
        /// The temporary name is per-process-unique rather than a fixed <c>.tmp</c>, so two processes writing the
        /// same entry at once cannot overwrite each other's partial file. Two processes writing the same entry is
        /// the ordinary case rather than the corner on a content-keyed cache: the payloads are identical, so the
        /// last rename wins and wins with the same bytes.
        /// </para>
        /// <para>
        /// THE DIRECTORY IS CREATED HERE AND NOT AT CONSTRUCTION, so a process that only ever reads (every entry
        /// already present, or every compile failing) leaves no directory behind.
        /// </para>
        /// </summary>
        internal static bool TryWriteAtomic(string path, ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return false;

            string temp = string.Empty;
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                if (temp.Length != 0) TryDelete(temp);
                return false;
            }
        }

        /// <summary>Delete <paramref name="path"/>, best effort, and never throw. A file that was never there is
        /// not a failure, and one that will not delete is litter rather than a failure: the next write takes a
        /// fresh temporary name, and an entry nothing could remove costs one more miss on the next launch.
        /// </summary>
        internal static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
            }
        }

        // Everything a file system can reasonably say no with. Deliberately not a bare catch: an
        // OutOfMemoryException or a cancellation is not a cache miss and must not be swallowed as one.
        static bool IsRecoverable(Exception ex)
            => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }
}
