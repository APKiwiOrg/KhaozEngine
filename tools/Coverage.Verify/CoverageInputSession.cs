using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal readonly record struct CoverageInputStorageUsage(long SnapshotBytes, int MaximumChunkBufferBytes, int MaximumLiveNumericPoseBytes);

/// <summary>Owns checked chunk bytes and metadata. Originals never supply poses after admission.</summary>
internal sealed class CoverageInputSession : IDisposable
{
    sealed record Snapshot(string Path, CoveragePackedHeader Header, CoverageInputLocation Location);
    readonly string scratch;
    readonly List<Snapshot> snapshots = [];
    JsonDocument? document;
    CoveragePackedInputJson.Metadata metadata = null!;
    CoverageInputBinding binding = null!;
    CoverageCampaign campaign = null!;
    bool disposed, failCleanup;
    long snapshotBytes, liveNumericPoseBytes, maximumLiveNumericPoseBytes;
    int maximumChunkBufferBytes;

    CoverageInputSession(string scratchDirectory) => scratch = Path.Combine(Path.GetFullPath(scratchDirectory), "coverage-input-" + Guid.NewGuid().ToString("N"));
    internal CoverageCampaign Campaign { get { EnsureAlive(); return campaign; } }
    internal CoverageInputStorageUsage StorageForTests =>
        new(snapshotBytes, maximumChunkBufferBytes, (int)Math.Min(int.MaxValue, maximumLiveNumericPoseBytes));

    /// <summary>Deletes one checked private copy, as a scratch cleaner or full disk would, so a later pose read from it
    /// fails. The original chunk is untouched.</summary>
    internal void FailCleanupForTests() => failCleanup = true;

    internal void MakeChunkUnreadableForTests(int chunkIndex)
    {
        EnsureAlive();
        File.Delete(snapshots[chunkIndex].Path);
    }

