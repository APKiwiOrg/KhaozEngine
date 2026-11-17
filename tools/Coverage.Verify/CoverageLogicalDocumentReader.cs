using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Incremental typed admission. Only one token and the current object's field hashes are resident.</summary>
internal static class CoverageLogicalDocumentReader
{
    static readonly string[] Provenance = ["formatVersion", "tool", "scope", "identity", "inputSha256", "tableSha256", "toolCodeSha256", "render3DCodeSha256"];
    internal sealed record Inspection(CoverageArtifactHeader Header, byte[] ContentHash, IReadOnlyDictionary<string, byte[]> ProvenanceHashes)
    {
        internal void Match(CoverageArtifactHeader expected)
        {
            if (Header.Kind != expected.Kind || Header.PayloadSchema != expected.PayloadSchema || Header.Scope != expected.Scope
                || Header.InputSha256 != expected.InputSha256 || Header.TableSha256 != expected.TableSha256 || Header.Identity != expected.Identity
                || Header.ToolCodeSha256 != expected.ToolCodeSha256 || Header.Render3DCodeSha256 != expected.Render3DCodeSha256
                || Header.Complete != expected.Complete || Header.ResumedEvidenceSha256 != expected.ResumedEvidenceSha256
                || Header.Command != expected.Command || Header.Outcome != expected.Outcome || Header.Totals.Count != expected.Totals.Count
                || Header.Totals.Any(p => !expected.Totals.TryGetValue(p.Key, out long count) || count != p.Value)) throw CoverageArtifactSet.Invalid();
        }
    }
    internal static void Compare(Stream actual, Stream expected, CoverageArtifactKind kind)
    {
        var left = Validate(actual, kind); var right = Validate(expected, kind);
        if (left.ProvenanceHashes.Any(p => !right.ProvenanceHashes.TryGetValue(p.Key, out var hash) || !p.Value.AsSpan().SequenceEqual(hash)))
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ProvenanceMismatch, "Resumed artifact provenance differs.");
        if (!left.ContentHash.AsSpan().SequenceEqual(right.ContentHash))
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ArtifactContentMismatch, "Resumed artifact records differ.");
    }
    internal static Inspection Validate(Stream stream, CoverageArtifactKind kind)
    {
        try { return new Parser(stream).Read(kind); }
        catch (JsonException error) { throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidJson, "Malformed artifact JSON.", error) { Path = error.Path ?? "$" }; }
    }
    sealed record Value(byte[] Hash, object? Scalar = null, long Count = 0, Dictionary<string, Value>? Members = null);
    sealed record Field(Type Type, bool Nullable);
    internal sealed class Tokens(Stream input)
    {
        byte[] buffer = new byte[CoverageArtifactSet.BufferBytes];
        int offset, length;
        bool final;
        JsonReaderState state = new(new JsonReaderOptions { MaxDepth = 32 });
        internal JsonTokenType Kind { get; private set; }
        internal string? Text { get; private set; }
        internal bool Next()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(offset, length - offset), final, state);
                bool read = reader.Read();
                offset += (int)reader.BytesConsumed; state = reader.CurrentState;
                if (read)
                {
                    Kind = reader.TokenType;
                    Text = Kind is JsonTokenType.String or JsonTokenType.PropertyName ? ReadString(ref reader)
                        : Kind == JsonTokenType.Number ? Encoding.UTF8.GetString(reader.ValueSpan) : null;
                    return true;
                }
                if (final) return false;
                int remaining = length - offset;
                buffer.AsSpan(offset, remaining).CopyTo(buffer);
                if (remaining == buffer.Length)
                {
                    CoverageArtifactSet.Capacity((long)buffer.Length + 1, CoverageArtifactSet.MaxBytes);
                    Array.Resize(ref buffer, Math.Min(CoverageArtifactSet.MaxBytes, checked(buffer.Length * 2)));
                }
                offset = 0; length = remaining;
                int count = input.Read(buffer, length, buffer.Length - length);
                length += count; final = count == 0;
            }
        }
        static string? ReadString(ref Utf8JsonReader reader)
        {
            try { return reader.GetString(); }
            catch (InvalidOperationException error)
            {
                // String decoding uses a different BCL exception than lexical JSON validation.
                throw new JsonException("Invalid JSON text encoding.", error);
            }
        }
        internal void Required() { if (!Next()) throw new JsonException(); }
    }
    internal static string? ProbeSchema(Stream stream)
    {
        try
        {
            var tokens = new Tokens(stream); tokens.Required();
            if (tokens.Kind != JsonTokenType.StartObject) throw Invalid("$");
            int depth = 1; string? schema = null;
            while (tokens.Next())
            {
                if (depth == 1 && tokens.Kind == JsonTokenType.PropertyName && tokens.Text == "schema")
                { tokens.Required(); if (tokens.Kind != JsonTokenType.String) throw Invalid("$.schema"); schema = tokens.Text; }
                if (tokens.Kind is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                if (tokens.Kind is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
            }
            return schema;
        }
        catch (JsonException error) { throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidJson, "Malformed artifact JSON.", error); }
    }
    static CoverageBuildDataException Invalid(string path) => new(CoverageBuildInputRefusal.InvalidValue, "Invalid artifact field.") { Path = path };
    sealed class Parser(Stream stream)
    {
        readonly Tokens tokens = new(stream);
        readonly Dictionary<Type, Dictionary<string, Field>> shapes = [];
        readonly Dictionary<string, long> totals = new(StringComparer.Ordinal);
        readonly HashSet<string> samples = new(StringComparer.Ordinal), pairSamples = new(StringComparer.Ordinal);
        readonly HashSet<int> entries = [];
        readonly HashSet<(int, string)> pairs = [];
        readonly HashSet<string> identifiers = new(StringComparer.Ordinal);
        readonly HashSet<string> claims = new(StringComparer.Ordinal), holes = new(StringComparer.Ordinal), candidates = new(StringComparer.Ordinal);
        readonly HashSet<int> claimEntries = [];
        long identifierBytes;
        bool evidence;
        internal Inspection Read(CoverageArtifactKind kind)
        {
            evidence = kind == CoverageArtifactKind.Evidence;
            tokens.Required();
            var root = ReadValue(kind == CoverageArtifactKind.Evidence ? typeof(CoverageEvidenceManifest) : typeof(DiagnosticLogical), "$", false);
            if (tokens.Next()) throw new JsonException();
            var fields = root.Members!;
            if (kind == CoverageArtifactKind.Evidence)
            {
                if ((int)fields["formatVersion"].Scalar! != 1) throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnsupportedVersion, "Unsupported evidence version.");
                foreach (string name in new[] { "domains", "samples", "candidateSampleIds", "candidates", "entries", "pairResults", "selectedClaims", "holes", "denseRows", "replayPoses" }) totals.TryAdd(name, 0);
                if (pairs.Count != checked(totals["samples"] * totals["entries"]) || pairSamples.Except(samples).Any()
                    || entries.Any(i => i < 0 || i >= entries.Count) || pairs.Any(p => !entries.Contains(p.Item1))
                    || claims.Count + holes.Count != samples.Count || claims.Overlaps(holes) || claims.Concat(holes).Except(samples).Any()
                    || candidates.Except(samples).Any() || claimEntries.Any(i => !entries.Contains(i))) throw CoverageArtifactSet.Invalid();
                if (fields["replay"].Members is { } replay && ((int)replay["poseCount"].Scalar! != totals["replayPoses"]
                    || totals["replayPoses"] != totals["denseRows"])) throw CoverageArtifactSet.Invalid("$.replay.poseCount");
            }
            else if ((string?)fields["schema"].Scalar != "khaozengine.coverage-diagnostics/2") throw CoverageArtifactSet.Invalid("$.schema");
            var identity = fields["identity"].Members!;
            string Text(string name) => (string)fields[name].Scalar!;
            string Digest(string name) => (string)identity[name].Scalar!;
            var header = new CoverageArtifactHeader(kind, kind == CoverageArtifactKind.Evidence ? Text("tool") : Text("schema"),
                Text("scope"), Text("inputSha256"), (string?)fields["tableSha256"].Scalar,
                new(Digest("hostSha256"), Digest("hostMapSha256"), Digest("configurationSha256"), Digest("refinementSha256"), Digest("arithmeticSha256"), Digest("stateSchemaSha256")),
                Text("toolCodeSha256"), Text("render3DCodeSha256"), totals, kind == CoverageArtifactKind.Evidence ? (bool)fields["complete"].Scalar! : true,
                kind == CoverageArtifactKind.Diagnostics ? (string?)fields["resumedEvidenceSha256"].Scalar : null,
                kind == CoverageArtifactKind.Diagnostics ? Text("command") : null, kind == CoverageArtifactKind.Diagnostics ? Text("outcome") : null);
            return new(header, root.Hash, fields.Where(p => Provenance.Contains(p.Key) || kind == CoverageArtifactKind.Diagnostics && p.Key == "schema").ToDictionary(p => p.Key, p => p.Value.Hash));
        }
        Value ReadValue(Type type, string path, bool nullable)
        {
            if (tokens.Kind == JsonTokenType.Null)
            {
                if (!nullable && type != typeof(object)) throw Invalid(path);
                return Scalar(null, "null");
            }
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(object)) return Any(path);
            if (type == typeof(string) || type.IsEnum)
            {
                if (tokens.Kind != JsonTokenType.String) throw Invalid(path);
                string text = tokens.Text!;
                if (path.EndsWith("Sha256", StringComparison.Ordinal) && (text.Length != 64 || text.Any(c => !char.IsAsciiHexDigitLower(c)))) throw Invalid(path);
                if (type.IsEnum)
                {
                    object? value;
                    try { value = JsonSerializer.Deserialize(JsonSerializer.Serialize(text), type, CoverageArtifactSet.Options); }
                    catch (JsonException) { throw Invalid(path); }
                    if (value == null || !Enum.IsDefined(type, value)) throw Invalid(path);
                }
                return Scalar(text, "s" + text);
            }
            if (type == typeof(bool))
            {
                if (tokens.Kind is not (JsonTokenType.True or JsonTokenType.False)) throw Invalid(path);
                bool value = tokens.Kind == JsonTokenType.True; return Scalar(value, value ? "true" : "false");
            }
            if (type.IsPrimitive || type == typeof(decimal)) return Number(type, path);
            Type? element = type.IsArray ? type.GetElementType() : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? type.GetGenericArguments()[0] : null;
            if (element != null) return ArrayValue(element, path);
            return ObjectValue(type, path);
        }
        static Value Scalar(object? value, string canonical) => new(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)), value);
        Value Number(Type type, string path)
        {
            if (tokens.Kind != JsonTokenType.Number) throw Invalid(path);
            try
            {
                object value = type == typeof(float) ? float.Parse(tokens.Text!, CultureInfo.InvariantCulture)
                    : type == typeof(double) ? double.Parse(tokens.Text!, CultureInfo.InvariantCulture)
                    : Convert.ChangeType(tokens.Text!, type, CultureInfo.InvariantCulture);
                if (value is float f) { if (!float.IsFinite(f)) throw Invalid(path); return Scalar(f, "f" + BitConverter.SingleToUInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture)); }
                if (value is double d) { if (!double.IsFinite(d)) throw Invalid(path); return Scalar(d, "d" + BitConverter.DoubleToUInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture)); }
                return Scalar(value, "i" + Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            catch (Exception error) when (error is FormatException or OverflowException) { throw Invalid(path); }
        }
        Value ArrayValue(Type element, string path)
        {
            if (tokens.Kind != JsonTokenType.StartArray) throw Invalid(path);
            string name = path[(path.LastIndexOf('.') + 1)..];
            long limit = element == typeof(float) ? 16 : name == "palette" ? 1024 : name is "commands" or "equipment" ? 4
                : name is "candidateSampleIds" or "candidates" or "entries" ? 4096
                : name is "pairResults" or "candidatePairs" || !evidence && name == "items" ? 4_194_304 : 65_536;
            if (evidence && name is "denseRows" or "poses") limit -= totals.GetValueOrDefault(name == "poses" ? "replayPoses" : name);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("["u8); long count = 0;
            while (true)
            {
                tokens.Required(); if (tokens.Kind == JsonTokenType.EndArray) break;
                CoverageArtifactSet.Capacity(++count, limit);
                var value = ReadValue(element, path + "[]", false);
                hash.AppendData(value.Hash);
                if (evidence && name is "candidateSampleIds" or "sampleIds")
                {
                    var inventory = name == "sampleIds" ? holes : candidates;
                    if (!inventory.Add(Intern((string)value.Scalar!))) throw CoverageArtifactSet.Invalid(path);
                }
            }
            hash.AppendData("]"u8);
            if (evidence && (path == "$." + name || name is "denseRows" or "poses"))
            {
                string key = name == "poses" ? "replayPoses" : name;
                totals[key] = totals.GetValueOrDefault(key) + count;
            }
            if (element == typeof(float) && count != 16) throw Invalid(path);
            if (name == "palette" && count is < 1 or > 1024 || name == "commands" && count is < 1 or > 4) throw Invalid(path);
            return new(hash.GetHashAndReset(), Count: count);
        }
        Value ObjectValue(Type type, string path)
        {
            if (tokens.Kind != JsonTokenType.StartObject) throw Invalid(path);
            Dictionary<string, Field>? shape = null;
            if (type != typeof(object) && !shapes.TryGetValue(type, out shape))
            {
                var nullability = new NullabilityInfoContext();
                shape = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
                    .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name),
                        p => new Field(p.PropertyType, nullability.Create(p).ReadState == NullabilityState.Nullable));
                shapes[type] = shape;
            }
            var fields = new Dictionary<string, Value>(StringComparer.Ordinal);
            while (true)
            {
                tokens.Required(); if (tokens.Kind == JsonTokenType.EndObject) break;
                if (tokens.Kind != JsonTokenType.PropertyName) throw Invalid(path);
                string name = tokens.Text!, memberPath = path + "." + name;
                if (fields.ContainsKey(name)) throw new CoverageBuildDataException(CoverageBuildInputRefusal.DuplicateMember, "Duplicate artifact member.") { Path = memberPath, MemberName = name };
                if (shape != null && !shape.ContainsKey(name)) throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnknownMember, "Unknown artifact member.") { Path = memberPath, MemberName = name };
                CoverageArtifactSet.Capacity(fields.Count + 1, 128); tokens.Required();
                fields[name] = ReadValue(shape?[name].Type ?? typeof(object), memberPath, shape?[name].Nullable ?? true);
            }
            if (shape != null)
                foreach (string name in shape.Keys)
                    if (!fields.ContainsKey(name)) throw new CoverageBuildDataException(CoverageBuildInputRefusal.MissingMember, "Missing artifact member.") { Path = path + "." + name, MemberName = name };
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("{"u8);
            foreach (var field in fields.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(field.Key))); hash.AppendData(field.Value.Hash);
            }
            hash.AppendData("}"u8); Observe(type, fields, path);
            return new(hash.GetHashAndReset(), Members: fields);
        }
        void Observe(Type type, Dictionary<string, Value> fields, string path)
        {
            if (type == typeof(CoverageSampleInventory) && !samples.Add(Intern((string)fields["id"].Scalar!))) throw CoverageArtifactSet.Invalid(path);
            if (type == typeof(CoverageEntryEvidence) && !entries.Add((int)fields["entryIndex"].Scalar!)) throw CoverageArtifactSet.Invalid(path);
            if (type == typeof(CoverageSelectedClaim))
            {
                if (!claims.Add(Intern((string)fields["sampleId"].Scalar!))) throw CoverageArtifactSet.Invalid(path);
                claimEntries.Add((int)fields["entryIndex"].Scalar!);
            }
            if (type == typeof(CoverageReplayPose) && (int)fields["equipmentCount"].Scalar! != fields["equipment"].Count) throw CoverageArtifactSet.Invalid(path + ".equipmentCount");
            if (type == typeof(CoveragePairResult))
            {
                string id = Intern((string)fields["sampleId"].Scalar!);
                pairSamples.Add(id); CoverageArtifactSet.Capacity(pairSamples.Count, 65_536);
                if (!pairs.Add(((int)fields["entryIndex"].Scalar!, id))) throw CoverageArtifactSet.Invalid(path);
            }
            if (type == typeof(DiagnosticCategory))
            {
                long count = (long)fields["total"].Scalar!;
                if (count != fields["items"].Count || (long)fields["omitted"].Scalar! != 0) throw CoverageArtifactSet.Invalid(path);
                totals[path[2..]] = count;
            }
        }
        string Intern(string id)
        {
            if (identifiers.TryGetValue(id, out string? shared)) return shared;
            identifierBytes += Encoding.UTF8.GetByteCount(id);
            CoverageArtifactSet.Capacity(identifierBytes, CoverageArtifactSet.MaxBytes);
            identifiers.Add(id);
            return id;
        }
        Value Any(string path) => tokens.Kind switch
        {
            JsonTokenType.StartObject => ObjectValue(typeof(object), path),
            JsonTokenType.StartArray => ArrayValue(typeof(object), path),
            JsonTokenType.String => Scalar(tokens.Text, "s" + tokens.Text),
            JsonTokenType.True => Scalar(true, "true"), JsonTokenType.False => Scalar(false, "false"),
            JsonTokenType.Number => decimal.TryParse(tokens.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
                ? Scalar(value, "n" + value.ToString("G29", CultureInfo.InvariantCulture)) : Number(typeof(double), path),
            _ => throw Invalid(path),
        };
    }
    sealed record DiagnosticCategory(long Total, long Omitted, object[] Items);
    sealed record DiagnosticLogical(string Schema, string Scope, string Command, string Outcome, string InputSha256,
        CoverageRecessionIdentity Identity, string? TableSha256, string? Reason, long FindingCount,
        string ToolCodeSha256, string Render3DCodeSha256, string? ResumedEvidenceSha256,
        DiagnosticCategory Domains, DiagnosticCategory Samples, DiagnosticCategory Candidates, DiagnosticCategory CandidateEntries,
        DiagnosticCategory CandidatePairs, DiagnosticCategory Entries, DiagnosticCategory PairResults, DiagnosticCategory SelectedClaims,
        DiagnosticCategory Holes, DiagnosticCategory SelectedFailures, DiagnosticCategory ReplayFailures, DiagnosticCategory MissingDenseWork,
        DiagnosticCategory ChangedRecordDepth, DiagnosticCategory InheritedDepth, DiagnosticCategory AnalysisRefusals, DiagnosticCategory RunRefusals);
}
