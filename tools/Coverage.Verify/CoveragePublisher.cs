using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KhaozEngine.Tools.Coverage;

internal sealed record CoverageStagedFile(string DestinationPath, string StagePath, long ByteLength, string Sha256);
internal enum CoveragePublicationStep { StagePart, FlushPart, Backup, ReplaceTable, ReplaceDiagnostic, ReplaceEvidence, Rollback, Cleanup }

/// <summary>Throws a synthetic I/O failure at the <see cref="Ordinal"/>th operation labelled <see cref="Step"/>, and
/// optionally at every rollback operation labelled <see cref="RollbackStep"/>. A rollback step of
/// <see cref="CoveragePublicationStep.Rollback"/> fails every rollback operation.</summary>
internal readonly record struct CoveragePublicationFault(CoveragePublicationStep Step, int Ordinal, CoveragePublicationStep? RollbackStep = null);

internal sealed class CoveragePublicationFaultException(CoveragePublicationStep step) : IOException("Injected publication fault.")
{
    internal CoveragePublicationStep Step { get; } = step;
}

/// <summary>Publishes a table, diagnostic roots and evidence roots with their parts as one recoverable set.
/// Parts are immutable and content addressed, so they are created beside any older generation and never replaced.
/// A journal and backups are written before any mutable output is replaced, then the table, the diagnostic roots
/// and the evidence roots are replaced in that order. The last replacement is the commit point. A handled failure
/// before it restores every earlier output. Crash atomicity across independently named files is not provided.</summary>
internal static class CoveragePublisher
{
    internal static void Publish(IReadOnlyList<CoverageStagedFile> files, IReadOnlyList<CoverageStagedArtifact> artifacts,
        IReadOnlyList<string> protectedInputPaths) => new Transaction(files, artifacts, null).Run(protectedInputPaths);

    internal static void PublishForTests(IReadOnlyList<CoverageStagedFile> files, IReadOnlyList<CoverageStagedArtifact> artifacts,
        IReadOnlyList<string> protectedInputPaths, CoveragePublicationFault fault) => new Transaction(files, artifacts, fault).Run(protectedInputPaths);

    /// <summary>Finishes or undoes an incomplete publication whose commit root is <paramref name="evidencePath"/>. That
    /// is the evidence root, or the diagnostic root of a diagnostic-only publication. No journal means nothing to do.</summary>
    internal static void Recover(string evidencePath) => CoveragePublicationJournal.Recover(evidencePath);

    sealed class Transaction
    {
        readonly string id = Guid.NewGuid().ToString("N");
        readonly CoveragePublicationFault? fault;
        readonly Dictionary<CoveragePublicationStep, int> counts = [];
        readonly List<CoveragePublicationEntry> entries = [];
        readonly List<(CoverageArtifactPart Part, bool Created)> parts = [];
        readonly List<string> directories = [], temporaries = [], backups = [], markers = [];
        readonly IReadOnlyList<(CoverageStagedFile Source, CoveragePublicationStep Step)> planned;
        readonly IReadOnlyList<CoverageArtifactPart> descriptors;
        bool journal;
        int replaced;

        internal Transaction(IReadOnlyList<CoverageStagedFile> files, IReadOnlyList<CoverageStagedArtifact> artifacts, CoveragePublicationFault? fault)
        {
            this.fault = fault;
            var diagnostics = artifacts.Where(a => a.Header.Kind == CoverageArtifactKind.Diagnostics).ToArray();
            var evidence = artifacts.Where(a => a.Header.Kind == CoverageArtifactKind.Evidence).ToArray();
            planned = [.. files.Select(f => (f, CoveragePublicationStep.ReplaceTable)),
                .. diagnostics.Select(a => (Root(a), CoveragePublicationStep.ReplaceDiagnostic)),
                .. evidence.Select(a => (Root(a), CoveragePublicationStep.ReplaceEvidence))];
            descriptors = [.. diagnostics.Concat(evidence).SelectMany(a => a.Parts)];
        }

        static CoverageStagedFile Root(CoverageStagedArtifact artifact) => new(artifact.RootDestinationPath, artifact.RootStagePath,
            new FileInfo(artifact.RootStagePath).Length, artifact.RootSha256);

        internal void Run(IReadOnlyList<string> protectedInputPaths)
        {
            if (planned.Count == 0) return;
            Validate(protectedInputPaths);
            try
            {
                foreach (var part in descriptors) PublishPart(part);
                foreach (var (source, step) in planned)
                {
                    var entry = CoveragePublicationJournal.Entry(step, Path.GetFullPath(source.DestinationPath), id, null, null, source.ByteLength, source.Sha256);
                    Copy(source.StagePath, entry.Stage, source.ByteLength, source.Sha256);
                    entries.Add(entry);
                }
                Prepare();
                foreach (var entry in entries.Where(e => e.OldSha256 != null)) Backup(entry);
                foreach (var entry in entries)
                {
                    Hit(entry.Step);
                    File.Move(entry.Stage, entry.Destination, true);
                    temporaries.Remove(entry.Stage);
                    replaced++;
                }
            }
            catch (Exception error) when (Handled(error))
            {
                var failures = Rollback();
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output publication refused.", error)
                { RollbackFailures = failures };
            }
            Cleanup();
        }

