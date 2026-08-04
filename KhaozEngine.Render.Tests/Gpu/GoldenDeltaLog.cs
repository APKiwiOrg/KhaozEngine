using System;
using System.Globalization;
using System.IO;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// EVERY golden compare's worst per-cell difference, written down. One line per golden, appended to a
    /// per-backend file in the evidence directory, on a PASS exactly as much as on a failure.
    /// <para>
    /// THREE PATHS RECORD NOTHING, and they are the ones that never reach a compare: a missing golden file and a
    /// golden whose cell count does not match the capture both fail inside <see cref="GoldenCompare"/> before
    /// the worst-cell number is computed, so a run carrying either one is short a line rather than holding a
    /// zero. The third is a bake, KE_UPDATE_GOLDENS set: the updateGoldens branch in
    /// <see cref="GoldenCompare"/> writes the new reference and returns before it ever gets to a compare, so a
    /// bake compares nothing and there is no delta to record. It is also the most common of the three, since it
    /// is the deliberate way to run the suite rather than a failure mode.
    /// </para>
    /// <para>
    /// WHY A PASSING COMPARE HAS TO WRITE ANYTHING AT ALL. Gate 1 of the native Direct3D 11 rollout
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/460) is the OBSERVED worst-cell delta of the 36 committed
    /// goldens, and the only run that can produce it is a GREEN one: a red run says a golden moved past the
    /// tolerance and says nothing about the headroom the other thirty-five had. <see cref="GoldenCompare"/>
    /// already computed that number and only ever put it in a failure message, so on the run the gate cares about
    /// it was computed and thrown away. This file is where it lands instead, and
    /// <c>.github/workflows/cross-platform-gpu.yml</c> uploads it as an artifact so the number is readable off a
    /// CI run rather than reproduced by hand on somebody's box.
    /// </para>
    /// <para>
    /// ACCUMULATIVE PER PROCESS RUN, and deliberately not truncated at startup: a golden suite is one process and
    /// the lines are the run's own, so appending is what makes the file the whole run rather than whichever test
    /// finished last. A fresh CI job starts with no file. A repeated local run stacks onto the previous one, which
    /// is the right failure direction for a gitignored scratch file (<c>Gpu/goldens-evidence/</c> is ignored, and
    /// so is anything written into a <c>KE_GOLDEN_EVIDENCE_DIR</c> pointed elsewhere): a duplicated line is
    /// obvious, a silently truncated run is not.
    /// </para>
    /// <para>
    /// THE LOCK IS NOT DECORATION. Golden tests run in parallel xunit collections, so several threads reach this
    /// at once, and two unsynchronised appends to one file interleave into a corrupt line or lose one outright.
    /// </para>
    /// </summary>
    internal static class GoldenDeltaLog
    {
        // One writer at a time, process-wide. Static because the file is per (evidence dir, backend) and the
        // colliding writers are threads of one run, so a lock per instance would have nothing to protect.
        static readonly object _writeLock = new();

        /// <summary>The filename stem, shared by the writer and by the tests that assert on it so the two cannot
        /// disagree about what the workflow is meant to upload.</summary>
        internal const string FileNamePrefix = "golden-deltas.";

        /// <summary>Where a backend's deltas land: <c>&lt;evidenceDir&gt;/golden-deltas.&lt;backend&gt;.txt</c>.
        /// </summary>
        internal static string PathFor(string evidenceDir, string backend)
            => Path.Combine(evidenceDir, FileNamePrefix + backend + ".txt");

        /// <summary>Append one golden's worst per-channel absolute difference, as
        /// <c>&lt;name&gt; worst=&lt;value&gt;</c> with four decimals in the invariant culture, so the file reads
        /// the same on a machine whose decimal separator is a comma.</summary>
        internal static void Append(string evidenceDir, string backend, string name, float worst)
        {
            string line = name + " worst=" + worst.ToString("0.####", CultureInfo.InvariantCulture)
                + Environment.NewLine;

            lock (_writeLock)
            {
                Directory.CreateDirectory(evidenceDir);
                File.AppendAllText(PathFor(evidenceDir, backend), line);
            }
        }
    }
}