    internal static CoverageInputSession Open(string manifestPath, string scratchDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        string path = Path.GetFullPath(manifestPath);
        CoverageInputSession? session = null;
        try
        {
            session = new(scratchDirectory);
            session.Load(path);
            return session;
        }
        catch (CoverageBuildDataException) { session?.Dispose(); throw; }
        catch (MalformedDataException e) { session?.Dispose(); throw new CoverageBuildDataException(CoverageBuildInputRefusal.AssetBinding, "An input asset was refused.", e) { Identity = path }; }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
        { session?.Dispose(); throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidValue, "Invalid input value.", e) { Identity = path }; }
        catch (JsonException e)
        { session?.Dispose(); throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidJson, "Malformed input JSON.", e) { Identity = path, Path = e.Path ?? "$" }; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { session?.Dispose(); throw new CoverageBuildDataException(CoverageBuildInputRefusal.InputIo, "Cannot read an input.", e) { Identity = path }; }
    }

    void Load(string path)
    {
        byte[] bytes = CoverageBuildInputJson.ReadBytes(path);
        document = CoverageBuildInputJson.Document(bytes);
        metadata = CoveragePackedInputJson.Read(document.RootElement);
        string folder = Path.GetDirectoryName(path)!;
        // Validate every path before opening the first chunk, including a late bad descriptor.
        string[] chunkPaths = metadata.Chunks.Select((c, i) => CoverageInputPath.ResolveContained(folder, c.Path,
            new($"$.poseChunks[{i}]", c.Path, i))).ToArray();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(scratch);
        else Directory.CreateDirectory(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        for (int i = 0; i < metadata.Chunks.Length; i++) SnapshotChunk(metadata.Chunks[i], chunkPaths[i], i);
        var sources = new List<string> { path };
        sources.AddRange(chunkPaths);
        binding = CoverageBuildInputJson.ReadBinding(document.RootElement, folder, sources);
        campaign = new(binding.Topology, binding.InverseBind, metadata.Domains, metadata.Samples.Select(s => s.Inventory).ToArray(),
            metadata.Candidates, binding.Identity, CoverageBuildInputJson.Hash(bytes), sources.Concat(snapshots.Select(s => s.Path)).ToArray(),
            i => Read(metadata.Samples[i]), ReadDense, EnsureAlive, sources);
        // Bind every admitted record without retaining any decoded pose. Replay policy stays in verification.
        foreach (var sample in metadata.Samples) Read(sample).Dispose();
        foreach (var domain in metadata.DenseRows) foreach (var sample in domain) Read(sample).Dispose();
    }

    void SnapshotChunk(CoveragePackedChunkReference descriptor, string sourcePath, int index)
    {
        var location = new CoverageInputLocation($"$.poseChunks[{index}]", sourcePath, index);
        string target = Path.Combine(scratch, index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".kepp");
        try
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = source.Length;
            if (length > CoverageBuildInput.MaxPackedChunkBytes)
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Packed chunk exceeds capacity.")
                { Path = location.Path, Identity = sourcePath, ChunkIndex = index, Count = length, Limit = CoverageBuildInput.MaxPackedChunkBytes };
            if (length != descriptor.ByteLength) throw Length(location, descriptor.ByteLength, length);
            using var snapshot = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            byte[] buffer = new byte[65_536];
            maximumChunkBufferBytes = Math.Max(maximumChunkBufferBytes, buffer.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            while (copied < length)
            {
                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied));
                if (count == 0) throw Changed(location, length, copied);
                hash.AppendData(buffer, 0, count); snapshot.Write(buffer, 0, count); copied += count;
            }
            if (source.ReadByte() != -1 || source.Length != length) throw Changed(location, length, source.Length);
            string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actualHash != descriptor.Sha256) throw HashMismatch(location, descriptor.Sha256, actualHash);
            snapshot.Flush(); snapshot.Position = 0;
            string privateHash = Convert.ToHexStringLower(SHA256.HashData(snapshot));
            if (snapshot.Length != length) throw Changed(location, length, snapshot.Length);
            if (privateHash != descriptor.Sha256) throw HashMismatch(location, descriptor.Sha256, privateHash);
            snapshot.Position = 0;
            byte[] headerBytes = new byte[(int)Math.Min(32, length)]; snapshot.ReadExactly(headerBytes);
            var header = CoveragePackedPoseCodec.ReadHeader(headerBytes, length, location);
            if (header.RecordCount != descriptor.RecordCount)
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.PackedCountMismatch, "Header and descriptor record counts differ.")
                { Path = location.Path, Identity = sourcePath, ChunkIndex = index, ByteOffset = 24, Count = header.RecordCount, Limit = descriptor.RecordCount };
            byte[] record = new byte[header.RecordStride];
            maximumChunkBufferBytes = Math.Max(maximumChunkBufferBytes, record.Length);
            for (int i = 0; i < header.RecordCount; i++)
            {
                snapshot.ReadExactly(record);
                CoveragePackedPoseCodec.ValidateRecord(record, header, location with { RecordIndex = i });
            }
            snapshots.Add(new(target, header, location)); snapshotBytes += length;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { throw new CoverageBuildDataException(CoverageBuildInputRefusal.PackedChunkMissing, "Packed chunk is missing.", e) { Path = location.Path, Identity = sourcePath, ChunkIndex = index }; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CoverageBuildDataException(CoverageBuildInputRefusal.InputIo, "Cannot snapshot a packed input.", e) { Path = location.Path, Identity = sourcePath, ChunkIndex = index }; }
    }

    /// <summary>Decodes one pose and hands it out as a lease. The decoded numeric pose and its immutable binding are
    /// both counted while they coexist, and the binding stays counted until its holder disposes the lease.</summary>
    CoveragePoseLease<CoverageSample> Read(CoveragePackedInputJson.Slot slot)
    {
        EnsureAlive();
        var numeric = Decode(slot);
        int numericBytes = checked(64 + 64 * numeric.Palette.Length + 144 * numeric.Commands.Length);
        Acquire(numericBytes);
        bool bound = false;
        try
        {
            if (numeric.Palette.Length != binding.InverseBind.Length) throw BindingError(slot, ".palette");
            var commands = new CoverageCommandPose[numeric.Commands.Length];
            for (int i = 0; i < commands.Length; i++)
            {
                var pose = numeric.Commands[i];
                var config = binding.Commands.SingleOrDefault(c => c.Definition.Id == pose.Id);
                if (config == null) throw BindingError(slot, ".commands.id");
                var definition = config.Definition;
                commands[i] = new(pose.Id, pose.EquipmentInstanceId, binding.Topology, config.Region, config.Support, pose.EquipmentWorld, pose.CoverageWorld,
                    definition.MinZ, definition.MaxZ, definition.CollarWidth, definition.Clearance, definition.MaxWorldRecession, pose.TransitionWeight);
            }
            if (commands.Length != binding.Commands.Length) throw BindingError(slot, ".commands");
            var inventory = slot.Inventory;
            Acquire(numericBytes);
            bound = true;
            var sample = new CoverageSample(inventory.Id, inventory.DomainId, inventory.AuthoredFrameId, inventory.BlendPathId,
                new(inventory.StateKey), numeric.ActorModel, numeric.Palette, commands);
            // A released pose is overwritten, so a holder that kept it past its lease cannot use it unnoticed.
            return new(sample, () => { sample.Invalidate(); liveNumericPoseBytes -= numericBytes; });
        }
        catch { if (bound) liveNumericPoseBytes -= numericBytes; throw; }
        // The decoded numeric pose is released once its binding exists or binding refused.
        finally { liveNumericPoseBytes -= numericBytes; }
    }

    CoveragePoseLease<CoverageDenseRow> ReadDense(int domainIndex, int rowIndex)
    {
        var row = metadata.Domains[domainIndex].DenseRows[rowIndex];
        var sample = Read(metadata.DenseRows[domainIndex][rowIndex]);
        return new(new(row.DenseIndex, row.PrecedingPhaseTick, row.ResolvedStateKey, sample.Value, row.Looped, row.Clamped), sample.Dispose);
    }

    /// <summary>Reads one record from its checked private copy. A copy that can no longer be read is the input failure
    /// it is, never an output failure, and names the chunk and record.</summary>
    CoverageNumericPose Decode(CoveragePackedInputJson.Slot slot)
    {
        if (slot.Reference is not { } reference) return CoveragePackedInputJson.ReadInline(slot.Value, slot.Path);
        var snapshot = snapshots[reference.ChunkIndex];
        var location = snapshot.Location with { RecordIndex = reference.RecordIndex };
        byte[] bytes = new byte[snapshot.Header.RecordStride];
        try
        {
            using var stream = File.OpenRead(snapshot.Path);
            stream.Position = checked(32L + (long)reference.RecordIndex * snapshot.Header.RecordStride);
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.InputIo, "Cannot read a checked pose copy.", e)
            { Path = location.Path, Identity = snapshot.Path, ChunkIndex = location.ChunkIndex, RecordIndex = location.RecordIndex };
        }
        maximumChunkBufferBytes = Math.Max(maximumChunkBufferBytes, bytes.Length);
        return CoveragePackedPoseCodec.ReadRecord(bytes, snapshot.Header, location);
    }

    void Acquire(int bytes)
    {
        liveNumericPoseBytes += bytes;
        maximumLiveNumericPoseBytes = Math.Max(maximumLiveNumericPoseBytes, liveNumericPoseBytes);
    }

    static CoverageBuildDataException BindingError(CoveragePackedInputJson.Slot slot, string field) =>
        new(CoverageBuildInputRefusal.AssetBinding, "Pose shape or command identity differs from its bound configuration.")
        { Path = slot.Path + field, SampleId = slot.Inventory.Id, ChunkIndex = slot.Reference?.ChunkIndex, RecordIndex = slot.Reference?.RecordIndex };
    static CoverageBuildDataException Length(CoverageInputLocation location, long expected, long actual) =>
        new(CoverageBuildInputRefusal.PackedChunkLengthMismatch, "Packed chunk length differs from its descriptor.")
        { Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, ExpectedLength = expected, ActualLength = actual };
    static CoverageBuildDataException Changed(CoverageInputLocation location, long expected, long actual) =>
        new(CoverageBuildInputRefusal.PackedInputChanged, "Packed input length changed during admission.")
        { Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, ExpectedLength = expected, ActualLength = actual };
    static CoverageBuildDataException HashMismatch(CoverageInputLocation location, string expected, string actual) =>
        new(CoverageBuildInputRefusal.PackedChunkHashMismatch, "Packed chunk digest differs from its descriptor.")
        { Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, ExpectedSha256 = expected, ActualSha256 = actual };
    void EnsureAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; document?.Dispose();
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        if (failCleanup) throw new IOException("Synthetic scratch cleanup failure.");
    }
}