        static bool Handled(Exception error) => error is IOException or UnauthorizedAccessException or CoverageBuildValidationException;

        static CoverageBuildValidationException Alias() =>
            new(CoverageBuildValidationRefusal.OutputAlias, "Outputs must be distinct from checked inputs and from each other.");

        static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;

        /// <summary>Every check runs before the first write. Mutable outputs must be distinct from each other and from
        /// every protected input by OS identity. Generated part paths must contain no link.</summary>
        void Validate(IReadOnlyList<string> protectedInputPaths)
        {
            var outputs = new List<CoveragePathIdentity.Destination>();
            foreach (var (source, _) in planned)
            {
                string destination = Path.GetFullPath(source.DestinationPath);
                CoveragePublicationJournal.EnsureReadable(destination);
                var identity = CoveragePathIdentity.Of(destination);
                if (outputs.Contains(identity)) throw Alias();
                outputs.Add(identity);
            }
            var inputs = new List<CoveragePathIdentity.Destination>();
            foreach (string input in protectedInputPaths)
            {
                // An input whose own location cannot be identified has no destination an output could reach.
                try { inputs.Add(CoveragePathIdentity.Of(input)); }
                catch (CoverageBuildValidationException) { }
            }
            if (outputs.Any(inputs.Contains)) throw Alias();
            foreach (var part in descriptors)
            {
                string generation = Path.GetDirectoryName(part.DestinationPath)!, set = Path.GetDirectoryName(generation)!;
                if (IsLink(set) || IsLink(generation) || IsLink(part.DestinationPath)) throw Alias();
                if (!Directory.Exists(generation)) continue;
                var identity = CoveragePathIdentity.Of(part.DestinationPath);
                if (outputs.Contains(identity) || inputs.Contains(identity)) throw Alias();
            }
        }

        void Hit(CoveragePublicationStep step)
        {
            int ordinal = counts.GetValueOrDefault(step);
            counts[step] = ordinal + 1;
            if (fault is { } value && value.Step == step && value.Ordinal == ordinal) throw new CoveragePublicationFaultException(step);
        }

        void PublishPart(CoverageArtifactPart part)
        {
            string generation = Path.GetDirectoryName(part.DestinationPath)!;
            OwnDirectory(Path.GetDirectoryName(generation)!);
            OwnDirectory(generation);
            if (File.Exists(part.DestinationPath)) { Reuse(part); return; }
            string temporary = part.DestinationPath + "." + id + ".tmp";
            Copy(part.StagePath, temporary, part.ByteLength, part.Sha256);
            try { File.Move(temporary, part.DestinationPath, overwrite: false); }
            catch (IOException) when (File.Exists(part.DestinationPath))
            {
                File.Delete(temporary);
                temporaries.Remove(temporary);
                Reuse(part);
                return;
            }
            temporaries.Remove(temporary);
            parts.Add((part, true));
        }

        /// <summary>A part name already present is reused only when its bytes are the descriptor's. Anything else is left
        /// untouched and refuses.</summary>
        void Reuse(CoverageArtifactPart part)
        {
            if (!CoveragePublicationJournal.Matches(CoveragePublicationJournal.State(part.DestinationPath), part.ByteLength, part.Sha256))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "An existing part differs from its descriptor.");
            parts.Add((part, false));
        }

        void OwnDirectory(string directory)
        {
            if (Directory.Exists(directory)) return;
            Directory.CreateDirectory(directory);
            directories.Add(directory);
        }

