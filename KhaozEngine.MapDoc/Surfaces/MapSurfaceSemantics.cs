using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Portable content identity. Physical topology and authored metadata are semantic inputs.</summary>
public static class MapSurfaceSemantics
{
    /// <summary>Portable native root identity, computable from a manifest alone. It covers globals and surface
    /// metadata only, never tile content: placements, spawns, player spawns and sculpt are outside it, and the
    /// resolver-2 whole token adds them through its content digest. Storage addresses and page packing are
    /// excluded.</summary>
    public static string RootDigest(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MapDocument root = MapTiledFile.GlobalsOnly(document);
        root.Surfaces = new MapSurfaceSet();
        foreach (MapSurfaceRef surface in document.Surfaces.Refs.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            MapSurfaceStorageIndex? index = document.Tiles?.Surfaces;
            bool unknown = index is not null && (index.Directory.Any(d => d.SurfaceId == surface.Id &&
                !index.DirectoryPages.ContainsKey(d.Sha256)) || index.Directory.Where(d => d.SurfaceId == surface.Id)
                .Where(d => index.DirectoryPages.ContainsKey(d.Sha256)).SelectMany(d => index.DirectoryPages[d.Sha256])
                .Any(p => !index.IndexPages.ContainsKey(p.Sha256)));
            var digests = new SortedDictionary<MapPatchKey, string>();
            if (index is not null)
                foreach (MapSurfaceIndexEntry entry in index.Entries.Where(e => e.Key.SurfaceId == surface.Id && !e.Loaded))
                    digests.Add(entry.Key, entry.SemanticSha256);
            foreach (var patch in document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id))
                digests[patch.Key] = PatchDigest(patch.Value);
            root.Surfaces.Refs.Add(surface with { SemanticSha256 = unknown ? surface.SemanticSha256 : SurfaceDigest(surface, digests) });
        }
        return MapSurfaceRootProjection.Digest(root);
    }
    public static string PatchDigest(MapSurfacePatch patch)
    {
        byte[] bytes = MapSurfacePatchCodec.Encode(patch);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("kemap/surface-patch/1\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A topology record's semantic digest: the <see cref="PatchDigest"/> of a fixed one-cell envelope at
    /// <paramref name="anchor"/> that holds only <paramref name="record"/>. It covers the record's canonical bytes and
    /// its anchor and nothing of the anchor patch's terrain. Edit effects report record digests in this form.</summary>
    public static string RecordDigest(MapPatchKey anchor, MapTopologyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var envelope = new MapSurfacePatch
        {
            Key = anchor,
            Width = 1,
            Depth = 1,
            Heights = new int[4],
            Cells = new MapSurfaceCell[1],
            Presence = new[] { 1UL },
        };
        envelope.Records.Add(record);
        return PatchDigest(envelope);
    }

    public static string SurfaceDigest(MapSurfaceRef surface, IEnumerable<KeyValuePair<MapPatchKey, string>> patches)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(patches);
        if (string.IsNullOrWhiteSpace(surface.Id) || surface.Frame is null || !Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy))
            throw new MapDocumentException("invalid surface semantic metadata");
        _ = surface.Frame.WorldXz(MapLatticeAddress.Corner(0, 0));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("kemap/surface/1\0"));
        using var sink = new HashSink(hash);
        using (var w = new Utf8JsonWriter(sink))
        {
            w.WriteStartObject(); w.WriteString("id", surface.Id);
            w.WriteStartObject("frame");
            Rational(w, "cellUnitMetres", surface.Frame.CellUnitMetres); Rational(w, "heightUnitMetres", surface.Frame.HeightUnitMetres);
            w.WriteNumber("rowDirection", (byte)surface.Frame.RowDirection); w.WriteNumber("datum", (byte)surface.Frame.Datum); w.WriteEndObject();
            w.WriteNumber("role", (byte)surface.Role); w.WriteNumber("presencePolicy", (byte)surface.PresencePolicy);
            w.WriteString("paintTargetSurfaceId", surface.PaintTargetSurfaceId);
            w.WritePropertyName("indoorSpan");
            if (surface.IndoorSpan is not { } span) w.WriteNullValue();
            else
            {
                w.WriteStartObject(); w.WriteString("id", span.Id); w.WriteStartObject("parentSpace");
                w.WriteString("id", span.ParentSpace.Id); w.WritePropertyName("anchor"); Key(w, span.ParentSpace.Anchor); w.WriteEndObject();
                w.WriteNumber("lowerOffsetUnits", span.LowerOffsetUnits); w.WriteNumber("upperOffsetUnits", span.UpperOffsetUnits);
                w.WriteStartArray("domainTags");
                foreach (string tag in span.DomainTags.OrderBy(t => t, StringComparer.Ordinal)) w.WriteStringValue(tag);
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteStartArray("patches");
            MapPatchKey? previous = null;
            foreach (KeyValuePair<MapPatchKey, string> patch in patches.OrderBy(p => p.Key))
            {
                if (patch.Key.SurfaceId != surface.Id || patch.Key == previous || !IsDigest(patch.Value))
                    throw new MapDocumentException("invalid or duplicate surface patch digest");
                previous = patch.Key;
                w.WriteStartObject(); w.WritePropertyName("key"); Key(w, patch.Key);
                w.WriteString("semanticSha256", patch.Value); w.WriteEndObject();
                w.Flush();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static bool IsDigest(string value) => value is not null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static void Rational(Utf8JsonWriter w, string name, MapRational value)
    {
        w.WriteStartObject(name); w.WriteNumber("numerator", value.Numerator); w.WriteNumber("denominator", value.Denominator); w.WriteEndObject();
    }
    static void Key(Utf8JsonWriter w, MapPatchKey key)
    {
        w.WriteStartObject(); w.WriteString("surfaceId", key.SurfaceId);
        w.WriteString("slotX", key.SlotX.ToString(CultureInfo.InvariantCulture));
        w.WriteString("slotZ", key.SlotZ.ToString(CultureInfo.InvariantCulture)); w.WriteEndObject();
    }
    sealed class HashSink(IncrementalHash hash) : Stream
    {
        long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); _length = checked(_length + buffer.Length); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
