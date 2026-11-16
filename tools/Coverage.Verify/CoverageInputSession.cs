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
    bool disposed;
    long snapshotBytes;
    int maximumChunkBufferBytes, maximumLiveNumericPoseBytes;

    CoverageInputSession(string scratchDirectory) => scratch = Path.Combine(Path.GetFullPath(scratchDirectory), "coverage-input-" + Guid.NewGuid().ToString("N"));
    internal CoverageCampaign Campaign { get { EnsureAlive(); return campaign; } }
    internal CoverageInputStorageUsage StorageForTests => new(snapshotBytes, maximumChunkBufferBytes, maximumLiveNumericPoseBytes);

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
        foreach (var sample in metadata.Samples) _ = Read(sample);
        foreach (var domain in metadata.DenseRows) foreach (var sample in domain) _ = Read(sample);
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

    CoverageSample Read(CoveragePackedInputJson.Slot slot)
    {
        EnsureAlive();
        CoverageNumericPose numeric;
        if (slot.Reference is { } reference)
        {
            var snapshot = snapshots[reference.ChunkIndex];
            using var stream = File.OpenRead(snapshot.Path);
            stream.Position = checked(32L + (long)reference.RecordIndex * snapshot.Header.RecordStride);
            byte[] bytes = new byte[snapshot.Header.RecordStride]; stream.ReadExactly(bytes);
            maximumChunkBufferBytes = Math.Max(maximumChunkBufferBytes, bytes.Length);
            numeric = CoveragePackedPoseCodec.ReadRecord(bytes, snapshot.Header, snapshot.Location with { RecordIndex = reference.RecordIndex });
        }
        else numeric = CoveragePackedInputJson.ReadInline(slot.Value, slot.Path);
        int numericBytes = checked(64 + 64 * numeric.Palette.Length + 144 * numeric.Commands.Length);
        // Binding copies the one decoded pose into the returned immutable sample. Both coexist here.
        maximumLiveNumericPoseBytes = Math.Max(maximumLiveNumericPoseBytes, checked(2 * numericBytes));
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
        return new(inventory.Id, inventory.DomainId, inventory.AuthoredFrameId, inventory.BlendPathId, new(inventory.StateKey),
            numeric.ActorModel, numeric.Palette, commands);
    }

    CoverageDenseRow ReadDense(int domainIndex, int rowIndex)
    {
        var row = metadata.Domains[domainIndex].DenseRows[rowIndex];
        return new(row.DenseIndex, row.PrecedingPhaseTick, row.ResolvedStateKey, Read(metadata.DenseRows[domainIndex][rowIndex]), row.Looped, row.Clamped);
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
    }
}