        void Copy(string source, string destination, long length, string sha256)
        {
            Hit(CoveragePublicationStep.StagePart);
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaries.Add(destination);
                input.CopyTo(output, CoverageArtifactSet.BufferBytes);
                Hit(CoveragePublicationStep.FlushPart);
                output.Flush(true);
            }
            if (!CoveragePublicationJournal.Matches(CoveragePublicationJournal.State(destination), length, sha256))
                throw new IOException("A staged output does not match its descriptor.");
        }

        /// <summary>Records each destination's old state, then writes the journal and an incomplete marker beside every
        /// mutable output. Readers refuse any root with a marker until recovery runs.</summary>
        void Prepare()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (Directory.Exists(entries[i].Destination)) throw new IOException("An output destination is a directory.");
                var state = CoveragePublicationJournal.State(entries[i].Destination);
                entries[i] = entries[i] with { OldLength = state?.Length, OldSha256 = state?.Sha256 };
            }
            var record = Record();
            using (var output = CoveragePublicationJournal.CreatePrivate(record.JournalPath))
            {
                journal = true;
                CoveragePublicationJournal.Write(output, record);
                output.Flush(true);
            }
            foreach (var entry in entries)
            {
                using var marker = CoveragePublicationJournal.CreatePrivate(entry.Marker);
                markers.Add(entry.Marker);
                marker.Write(System.Text.Encoding.UTF8.GetBytes(record.JournalPath));
                marker.Flush(true);
            }
        }

        CoveragePublicationRecord Record() => new(id, entries.ToArray(),
            parts.Select(p => new CoveragePublicationPart(p.Part.DestinationPath, p.Part.ByteLength, p.Part.Sha256, p.Created)).ToArray(),
            directories.ToArray());

        void Backup(CoveragePublicationEntry entry)
        {
            Hit(CoveragePublicationStep.Backup);
            using (var input = File.OpenRead(entry.Destination))
            using (var output = CoveragePublicationJournal.CreatePrivate(entry.Backup))
            {
                backups.Add(entry.Backup);
                input.CopyTo(output, CoverageArtifactSet.BufferBytes);
                output.Flush(true);
            }
            if (!CoveragePublicationJournal.Matches(CoveragePublicationJournal.State(entry.Backup), entry.OldLength, entry.OldSha256))
                throw new IOException("An output changed during publication.");
        }

        /// <summary>Attempts every restore in reverse order, so one failure never skips the rest. Created parts and
        /// directories go only once every output is restored, since a root left new may still name them. Recovery
        /// material goes only when nothing failed.</summary>
        List<Exception> Rollback()
        {
            var failures = new List<Exception>();
            bool restored = true;
            for (int i = replaced - 1; i >= 0; i--)
            {
                var entry = entries[i];
                bool done = Attempt(failures, entry.Step, () =>
                {
                    if (entry.OldSha256 == null) File.Delete(entry.Destination);
                    else File.Move(entry.Backup, entry.Destination, true);
                });
                if (!done) restored = false;
                else if (entry.OldSha256 != null) backups.Remove(entry.Backup);
            }
            foreach (string temporary in temporaries.ToArray())
                if (Attempt(failures, CoveragePublicationStep.StagePart, () => CoveragePublicationJournal.Remove(temporary))) temporaries.Remove(temporary);
            if (restored)
            {
                foreach (var (part, _) in parts.Where(p => p.Created).Reverse())
                    Attempt(failures, CoveragePublicationStep.StagePart, () => File.Delete(part.DestinationPath));
                foreach (string directory in Enumerable.Reverse(directories))
                    Attempt(failures, CoveragePublicationStep.StagePart, () =>
                    {
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    });
            }
            if (failures.Count == 0) foreach (string backup in backups) Attempt(failures, CoveragePublicationStep.Backup, () => CoveragePublicationJournal.Remove(backup));
            if (failures.Count == 0) RemoveRecoveryFiles(failures, step => Attempt(failures, step.Step, step.Action));
            return failures;
        }

        bool Attempt(List<Exception> failures, CoveragePublicationStep step, Action action)
        {
            try
            {
                if (fault?.RollbackStep is { } rollback && (rollback == step || rollback == CoveragePublicationStep.Rollback))
                    throw new CoveragePublicationFaultException(step);
                action();
                return true;
            }
            catch (Exception error) when (Handled(error)) { failures.Add(error); return false; }
        }

        /// <summary>Markers go before the journal, and the journal only once every marker is gone, so a reader never
        /// meets a marker that recovery cannot explain.</summary>
        void RemoveRecoveryFiles(List<Exception> failures, Func<(CoveragePublicationStep Step, Action Action), bool> attempt)
        {
            foreach (string marker in markers) attempt((CoveragePublicationStep.Cleanup, () => CoveragePublicationJournal.Remove(marker)));
            if (failures.Count == 0 && journal) attempt((CoveragePublicationStep.Cleanup, () => File.Delete(Record().JournalPath)));
        }

        /// <summary>After the commit point the new set is complete. A cleanup failure keeps the journal, so readers
        /// refuse and recovery later confirms the new set and removes what remains.</summary>
        void Cleanup()
        {
            var failures = new List<Exception>();
            bool Step(CoveragePublicationStep label, Action action)
            {
                try { Hit(label); action(); return true; }
                catch (Exception error) when (Handled(error)) { failures.Add(error); return false; }
            }
            foreach (string backup in backups) Step(CoveragePublicationStep.Cleanup, () => CoveragePublicationJournal.Remove(backup));
            if (failures.Count == 0) RemoveRecoveryFiles(failures, step => Step(step.Step, step.Action));
            if (failures.Count > 0)
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Published outputs need recovery cleanup.",
                    failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }
}
